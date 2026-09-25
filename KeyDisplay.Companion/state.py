"""输入状态模型与二进制快照协议。

快照为固定 76 字节小端二进制（v4，含虚拟屏幕范围 + 全量 256 VK 位图 + 输入时间戳）：
    [0:4]   MAGIC      = b"KDSP"
    [4]     version    = 4
    [5:7]   keys       = uint16，12 个键盘键位掩码
    [7]     mouse      = uint8，5 个鼠标按键掩码
    [8:12]  mouse_x    = int32，屏幕坐标
    [12:16] mouse_y    = int32，屏幕坐标
    [16:20] vs_x       = int32，虚拟屏幕原点 X（GetSystemMetrics SM_XVIRTUALSCREEN）
    [20:24] vs_y       = int32，虚拟屏幕原点 Y
    [24:28] vs_w       = int32，虚拟屏幕宽度
    [28:32] vs_h       = int32，虚拟屏幕高度
    [32:36] seq        = uint32，自增序号
    [36:44] ts_ns      = uint64，输入时间戳（perf_counter_ns = QPC 纳秒，供小组件算延迟）
    [44:76] extra      = 32 字节 = 256 位，按虚拟键码 VK 直接索引：
                         位 = (extra[vk>>3] >> (vk&7)) & 1；1=按下，0=松开

v3（68 字节，无时间戳）仍可被 parse_snapshot 解析（ts_ns 回退 0）；v2 不兼容被拒绝。

keys 位序：bit0=Q bit1=W bit2=E bit3=R bit4=A bit5=S bit6=D bit7=F
           bit8=Shift bit9=Ctrl bit10=Alt bit11=Space
mouse 位序：bit0=左键 bit1=右键 bit2=中键 bit3=侧键1 bit4=侧键2
"""
import struct
import time

MAGIC = b"KDSP"
VERSION = 4
SNAPSHOT_SIZE = 76
SNAPSHOT_SIZE_V3 = 68

KEY_ORDER = ["Q", "W", "E", "R", "A", "S", "D", "F",
             "Shift", "Ctrl", "Alt", "Space"]
MOUSE_ORDER = ["L", "R", "M", "X1", "X2"]

_FMT_V4 = "<4sBHBiiiiiiI32sQ"     # 0.9.5 修复：32 字节 VK 位图仍在 [36:68]（与 v3 完全一致），
_FMT_V3 = "<4sBHBiiiiiiI32s"      # 时间戳追加在末尾 [68:76]。
                                  # 事故还原：先前实现把 Q 放在 32s 之前，导致位图整体后移 8 字节；
                                  # 小组件按 v3 偏移读位图，读到的其实是时间戳低字节 → 每帧乱变 →
                                  # 只有"自定义键"和"滚轮上/下"（都走位图）疯狂连点，其余键正常。


class InputState:
    __slots__ = ("keys", "mouse", "mx", "my", "vx", "vy", "vw", "vh", "seq",
                 "extra")

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
        self.extra = bytearray(32)

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

    def set_vk(self, vk, down):
        """置位/复位 VK 位图（vk 0~255，越界自动截断）。"""
        vk &= 0xFF
        byte_idx = vk >> 3
        bit = 1 << (vk & 7)
        if down:
            self.extra[byte_idx] |= bit
        else:
            self.extra[byte_idx] &= 0xFF ^ bit

    def serialize(self):
        return struct.pack(_FMT_V4, MAGIC, VERSION, self.keys,
                           self.mouse, self.mx, self.my,
                           self.vx, self.vy, self.vw, self.vh, self.seq,
                           bytes(self.extra), time.perf_counter_ns())

    def serialize_into(self, buf):
        """0.8.4 性能：原地写入复用缓冲（避免每帧 bytes 分配与拷贝），返回同一缓冲。

        240Hz 下每帧一次分配看似便宜，但叠加 GIL 与 GC 压力会影响推送时间的均匀性
        （表现为光标/按键反馈的偶发延迟尖峰）。
        """
        struct.pack_into(_FMT_V4, buf, 0, MAGIC, VERSION, self.keys,
                         self.mouse, self.mx, self.my,
                         self.vx, self.vy, self.vw, self.vh, self.seq,
                         bytes(self.extra), time.perf_counter_ns())
        return buf


def parse_snapshot(data):
    """解析快照，返回 dict 或 None（数据无效）。

    兼容 v4（76 字节，含 ts_ns）与 v3（68 字节，无时间戳，ts_ns 回退 0）；
    v2（36 字节）及其它版本不兼容，返回 None（保持旧行为）。
    """
    if not isinstance(data, (bytes, bytearray)) or len(data) < 5:
        return None
    if bytes(data[:4]) != MAGIC:
        return None
    ver = data[4]
    if ver == 4:
        if len(data) < SNAPSHOT_SIZE:
            return None
        data = data[:SNAPSHOT_SIZE]
        (magic, ver, keys, mouse, mx, my, vx, vy, vw, vh, seq,
         extra, ts_ns) = struct.unpack(_FMT_V4, data)
    elif ver == 3:
        if len(data) < SNAPSHOT_SIZE_V3:
            return None
        data = data[:SNAPSHOT_SIZE_V3]
        (magic, ver, keys, mouse, mx, my, vx, vy, vw, vh, seq,
         extra) = struct.unpack(_FMT_V3, data)
        ts_ns = 0
    else:
        return None
    return {"keys": keys, "mouse": mouse, "mx": mx, "my": my,
            "vx": vx, "vy": vy, "vw": vw, "vh": vh, "seq": seq,
            "ts_ns": ts_ns, "extra": extra}
