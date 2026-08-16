"""低层全局键盘/鼠标钩子（ctypes 直调 Win32 API，无第三方依赖）。

- 键盘：SetWindowsHookEx(WH_KEYBOARD_LL)  采集 Q/W/E/R/A/S/D/F + 修饰键 + 空格
- 鼠标：SetWindowsHookEx(WH_MOUSE_LL)    采集左右/中/侧键 与 屏幕坐标
- 键位状态同时以 GetAsyncKeyState 兜底校准，避免事件丢失导致的漂移
- 独占全屏游戏会用原始输入（RAWINPUT）接管鼠标并隐藏光标，此时
  WH_MOUSE_LL 不再产生 WM_MOUSEMOVE；通过 RegisterRawInputDevices 收
  增量，光标隐藏时累计、可见时以 GetCursorPos 校准绝对坐标
"""
import ctypes
import ctypes.wintypes as wt
import time

import debuglog
from state import KEY_ORDER

# --- Win32 常量 -----------------------------------------------------------
WH_KEYBOARD_LL = 13
WH_MOUSE_LL = 14

WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_SYSKEYDOWN = 0x0104
WM_SYSKEYUP = 0x0105

WM_MOUSEMOVE = 0x0200
WM_LBUTTONDOWN = 0x0201
WM_LBUTTONUP = 0x0202
WM_RBUTTONDOWN = 0x0204
WM_RBUTTONUP = 0x0205
WM_MBUTTONDOWN = 0x0207
WM_MBUTTONUP = 0x0208
WM_XBUTTONDOWN = 0x020B
WM_XBUTTONUP = 0x020C

LLKHF_UP = 0x80

# 原始输入（RAWINPUT）
WM_INPUT = 0x00FF
RID_INPUT = 0x10000003
RIM_TYPEMOUSE = 0
MOUSE_MOVE_ABSOLUTE = 0x1
RIDEV_INPUTSINK = 0x00000100
HWND_MESSAGE = ctypes.c_void_p(-3).value
CURSOR_SHOWING = 0x00000001

# 检测回报率上限（Hz）：高回报率鼠标（1000Hz/8000Hz）每个事件都做两次
# GetRawInputData 调用，CPU 开销大易卡顿；限频到该值以下的事件直接跳过。
# 桌面坐标走 GetCursorPos 绝对校准（限频无损），游戏内经活动范围归一化，
# 限频不影响触边/比例。先按需求取 500，可调。
RAW_REPORT_LIMIT = 500.0
_last_raw_ts = 0.0

# --- 原生输入/坐标链路监控计数（供 pipe-debug.log 周期摘要读取）---
_raw_count = 0        # 已处理的 WM_INPUT 事件数
_raw_skip = 0         # 被限频跳过的事件数
_coord_src = "none"   # 最近一次坐标来源：sync(GetCursorPos)/acc(增量累计)/abs(绝对原始坐标)
_cursor_hidden = None  # 最近一次可见性判定（None=未知）
_hidden_delta_logged = 0  # 进入隐藏状态后已记录的增量条数（防刷屏）


def raw_stats():
    """返回 (已处理数, 跳过数, 坐标来源)，供泵周期日志清零。"""
    return _raw_count, _raw_skip, _coord_src

# 按键虚拟键码（左右修饰键统一映射到同一标签）
VK = {"Q": 0x51, "W": 0x57, "E": 0x45, "R": 0x52,
      "A": 0x41, "S": 0x53, "D": 0x44, "F": 0x46,
      "Shift": 0xA0, "Ctrl": 0xA2, "Alt": 0xA4, "Space": 0x20}
# 右修饰键的额外 VK
VK_RIGHT = {"Shift": 0xA1, "Ctrl": 0xA3, "Alt": 0xA5}
# 鼠标键 VK
MOUSE_VK = {"L": 0x01, "R": 0x02, "M": 0x04, "X1": 0x05, "X2": 0x06}

MOUSE_MSGS = {
    "L": (WM_LBUTTONDOWN, WM_LBUTTONUP),
    "R": (WM_RBUTTONDOWN, WM_RBUTTONUP),
    "M": (WM_MBUTTONDOWN, WM_MBUTTONUP),
}

# --- DLL 与结构体 ----------------------------------------------------------
user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

HOOKPROC = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_int,
                              wt.WPARAM, wt.LPARAM)


class KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [("vkCode", wt.DWORD), ("scanCode", wt.DWORD),
                ("flags", wt.DWORD), ("time", wt.DWORD),
                ("dwExtraInfo", ctypes.c_void_p)]


class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class MSLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [("pt", POINT), ("mouseData", wt.DWORD),
                ("flags", wt.DWORD), ("time", wt.DWORD),
                ("dwExtraInfo", ctypes.c_void_p)]


