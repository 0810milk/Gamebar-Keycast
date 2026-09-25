"""底层参数测量与无害开关（ctypes 直调 Win32 API，无第三方依赖）。

- 鼠标回报率/抖动：在 hooks.py 的 WM_INPUT 处理里记录每个鼠标报文时间戳，
  用 deque 保存最近 ~2 秒样本，统计实测报文频率（Hz）与相邻间隔标准差（ms）。
- 显示器模式：EnumDisplaySettingsW 取主显示器当前刷新率/宽高（缓存 2 秒）。
- 鼠标加速："提高指针精确度"读（SPI_GETMOUSE）/写（SPI_SETMOUSE）+ 回读校验。
- 低延迟模式：开关电源节流（EcoQoS）/进程优先级/计时器精度，可反复切换。

所有样本与系统读取均在短锁或简单赋值下进行，避免给 240Hz 主循环/钩子回调
增加深拷贝大列表或长临界区开销。
"""
import collections
import ctypes
import ctypes.wintypes as wt
import threading
import time

import debuglog

# --- Win32 常量 -----------------------------------------------------------
ENUM_CURRENT_SETTINGS = -1
SPI_GETMOUSE = 0x0003
SPI_SETMOUSE = 0x0004
SPIF_UPDATEINIFILE = 0x0001
SPIF_SENDCHANGE = 0x0002

NORMAL_PRIORITY_CLASS = 0x00000020
ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000
PROCESS_POWER_THROTTLING = 4
PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1

# 鼠标回报率统计窗口（秒）：保留最近该窗口内的报文时间戳
_REPORT_WINDOW = 2.0
_reports = collections.deque()
_reports_lock = threading.Lock()

# 显示器模式缓存（避免 followRefresh 时每帧都 EnumDisplaySettingsW）
_DISPLAY_CACHE_TTL = 2.0
_display_cache = None
_display_cache_at = 0.0

# 低延迟模式内部状态（timeBeginPeriod 是否已提升，供反复切换/退出恢复）
_timer_raised = False

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)


