"""按键显示 - tkinter 开发预览（无需 Game Bar / 伴生进程）。

直接通过 ctypes 轮询键盘/鼠标状态，用 Canvas 渲染与真实小组件一致的布局，
用于在开发阶段验证键位布局、尺寸与按下反色效果。

用法:
    python tools\\preview.py
"""

import ctypes
import ctypes.wintypes as wt
import tkinter as tk

# 键位布局（名称 -> 宽/标签），顺序与伴生进程快照位序一致
KEY_ROWS = [
    [("Q", 52), ("W", 52), ("E", 52), ("R", 52)],
    [("A", 52), ("S", 52), ("D", 52), ("F", 52)],
    [("Shift", 68), ("Ctrl", 68), ("Alt", 68), ("Space", 176)],
]
MOUSE_BUTTONS = [("左", 1), ("中", 4), ("右", 2), ("侧1", 5), ("侧2", 6)]

KEY_H = 48
KEY_GAP = 6
ROW_GAP = 8
PAD = 16
PAD_W, PAD_H = 80, 80
BTN_W, BTN_H = 36, 36

KEYS = {
    "Q": 0x51, "W": 0x57, "E": 0x45, "R": 0x52,
    "A": 0x41, "S": 0x53, "D": 0x44, "F": 0x46,
    "Shift": 0x10, "Ctrl": 0x11, "Alt": 0x12, "Space": 0x20,
}

BG_PANEL = "#1c1c1c"
BG_KEY = "#000000"
FG_KEY = "#ffffff"
BG_KEY_ON = "#ffffff"
FG_KEY_ON = "#000000"
BORDER = "#666666"
BG_PAD = "#2a2a2a"


def key_down(vk):
    return (ctypes.windll.user32.GetAsyncKeyState(vk) & 0x8000) != 0


class Preview(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("按键显示 - 开发预览")
        self.configure(bg=BG_PANEL)
        self.resizable(False, False)

        kb_width = sum(w for _, w in KEY_ROWS[2]) + KEY_GAP * 3
        mouse_width = 3 * BTN_W + 2 * KEY_GAP
        total_w = PAD * 2 + kb_width + 16 + mouse_width + PAD
        total_h = PAD * 2 + 3 * KEY_H + 2 * ROW_GAP + PAD

        self.canvas = tk.Canvas(self, width=total_w, height=total_h, bg=BG_PANEL,
                                highlightthickness=0)
        self.canvas.pack()
        self._total_h = total_h

        self._key_items = {}
        self._mouse_items = {}
        self._pad_origin = None
        self._dot = None
        self._build()

        self.after(33, self._tick)

    def _round_rect(self, x1, y1, x2, y2, r, **kw):
        return self.canvas.create_rectangle(x1, y1, x2, y2, **kw)

    def _build(self):
        y = PAD
        for row in KEY_ROWS:
            x = PAD
            for name, w in row:
                items = self._draw_key(x, y, w, KEY_H, name)
                self._key_items[name] = items
                x += w + KEY_GAP
            y += KEY_H + ROW_GAP

        x0 = PAD * 2 + sum(w for _, w in KEY_ROWS[2]) + KEY_GAP * 3 + 16
        y0 = PAD
        # 鼠标垫
        self.canvas.create_rectangle(x0, y0, x0 + PAD_W, y0 + PAD_H,
                                     fill=BG_PAD, outline=BORDER)
        self._pad_origin = (x0, y0)
        self._dot = self.canvas.create_oval(x0, y0, x0 + 10, y0 + 10,
                                            fill=FG_KEY, outline="")

        y = y0 + PAD_H + 8
        x = x0
        for label, vk in MOUSE_BUTTONS[:3]:
            self._mouse_items[vk] = self._draw_key(x, y, BTN_W, BTN_H, label, 10)
            x += BTN_W + KEY_GAP
        y += BTN_H + ROW_GAP
        x = x0
        for label, vk in MOUSE_BUTTONS[3:]:
            self._mouse_items[vk] = self._draw_key(x, y, BTN_W, BTN_H, label, 10)
            x += BTN_W + KEY_GAP

        hint = "按 Win+G 打开真实小组件；本窗口用于开发预览。右键拖到一边即可。"
        self.canvas.create_text(PAD, self._total_h - 12, anchor="sw", text=hint,
                                fill="#888888", font=("Segoe UI", 9))

    def _draw_key(self, x, y, w, h, label, font=13):
        bg = self.canvas.create_rectangle(x, y, x + w, y + h, fill=BG_KEY, outline=BORDER)
        text = self.canvas.create_text(x + w / 2, y + h / 2, text=label,
                                       fill=FG_KEY, font=("Segoe UI", font, "bold"))
        return bg, text

    def _tick(self):
        for name, vk in KEYS.items():
            on = key_down(vk)
            bg, text = self._key_items[name]
            self.canvas.itemconfig(bg, fill=BG_KEY_ON if on else BG_KEY)
            self.canvas.itemconfig(text, fill=FG_KEY_ON if on else FG_KEY)
        for vk, (bg, text) in self._mouse_items.items():
            on = key_down(vk)
            self.canvas.itemconfig(bg, fill=BG_KEY_ON if on else BG_KEY)
            self.canvas.itemconfig(text, fill=FG_KEY_ON if on else FG_KEY)

        x0, y0 = self._pad_origin
        pt = wt.POINT()
        ctypes.windll.user32.GetCursorPos(ctypes.byref(pt))
        vx = ctypes.windll.user32.GetSystemMetrics(76)
        vy = ctypes.windll.user32.GetSystemMetrics(77)
        vw = ctypes.windll.user32.GetSystemMetrics(78)
        vh = ctypes.windll.user32.GetSystemMetrics(79)
        if vw <= 0:
            vw, vh, vx, vy = 1920, 1080, 0, 0
        px = min(max((pt.x - vx) / vw * PAD_W, 0), PAD_W - 10)
        py = min(max((pt.y - vy) / vh * PAD_H, 0), PAD_H - 10)
        self.canvas.coords(self._dot, x0 + px, y0 + py, x0 + px + 10, y0 + py + 10)

        self.after(33, self._tick)


if __name__ == "__main__":
    Preview().mainloop()