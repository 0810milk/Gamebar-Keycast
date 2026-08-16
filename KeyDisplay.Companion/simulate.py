"""开发辅助：用 SendInput 模拟按住某个键指定秒数，用于验证钩子采集。

用法：python simulate.py A 1.5
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time

INPUT_KEYBOARD = 1
KEYEVENTF_KEYUP = 0x0002

VK = {"Q": 0x51, "W": 0x57, "E": 0x45, "R": 0x52,
      "A": 0x41, "S": 0x53, "D": 0x44, "F": 0x46,
      "Shift": 0xA0, "Ctrl": 0xA2, "Alt": 0xA4, "Space": 0x20}


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ctypes.c_ulonglong)]


class INPUTUNION(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", INPUTUNION)]


def press_key(name, seconds=1.0):
    user32 = ctypes.WinDLL("user32")
    user32.SendInput.argtypes = [wt.UINT, ctypes.POINTER(INPUT), ctypes.c_int]
    user32.SendInput.restype = wt.UINT

    inp = INPUT()
    inp.type = INPUT_KEYBOARD
    inp.u.ki.wVk = VK[name]
    user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))
    time.sleep(seconds)
    inp.u.ki.dwFlags = KEYEVENTF_KEYUP
    user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))


if __name__ == "__main__":
    name = sys.argv[1] if len(sys.argv) > 1 else "A"
    secs = float(sys.argv[2]) if len(sys.argv) > 2 else 1.0
    press_key(name, secs)
    print("模拟按键 %s 完成" % name)