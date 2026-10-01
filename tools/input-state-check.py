"""输入接收器状态查看器（原生接收器 P1 的验证工具，零依赖）。

用途：直接连到 KeyDisplayInput.exe 的管道，实时显示它收到了什么 —— 你按一下键，
这里就该亮一格；鼠标移动坐标会变；手柄按键会亮。用来亲眼确认"底层到底收没收到输入"。

用法：
    python tools\\input-state-check.py                    # 默认连测试管道
    python tools\\input-state-check.py --pipe=KeyDisplayState
    python tools\\input-state-check.py --hz=5             # 刷新慢一点，便于复制输出
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import struct
import sys
import time

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.CreateFileW.restype = wt.HANDLE
k32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p,
                            wt.DWORD, wt.DWORD, wt.HANDLE]
GENERIC_READ, GENERIC_WRITE, OPEN_EXISTING = 0x80000000, 0x40000000, 3
PIPE_READMODE_MESSAGE = 0x02

BUILTIN = [("Q", 0x51), ("W", 0x57), ("E", 0x45), ("R", 0x52),
           ("A", 0x41), ("S", 0x53), ("D", 0x44), ("F", 0x46),
           ("Shift", 0x10), ("Ctrl", 0x11), ("Alt", 0x12), ("Space", 0x20)]
MOUSE = ["左", "右", "中", "侧1", "侧2"]
GP_BUTTONS = [(0x0001, "↑"), (0x0002, "↓"), (0x0004, "←"), (0x0008, "→"),
              (0x0010, "Start"), (0x0020, "Back"), (0x0040, "L3"), (0x0080, "R3"),
              (0x0100, "LB"), (0x0200, "RB"), (0x0400, "Guide"),
              (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y")]

VK_NAMES = {0x07: "滚轮上", 0x08: "滚轮下", 0x09: "Tab", 0x0D: "Enter", 0x10: "Shift",
            0x11: "Ctrl", 0x12: "Alt", 0x14: "CapsLock", 0x1B: "Esc", 0x20: "Space",
            0x25: "←", 0x26: "↑", 0x27: "→", 0x28: "↓", 0x2D: "Insert",
            0x2E: "Delete", 0x5B: "左Win", 0x5C: "右Win",
            0xA0: "左Shift", 0xA1: "右Shift", 0xA2: "左Ctrl", 0xA3: "右Ctrl",
            0xA4: "左Alt", 0xA5: "右Alt"}


def vk_name(vk):
    if vk in VK_NAMES:
        return VK_NAMES[vk]
    if 0x30 <= vk <= 0x39 or 0x41 <= vk <= 0x5A:
        return chr(vk)
    if 0x60 <= vk <= 0x69:
        return "小键盘%d" % (vk - 0x60)
    if 0x70 <= vk <= 0x87:
        return "F%d" % (vk - 0x6F + 1)
    return "VK%02X" % vk


def open_pipe(name):
    h = k32.CreateFileW(r"\\.\pipe\%s" % name, GENERIC_READ | GENERIC_WRITE, 0, None,
                        OPEN_EXISTING, 0, None)
    if not h or h == -1:
        return None
    mode = wt.DWORD(PIPE_READMODE_MESSAGE)
    k32.SetNamedPipeHandleState(h, ctypes.byref(mode), None, None)
    return h


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    ap = argparse.ArgumentParser()
    ap.add_argument("--pipe", default="KeyDisplayInputTest")
    ap.add_argument("--hz", type=float, default=10.0)
    ap.add_argument("--once", action="store_true", help="只打印一次后退出")
    args = ap.parse_args()

    h = open_pipe(args.pipe)
    if h is None:
        print("连不上管道 \\\\.\\pipe\\%s（err=%d）" % (args.pipe, ctypes.get_last_error()))
        print("请先启动接收器：KeyDisplay.Input\\KeyDisplayInput.exe --pipe=%s" % args.pipe)
        return 1

    buf = ctypes.create_string_buffer(512)
    n = wt.DWORD()

    def read():
        if not k32.ReadFile(h, buf, 512, ctypes.byref(n), None):
            return None
        return bytes(buf.raw[:n.value])

    def write(b):
        return bool(k32.WriteFile(h, b, len(b), ctypes.byref(n), None))

    # 问版本 + 切 92 字节帧（带手柄）
    write(b"CMD|VERSION")
    ver = ""
    write(b"CMD|PROTO|92")

    last_seq, last_at, fps, missed = None, time.time(), 0.0, 0
    interval = 1.0 / max(0.5, args.hz)
    next_out = 0.0
    print("连接成功：\\\\.\\pipe\\%s   （Ctrl+C 退出）" % args.pipe)
    try:
        while True:
            f = read()
            if f is None:
                print("\n管道断开（接收器退出了？）")
                return 1
            if f.startswith(b"RESP|"):
                txt = f.decode("utf-8", "replace").strip()
                if "KeyDisplayInput" in txt:
                    ver = txt.replace("RESP|OK|", "").strip()
                continue
            if len(f) < 76 or f[:4] != b"KDSP":
                continue
            keys = struct.unpack_from("<H", f, 5)[0]
            mouse = f[7]
            mx, my = struct.unpack_from("<ii", f, 8)
            vw, vh = struct.unpack_from("<ii", f, 24)
            seq = struct.unpack_from("<I", f, 32)[0]
            extra = f[36:68]
            pressed = [vk for vk in range(256) if (extra[vk >> 3] >> (vk & 7)) & 1]
            gp = struct.unpack_from("<BBHBBhhhhBB", f, 76) if len(f) >= 92 else None

            now = time.time()
            if last_seq is not None:
                gap = (seq - last_seq) & 0xFFFFFFFF
                if gap > 1:
                    missed += gap - 1
                fps = fps * 0.8 + (gap / max(1e-6, now - last_at)) * 0.2
            last_seq, last_at = seq, now

            if args.once or now >= next_out:
                next_out = now + interval
                kb = " ".join("[%s]" % nm if (keys >> i) & 1 else " %s " % nm
                              for i, (nm, _) in enumerate(BUILTIN))
                ms = " ".join("%s%s" % ("●" if (mouse >> i) & 1 else "○", nm)
                              for i, nm in enumerate(MOUSE))
                line = ["=" * 78,
                        (ver or "（未取到版本）"),
                        "键盘 12 键: %s" % kb,
                        "按下的键  : %s" % (", ".join(vk_name(v) for v in pressed) or "（无）"),
                        "鼠标      : %s   坐标=(%d,%d) 虚拟屏=%dx%d" % (ms, mx, my, vw, vh),
                        "帧率      : %.1f Hz   seq=%d   累计丢帧(按 seq 计)=%d" % (fps, seq, missed)]
                if gp:
                    conn, act, btn, lt, rt, lx, ly, rx, ry, bat, sub = gp
                    downs = " ".join(nm for bit, nm in GP_BUTTONS if btn & bit)
                    line.append("手柄      : 连接掩码=0x%X 活跃槽=%s 按键=[%s]"
                                % (conn, "-" if act == 0xFF else act, downs or "无"))
                    line.append("            扳机 LT=%d RT=%d  左摇杆=(%d,%d) 右摇杆=(%d,%d) 电量=%s"
                                % (lt, rt, lx, ly, rx, ry, "-" if bat == 0xFF else bat))
                else:
                    line.append("手柄      : （未协商 92 字节帧）")
                out = "\n".join(line)
                if args.once:
                    print(out)
                    return 0
                # 用“光标归位 + 清行”重绘，避免闪屏
                sys.stdout.write("\r" + out.replace("\n", "\n").ljust(1) + "\n")
                sys.stdout.flush()
                # 上移回去，形成原地刷新
                sys.stdout.write("\033[%dA" % (len(line)))
                sys.stdout.flush()
    except KeyboardInterrupt:
        print("\n已退出")
        return 0


if __name__ == "__main__":
    sys.exit(main())