class MSG(ctypes.Structure):
    _fields_ = [("hwnd", wt.HWND), ("message", wt.UINT),
                ("wParam", wt.WPARAM), ("lParam", wt.LPARAM),
                ("time", wt.DWORD), ("pt", POINT)]


class RAWINPUTDEVICE(ctypes.Structure):
    _fields_ = [("usUsagePage", wt.USHORT), ("usUsage", wt.USHORT),
                ("dwFlags", wt.DWORD), ("hwndTarget", wt.HWND)]


class RAWINPUTHEADER(ctypes.Structure):
    _fields_ = [("dwType", wt.DWORD), ("dwSize", wt.DWORD),
                ("hDevice", ctypes.c_void_p), ("wParam", wt.WPARAM)]


class RAWMOUSE(ctypes.Structure):
    _fields_ = [("usFlags", wt.USHORT), ("ulButtons", wt.ULONG),
                ("ulRawButtons", wt.ULONG), ("lLastX", ctypes.c_long),
                ("lLastY", ctypes.c_long), ("ulExtraInformation", wt.ULONG)]


class RAWINPUT(ctypes.Structure):
    _fields_ = [("header", RAWINPUTHEADER), ("mouse", RAWMOUSE)]


class CURSORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("flags", wt.DWORD),
                ("hCursor", ctypes.c_void_p), ("ptScreenPos", POINT)]


user32.GetCursorInfo.argtypes = [ctypes.POINTER(CURSORINFO)]
user32.GetCursorInfo.restype = wt.BOOL
user32.GetCursorPos.argtypes = [ctypes.POINTER(POINT)]
user32.GetCursorPos.restype = wt.BOOL


# 可见性去抖：GetCursorInfo 在游戏/菜单切换时可能高频闪烁，需持续
# VIS_DEBOUNCE 秒才切换模式，避免累计坐标被 GetCursorPos 反复打回冻结位置
VIS_DEBOUNCE = 0.15
_vis_state = True       # 去抖后的可见性
_vis_change_at = 0.0    # 首次检测到状态变化的时刻（0=无待定切换）


def _cursor_visible():
    """光标是否可见（150ms 去抖）；GetCursorInfo 失败时按可见处理。

    记录可见性切换（进入/退出游戏隐藏状态），便于监控日志判断坐标链路。
    """
    global _cursor_hidden, _hidden_delta_logged, _vis_state, _vis_change_at
    ci = CURSORINFO()
    ci.cbSize = ctypes.sizeof(CURSORINFO)
    visible = True
    if user32.GetCursorInfo(ctypes.byref(ci)):
        visible = bool(ci.flags & CURSOR_SHOWING)

    now = time.monotonic()
    if visible != _vis_state:
        if _vis_change_at == 0.0:
            _vis_change_at = now
        elif now - _vis_change_at >= VIS_DEBOUNCE:
            _vis_state = visible
            _vis_change_at = 0.0
            _cursor_hidden = not visible
            debuglog.log("[raw] cursor %s" % ("visible" if visible else "hidden"))
            if not visible:
                _hidden_delta_logged = 0  # 每次进入隐藏状态重置增量日志计数
    else:
        _vis_change_at = 0.0
    return _vis_state


def _read_cursor_position():
    """读取光标当前屏幕坐标；失败返回 None。"""
    pt = POINT()
    if user32.GetCursorPos(ctypes.byref(pt)):
        return pt.x, pt.y
    return None


def sync_mouse_position(state):
    """桌面坐标校准（供 60Hz 推送循环调用）。

    光标可见时以 GetCursorPos 为准；隐藏（游戏）时不写入，坐标由
    RAWINPUT 增量累计维护（见 _handle_raw_input）。任何时刻只有一个
    坐标来源生效，避免两个来源互相覆写造成坐标在两位置间振荡。
    """
    global _coord_src
    if not _cursor_visible():
        return
    pos = _read_cursor_position()
    if pos is not None:
        _coord_src = "sync"
        state.mx, state.my = pos


WNDPROC = ctypes.WINFUNCTYPE(ctypes.c_long, wt.HWND, wt.UINT,
                             wt.WPARAM, wt.LPARAM)


class WNDCLASSEXW(ctypes.Structure):
    _fields_ = [("cbSize", wt.UINT), ("style", wt.UINT),
                ("lpfnWndProc", WNDPROC), ("cbClsExtra", ctypes.c_int),
                ("cbWndExtra", ctypes.c_int), ("hInstance", wt.HINSTANCE),
                ("hIcon", ctypes.c_void_p), ("hCursor", ctypes.c_void_p),
                ("hbrBackground", ctypes.c_void_p), ("lpszMenuName", wt.LPCWSTR),
                ("lpszClassName", wt.LPCWSTR), ("hIconSm", ctypes.c_void_p)]


