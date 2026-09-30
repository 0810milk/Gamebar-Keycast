"""手柄显示预览窗口（Windows 桌面窗口，tkinter，零第三方依赖）。

用途：在没有 Game Bar 的环境下先看"手柄按键显示"长什么样、和现有键盘键是否一致。
窗口里同时画出**现有键盘键**和**新手柄组**，配色与圆角样式直接取自小组件
`Widget1.xaml.cs` 里五个主题的 10 个色槽（面板/边框/按键底/文字/按下底/按下字/
鼠标垫/鼠标点/强调色/鼠标点按下），所以看到的就是接进小组件后的观感。

三种数据来源：
    演示   不用手柄也能看：内置动画循环按键、摇杆绕圈（顺便验证斜角不越出圆环）、扳机渐变
    实时   直接轮询 XInput（4 槽位），与将来伴生进程用的是同一套 gamepad.py
    空闲   全部松开（用于看静止态）

用法：
    python tools\\pad_preview.py                  # 演示模式启动
    python tools\\pad_preview.py --mode live      # 实时读手柄
    python tools\\pad_preview.py --theme gray --scale 50 --parts all
"""
import argparse
import math
import os
import sys
import time
import tkinter as tk

# 复用伴生进程的手柄采集模块（XInput 轮询 + 死区/圆度处理都在那里）
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                "..", "KeyDisplay.Companion"))
try:
    import gamepad as gp
except Exception:                                    # pragma: no cover
    gp = None

# ---- 主题色（与 Widget1.xaml.cs 的 10 个色槽一一对应） ----------------------
# 每项：panel, border, keyBg, keyFg, pressedBg, pressedFg, pad, dot, accent, dotPressed
THEMES = {
    "dark": ["E8121212", "52FFFFFF", "F21A1A1A", "FFFFFFFF", "FFFFFFFF", "FF101010",
             "4D000000", "FFFFFFFF", "FF4CC2FF", "FF4CC2FF"],
    "gray": ["E0CFCFCF", "5C5A5A5A", "FFEAEAEA", "FF1A1A1A", "FF4A4A4A", "FFFFFFFF",
             "47000000", "FF1A1A1A", "FF0067C0", "FF0067C0"],
    "light": ["E0F5F5F5", "59333333", "F2FFFFFF", "FF000000", "FF000000", "FFFFFFFF",
              "42000000", "FF000000", "FF0067C0", "FF0067C0"],
    "pink": ["E0FFB3C6", "CCB0577E", "FFFFB3C6", "FFFFFFFF", "FFFFFFFF", "FFB0577E",
             "4DFFB3C6", "FFB0577E", "FFC2185B", "FFC2185B"],
    "blue": ["E0C3DCF0", "663A6EA5", "FFD2E5F7", "FF1F4E79", "FFFFFFFF", "FF1F4E79",
             "59BFD9EE", "FF1F4E79", "FF0A64B4", "FF0A64B4"],
}
THEME_NAMES = [("dark", "深色"), ("gray", "灰色"), ("light", "浅色"),
               ("pink", "粉色"), ("blue", "蓝色")]

# 手柄按键的"品牌色"（可选开关；默认关，跟随主题色 —— 与现有键保持一致的观感）
BRAND = {"A": "107C10", "B": "D83B01", "X": "0078D7", "Y": "FFB900"}

# 面位（Xbox 的物理位置固定，只有印在上面的字随风格变）
FACE_POS = {"bottom": (0, 1), "right": (1, 0), "left": (-1, 0), "top": (0, -1)}
FACE_TEXT = {
    "xbox": {"bottom": "A", "right": "B", "left": "X", "top": "Y"},
    "ps": {"bottom": "\u2715", "right": "\u25CB", "left": "\u25A1", "top": "\u25B3"},
    "switch": {"bottom": "B", "right": "A", "left": "Y", "top": "X"},
}
SHOULDER_TEXT = {"xbox": ("LB", "RB"), "ps": ("L1", "R1"), "switch": ("L", "R")}
TRIGGER_TEXT = {"xbox": ("LT", "RT"), "ps": ("L2", "R2"), "switch": ("ZL", "ZR")}
STICK_TEXT = {"xbox": ("L3", "R3"), "ps": ("L3", "R3"), "switch": ("L3", "R3")}


def rgba(hex_argb):
    """'E8121212' → (r, g, b, a)；tkinter 没有 alpha，需要自己压到背景上。"""
    h = hex_argb
    a = int(h[0:2], 16) / 255.0
    return (int(h[2:4], 16), int(h[4:6], 16), int(h[6:8], 16), a)


def blend(fg_argb, bg_rgb):
    """把半透明色压到不透明背景上，返回 '#rrggbb'（模拟小组件的半透明观感）。"""
    r, g, b, a = rgba(fg_argb)
    br, bg, bb = bg_rgb
    return "#%02X%02X%02X" % (int(r * a + br * (1 - a)),
                              int(g * a + bg * (1 - a)),
                              int(b * a + bb * (1 - a)))


def opaque(fg_argb):
    return "#%02X%02X%02X" % rgba(fg_argb)[:3]


def mix(c1, c2, k):
    """两个 '#rrggbb' 按 k 混合（k=0 → c1，k=1 → c2）。"""
    a = [int(c1[i:i + 2], 16) for i in (1, 3, 5)]
    b = [int(c2[i:i + 2], 16) for i in (1, 3, 5)]
    return "#%02X%02X%02X" % tuple(int(a[i] + (b[i] - a[i]) * k) for i in range(3))


