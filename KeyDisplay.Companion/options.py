"""用户选项持久化：%LOCALAPPDATA%\\KeyDisplay\\options.json（包外，卸载重装不丢）。

字段：
    pushHz        推送频率上限，白名单 60/120/144/165/240/360/480（越界夹到最近合法值）
    followRefresh true 时用实测显示器刷新率替换 pushHz 作为上限（取不到用 240）
    lowLatency    true 保持低延迟优化（关闭电源节流 + ABOVE_NORMAL + timeBeginPeriod(1)），
                  false 恢复普通优先级并 timeEndPeriod(1)，可反复切换
    mouseAccel    true/false 设置系统"提高指针精确度"，null 不管理（不动系统设置）

- load()：读文件；缺失/损坏回退默认并记日志（不做系统副作用，仅缓存到内存）
- apply_startup()：把当前选项应用到系统（lowLatency + mouseAccel 非 null 时）
- update(partial)：校验并应用运行中选项子集，写回 options.json（非法值抛 ValueError）
- 路径可被测试覆盖：options.set_options_path(path) 或环境变量 KEYDISPLAY_OPTIONS_PATH

纯标准库（json + os + threading）+ metrics（Win32 侧效应），无第三方依赖。
"""
import json
import os
import threading

import debuglog
import metrics

DEFAULTS = {"pushHz": 240, "followRefresh": False,
            "lowLatency": True, "mouseAccel": None}

LEGAL_PUSH_HZ = (60, 120, 144, 165, 240, 360, 480)

_path_override = None
_state_lock = threading.Lock()
_current = None  # 当前生效选项（dict，含 mouseAccel 回读真实值）；None=尚未 load


def get_options_path():
    """返回当前生效的 options.json 绝对路径。"""
    if _path_override:
        return _path_override
    env = os.environ.get("KEYDISPLAY_OPTIONS_PATH")
    if env:
        return env
    return os.path.join(os.environ.get("LOCALAPPDATA", ""),
                        "KeyDisplay", "options.json")


def set_options_path(path):
    """测试注入：覆盖 options.json 路径；传 None 恢复默认。"""
    global _path_override
    _path_override = path


def clamp_push_hz(value):
    """把任意数值夹到最近的合法推送频率（白名单）。"""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError("pushHz 必须是数字")
    value = int(value)
    return min(LEGAL_PUSH_HZ, key=lambda v: abs(v - value))


def _field_value(key, value):
    """把单个字段规范化为合法值；非法抛 ValueError。"""
    if key == "pushHz":
        return clamp_push_hz(value)
    if key == "followRefresh":
        if not isinstance(value, bool):
            raise ValueError("followRefresh 必须是布尔值")
        return value
    if key == "lowLatency":
        if not isinstance(value, bool):
            raise ValueError("lowLatency 必须是布尔值")
        return value
    if key == "mouseAccel":
        if value is not None and not isinstance(value, bool):
            raise ValueError("mouseAccel 必须是布尔值或 null")
        return value
    raise ValueError("未知字段 %s" % key)


def _validate_partial(obj, strict):
    """校验选项 dict（可能为子集）。

    strict=True（SET_OPT）：未知字段/非法值抛 ValueError，绝不动任何状态。
    strict=False（load）：跳过未知字段，非法字段忽略并记日志（尽力回退）。
    """
    if not isinstance(obj, dict):
        raise ValueError("options 必须是 JSON 对象")
    cleaned = {}
    for key, value in obj.items():
        try:
            cleaned[key] = _field_value(key, value)
        except ValueError as exc:
            if strict:
                raise
            debuglog.log("[options] 忽略非法字段 %s: %s" % (key, exc))
    return cleaned


def load():
    """读取 options.json，缺失/损坏回退默认并记日志；仅缓存，不做系统副作用。

    返回生效的选项 dict（独立副本）。
    """
    global _current
    path = get_options_path()
    raw = None
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            raw = json.load(f)
    except FileNotFoundError:
        raw = None
    except Exception as exc:  # noqa: BLE001
        debuglog.log("[options] 读取失败，回退默认值: %s" % exc)
        raw = None

    obj = dict(DEFAULTS)
    if raw is not None:
        if not isinstance(raw, dict):
            debuglog.log("[options] 顶层非 JSON 对象，回退默认值")
        else:
            obj.update(_validate_partial(raw, strict=False))
    with _state_lock:
        _current = obj
    return dict(obj)


def save(obj):
    """原子写入 options.json（UTF-8 无 BOM）；失败抛异常，由调用方应答 RESP|ERR。"""
    if not isinstance(obj, dict):
        raise ValueError("options 必须是 JSON 对象")
    path = get_options_path()
    directory = os.path.dirname(path)
    if directory:
        os.makedirs(directory, exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)
    os.replace(tmp, path)


def current():
    """返回当前生效选项（独立副本）。"""
    with _state_lock:
        return dict(_current if _current is not None else DEFAULTS)


def effective_push_hz():
    """计算实际推送上限：followRefresh 用实测刷新率（取不到 240），否则 pushHz。"""
    with _state_lock:
        base = _current if _current is not None else DEFAULTS
        push_hz = base["pushHz"]
        follow = base["followRefresh"]
    if follow:
        hz, _, _ = metrics.get_display_mode()
        return int(hz) if hz and hz > 0 else 240
    return push_hz


def apply_startup():
    """启动时把当前选项应用到系统（lowLatency + mouseAccel 非 null 时）。

    mouseAccel 设置后回读校验，真实结果写回 options.json。
    """
    opts = current()
    metrics.apply_low_latency(bool(opts["lowLatency"]))
    if opts["mouseAccel"] is not None:
        try:
            actual = metrics.set_mouse_accel(bool(opts["mouseAccel"]))
            with _state_lock:
                _current["mouseAccel"] = actual
            save(current())
        except Exception as exc:  # noqa: BLE001
            debuglog.log("[options] 应用 mouseAccel 失败: %s" % exc)


def update(partial):
    """应用运行中选项子集并写回 options.json；返回更新后的完整 options。

    非法 JSON 对象/未知字段/非法值抛 ValueError（由调用方应答 RESP|ERR），
    且不会改动任何内存状态或系统状态。mouseAccel 设置后回读真实结果写回。
    """
    global _current
    cleaned = _validate_partial(partial, strict=True)
    if not cleaned:
        return current()
    with _state_lock:
        base = dict(_current if _current is not None else DEFAULTS)
    new = dict(base)
    new.update(cleaned)

    # 先应用系统副作用，再写盘：系统设置失败（抛异常）则既不写盘也不改 _current
    if "lowLatency" in cleaned:
        metrics.apply_low_latency(bool(cleaned["lowLatency"]))
    if "mouseAccel" in cleaned and cleaned["mouseAccel"] is not None:
        new["mouseAccel"] = metrics.set_mouse_accel(bool(cleaned["mouseAccel"]))

    save(new)
    with _state_lock:
        _current = new
    return dict(new)