class POINTL(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class DEVMODEW(ctypes.Structure):
    _fields_ = [
        ("dmDeviceName", ctypes.c_wchar * 32),
        ("dmSpecVersion", wt.WORD),
        ("dmDriverVersion", wt.WORD),
        ("dmSize", wt.WORD),
        ("dmDriverExtra", wt.WORD),
        ("dmFields", wt.DWORD),
        # union：display 变体（POINTL + 2×DWORD，16 字节）
        ("dmPosition", POINTL),
        ("dmDisplayOrientation", wt.DWORD),
        ("dmDisplayFixedOutput", wt.DWORD),
        ("dmColor", ctypes.c_short),
        ("dmDuplex", ctypes.c_short),
        ("dmYResolution", ctypes.c_short),
        ("dmTTOption", ctypes.c_short),
        ("dmCollate", ctypes.c_short),
        ("dmFormName", ctypes.c_wchar * 32),
        ("dmLogPixels", wt.WORD),
        ("dmBitsPerPel", wt.DWORD),
        ("dmPelsWidth", wt.DWORD),
        ("dmPelsHeight", wt.DWORD),
        # union：dmDisplayFlags / dmNup
        ("dmDisplayFlags", wt.DWORD),
        ("dmDisplayFrequency", wt.DWORD),
        ("dmICMMethod", wt.DWORD),
        ("dmICMIntent", wt.DWORD),
        ("dmMediaType", wt.DWORD),
        ("dmDitherType", wt.DWORD),
        ("dmReserved1", wt.DWORD),
        ("dmReserved2", wt.DWORD),
        ("dmPanningWidth", wt.DWORD),
        ("dmPanningHeight", wt.DWORD),
    ]


# --- 鼠标回报率 -----------------------------------------------------------
def record_mouse_report():
    """在 WM_INPUT 处理里记录每个鼠标报文时间戳（perf_counter = QPC 同源）。

    只做一次 perf_counter + 一次 deque.append（短锁内），开销可忽略；
    裁剪保持最近 ~2 秒，避免无界增长。
    """
    ts = time.perf_counter()
    with _reports_lock:
        _reports.append(ts)
        while _reports and ts - _reports[0] > _REPORT_WINDOW:
            _reports.popleft()


def mouse_report_stats():
    """返回 (mouseHz, mouseJitterMs)；样本不足或无鼠标活动返回 (0.0, 0.0)。

    mouseHz：最近窗口实测报文频率（浮点，1 位小数）。
    mouseJitterMs：相邻报文间隔标准差（毫秒，浮点；样本不足返回 0）。
    """
    now = time.perf_counter()
    with _reports_lock:
        while _reports and now - _reports[0] > _REPORT_WINDOW:
            _reports.popleft()
        samples = list(_reports)
    n = len(samples)
    if n < 2:
        return 0.0, 0.0
    span = samples[-1] - samples[0]
    if span <= 0.0:
        return 0.0, 0.0
    hz = (n - 1) / span
    if n < 3:
        return round(hz, 1), 0.0
    intervals = [samples[i + 1] - samples[i] for i in range(n - 1)]
    mean = sum(intervals) / (n - 1)
    var = sum((x - mean) ** 2 for x in intervals) / (n - 1)
    jitter_ms = var ** 0.5 * 1000.0
    return round(hz, 1), round(jitter_ms, 2)


# --- 显示器模式 -----------------------------------------------------------
def get_display_mode():
    """返回主显示器当前模式 (refreshHz, width, height)；失败返回 (0, 0, 0)。"""
    global _display_cache, _display_cache_at
    now = time.monotonic()
    if _display_cache is not None and now - _display_cache_at < _DISPLAY_CACHE_TTL:
        return _display_cache
    hz = width = height = 0
    try:
        dm = DEVMODEW()
        dm.dmSize = ctypes.sizeof(DEVMODEW)
        user32.EnumDisplaySettingsW.restype = wt.BOOL
        user32.EnumDisplaySettingsW.argtypes = [
            wt.LPCWSTR, wt.DWORD, ctypes.POINTER(DEVMODEW)]
        if user32.EnumDisplaySettingsW(None, ENUM_CURRENT_SETTINGS,
                                       ctypes.byref(dm)):
            hz = dm.dmDisplayFrequency
            width = dm.dmPelsWidth
            height = dm.dmPelsHeight
    except Exception as exc:  # noqa: BLE001
        debuglog.log("[metrics] 读取显示器模式失败: %s" % exc)
    _display_cache = (hz, width, height)
    _display_cache_at = now
    return _display_cache


# --- 鼠标加速 -------------------------------------------------------------
def get_mouse_accel():
    """读取系统"提高指针精确度"是否开启；失败返回 False。"""
    try:
        arr = (ctypes.c_int * 3)()
        user32.SystemParametersInfoW.restype = wt.BOOL
        user32.SystemParametersInfoW.argtypes = [
            wt.UINT, wt.UINT, ctypes.c_void_p, wt.UINT]
        if not user32.SystemParametersInfoW(SPI_GETMOUSE, 0,
                                            ctypes.byref(arr), 0):
            return False
        return bool(arr[2] != 0)
    except Exception as exc:  # noqa: BLE001
        debuglog.log("[metrics] 读取鼠标加速失败: %s" % exc)
        return False


def set_mouse_accel(enable):
    """设置系统"提高指针精确度"，前两项沿用当前值，设置后回读校验。

    返回回读到的真实状态（bool）。读当前参数失败时抛 OSError（调用方应答 ERR）。
    """
    arr = (ctypes.c_int * 3)()
    user32.SystemParametersInfoW.restype = wt.BOOL
    user32.SystemParametersInfoW.argtypes = [
        wt.UINT, wt.UINT, ctypes.c_void_p, wt.UINT]
    if not user32.SystemParametersInfoW(SPI_GETMOUSE, 0, ctypes.byref(arr), 0):
        raise OSError("读取鼠标参数失败")
    arr[2] = 1 if enable else 0
    user32.SystemParametersInfoW(SPI_SETMOUSE, 0, ctypes.byref(arr),
                                 SPIF_UPDATEINIFILE | SPIF_SENDCHANGE)
    return get_mouse_accel()


# --- 低延迟模式 -----------------------------------------------------------
def apply_low_latency(enabled):
    """应用/恢复低延迟优化，可反复切换。

    enabled=True：关闭电源节流（EcoQoS）+ 优先级 ABOVE_NORMAL + timeBeginPeriod(1)。
    enabled=False：恢复普通优先级 + 恢复节流 + timeEndPeriod(1)（若已提升）。
    返回当前计时器精度是否已提升。
    """
    global _timer_raised
    try:
        class PROCESS_POWER_THROTTLING_STATE(ctypes.Structure):
            _fields_ = [("Version", wt.ULONG), ("ControlMask", wt.ULONG),
                        ("StateMask", wt.ULONG)]

        kernel32.GetCurrentProcess.restype = ctypes.c_void_p
        kernel32.SetProcessInformation.argtypes = [
            ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, wt.DWORD]
        st = PROCESS_POWER_THROTTLING_STATE(
            1, PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            0 if enabled else PROCESS_POWER_THROTTLING_EXECUTION_SPEED)
        kernel32.SetProcessInformation(kernel32.GetCurrentProcess(),
                                       PROCESS_POWER_THROTTLING,
                                       ctypes.byref(st), ctypes.sizeof(st))
    except Exception as exc:  # noqa: BLE001
        debuglog.log("[metrics] 电源节流开关失败: %s" % exc)
    try:
        kernel32.SetPriorityClass.restype = wt.BOOL
        kernel32.SetPriorityClass.argtypes = [ctypes.c_void_p, wt.DWORD]
        kernel32.SetPriorityClass(
            kernel32.GetCurrentProcess(),
            ABOVE_NORMAL_PRIORITY_CLASS if enabled else NORMAL_PRIORITY_CLASS)
    except Exception as exc:  # noqa: BLE001
        debuglog.log("[metrics] 进程优先级设置失败: %s" % exc)
    try:
        winmm = ctypes.WinDLL("winmm")
        if enabled and not _timer_raised:
            winmm.timeBeginPeriod(1)
            _timer_raised = True
        elif not enabled and _timer_raised:
            winmm.timeEndPeriod(1)
            _timer_raised = False
    except Exception as exc:  # noqa: BLE001
        debuglog.log("[metrics] 计时器精度切换失败: %s" % exc)
    return _timer_raised
