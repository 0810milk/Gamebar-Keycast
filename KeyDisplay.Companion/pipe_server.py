"""命名管道服务器：向 Game Bar 小组件持续推送状态快照。

- 管道名 \\\\.\\pipe\\KeyDisplayState
- SDDL 授权：Everyone + 当前用户 + 可选 UWP 包 SID（来自 packageFamilyName）
- 使用重叠 I/O 连接，支持优雅退出
"""
import ctypes
import ctypes.wintypes as wt
import threading
import time

import debuglog
import hooks
from hooks import reconcile, sync_mouse_position, raw_stats
from state import SNAPSHOT_SIZE


PIPE_NAME = r"\\.\pipe\KeyDisplayState"

# 虚拟屏幕度量索引（与 GetSystemMetrics 常量一致）
SM_XVIRTUALSCREEN = 76
SM_YVIRTUALSCREEN = 77
SM_CXVIRTUALSCREEN = 78
SM_CYVIRTUALSCREEN = 79

PIPE_ACCESS_DUPLEX = 0x3
FILE_FLAG_OVERLAPPED = 0x40000000
PIPE_TYPE_MESSAGE = 0x4
PIPE_READMODE_MESSAGE = 0x2
PIPE_WAIT = 0x0
PIPE_UNLIMITED_INSTANCES = 255
BUFFER_SIZE = 1024
ERROR_IO_PENDING = 997
ERROR_PIPE_CONNECTED = 535
ERROR_PIPE_BUSY = 231
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)

SDDL_REVISION_1 = 1


class SECURITY_ATTRIBUTES(ctypes.Structure):
    _fields_ = [("nLength", wt.DWORD), ("lpSecurityDescriptor", wt.LPVOID),
                ("bInheritHandle", wt.BOOL)]


class OVERLAPPED(ctypes.Structure):
    _fields_ = [("Internal", ctypes.c_void_p), ("InternalHigh", ctypes.c_void_p),
                ("Offset", wt.DWORD), ("OffsetHigh", wt.DWORD),
                ("hEvent", wt.HANDLE)]


def _derive_package_sid(package_family_name):
    """根据 Package Family Name 计算 UWP 包 SID 字符串；失败返回 None。"""
    try:
        kernel32.DeriveAppContainerSidFromAppContainerName.restype = ctypes.c_void_p
        kernel32.DeriveAppContainerSidFromAppContainerName.argtypes = [wt.LPCWSTR, ctypes.POINTER(ctypes.c_void_p)]
        sid_ptr = ctypes.c_void_p()
        if not kernel32.DeriveAppContainerSidFromAppContainerName(package_family_name, ctypes.byref(sid_ptr)):
            return None
        advapi32.ConvertSidToStringSidW.restype = wt.BOOL
        advapi32.ConvertSidToStringSidW.argtypes = [ctypes.c_void_p, ctypes.POINTER(wt.LPWSTR)]
        out = wt.LPWSTR()
        if not advapi32.ConvertSidToStringSidW(sid_ptr, ctypes.byref(out)):
            return None
        return out.value
    except Exception:
        return None


def _security_attributes(package_family_name=None):
    """构造允许 Everyone + Owner + 可选包 SID 访问的安全属性。

    UWP/AppContainer 令牌即使含 Everyone 组，也仍被 DACL 拒绝访问，
    必须显式授予 "ALL APPLICATION PACKAGES" (S-1-15-2-1) 或具体包 SID。
    """
    sddl = "D:(A;;GA;;;WD)(A;;GA;;;OW)(A;;GA;;;BU)(A;;GA;;;S-1-15-2-1)"
    if package_family_name:
        sid = _derive_package_sid(package_family_name)
        if sid:
            sddl += "(A;;GA;;;%s)" % sid

    sec = SECURITY_ATTRIBUTES()
    sec.nLength = ctypes.sizeof(SECURITY_ATTRIBUTES)
    sec.bInheritHandle = False
    sd = wt.LPVOID()

    advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW.restype = wt.BOOL
    advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [
        wt.LPCWSTR, wt.DWORD, ctypes.POINTER(wt.LPVOID), ctypes.POINTER(wt.ULONG)]
    if not advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl, SDDL_REVISION_1, ctypes.byref(sd), None):
        raise OSError("构造安全描述符失败，错误码 %d" % ctypes.get_last_error())
    sec.lpSecurityDescriptor = sd
    return sec


def _make_pipe(sec_attr):
    kernel32.CreateNamedPipeW.restype = ctypes.c_void_p
    kernel32.CreateNamedPipeW.argtypes = [
        wt.LPCWSTR, wt.DWORD, wt.DWORD, wt.DWORD, wt.DWORD, wt.DWORD,
        wt.DWORD, ctypes.POINTER(SECURITY_ATTRIBUTES)]
    handle = kernel32.CreateNamedPipeW(
        PIPE_NAME, PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
        PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
        PIPE_UNLIMITED_INSTANCES, BUFFER_SIZE, BUFFER_SIZE, 0,
        ctypes.byref(sec_attr))
    return handle


