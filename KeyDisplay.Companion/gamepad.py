"""XInput 手柄采集（只读轮询）。

为什么放在伴生进程：
- 伴生进程是普通桌面进程，轮询成本极低（4 个槽位 = 每帧 4 次 XInputGetState）；
- 只用 XInputGetState **被动读取**，不挂钩子、不注入、不写震动 —— 对手柄这块不增加反作弊暴露面；
- 小组件保持"纯渲染"定位，不需要新增线程、设备权限或沙箱内可用性验证。

为什么要做：
- XInput 覆盖 Xbox 360/One/Series（有线/蓝牙/无线适配器），也覆盖 **Steam Input 与 DS4Windows
  生成的虚拟手柄** —— 也就是说"用 PS 手柄玩 PC"的绝大多数用户同样能被看到；
- 原生直连的 DS4/DualSense/Switch Pro（不经 Steam）XInput 看不到，留待后续用
  Windows.Gaming.Input / RawGameController 补（阶段 3）。

按钮掩码（与 XINPUT_GAMEPAD_* 官方常量一致）：
    0x0001 DPadUp    0x0002 DPadDown    0x0004 DPadLeft     0x0008 DPadRight
    0x0010 Start     0x0020 Back        0x0040 LeftThumb    0x0080 RightThumb
    0x0100 LeftShoulder                0x0200 RightShoulder
    0x1000 A         0x2000 B           0x4000 X            0x8000 Y
    0x0400 Guide（未公开但长期稳定；按下会**同时打开 Game Bar 叠加层**，那是系统行为）

死区与圆度**不在采集侧处理**：这里只下发原始值（lt/rt 0~255，摇杆 -32768~32767），
由渲染侧按用户设置应用死区与径向钳制。原因：XInput 摇杆是"方框钳制"（斜角能到 ±32767），
直接按 x/32767 画点会在四个对角戳出圆环外，必须按幅值径向缩放才能贴到环边；
而"要不要按死区显示"是显示偏好，属于设置项而不是采集语义。
"""
import ctypes
import threading
import time

# ---- XInput 常量 -----------------------------------------------------------
ERROR_DEVICE_NOT_CONNECTED = 1167

GP_DPAD_UP = 0x0001
GP_DPAD_DOWN = 0x0002
GP_DPAD_LEFT = 0x0004
GP_DPAD_RIGHT = 0x0008
GP_START = 0x0010
GP_BACK = 0x0020
GP_LEFT_THUMB = 0x0040
GP_RIGHT_THUMB = 0x0080
GP_LEFT_SHOULDER = 0x0100
GP_RIGHT_SHOULDER = 0x0200
GP_A = 0x1000
GP_B = 0x2000
GP_X = 0x4000
GP_Y = 0x8000
GP_GUIDE = 0x0400          # 未公开位，实测稳定

# 官方死区常量（XInput 文档）：左摇杆 7849、右摇杆 8689、扳机 30
LEFT_THUMB_DEADZONE = 7849
RIGHT_THUMB_DEADZONE = 8689
TRIGGER_THRESHOLD = 30

BATTERY_DEVTYPE_GAMEPAD = 0
BATTERY_LEVEL_EMPTY = 0
BATTERY_LEVEL_LOW = 1
BATTERY_LEVEL_MEDIUM = 2
BATTERY_LEVEL_FULL = 3
BATTERY_LEVEL_UNKNOWN = 0xFF

SUBTYPE_UNKNOWN = 0xFF
ACTIVE_NONE = 0xFF

_XINPUT_DLLS = ("XInput1_4.dll", "XInput1_3.dll", "XInput9_1_0.dll")


class _XINPUT_GAMEPAD(ctypes.Structure):
    _fields_ = [("wButtons", ctypes.c_ushort),
                ("bLeftTrigger", ctypes.c_ubyte),
                ("bRightTrigger", ctypes.c_ubyte),
                ("sThumbLX", ctypes.c_short),
                ("sThumbLY", ctypes.c_short),
                ("sThumbRX", ctypes.c_short),
                ("sThumbRY", ctypes.c_short)]


