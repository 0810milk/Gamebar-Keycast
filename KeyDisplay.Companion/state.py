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
    [36:68] extra      = 32 字节 = 256 位，按虚拟键码 VK 直接索引：
                         位 = (extra[vk>>3] >> (vk&7)) & 1；1=按下，0=松开
    [68:76] ts_ns      = uint64，输入时间戳（perf_counter_ns = QPC 纳秒，供小组件算延迟）
    [76:92] gamepad    = 16 字节手柄尾块（**默认不发**，客户端发 `CMD|PROTO|92` 握手后才带）

    注意：`[36:68]` 是 VK 位图、`[68:76]` 才是时间戳（与 `_FMT_V4` 的字段顺序一致）。
    早期本文档曾把两者写成 [36:44]/[44:76]，属于错误描述 —— 0.9.5 那次"滚轮疯狂连点"
    就是字段顺序错位导致的，所以这里明确写死，改协议前请先看 test_units.GamepadTailTests。

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

# ---- 0.9.6/1.2：手柄块（加法式扩展，**默认不发**） -----------------------------
# 兼容策略：帧长默认仍是 76 字节（老小组件逐字节不受影响）。只有客户端在连接后发送
# `CMD|PROTO|92` 握手，服务端才把该连接切到 92 字节（= 前 76 字节完全相同 + 16 字节尾块）。
# 之所以不动 VERSION（保持 4）：老小组件是按长度/版本分支解析的，若把 ver 改成 5，
# 老客户端可能直接判定为不兼容而显示"未连接"。
SNAPSHOT_SIZE_V5 = 92
_FMT_V5_TAIL = "<BBHBBhhhhBB"      # connected, active, buttons, lt, rt, lx, ly, rx, ry, battery, subtype
"""
尾块布局（偏移 76..92，16 字节）：
    [76]    connected  uint8  bit0..3 = XInput 槽 0..3 已连接
    [77]    active     uint8  活跃槽（最后真的产生输入的那个），0xFF = 无
    [78:80] buttons    uint16 XINPUT_GAMEPAD_* 掩码（含未公开的 Guide=0x0400）
    [80]    lt         uint8  左扳机 0~255
    [81]    rt         uint8  右扳机 0~255
    [82:84] lx         int16  左摇杆 X  -32768~32767（原始值，死区由渲染侧处理）
    [84:86] ly         int16  左摇杆 Y  （注意 XInput 的 Y 轴向上为正）
    [86:88] rx         int16  右摇杆 X
    [88:90] ry         int16  右摇杆 Y
    [90]    battery    uint8  0=空 1=低 2=中 3=满，0xFF=未知
    [91]    subtype    uint8  XInput SubType（1=手柄），0xFF=未知
"""

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
                 "extra", "gp")

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
        # 手柄尾块：默认"无手柄"（0xFF 全 0 且 active=0xFF）
        self.gp = (0, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF)

    def set_gamepad(self, snapshot_tuple):
        """写入手柄状态（参数即 gamepad.GamepadSnapshot.as_tuple() 的 11 个值）。"""
        if snapshot_tuple is None:
            return
        if len(snapshot_tuple) != 11:
            raise ValueError("手柄状态必须是 11 元组")
        self.gp = tuple(snapshot_tuple)

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

    def serialize(self, v5=False):
        """v5=True 时返回 92 字节（前 76 字节与 v4 逐字节相同 + 16 字节手柄尾块）。"""
        out = struct.pack(_FMT_V4, MAGIC, VERSION, self.keys,
                          self.mouse, self.mx, self.my,
                          self.vx, self.vy, self.vw, self.vh, self.seq,
                          bytes(self.extra), time.perf_counter_ns())
        if v5:
            out += struct.pack(_FMT_V5_TAIL, *self.gp)
        return out

    def serialize_into(self, buf, v5=False):
        """0.8.4 性能：原地写入复用缓冲（避免每帧 bytes 分配与拷贝），返回同一缓冲。

        240Hz 下每帧一次分配看似便宜，但叠加 GIL 与 GC 压力会影响推送时间的均匀性
        （表现为光标/按键反馈的偶发延迟尖峰）。
        0.9.6：buf 需 ≥ 76（v4）或 ≥ 92（v5）字节；v5=True 时追加 16 字节手柄尾块。
        """
        struct.pack_into(_FMT_V4, buf, 0, MAGIC, VERSION, self.keys,
                         self.mouse, self.mx, self.my,
                         self.vx, self.vy, self.vw, self.vh, self.seq,
                         bytes(self.extra), time.perf_counter_ns())
        if v5:
            struct.pack_into(_FMT_V5_TAIL, buf, SNAPSHOT_SIZE, *self.gp)
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
    gamepad = None
    if ver == 4:
        if len(data) < SNAPSHOT_SIZE:
            return None
        if len(data) >= SNAPSHOT_SIZE_V5:
            # 加法式尾块：92 字节帧比 76 字节帧多出手柄状态；76 字节帧 gamepad 保持 None
            try:
                gamepad = struct.unpack_from(_FMT_V5_TAIL, data, SNAPSHOT_SIZE)
            except struct.error:
                gamepad = None
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
            "ts_ns": ts_ns, "extra": extra, "gamepad": gamepad}