class PipeServer:
    def __init__(self, state, stop_event, package_family_name=None, fps=240):
        self._state = state
        self._stop = stop_event
        self._package_family_name = package_family_name
        self._sec = _security_attributes(package_family_name)
        # 推送帧率由 config.json 的 fps 决定，默认 240Hz（覆盖高刷显示器），可再调高
        self._interval = 1.0 / max(1.0, float(fps))
        self._connected = False

    @property
    def connected(self):
        return self._connected

    def run(self):
        """接受客户端循环：每来一个客户端创建一个管道实例并起独立推送线程。

        多客户端（PIPE_UNLIMITED_INSTANCES）—— Game Bar 会给同一小组件创建多个
        widget 实例（每次打开/固定都会新建），单客户端模型会让后续实例连不上
        （err=231），导致"没有位置显示"。
        """
        while not self._stop.is_set():
            handle = _make_pipe(self._sec)
            if handle == INVALID_HANDLE_VALUE:
                time.sleep(0.25)
                continue
            if not self._connect(handle):
                continue
            threading.Thread(target=self._pump, args=(handle,), daemon=True).start()

    def _connect(self, handle):
        ov = OVERLAPPED()
        ov.hEvent = kernel32.CreateEventW(None, True, False, None)
        if not ov.hEvent:
            kernel32.CloseHandle(handle)
            return False
        kernel32.ConnectNamedPipe.argtypes = [ctypes.c_void_p, ctypes.POINTER(OVERLAPPED)]
        kernel32.ConnectNamedPipe.restype = wt.BOOL

        pending = False
        if not kernel32.ConnectNamedPipe(handle, ctypes.byref(ov)):
            err = ctypes.get_last_error()
            if err == ERROR_PIPE_CONNECTED:
                pass  # 客户端在 ConnectNamedPipe 前已连接
            elif err == ERROR_IO_PENDING:
                pending = True
                wait_handles = (ctypes.c_void_p * 2)(ov.hEvent, self._stop.h_event)
                kernel32.WaitForMultipleObjects(2, wait_handles, False, -1)
                if self._stop.is_set():
                    kernel32.CancelIoEx(handle, ctypes.byref(ov))
                    kernel32.CloseHandle(ov.hEvent)
                    kernel32.CloseHandle(handle)
                    return False
                ok = wt.DWORD()
                kernel32.GetOverlappedResult(handle, ctypes.byref(ov), ctypes.byref(ok), False)
            else:
                kernel32.CloseHandle(ov.hEvent)
                kernel32.CloseHandle(handle)
                time.sleep(0.2)
                return False
        kernel32.CloseHandle(ov.hEvent)
        return True

    def _pump(self, handle):
        """为单个客户端推送快照；客户端断开或停止时退出并关闭句柄。"""
        kernel32.WriteFile.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                       wt.DWORD, ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
        kernel32.WriteFile.restype = wt.BOOL
        debuglog.log("[pipe] client connected")
        try:
            self._pump_loop(handle)
        except Exception as exc:  # noqa: BLE001 泵线程异常必须落盘，否则连接静默抖动
            debuglog.log("[pipe] pump error: %s: %s" % (type(exc).__name__, exc))
        finally:
            kernel32.CloseHandle(handle)
            debuglog.log("[pipe] client disconnected")

    def _pump_loop(self, handle):
        user32 = ctypes.WinDLL("user32", use_last_error=True)
        frames = 0
        summary_at = time.monotonic()
        last_raw = 0
        last_skip = 0
        while not self._stop.is_set():
            reconcile(self._state)
            self._state.vx = user32.GetSystemMetrics(SM_XVIRTUALSCREEN)
            self._state.vy = user32.GetSystemMetrics(SM_YVIRTUALSCREEN)
            self._state.vw = user32.GetSystemMetrics(SM_CXVIRTUALSCREEN)
            self._state.vh = user32.GetSystemMetrics(SM_CYVIRTUALSCREEN)
            # 桌面（光标可见）坐标的 60Hz 校准；隐藏时坐标由 RAWINPUT 增量维护
            sync_mouse_position(self._state)
            blob = self._state.serialize()
            self._state.seq += 1
            written = wt.DWORD()
            buf = ctypes.create_string_buffer(blob)
            if not kernel32.WriteFile(handle, buf, SNAPSHOT_SIZE,
                                      ctypes.byref(written), None):
                debuglog.log("[pipe] write failed err=%d"
                             % ctypes.get_last_error())
                return  # 客户端断开，返回等待重新连接
            frames += 1
            # 每 0.5s 记一条坐标链路摘要（原生输入/限频/坐标来源/坐标，均为本周期增量）
            now = time.monotonic()
            if now - summary_at >= 0.5:
                proc, skip, src = raw_stats()
                delta_raw = proc - last_raw
                delta_skip = skip - last_skip
                last_raw, last_skip = proc, skip
                sx, sy = hooks.scale_stats()
                fps = frames / max(now - summary_at, 1e-6)
                vis = "1" if hooks._cursor_visible() else "0"
                debuglog.log(
                    "[pump] fps=%.0f raw=%d skip=%d src=%s vis=%s "
                    "sx=%.2f sy=%.2f mx=%d my=%d" % (
                        fps, delta_raw, delta_skip, src, vis, sx, sy,
                        self._state.mx, self._state.my))
                summary_at = now
                frames = 0
            time.sleep(self._interval)


class StopFlag:
    """线程安全停止标志，携带可等待的 Win32 事件句柄。"""

    def __init__(self):
        self._lock = threading.Lock()
        self._flag = False
        self.h_event = kernel32.CreateEventW(None, True, False, None)

    def is_set(self):
        return self._flag

    def set(self):
        with self._lock:
            if not self._flag:
                self._flag = True
                kernel32.SetEvent(self.h_event)

    def wait(self, timeout=None):
        if self._flag:
            return True
        return not bool(kernel32.WaitForSingleObject(self.h_event, timeout or -1))