# --- 钩子回调（需保持模块级引用，防止被 GC）-------------------------------
user32.CallNextHookEx.argtypes = [wt.HHOOK, ctypes.c_int,
                                  wt.WPARAM, wt.LPARAM]
user32.CallNextHookEx.restype = ctypes.c_long


def _keyboard_proc(n_code, w_param, l_param):
    if n_code >= 0:
        kb = ctypes.cast(l_param, ctypes.POINTER(KBDLLHOOKSTRUCT)).contents
        down = w_param in (WM_KEYDOWN, WM_SYSKEYDOWN)
        for name, vk in VK.items():
            if kb.vkCode == vk or kb.vkCode == VK_RIGHT.get(name, -1):
                _state.set_key(name, down)
                break
    return user32.CallNextHookEx(None, n_code, w_param, l_param)


def _mouse_proc(n_code, w_param, l_param):
    # 坐标不再由钩子维护：桌面（光标可见）由 60Hz GetCursorPos 轮询校准，
    # 游戏（光标隐藏）由 RAWINPUT 增量累计。钩子只负责鼠标按键采集。
    if n_code >= 0:
        ms = ctypes.cast(l_param, ctypes.POINTER(MSLLHOOKSTRUCT)).contents
        for name, (down_msg, up_msg) in MOUSE_MSGS.items():
            if w_param == down_msg:
                _state.set_mouse(name, True)
                break
            if w_param == up_msg:
                _state.set_mouse(name, False)
                break
        if w_param == WM_XBUTTONDOWN or w_param == WM_XBUTTONUP:
            xbtn = (ms.mouseData >> 16) & 0xFFFF
            name = "X1" if xbtn == 1 else ("X2" if xbtn == 2 else None)
            if name:
                _state.set_mouse(name, w_param == WM_XBUTTONDOWN)
    return user32.CallNextHookEx(None, n_code, w_param, l_param)


def _handle_raw_input(l_param):
    """解析 WM_INPUT 鼠标原始数据，维护"光标隐藏"时的坐标。

    桌面（光标可见）时坐标由 60Hz GetCursorPos 轮询校准（sync_mouse_position），
    原始输入只负责光标隐藏（独占全屏游戏）时的增量累计；两个来源不会同时写入。
    处理频率限制在 RAW_REPORT_LIMIT（500Hz），跳过超限事件以降低 CPU 开销。
    """
    global _last_raw_ts, _raw_count, _raw_skip, _coord_src, _hidden_delta_logged
    now = time.monotonic()
    if now - _last_raw_ts < 1.0 / RAW_REPORT_LIMIT:
        _raw_skip += 1
        return
    _last_raw_ts = now
    _raw_count += 1

    size = wt.UINT()
    user32.GetRawInputData(l_param, RID_INPUT, None, ctypes.byref(size),
                           ctypes.sizeof(RAWINPUTHEADER))
    if not size.value:
        return
    buf = ctypes.create_string_buffer(size.value)
    got = user32.GetRawInputData(l_param, RID_INPUT, buf, ctypes.byref(size),
                                 ctypes.sizeof(RAWINPUTHEADER))
    if got != size.value:
        return
    raw = ctypes.cast(buf, ctypes.POINTER(RAWINPUT)).contents
    if raw.header.dwType != RIM_TYPEMOUSE:
        return

    if _cursor_visible():
        # 光标可见：坐标由 60Hz GetCursorPos 轮询维护，这里不再写入
        return

    if raw.mouse.usFlags & MOUSE_MOVE_ABSOLUTE:
        # 极少数游戏用绝对坐标（0..65535，跨虚拟屏幕）
        _coord_src = "abs"
        vw = _state.vw or 1920
        vh = _state.vh or 1080
        _state.mx = _state.vx + int(raw.mouse.lLastX * vw / 65535)
        _state.my = _state.vy + int(raw.mouse.lLastY * vh / 65535)
    else:
        _coord_src = "acc"
        _state.mx += raw.mouse.lLastX
        _state.my += raw.mouse.lLastY
        # 隐藏态增量日志：每次进入隐藏只记前 20 条，防刷屏
        if _hidden_delta_logged < 20:
            _hidden_delta_logged += 1
            debuglog.log("[raw] delta %+d,%+d mx=%d my=%d"
                         % (raw.mouse.lLastX, raw.mouse.lLastY,
                            _state.mx, _state.my))


def _raw_wnd_proc(hwnd, msg, w_param, l_param):
    if msg == WM_INPUT:
        _handle_raw_input(l_param)
        return 0
    return user32.DefWindowProcW(hwnd, msg, w_param, l_param)


_raw_wnd_cb = WNDPROC(_raw_wnd_proc)


_keyboard_cb = HOOKPROC(_keyboard_proc)
_mouse_cb = HOOKPROC(_mouse_proc)