class PadPanel(tk.Canvas):
    """把"手柄组 + 键盘键"画在同一个面板上，配色/样式与小组件一致。"""

    # 手柄组设计尺寸（单位与小组件同一套设计单位，≈ 168×104）
    W, H = 168, 104
    # 键盘 12 键（小组件默认布局的紧凑摆法，仅用于对比观感）
    KB_ROWS = [("Q", 0), ("W", 1), ("E", 2), ("R", 3),
               ("A", 0), ("S", 1), ("D", 2), ("F", 3),
               ("Shift", 0), ("Ctrl", 1), ("Alt", 2), ("Space", 3)]

    def __init__(self, master, scale=1.0, **kw):
        self.panel_w, self.panel_h = int(560 * scale), int(300 * scale)
        tk.Canvas.__init__(self, master, width=self.panel_w + 40,
                           height=self.panel_h + 40, highlightthickness=0, **kw)
        self.scale = scale
        self.theme = "dark"
        self.style = "xbox"
        self.state = {}          # 当前手柄状态
        self.parts = {"ls": True, "rs": True, "dpad": True, "face": True,
                      "shoulder": True, "trigger": True, "menu": True,
                      "guide": True, "battery": True}
        self.trigger_style = "bar"      # bar / value / highlight
        self.deadzone = 24              # %
        self.brand_colors = False
        self.show_dz = True
        self.kb_down = set()
        self._items = {}
        self._build()

    # ---- 几何工具 ---------------------------------------------------------
    def _s(self, v):
        return v * self.scale

    def _ox(self):
        return 20

    def _oy(self):
        return 20

    def P(self, x, y):
        return (self._ox() + self._s(x), self._oy() + self._s(y))

    def _round_rect(self, x1, y1, x2, y2, r, **kw):
        pts = [x1 + r, y1, x2 - r, y1, x2, y1, x2, y1 + r, x2, y2 - r, x2, y2,
               x2 - r, y2, x1 + r, y2, x1, y2, x1, y2 - r, x1, y1 + r, x1, y1]
        return self.create_polygon(pts, smooth=True, **kw)

    # ---- 构建（所有图元只创建一次，之后用 itemconfig 更新） ----------------
    def _build(self):
        self.delete("all")
        # 背景：模拟"游戏画面"，用来看半透明面板的真实观感
        self._items["bg"] = []
        for i in range(0, self.panel_h + 40, 4):
            k = i / float(self.panel_h + 40)
            self._items["bg"].append(self.create_rectangle(
                0, i, self.panel_w + 40, i + 4, width=0, fill=mix("#101820", "#2A3A4A", k)))
        # 面板
        self._items["panel"] = self._round_rect(self._ox(), self._oy(),
                                                self._ox() + self._s(540),
                                                self._oy() + self._s(280), self._s(10))

        # ---- 键盘键区（仅作观感对比）----
        self._keys = {}
        for name, col in self.KB_ROWS:
            row = 0 if name in ("Q", "W", "E", "R") else (1 if name in ("A", "S", "D", "F") else 2)
            w = 52 if name != "Space" else 176
            x = 8 + col * 58 if name != "Space" else 8
            y = 8 + row * 56
            if name == "Shift":
                x, w = 8, 68
            elif name == "Ctrl":
                x = 84
            elif name == "Alt":
                x = 160
            self._keys[name] = self._make_key(x, y, w, 48, name)

        # ---- 手柄组（相对面板右下角摆放，模拟"独立一组键"）----
        gx, gy = 356, 156
        self.gx, self.gy = gx, gy
        self._gp = {}
        s = self.style
        # 肩键 / 扳机
        self._gp["LB"] = self._make_key(gx + 24, gy + 8, 26, 14, SHOULDER_TEXT[s][0], fs=8)
        self._gp["RB"] = self._make_key(gx + 118, gy + 8, 26, 14, SHOULDER_TEXT[s][1], fs=8)
        self._gp["LT"] = self._make_trigger(gx + 6, gy + 2, 12, 30, TRIGGER_TEXT[s][0])
        self._gp["RT"] = self._make_trigger(gx + 150, gy + 2, 12, 30, TRIGGER_TEXT[s][1])
        # ABXY 菱形（中心 (126,48)、偏移 13、半径 9：与肩键/右摇杆各留 3~4 单位间隙）
        self._gp["face"] = {}
        for face, (dx, dy) in FACE_POS.items():
            cx, cy = gx + 126 + dx * 13, gy + 48 + dy * 13
            self._gp["face"][face] = self._make_round(cx, cy, 9, FACE_TEXT[s][face])
        # 十字键
        self._gp["dpad"] = {}
        for name, (dx, dy) in (("up", (0, -1)), ("down", (0, 1)),
                               ("left", (-1, 0)), ("right", (1, 0))):
            self._gp["dpad"][name] = self._make_dpad_arm(gx + 34 + dx * 13,
                                                         gy + 84 + dy * 13)
        # 摇杆环 + 点（L3/R3 标签放在环外侧，避免压到十字键/面位）
        self._gp["ls"] = self._make_stick(gx + 34, gy + 44, 15)
        self._gp["rs"] = self._make_stick(gx + 126, gy + 88, 15)
        self._gp["ls_hint"] = self.create_text(*self.P(gx + 4, gy + 44),
                                               text=STICK_TEXT[s][0], font=("Segoe UI", 7))
        self._gp["rs_hint"] = self.create_text(*self.P(gx + 158, gy + 88),
                                               text=STICK_TEXT[s][1], font=("Segoe UI", 7))
        # View / Menu / Guide（宽度 18，给十字键右侧留出间隙）
        self._gp["View"] = self._make_key(gx + 56, gy + 78, 18, 13, "View", fs=8)
        self._gp["Menu"] = self._make_key(gx + 78, gy + 78, 18, 13, "Menu", fs=8)
        self._gp["Guide"] = self._make_round(gx + 84, gy + 56, 11, "")
        # 电量角标
        self._gp["battery"] = self.create_text(*self.P(gx + 84, gy + 14),
                                               text="", font=("Segoe UI", 8))
        self.apply_theme()

    def _make_key(self, x, y, w, h, label, fs=11):
        x1, y1 = self.P(x, y)
        x2, y2 = self.P(x + w, y + h)
        rr = self._round_rect(x1, y1, x2, y2, self._s(6))
        tx = self.create_text((x1 + x2) / 2.0, (y1 + y2) / 2.0, text=label,
                              font=("Microsoft YaHei UI", max(7, int(fs * self.scale))))
        return {"shape": rr, "text": tx, "box": (x1, y1, x2, y2)}

    def _make_round(self, cx, cy, r, label):
        x1, y1 = self.P(cx - r, cy - r)
        x2, y2 = self.P(cx + r, cy + r)
        circ = self.create_oval(x1, y1, x2, y2)
        tx = self.create_text((x1 + x2) / 2.0, (y1 + y2) / 2.0, text=label,
                              font=("Segoe UI", max(6, int(9 * self.scale)), "bold"))
        return {"shape": circ, "text": tx, "box": (x1, y1, x2, y2)}

    def _make_dpad_arm(self, cx, cy):
        x1, y1 = self.P(cx - 6, cy - 6)
        x2, y2 = self.P(cx + 6, cy + 6)
        rr = self._round_rect(x1, y1, x2, y2, self._s(3))
        return {"shape": rr, "box": (x1, y1, x2, y2)}

    def _make_trigger(self, x, y, w, h, label):
        x1, y1 = self.P(x, y)
        x2, y2 = self.P(x + w, y + h)
        rr = self._round_rect(x1, y1, x2, y2, self._s(4))
        fill = self.create_rectangle(x1, y2, x2, y2, width=0)     # 从底部向上填充
        tx = self.create_text((x1 + x2) / 2.0, y1 - self._s(6), text=label,
                              font=("Segoe UI", max(6, int(8 * self.scale))))
        return {"shape": rr, "fill": fill, "text": tx,
                "box": (x1, y1, x2, y2)}

    def _make_stick(self, cx, cy, r):
        x1, y1 = self.P(cx - r, cy - r)
        x2, y2 = self.P(cx + r, cy + r)
        ring = self.create_oval(x1, y1, x2, y2)
        dz_r = self._s(r) * (self.deadzone / 100.0)
        mcx, mcy = (x1 + x2) / 2.0, (y1 + y2) / 2.0
        dz = self.create_oval(mcx - dz_r, mcy - dz_r, mcx + dz_r, mcy + dz_r, dash=(2, 2))
        dot_r = self._s(5)
        dot = self.create_oval(mcx - dot_r, mcy - dot_r, mcx + dot_r, mcy + dot_r, width=0)
        return {"ring": ring, "dz": dz, "dot": dot, "box": (x1, y1, x2, y2),
                "center": (mcx, mcy), "r": self._s(r)}

    # ---- 主题 --------------------------------------------------------------
    def apply_theme(self):
        c = THEMES[self.theme]
        backdrop = (16, 24, 32)
        panel_bg = blend(c[0], backdrop)
        border = blend(c[1], backdrop)
        key_bg = blend(c[2], backdrop)
        key_fg = opaque(c[3])
        press_bg = blend(c[4], backdrop)
        press_fg = opaque(c[5])
        accent = opaque(c[8])
        dotc = opaque(c[7])
        self.cfg = dict(panel_bg=panel_bg, border=border, key_bg=key_bg, key_fg=key_fg,
                        press_bg=press_bg, press_fg=press_fg, accent=accent,
                        dot=dotc, dot_pressed=opaque(c[9]))
        self.itemconfig(self._items["panel"], fill=panel_bg, outline=border,
                        width=max(1, int(self.scale)))
        for name, k in self._keys.items():
            self._paint_key(k, False, key_bg, key_fg, border)
        for k in self._gp.get("face", {}).values():
            self._paint_key(k, False, key_bg, key_fg, border)
        for k in (self._gp.get("LB"), self._gp.get("RB"), self._gp.get("View"),
                  self._gp.get("Menu"), self._gp.get("Guide")):
            if k:
                self._paint_key(k, False, key_bg, key_fg, border)
        for k in self._gp["dpad"].values():
            self._paint_key(k, False, key_bg, key_fg, border)
        for t in (self._gp.get("LT"), self._gp.get("RT")):
            if t:
                self.itemconfig(t["shape"], fill=key_bg, outline=border,
                                width=max(1, int(self.scale)))
                self.itemconfig(t["fill"], fill=panel_bg)
                self.itemconfig(t["text"], fill=key_fg)
        for sk in ("ls", "rs"):
            st = self._gp[sk]
            self.itemconfig(st["ring"], outline=border, width=max(1, int(1.5 * self.scale)))
            self.itemconfig(st["dz"], outline=mix(border, panel_bg, 0.35))
            self.itemconfig(st["dot"], fill=dotc)
        for hint in ("ls_hint", "rs_hint"):
            self.itemconfig(self._gp[hint], fill=key_fg)
        self.itemconfig(self._gp["battery"], fill=key_fg)
        self.itemconfig(self._items["panel"], tags=("panel",))

    def _paint_key(self, k, down, bg, fg, border):
        self.itemconfig(k["shape"], fill=bg, outline=border, width=max(1, int(self.scale)))
        if "text" in k and k["text"]:
            self.itemconfig(k["text"], fill=fg)

    def _set_key(self, k, down):
        c = self.cfg
        bg = c["press_bg"] if down else c["key_bg"]
        fg = c["press_fg"] if down else c["key_fg"]
        self.itemconfig(k["shape"], fill=bg, outline=c["border"])
        if k.get("text"):
            self.itemconfig(k["text"], fill=fg)

    def set_scale(self, scale):
        self.scale = scale
        self._build()

    # ---- 每帧刷新 ----------------------------------------------------------
    def update_state(self, st, kb_down):
        self.state = st
        c = self.cfg
        btn = st.get("buttons", 0)
        gp = gp_mod
        if gp:
            def has(bit):
                return bool(btn & bit)
        else:                                             # pragma: no cover
            def has(bit):
                return False
        # 肩键 / 十字 / 面位 / View·Menu / Guide / 摇杆按下
        flags = {
            "LB": gp and has(gp.GP_LEFT_SHOULDER), "RB": gp and has(gp.GP_RIGHT_SHOULDER),
            "View": gp and has(gp.GP_BACK), "Menu": gp and has(gp.GP_START),
            "Guide": gp and has(gp.GP_GUIDE),
        }
        for name, down in flags.items():
            if self.parts.get({"View": "menu", "Menu": "menu"}.get(name, name), True) and \
               self.parts.get(name.lower(), True):
                self._set_key(self._gp[name], bool(down))
        for face, (dx, dy) in FACE_POS.items():
            bit = {"bottom": gp.GP_A, "right": gp.GP_B,
                   "left": gp.GP_X, "top": gp.GP_Y}[face] if gp else 0
            down = bool(btn & bit) if gp else False
            k = self._gp["face"][face]
            self._set_key(k, down)
            brand = BRAND.get(FACE_TEXT[self.style][face])
            if self.brand_colors and brand and not down:
                self.itemconfig(k["text"], fill="#%s" % brand)
        for name, bit in (("up", gp.GP_DPAD_UP if gp else 0),
                          ("down", gp.GP_DPAD_DOWN if gp else 0),
                          ("left", gp.GP_DPAD_LEFT if gp else 0),
                          ("right", gp.GP_DPAD_RIGHT if gp else 0)):
            self._set_key(self._gp["dpad"][name], bool(btn & bit) if gp else False)
        # 键区对比用（键盘键）
        for name, k in self._keys.items():
            self._set_key(k, name in kb_down)
        # 扳机
        dz = st.get("deadzone", self.deadzone)
        for key, val in (("LT", st.get("lt", 0)), ("RT", st.get("rt", 0))):
            t = self._gp[key]
            if self.trigger_style == "highlight":
                self.itemconfig(t["fill"], fill=c["panel_bg"])
                self.itemconfig(t["shape"], fill=c["press_bg"] if val > 30 else c["key_bg"])
            else:
                self.itemconfig(t["shape"], fill=c["key_bg"])
                x1, y1, x2, y2 = t["box"]
                h = (y2 - y1) * (val / 255.0)
                self.coords(t["fill"], x1, y2, x2, y2 - h)
                self.itemconfig(t["fill"], fill=c["accent"])
            self.itemconfig(t["text"],
                            fill=c["accent"] if val > 30 else c["key_fg"],
                            text=(TRIGGER_TEXT[self.style][0 if key == "LT" else 1]
                                  + (" %d%%" % int(val / 2.55) if self.trigger_style == "value" else "")))
        # 摇杆
        for sk, (x, y, press_bit) in (("ls", (st.get("lx", 0), st.get("ly", 0),
                                              gp.GP_LEFT_THUMB if gp else 0)),
                                      ("rs", (st.get("rx", 0), st.get("ry", 0),
                                              gp.GP_RIGHT_THUMB if gp else 0))):
            stk = self._gp[sk]
            dzv = gp.LEFT_THUMB_DEADZONE if (gp and sk == "ls") else (
                gp.RIGHT_THUMB_DEADZONE if gp else 7849)
            if self.deadzone <= 0:
                dzv = 1
            elif self.deadzone >= 40:
                dzv = int(32767 * 0.4)
            else:
                dzv = int(32767 * self.deadzone / 100.0)
            if gp:
                nx, ny = gp.apply_stick_deadzone(x, y, dzv)
            else:                                          # pragma: no cover
                nx, ny = 0.0, 0.0
            cx, cy = stk["center"]
            r = stk["r"]
            r = max(2.0, r - self._s(5))
            self.coords(stk["dot"], cx + nx * r - self._s(5), cy - ny * r - self._s(5),
                        cx + nx * r + self._s(5), cy - ny * r + self._s(5))
            pressed = bool(btn & press_bit) if gp else False
            self.itemconfig(stk["dot"], fill=c["dot_pressed"] if pressed else c["dot"])
        # 电量
        if self.parts.get("battery"):
            lv = st.get("battery", 0xFF)
            txt = {0: "电量 空", 1: "电量 低", 2: "电量 中", 3: "电量 满"}.get(lv, "")
            self.itemconfig(self._gp["battery"], text=txt)
        else:
            self.itemconfig(self._gp["battery"], text="")
        # 部件可见性
        self._apply_parts()

    def _apply_parts(self):
        p = self.parts
        hide = []
        if not p.get("shoulder"):
            hide += [self._gp["LB"], self._gp["RB"]]
        if not p.get("trigger"):
            hide += [self._gp["LT"], self._gp["RT"]]
        if not p.get("face"):
            hide += list(self._gp["face"].values())
        if not p.get("dpad"):
            hide += list(self._gp["dpad"].values())
        if not p.get("menu"):
            hide += [self._gp["View"], self._gp["Menu"]]
        if not p.get("guide"):
            hide.append(self._gp["Guide"])
        for k in hide:
            self.itemconfig(k["shape"], state="hidden")
            if k.get("text"):
                self.itemconfig(k["text"], state="hidden")
            if k.get("fill"):
                self.itemconfig(k["fill"], state="hidden")
        for sk in ("ls", "rs"):
            state = "normal" if p.get(sk) else "hidden"
            for sub in ("ring", "dz", "dot"):
                self.itemconfig(self._gp[sk][sub], state=state)
        self.itemconfig(self._gp["ls_hint"],
                        state="normal" if p.get("ls") else "hidden")
        self.itemconfig(self._gp["rs_hint"],
                        state="normal" if p.get("rs") else "hidden")