class _XINPUT_STATE(ctypes.Structure):
    _fields_ = [("dwPacketNumber", ctypes.c_ulong),
                ("Gamepad", _XINPUT_GAMEPAD)]


class _XINPUT_VIBRATION(ctypes.Structure):
    _fields_ = [("wLeftMotorSpeed", ctypes.c_ushort),
                ("wRightMotorSpeed", ctypes.c_ushort)]


class _XINPUT_CAPABILITIES(ctypes.Structure):
    _fields_ = [("Type", ctypes.c_ubyte),
                ("SubType", ctypes.c_ubyte),
                ("Flags", ctypes.c_ushort),
                ("Gamepad", _XINPUT_GAMEPAD),
                ("Vibration", _XINPUT_VIBRATION)]


class _XINPUT_BATTERY_INFORMATION(ctypes.Structure):
    _fields_ = [("BatteryType", ctypes.c_ubyte),
                ("BatteryLevel", ctypes.c_ubyte)]


def _load_xinput():
    """按 1_4 → 1_3 → 9_1_0 顺序探测 XInput DLL；都不可用返回 None（手柄功能静默关闭）。"""
    for name in _XINPUT_DLLS:
        try:
            lib = ctypes.WinDLL(name)
        except OSError:
            continue
        try:
            lib.XInputGetState.argtypes = [ctypes.c_uint,
                                           ctypes.POINTER(_XINPUT_STATE)]
            lib.XInputGetState.restype = ctypes.c_uint
        except AttributeError:
            continue
        try:
            lib.XInputGetCapabilities.argtypes = [ctypes.c_uint, ctypes.c_uint,
                                                  ctypes.POINTER(_XINPUT_CAPABILITIES)]
            lib.XInputGetCapabilities.restype = ctypes.c_uint
        except AttributeError:
            pass
        try:
            lib.XInputGetBatteryInformation.argtypes = [
                ctypes.c_uint, ctypes.c_ubyte,
                ctypes.POINTER(_XINPUT_BATTERY_INFORMATION)]
            lib.XInputGetBatteryInformation.restype = ctypes.c_uint
        except AttributeError:
            pass
        try:
            lib.XInputSetState.argtypes = [ctypes.c_uint,
                                           ctypes.POINTER(_XINPUT_VIBRATION)]
            lib.XInputSetState.restype = ctypes.c_uint
        except AttributeError:
            pass
        return lib
    return None


def has_real_input(pad):
    """该 XInput 手柄是否"真的有操作"（用于挑选活跃手柄，避免静止噪声抢焦点）。

    判定：任意按键位 / 扳机超过阈值 / 摇杆超过官方死区。
    """
    if pad.wButtons:
        return True
    if pad.bLeftTrigger > TRIGGER_THRESHOLD or pad.bRightTrigger > TRIGGER_THRESHOLD:
        return True
    if abs(pad.sThumbLX) > LEFT_THUMB_DEADZONE or abs(pad.sThumbLY) > LEFT_THUMB_DEADZONE:
        return True
    if abs(pad.sThumbRX) > RIGHT_THUMB_DEADZONE or abs(pad.sThumbRY) > RIGHT_THUMB_DEADZONE:
        return True
    return False


def apply_stick_deadzone(x, y, deadzone):
    """死区 + 径向钳制，返回 (nx, ny) ∈ [-1, 1]，可直接乘圆环半径。

    两步：
    1) 幅值小于死区 → 归零；否则把死区内的"死区到满量程"重新线性映射到 0..1
       （这样点从死区边界开始动，不会有跳变）；
    2) 幅值 > 1 → 整体缩放到 1，保证斜角方向刚好贴住圆环边（XInput 是方框钳制）。
    """
    try:
        x = float(x)
        y = float(y)
    except (TypeError, ValueError):
        return (0.0, 0.0)
    dz = max(0.0, min(0.99, float(deadzone) / 32767.0))
    mag = (x * x + y * y) ** 0.5
    if mag <= 0.0:
        return (0.0, 0.0)
    norm = mag / 32767.0
    if norm <= dz:
        return (0.0, 0.0)
    scaled = (norm - dz) / (1.0 - dz)
    if scaled > 1.0:
        scaled = 1.0
    if mag > 1.0:
        # 径向钳制：斜角方向不越出圆环
        k = scaled / norm
    else:
        k = scaled / norm
    return (x * k / 32767.0, y * k / 32767.0)


