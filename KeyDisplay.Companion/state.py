"""输入状态模型与二进制快照协议。

快照为固定 36 字节小端二进制（v2，含虚拟屏幕范围）：
    [0:4]   MAGIC      = b"KDSP"
    [4]     version    = 2
    [5:7]   keys       = uint16，12 个键盘键位掩码
    [7]     mouse      = uint8，5 个鼠标按键掩码
    [8:12]  mouse_x    = int32，屏幕坐标
    [12:16] mouse_y    = int32，屏幕坐标
    [16:20] vs_x       = int32，虚拟屏幕原点 X（GetSystemMetrics SM_XVIRTUALSCREEN）
    [20:24] vs_y       = int32，虚拟屏幕原点 Y
    [24:28] vs_w       = int32，虚拟屏幕宽度
    [28:32] vs_h       = int32，虚拟屏幕高度
    [32:36] seq        = uint32，自增序号

keys 位序：bit0=Q bit1=W bit2=E bit3=R bit4=A bit5=S bit6=D bit7=F
           bit8=Shift bit9=Ctrl bit10=Alt bit11=Space
mouse 位序：bit0=左键 bit1=右键 bit2=中键 bit3=侧键1 bit4=侧键2
"""
import struct

MAGIC = b"KDSP"
VERSION = 2
SNAPSHOT_SIZE = 36

KEY_ORDER = ["Q", "W", "E", "R", "A", "S", "D", "F",
             "Shift", "Ctrl", "Alt", "Space"]
MOUSE_ORDER = ["L", "R", "M", "X1", "X2"]


class InputState:
    __slots__ = ("keys", "mouse", "mx", "my", "vx", "vy", "vw", "vh", "seq")

    def __init__(self):
        self.keys = 0
        self.mouse = 0
        self.mx = 0
        self.my = 0
        self.vx = 0
        self.vy = 0
        self.vw = 1920
        self.vh = 1080
        self.seq = 0

    def set_key(self, name, down):
        bit = 1 << KEY_ORDER.index(name)
        if down:
            self.keys |= bit
        else:
            self.keys &= ~bit

    def set_mouse(self, name, down):
        bit = 1 << MOUSE_ORDER.index(name)
        if down:
            self.mouse |= bit
        else:
            self.mouse &= ~bit

    def serialize(self):
        return struct.pack("<4sBHBiiiiiiI", MAGIC, VERSION, self.keys,
                           self.mouse, self.mx, self.my,
                           self.vx, self.vy, self.vw, self.vh, self.seq)


def parse_snapshot(data):
    """解析快照，返回 dict 或 None（数据无效）。"""
    if len(data) < SNAPSHOT_SIZE:
        return None
    data = data[:SNAPSHOT_SIZE]
    magic, ver, keys, mouse, mx, my, vx, vy, vw, vh, seq = struct.unpack(
        "<4sBHBiiiiiiI", data)
    if magic != MAGIC or ver != VERSION:
        return None
    return {"keys": keys, "mouse": mouse, "mx": mx, "my": my,
            "vx": vx, "vy": vy, "vw": vw, "vh": vh, "seq": seq}