gp_mod = gp


class App(object):
    def __init__(self, root, mode="demo", theme="dark", scale=1.0, style="xbox",
                 parts=None, trigger_style="bar", deadzone=24, brand=False):
        self.root = root
        root.title("手柄按键显示 —— 预览（与小组件同一套主题配色）")
        root.configure(bg="#0E141A")
        root.resizable(False, False)

        left = tk.Frame(root, bg="#0E141A")
        left.grid(row=0, column=0, padx=10, pady=10, sticky="n")
        self.panel = PadPanel(left, scale=scale, bg="#0E141A")
        self.panel.pack()
        self.panel.theme = theme
        self.panel.style = style
        self.panel.trigger_style = trigger_style
        self.panel.deadzone = deadzone
        self.panel.brand_colors = brand
        if parts:
            self.panel.parts = dict(parts)
        self.panel.apply_theme()

        right = tk.Frame(root, bg="#0E141A")
        right.grid(row=0, column=1, padx=(0, 12), pady=10, sticky="n")

        self.mode = tk.StringVar(value=mode)
        self.theme_var = tk.StringVar(value=theme)
        self.style_var = tk.StringVar(value=style)
        self.scale_var = tk.IntVar(value=int((scale - 1.0) * 100))
        self.dz_var = tk.IntVar(value=deadzone)
        self.trig_var = tk.StringVar(value=trigger_style)
        self.brand_var = tk.BooleanVar(value=brand)
        self.pad_var = tk.StringVar(value="auto")
        self.status = tk.StringVar(value="")

        def section(txt):
            tk.Label(right, text=txt, bg="#0E141A", fg="#7FB4E0",
                     font=("Microsoft YaHei UI", 9, "bold"), anchor="w").pack(fill="x", pady=(8, 2))

        def row():
            f = tk.Frame(right, bg="#0E141A")
            f.pack(fill="x")
            return f

        section("数据来源")
        r = row()
        for val, label in (("demo", "演示动画"), ("live", "实时 XInput"), ("idle", "静止")):
            tk.Radiobutton(r, text=label, value=val, variable=self.mode, bg="#0E141A",
                           fg="#DDDDDD", selectcolor="#22303C", activebackground="#0E141A",
                           font=("Microsoft YaHei UI", 9), command=self.on_mode).pack(side="left")
        r = row()
        tk.Label(r, text="手柄：", bg="#0E141A", fg="#AAAAAA",
                 font=("Microsoft YaHei UI", 9)).pack(side="left")
        tk.OptionMenu(r, self.pad_var, "auto", "P1", "P2", "P3", "P4").pack(side="left")

        section("主题（与小组件五套主题同色）")
        r = row()
        for key, label in THEME_NAMES:
            tk.Radiobutton(r, text=label, value=key, variable=self.theme_var, bg="#0E141A",
                           fg="#DDDDDD", selectcolor="#22303C", activebackground="#0E141A",
                           font=("Microsoft YaHei UI", 9),
                           command=self.on_theme).pack(side="left")

        section("键位风格（只换印在按键上的字，物理位置不变）")
        r = row()
        for key, label in (("xbox", "Xbox"), ("ps", "PlayStation"), ("switch", "Switch")):
            tk.Radiobutton(r, text=label, value=key, variable=self.style_var, bg="#0E141A",
                           fg="#DDDDDD", selectcolor="#22303C", activebackground="#0E141A",
                           font=("Microsoft YaHei UI", 9),
                           command=self.on_style).pack(side="left")

        section("整体大小（等同布局页「整体按键大小」）")
        tk.Scale(right, from_=-60, to=100, orient="horizontal", variable=self.scale_var,
                 bg="#0E141A", fg="#DDDDDD", troughcolor="#22303C", highlightthickness=0,
                 length=300, command=self.on_scale).pack(fill="x")

        section("摇杆死区")
        tk.Scale(right, from_=0, to=40, orient="horizontal", variable=self.dz_var,
                 bg="#0E141A", fg="#DDDDDD", troughcolor="#22303C", highlightthickness=0,
                 length=300, command=self.on_dz).pack(fill="x")

        section("扳机显示")
        r = row()
        for key, label in (("bar", "进度条"), ("value", "百分比"), ("highlight", "仅高亮")):
            tk.Radiobutton(r, text=label, value=key, variable=self.trig_var, bg="#0E141A",
                           fg="#DDDDDD", selectcolor="#22303C", activebackground="#0E141A",
                           font=("Microsoft YaHei UI", 9),
                           command=self.on_trig).pack(side="left")

        section("显示部件")
        r = row()
        self.part_vars = {}
        for key, label in (("ls", "左摇杆"), ("rs", "右摇杆"), ("dpad", "十字键"),
                           ("face", "ABXY"), ("shoulder", "肩键"), ("trigger", "扳机"),
                           ("menu", "View/Menu"), ("guide", "Guide"), ("battery", "电量")):
            v = tk.BooleanVar(value=self.panel.parts.get(key, True))
            self.part_vars[key] = v
            tk.Checkbutton(r, text=label, variable=v, bg="#0E141A", fg="#DDDDDD",
                           selectcolor="#22303C", activebackground="#0E141A",
                           font=("Microsoft YaHei UI", 9),
                           command=self.on_parts).pack(side="left")

        r = row()
        tk.Checkbutton(r, text="ABXY 用品牌色（默认关，跟随主题）", variable=self.brand_var,
                       bg="#0E141A", fg="#DDDDDD", selectcolor="#22303C",
                       activebackground="#0E141A", font=("Microsoft YaHei UI", 9),
                       command=self.on_brand).pack(side="left")

        section("状态")
        tk.Label(right, textvariable=self.status, bg="#0E141A", fg="#8FE3A0",
                 font=("Consolas", 9), justify="left", anchor="w").pack(fill="x")

        self.poller = None
        if gp is not None:
            self.poller = gp.open_poller(hz=120.0)
        self.t0 = time.time()
        self.tick()

    # ---- 控件回调 ----------------------------------------------------------
    def on_mode(self):
        if self.poller:
            if self.mode.get() == "live":
                self.poller.resume()
            else:
                self.poller.pause()
        self.t0 = time.time()

    def on_theme(self):
        self.panel.theme = self.theme_var.get()
        self.panel.apply_theme()

    def on_style(self):
        self.panel.style = self.style_var.get()
        self.panel._build()
        self.panel.apply_theme()

    def on_scale(self, _=None):
        self.panel.set_scale(1.0 + self.scale_var.get() / 100.0)
        self.panel.apply_theme()

    def on_dz(self, _=None):
        self.panel.deadzone = self.dz_var.get()
        self.panel._build()
        self.panel.apply_theme()

    def on_trig(self):
        self.panel.trigger_style = self.trig_var.get()

    def on_parts(self):
        for k, v in self.part_vars.items():
            self.panel.parts[k] = v.get()

    def on_brand(self):
        self.panel.brand_colors = self.brand_var.get()

    # ---- 主循环 ------------------------------------------------------------
    def demo_state(self, t):
        """演示动画：按键轮转 + 摇杆绕圈（验证斜角不越出圆环）+ 扳机渐变 + 十字键扫过。"""
        if gp is None:
            return {"buttons": 0, "lt": 0, "rt": 0, "lx": 0, "ly": 0, "rx": 0, "ry": 0,
                    "battery": 0xFF}
        seq = [gp.GP_A, gp.GP_B, gp.GP_X, gp.GP_Y, gp.GP_LEFT_SHOULDER,
               gp.GP_RIGHT_SHOULDER, gp.GP_START, gp.GP_BACK, gp.GP_GUIDE,
               gp.GP_LEFT_THUMB, gp.GP_RIGHT_THUMB]
        idx = int(t / 0.7) % len(seq)
        buttons = seq[idx]
        if int(t / 3.5) % 2 == 1:                        # 十字键整轮扫
            buttons |= [gp.GP_DPAD_UP, gp.GP_DPAD_RIGHT,
                        gp.GP_DPAD_DOWN, gp.GP_DPAD_LEFT][int(t / 0.6) % 4]
        ang = t * 1.6
        lx = int(math.cos(ang) * 32767)
        ly = int(math.sin(ang) * 32767)
        ang2 = -t * 1.1
        rx = int(math.cos(ang2) * 22000)
        ry = int(math.sin(ang2) * 22000)
        lt = int((math.sin(t * 0.9) * 0.5 + 0.5) * 255)
        rt = int((math.sin(t * 0.6 + 1.5) * 0.5 + 0.5) * 255)
        return {"buttons": buttons, "lt": lt, "rt": rt, "lx": lx, "ly": ly,
                "rx": rx, "ry": ry, "battery": 3, "connected": 1, "active": 0}

    def live_state(self):
        if self.poller is None or not self.poller.available:
            return None
        c, a, b, lt, rt, lx, ly, rx, ry, bat, sub = self.poller.snapshot()
        want = self.pad_var.get()
        slot = None
        if want != "auto" and (c & (1 << (int(want[1]) - 1))):
            slot = int(want[1]) - 1
        return {"buttons": b, "lt": lt, "rt": rt, "lx": lx, "ly": ly, "rx": rx,
                "ry": ry, "battery": bat, "connected": c,
                "active": a if slot is None else slot,
                "chosen": (a if slot is None else slot)}

    def tick(self):
        t = time.time() - self.t0
        mode = self.mode.get()
        kb = set()
        if mode == "demo":
            seq = ["Q", "W", "E", "R", "A", "S", "D", "F", "Shift", "Ctrl", "Alt", "Space"]
            kb.add(seq[int(t / 0.35) % len(seq)])
            st = self.demo_state(t)
            self.status.set("演示动画中：按键轮转 / 摇杆绕圈 / 扳机渐变\n"
                            "（没有手柄也能看观感）")
        elif mode == "live":
            st = self.live_state()
            if st is None:
                st = {"buttons": 0, "lt": 0, "rt": 0, "lx": 0, "ly": 0,
                      "rx": 0, "ry": 0, "battery": 0xFF}
                self.status.set("XInput 不可用（三个 DLL 都没加载成功）")
            else:
                conn = st.get("connected", 0)
                if conn == 0:
                    self.status.set("未检测到手柄：请连接手柄或改用「演示动画」\n"
                                    "XInput 覆盖 Xbox 系列 + Steam Input/DS4Windows 虚拟手柄")
                else:
                    self.status.set("已连接掩码 0x%X  活跃槽 %s  按键 0x%04X\n"
                                    "LT=%d RT=%d  L=(%d,%d) R=(%d,%d)  电量=%s"
                                    % (conn, st.get("active"),
                                       st.get("buttons", 0), st.get("lt", 0),
                                       st.get("rt", 0), st.get("lx", 0), st.get("ly", 0),
                                       st.get("rx", 0), st.get("ry", 0),
                                       st.get("battery")))
        else:
            st = {"buttons": 0, "lt": 0, "rt": 0, "lx": 0, "ly": 0, "rx": 0, "ry": 0,
                  "battery": 0xFF}
            self.status.set("静止态：全部松开")
        self.panel.update_state(st, kb)
        self.root.after(16, self.tick)