class GamepadSnapshot(object):
    """某一时刻的手柄状态（值拷贝，可安全跨线程传递）。"""

    __slots__ = ("connected", "active", "buttons", "lt", "rt",
                 "lx", "ly", "rx", "ry", "battery", "subtype")

    def __init__(self):
        self.connected = 0                 # bit0..3 = 槽 0..3 已连接
        self.active = ACTIVE_NONE          # 活跃槽（0..3，0xFF=无）
        self.buttons = 0
        self.lt = 0
        self.rt = 0
        self.lx = 0
        self.ly = 0
        self.rx = 0
        self.ry = 0
        self.battery = BATTERY_LEVEL_UNKNOWN
        self.subtype = SUBTYPE_UNKNOWN

    def copy(self):
        s = GamepadSnapshot()
        for f in GamepadSnapshot.__slots__:
            setattr(s, f, getattr(self, f))
        return s

    def as_tuple(self):
        """(connected, active, buttons, lt, rt, lx, ly, rx, ry, battery, subtype)"""
        return (self.connected, self.active, self.buttons, self.lt, self.rt,
                self.lx, self.ly, self.rx, self.ry, self.battery, self.subtype)


class GamepadPoller(threading.Thread):
    """后台轮询线程。

    默认 120Hz：XInput 自身约 60Hz，蓝牙手柄报文 60~125Hz，再高没有意义。
    没有任何客户端连接时调用方可以 pause()，此时线程只做 sleep，CPU 占用≈0。
    """

    def __init__(self, hz=120.0):
        threading.Thread.__init__(self, name="gamepad-poller")
        self.daemon = True
        self._lib = _load_xinput()
        self._interval = 1.0 / max(30.0, min(480.0, float(hz)))
        self._stop = threading.Event()
        self._pause = threading.Event()
        self._lock = threading.Lock()
        self._snap = GamepadSnapshot()
        self._packets = [0, 0, 0, 0]
        self._active = ACTIVE_NONE
        self._battery = BATTERY_LEVEL_UNKNOWN
        self._subtype = SUBTYPE_UNKNOWN
        self._battery_at = 0.0
        self._caps_at = 0.0

    # ---- 外部接口 ---------------------------------------------------------
    @property
    def available(self):
        return self._lib is not None

    def snapshot(self):
        """返回当前状态的元组（与 GamepadSnapshot.as_tuple 一致）。"""
        with self._lock:
            return self._snap.as_tuple()

    def pause(self):
        self._pause.set()

    def resume(self):
        self._pause.clear()

    def stop(self):
        self._stop.set()

    def set_vibration(self, slot, left, right):
        """可选：震动测试（唯一会"写"手柄状态的地方，默认不被主流程调用）。"""
        if self._lib is None or not hasattr(self._lib, "XInputSetState"):
            return False
        vib = _XINPUT_VIBRATION(int(max(0, min(65535, left))),
                                int(max(0, min(65535, right))))
        return self._lib.XInputSetState(int(slot), ctypes.byref(vib)) == 0

    # ---- 线程主体 ---------------------------------------------------------
    def run(self):
        if self._lib is None:
            return
        state = _XINPUT_STATE()
        batt = _XINPUT_BATTERY_INFORMATION()
        while not self._stop.is_set():
            try:
                if not self._pause.is_set():
                    self._poll_once(state, batt)
            except Exception:
                # 轮询异常绝不能杀掉线程（否则手柄显示会永久停在最后一帧）
                pass
            self._stop.wait(self._interval)

    def _poll_once(self, state, batt):
        now = time.perf_counter()
        mask = 0
        for i in range(4):
            rc = self._lib.XInputGetState(i, ctypes.byref(state))
            if rc != 0:
                # ERROR_DEVICE_NOT_CONNECTED：该槽位空（也是"未连接"的唯一检测手段）
                self._packets[i] = 0
                continue
            mask |= 1 << i
            if state.dwPacketNumber != self._packets[i]:
                first_seen = self._packets[i] == 0        # 0 = 上一次该槽还是空的（刚接上）
                self._packets[i] = state.dwPacketNumber
                if first_seen:
                    # 刚接上就读取 SubType（原来只在产生输入时才读，静止时一直是未知）
                    self._caps_at = now
                    self._read_caps(i)
                if has_real_input(state.Gamepad):
                    self._active = i
                    if not first_seen and now - self._caps_at > 2.0:
                        self._caps_at = now
                        self._read_caps(i)

        chosen = self._active if (self._active != ACTIVE_NONE and
                                 (mask & (1 << self._active))) else ACTIVE_NONE
        if chosen == ACTIVE_NONE:
            # 活跃手柄断开了（或还没有任何操作）：退回第一个已连接的手柄
            for i in range(4):
                if mask & (1 << i):
                    chosen = i
                    self._active = i
                    break
        if chosen == ACTIVE_NONE:
            self._active = ACTIVE_NONE

        snap = GamepadSnapshot()
        snap.connected = mask
        snap.active = chosen
        if chosen != ACTIVE_NONE:
            self._lib.XInputGetState(chosen, ctypes.byref(state))
            gp = state.Gamepad
            snap.buttons = gp.wButtons
            snap.lt = gp.bLeftTrigger
            snap.rt = gp.bRightTrigger
            snap.lx = gp.sThumbLX
            snap.ly = gp.sThumbLY
            snap.rx = gp.sThumbRX
            snap.ry = gp.sThumbRY
            snap.subtype = self._subtype
            if now - self._battery_at > 5.0:
                # 电量变化很慢，5 秒读一次足够（无线手柄才有意义）
                self._battery_at = now
                self._battery = self._read_battery(chosen, batt)
            snap.battery = self._battery
        with self._lock:
            self._snap = snap

    def _read_caps(self, slot):
        if not hasattr(self._lib, "XInputGetCapabilities"):
            return
        caps = _XINPUT_CAPABILITIES()
        if self._lib.XInputGetCapabilities(slot, 0, ctypes.byref(caps)) == 0:
            self._subtype = int(caps.SubType)

    def _read_battery(self, slot, batt):
        if not hasattr(self._lib, "XInputGetBatteryInformation"):
            return BATTERY_LEVEL_UNKNOWN
        if self._lib.XInputGetBatteryInformation(
                slot, BATTERY_DEVTYPE_GAMEPAD, ctypes.byref(batt)) == 0:
            # 蓝牙连接的手柄不经 XInput 上报电量：BatteryType=0（未连接/不支持）时
            # BatteryLevel 往往是 0，照原样返回会被界面显示成"电量 空"（实测本机蓝牙
            # 手柄就是 0，属于误导）。只信任 低/中/满 三档，其余一律按"未知"处理。
            level = int(batt.BatteryLevel)
            if int(batt.BatteryType) == 0 or level < BATTERY_LEVEL_LOW:
                return BATTERY_LEVEL_UNKNOWN
            return level
        return BATTERY_LEVEL_UNKNOWN


def open_poller(hz=120.0, start=True):
    """便捷构造：返回 GamepadPoller（DLL 不可用时 available=False，线程为空转）。"""
    p = GamepadPoller(hz=hz)
    if start:
        p.start()
    return p


if __name__ == "__main__":     # 手动自检：python gamepad.py
    import sys
    poller = open_poller()
    if not poller.available:
        print("XInput 不可用（三个 DLL 都没加载成功）")
        sys.exit(1)
    print("轮询中，按 Ctrl+C 退出（未连接手柄时 connected 恒为 0）")
    try:
        while True:
            c, a, b, lt, rt, lx, ly, rx, ry, bat, sub = poller.snapshot()
            print("connected=%s active=%s buttons=0x%04X LT=%3d RT=%3d "
                  "L=(%6d,%6d) R=(%6d,%6d) battery=%s subtype=%s"
                  % (bin(c), a, b, lt, rt, lx, ly, rx, ry, bat, sub))
            time.sleep(0.25)
    except KeyboardInterrupt:
        poller.stop()
