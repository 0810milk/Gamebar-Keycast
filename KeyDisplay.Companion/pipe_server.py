"""命名管道服务器：向 Game Bar 小组件持续推送状态快照。

- 管道名 \\\\.\\pipe\\KeyDisplayState
- SDDL 授权：Everyone + 当前用户 + 可选 UWP 包 SID（来自 packageFamilyName）
- 使用重叠 I/O 连接，支持优雅退出
"""
import ctypes
import ctypes.wintypes as wt
import threading
import time
from collections import deque

from hooks import reconcile, sync_mouse_position
from state import SNAPSHOT_SIZE


def normalize_position(x, y, min_x, max_x, min_y, max_y,
                       floor_x=0, floor_y=0, scale=1000):
    """把坐标归一化到 0..scale（在给定活动范围内）；范围退化（单点）时返回中间值。

    floor_x/floor_y：活动范围小于该值时，以当前位置为中心扩到该值再归一化。
    防止两种严重伪影：
    1) 光标静止时滑动窗口 min/max 朝当前位置收敛，冻结点被归一化后漂移（自行行走）；
    2) 范围过小时微抖动被放大成满垫跑动。
    """
    def norm(v, lo, hi):
        if hi <= lo:
            return scale // 2
        return max(0, min(scale, int((v - lo) * scale / (hi - lo))))

    def window(v, lo, hi, floor):
        span = hi - lo
        if floor > 0 and span < floor:
            center = v
            lo = center - floor / 2
            hi = center + floor / 2
        return lo, hi

    lo_x, hi_x = window(x, min_x, max_x, floor_x)
    lo_y, hi_y = window(y, min_y, max_y, floor_y)
    return norm(x, lo_x, hi_x), norm(y, lo_y, hi_y)

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
        1, BUFFER_SIZE, BUFFER_SIZE, 0, ctypes.byref(sec_attr))
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
        """循环：创建管道 → 等待连接（可中断）→ 按 60Hz 推送快照。"""
        while not self._stop.is_set():
            handle = _make_pipe(self._sec)
            if handle == INVALID_HANDLE_VALUE:
                time.sleep(0.25)
                continue
            if not self._connect(handle):
                continue
            self._connected = True
            print("PIPE: client connected", flush=True)
            try:
                self._pump(handle)
            finally:
                kernel32.CloseHandle(handle)
                self._connected = False
                print("PIPE: client disconnected", flush=True)

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
        kernel32.WriteFile.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                       wt.DWORD, ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
        kernel32.WriteFile.restype = wt.BOOL
        user32 = ctypes.WinDLL("user32", use_last_error=True)
        # 活动范围滑动窗口：最近 N 帧（约 1.5 秒）的鼠标坐标 min/max，
        # 归一化后无论光标被限制在屏幕哪个子区域，点都能走满鼠标垫并触边
        n_history = max(1, int(round(1.5 / self._interval)))
        history = deque()
        while not self._stop.is_set():
            reconcile(self._state)
            self._state.vx = user32.GetSystemMetrics(SM_XVIRTUALSCREEN)
            self._state.vy = user32.GetSystemMetrics(SM_YVIRTUALSCREEN)
            self._state.vw = user32.GetSystemMetrics(SM_CXVIRTUALSCREEN)
            self._state.vh = user32.GetSystemMetrics(SM_CYVIRTUALSCREEN)
            # 桌面（光标可见）坐标的 60Hz 校准；隐藏时坐标由 RAWINPUT 增量维护
            sync_mouse_position(self._state)
            self._update_normalized(history, n_history)
            blob = self._state.serialize()
            self._state.seq += 1
            written = wt.DWORD()
            buf = ctypes.create_string_buffer(blob)
            if not kernel32.WriteFile(handle, buf, SNAPSHOT_SIZE,
                                      ctypes.byref(written), None):
                return  # 客户端断开，返回等待重新连接
            time.sleep(self._interval)

    def _update_normalized(self, history, n_history):
        """把当前 (mx,my) 推进滑动窗口并归一化到 ux/uy（0..1000）。

        静止（位置与上帧相同）时窗口冻结：不推进也不过期旧样本，
        光标冻结时光标点完全静止，杜绝"窗口收敛导致的自行行走"。
        floor 取屏幕尺寸的 10%：活动范围小于地板时以当前位置为中心锚定，
        防止小范围移动时微抖动被放大成满垫跑动。
        """
        self._push_sample(history, n_history, self._state.mx, self._state.my)
        if not history:
            return
        min_x = min(p[0] for p in history)
        max_x = max(p[0] for p in history)
        min_y = min(p[1] for p in history)
        max_y = max(p[1] for p in history)
        floor_x = max(96.0, self._state.vw * 0.10)
        floor_y = max(96.0, self._state.vh * 0.10)
        self._state.ux, self._state.uy = normalize_position(
            self._state.mx, self._state.my, min_x, max_x, min_y, max_y,
            floor_x, floor_y)

    @staticmethod
    def _push_sample(history, n_history, mx, my):
        """样本推进滑动窗口；与末尾样本相同（静止）时不推进，避免窗口随时间收敛漂移。"""
        cur = (mx, my)
        if history and history[-1] == cur:
            return False
        history.append(cur)
        while len(history) > n_history:
            history.popleft()
        return True


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