def selftest(theme="dark"):
    """几何/状态自检：不依赖人眼，直接检查 canvas 的真实绘制包围盒。

    检查项：
      1) 每个手柄部件、每个键盘键都完整落在面板内；
      2) 手柄部件之间没有意外重叠（允许贴边：重叠面积 ≤ 6px² 视为相邻）；
      3) 手柄组不与键盘键区重叠；
      4) 五套主题的 10 个色槽都能解析成合法颜色；
      5) 状态映射：按下 A 用"按下字色"、扳机满值填满、摇杆满偏时点贴到环内缘。
    """
    root = tk.Tk()
    root.withdraw()
    panel = PadPanel(root, scale=1.0, bg="#0E141A")
    panel.theme = theme
    panel.apply_theme()
    panel.update_state({"buttons": 0, "lt": 0, "rt": 0, "lx": 0, "ly": 0,
                        "rx": 0, "ry": 0, "battery": 0xFF}, set())
    root.update_idletasks()

    fails = []
    panel_box = panel.bbox(panel._items["panel"])
    print("面板包围盒:", panel_box)

    # 收集部件：圆形（ABXY/Guide/摇杆环）与矩形分开，避免用包围盒比较圆形造成误报。
    # 圆半径必须用**绘制时的真实半径**（bbox 会因描边/平滑多出 1~2px）。
    RAD = {"Guide": 11, "ls": 15, "rs": 15}
    for face in ("bottom", "top", "left", "right"):
        RAD["face:" + face] = 9
    shapes = {}
    for name in ("LB", "RB", "View", "Menu", "LT", "RT"):
        shapes[name] = ("box", panel.bbox(panel._gp[name]["shape"]))
    for face, k in panel._gp["face"].items():
        b = panel.bbox(k["shape"])
        nm = "face:" + face
        shapes[nm] = ("circle", ((b[0] + b[2]) / 2.0, (b[1] + b[3]) / 2.0,
                                 panel._s(RAD[nm])))
    for d, k in panel._gp["dpad"].items():
        shapes["dpad:" + d] = ("box", panel.bbox(k["shape"]))
    for sk in ("ls", "rs"):
        b = panel.bbox(panel._gp[sk]["ring"])
        shapes[sk] = ("circle", ((b[0] + b[2]) / 2.0, (b[1] + b[3]) / 2.0,
                                 panel._s(RAD[sk])))
    b = panel.bbox(panel._gp["Guide"]["shape"])
    shapes["Guide"] = ("circle", ((b[0] + b[2]) / 2.0, (b[1] + b[3]) / 2.0,
                                  panel._s(RAD["Guide"])))
    kb = {}
    for name, k in panel._keys.items():
        kb[name] = panel.bbox(k["shape"])

    def erode(r, k=2.0):
        """内缩：抵消描边与平滑多边形造成的包围盒膨胀（bbox 里那 1~2px 不是真实重叠）。"""
        return (r[0] + k, r[1] + k, r[2] - k, r[3] - k)

    def overlap_area(a, b):
        a, b = erode(a), erode(b)
        x = min(a[2], b[2]) - max(a[0], b[0])
        y = min(a[3], b[3]) - max(a[1], b[1])
        return x * y if (x > 0 and y > 0) else 0

    def circle_rect_gap(c, r):
        """圆心到矩形的距离 - 半径：> 0 表示不相交。"""
        cx, cy, rad = c
        r = erode(r)
        dx = max(r[0] - cx, 0.0, cx - r[2])
        dy = max(r[1] - cy, 0.0, cy - r[3])
        return (dx * dx + dy * dy) ** 0.5 - rad

    def hit(n1, n2):
        # 十字键的四个臂本来就相连成一个十字 → 组内相邻不算相交
        if n1.startswith("dpad:") and n2.startswith("dpad:"):
            return False
        k1, v1 = shapes[n1]
        k2, v2 = shapes[n2]
        if k1 == "box" and k2 == "box":
            return overlap_area(v1, v2) > 8
        if k1 == "circle" and k2 == "circle":
            d = ((v1[0] - v2[0]) ** 2 + (v1[1] - v2[1]) ** 2) ** 0.5
            return d < (v1[2] + v2[2]) - 0.5
        if k1 == "circle":
            return circle_rect_gap(v1, v2) < -0.5
        return circle_rect_gap(v2, v1) < -0.5

    # 1) 面板内（用原始包围盒，含描边）
    for name, item in list(shapes.items()) + [("kb:" + k, ("box", v)) for k, v in kb.items()]:
        r = item[1] if item[0] == "box" else None
        if r is None:
            r = (item[1][0] - item[1][2], item[1][1] - item[1][2],
                 item[1][0] + item[1][2], item[1][1] + item[1][2])
        if not (panel_box[0] <= r[0] and r[2] <= panel_box[2] and
                panel_box[1] <= r[1] and r[3] <= panel_box[3]):
            fails.append("%s 超出面板: %s" % (name, [int(v) for v in r]))

    # 2) 手柄内部两两重叠（形状感知）
    names = sorted(shapes)
    for i in range(len(names)):
        for j in range(i + 1, len(names)):
            if hit(names[i], names[j]):
                fails.append("%s 与 %s 相交" % (names[i], names[j]))

    # 3) 手柄组 vs 键盘键
    for kn, kr in kb.items():
        for gn in shapes:
            kind, v = shapes[gn]
            if kind == "box":
                if overlap_area(kr, v) > 8:
                    fails.append("键盘键 %s 与手柄 %s 重叠" % (kn, gn))
            elif circle_rect_gap(v, kr) < -1.0:
                fails.append("键盘键 %s 与手柄 %s 相交" % (kn, gn))

    # 4) 主题色
    for tname, slots in THEMES.items():
        try:
            for s in slots:
                c = blend(s, (16, 24, 32))
                assert len(c) == 7 and c[0] == "#"
        except Exception as exc:                          # pragma: no cover
            fails.append("主题 %s 色槽异常: %s" % (tname, exc))

    # 5) 状态映射
    if gp is not None:
        panel.update_state({"buttons": gp.GP_A, "lt": 255, "rt": 0, "lx": 32767,
                            "ly": 0, "rx": 0, "ry": 0, "battery": 3}, set())
        root.update_idletasks()
        a_text = panel.itemcget(panel._gp["face"]["bottom"]["text"], "fill").upper()
        if a_text != panel.cfg["press_fg"].upper():
            fails.append("按下 A 未使用按下字色（%s != %s）" % (a_text, panel.cfg["press_fg"]))
        x1, y1, x2, y2 = panel._gp["LT"]["box"]
        fx1, fy1, fx2, fy2 = panel.coords(panel._gp["LT"]["fill"])
        # 注意：tkinter 会把矩形坐标规范化（y1<y2），所以用高度比较而不是原始顺序
        if abs((fy2 - fy1) - (y2 - y1)) > 2:
            fails.append("扳机满值未填满：fill 高 %d / 条高 %d" % (fy2 - fy1, y2 - y1))
        st = panel._gp["ls"]
        dx1, dy1, dx2, dy2 = panel.coords(st["dot"])
        cx, cy = st["center"]
        r_travel = st["r"] - panel._s(5)          # 点的圆心可移动半径
        if abs(((dx1 + dx2) / 2.0 - cx) - r_travel) > 1.5:
            fails.append("摇杆满偏时点未到环内缘：%0.1f vs %0.1f"
                         % ((dx1 + dx2) / 2.0 - cx, r_travel))

    print("部件几何（circle=圆心+半径, box=包围盒）:")
    for name in sorted(shapes):
        kind, v = shapes[name]
        print("   %-12s %-7s %s" % (name, kind, [round(x, 1) for x in v]))
    print("主题配色（深色，压到游戏画面后的实际显示色）:")
    for key in ("panel_bg", "border", "key_bg", "key_fg", "press_bg", "press_fg",
                "accent", "dot", "dot_pressed"):
        print("   %-12s %s" % (key, panel.cfg[key]))
    if fails:
        print("\n自检失败 %d 项:" % len(fails))
        for f in fails:
            print("   [x]", f)
        return 1
    print("\n自检通过：布局无越界 / 无重叠，主题色与状态映射正常")
    return 0


