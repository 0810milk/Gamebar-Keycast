"""低层全局键盘/鼠标钩子（ctypes 直调 Win32 API，无第三方依赖）。

- 键盘：SetWindowsHookEx(WH_KEYBOARD_LL)  采集 Q/W/E/R/A/S/D/F + 修饰键 + 空格
- 鼠标：SetWindowsHookEx(WH_MOUSE_LL)    采集左右/中/侧键 与 屏幕坐标
- 键位状态同时以 GetAsyncKeyState 兜底校准，避免事件丢失导致的漂移
"""
import ctypes
import ctypes.wintypes as wt

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
    if n_code >= 0:
        ms = ctypes.cast(l_param, ctypes.POINTER(MSLLHOOKSTRUCT)).contents
        if w_param == WM_MOUSEMOVE:
            _state.mx, _state.my = ms.pt.x, ms.pt.y
        else:
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

    msg = MSG()
    while not stop_event.is_set():
        ret = user32.GetMessageW(ctypes.byref(msg), None, 0, 0)
        if ret == 0:
            break
        if ret == -1:
            break
        user32.TranslateMessage(ctypes.byref(msg))
        user32.DispatchMessageW(ctypes.byref(msg))

    if kb_hook:
        user32.UnhookWindowsHookEx(kb_hook)
    if ms_hook:
        user32.UnhookWindowsHookEx(ms_hook)