def _get_async_key_state(vk):
    """GetAsyncKeyState 高位表示当前按下。"""
    return bool(user32.GetAsyncKeyState(vk) & 0x8000)


def reconcile(state):
    """兜底校准：以 GetAsyncKeyState 为准刷新所有按键状态。"""
    for name in KEY_ORDER:
        state.set_key(name, _get_async_key_state(VK[name]))
    for name, vk in MOUSE_VK.items():
        state.set_mouse(name, _get_async_key_state(vk))


def start_hooks(state, stop_event):
    """在专用线程中安装钩子并运行消息泵。阻塞直到 stop_event 置位。"""
    global _state
    _state = state

    kernel32.GetModuleHandleW.restype = ctypes.c_void_p
    kernel32.GetModuleHandleW.argtypes = [wt.LPCWSTR]

    user32.SetWindowsHookExW.argtypes = [ctypes.c_int, HOOKPROC,
                                         wt.HINSTANCE, wt.DWORD]
    user32.SetWindowsHookExW.restype = ctypes.c_void_p

    # 低层钩子（WH_KEYBOARD_LL / WH_MOUSE_LL）无需模块句柄，传 NULL 即可
    kb_hook = user32.SetWindowsHookExW(WH_KEYBOARD_LL, _keyboard_cb, None, 0)
    ms_hook = user32.SetWindowsHookExW(WH_MOUSE_LL, _mouse_cb, None, 0)
    if not kb_hook or not ms_hook:
        raise OSError("无法安装全局输入钩子，错误码 %d"
                      % ctypes.get_last_error())

    raw_hwnd = _setup_raw_input()

    msg = MSG()
    while not stop_event.is_set():
        ret = user32.GetMessageW(ctypes.byref(msg), None, 0, 0)
        if ret == 0:
            break
        if ret == -1:
            break
        user32.TranslateMessage(ctypes.byref(msg))
        user32.DispatchMessageW(ctypes.byref(msg))

    if raw_hwnd:
        user32.DestroyWindow(raw_hwnd)
        user32.UnregisterClassW("KeyDisplayRawInput", None)

    if kb_hook:
        user32.UnhookWindowsHookEx(kb_hook)
    if ms_hook:
        user32.UnhookWindowsHookEx(ms_hook)


def _setup_raw_input():
    """创建消息窗口并注册鼠标原始输入；失败时静默返回 None（退化到钩子）。"""
    user32.RegisterClassExW.argtypes = [ctypes.POINTER(WNDCLASSEXW)]
    user32.RegisterClassExW.restype = wt.USHORT
    user32.CreateWindowExW.argtypes = [
        wt.DWORD, wt.LPCWSTR, wt.LPCWSTR, wt.DWORD,
        ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
        wt.HWND, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
    user32.CreateWindowExW.restype = wt.HWND
    user32.RegisterRawInputDevices.argtypes = [
        ctypes.POINTER(RAWINPUTDEVICE), wt.UINT, wt.UINT]
    user32.RegisterRawInputDevices.restype = wt.BOOL
    user32.GetRawInputData.argtypes = [
        ctypes.c_void_p, wt.UINT, ctypes.c_void_p,
        ctypes.POINTER(wt.UINT), wt.UINT]
    user32.GetRawInputData.restype = wt.UINT
    user32.GetCursorInfo.argtypes = [ctypes.POINTER(CURSORINFO)]
    user32.GetCursorInfo.restype = wt.BOOL
    user32.DefWindowProcW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
    user32.DefWindowProcW.restype = ctypes.c_long
    user32.DestroyWindow.argtypes = [wt.HWND]
    user32.DestroyWindow.restype = wt.BOOL
    user32.UnregisterClassW.argtypes = [wt.LPCWSTR, wt.HINSTANCE]
    user32.UnregisterClassW.restype = wt.BOOL

    try:
        cls = WNDCLASSEXW()
        cls.cbSize = ctypes.sizeof(WNDCLASSEXW)
        cls.lpfnWndProc = _raw_wnd_cb
        cls.hInstance = kernel32.GetModuleHandleW(None)
        cls.lpszClassName = "KeyDisplayRawInput"
        if not user32.RegisterClassExW(ctypes.byref(cls)):
            return None
        hwnd = user32.CreateWindowExW(
            0, "KeyDisplayRawInput", "", 0,
            0, 0, 0, 0, HWND_MESSAGE, None, None, None)
        if not hwnd:
            return None
        dev = RAWINPUTDEVICE(0x01, 0x02, RIDEV_INPUTSINK, hwnd)
        if not user32.RegisterRawInputDevices(
                ctypes.byref(dev), 1, ctypes.sizeof(RAWINPUTDEVICE)):
            user32.DestroyWindow(hwnd)
            return None
        return hwnd
    except Exception:
        return None