def main(argv=None):
    try:
        # 控制台默认是 GBK，中文/符号打印会炸；统一按 UTF-8 输出（失败则退回替换字符）
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    ap = argparse.ArgumentParser(description="手柄按键显示预览窗口")
    ap.add_argument("--mode", default="demo", choices=("demo", "live", "idle"))
    ap.add_argument("--theme", default="dark", choices=[k for k, _ in THEME_NAMES])
    ap.add_argument("--style", default="xbox", choices=("xbox", "ps", "switch"))
    ap.add_argument("--scale", type=int, default=0, help="整体大小 -60..100（同小组件语义）")
    ap.add_argument("--trigger", default="bar", choices=("bar", "value", "highlight"))
    ap.add_argument("--deadzone", type=int, default=24)
    ap.add_argument("--brand", action="store_true", help="ABXY 用品牌色")
    ap.add_argument("--selftest", action="store_true",
                    help="不开窗口，直接做几何/状态自检并打印结果")
    args = ap.parse_args(argv)

    if args.selftest:
        return selftest(theme=args.theme)

    root = tk.Tk()
    App(root, mode=args.mode, theme=args.theme,
        scale=1.0 + args.scale / 100.0, style=args.style,
        trigger_style=args.trigger, deadzone=args.deadzone, brand=args.brand)
    root.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
