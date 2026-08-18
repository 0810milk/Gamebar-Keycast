"""用户预设持久化：%LOCALAPPDATA%\\KeyDisplay\\presets.json（包外，卸载重装不丢）。

- load()：读文件解析 JSON 返回 dict；文件不存在返回空结构；
          文件损坏（解析失败 / 顶层非对象）→ 改名为 presets.json.bak 后返回空结构，不崩溃
- save(obj)：原子写（先写 presets.json.tmp 再 os.replace），UTF-8 无 BOM（encoding="utf-8"）
- 路径可被测试覆盖：presets.set_presets_path(path) 或环境变量 KEYDISPLAY_PRESETS_PATH

纯标准库（json + os），无第三方依赖。
"""
import json
import os

# 空结构：文件缺失 / 损坏时的默认值
EMPTY_STRUCT = {"version": 1, "themePresets": [], "layoutPresets": []}

# 测试注入用路径覆盖（None = 使用默认/环境变量）
_path_override = None


def get_presets_path():
    """返回当前生效的 presets.json 绝对路径。"""
    if _path_override:
        return _path_override
    env = os.environ.get("KEYDISPLAY_PRESETS_PATH")
    if env:
        return env
    return os.path.join(os.environ.get("LOCALAPPDATA", ""),
                        "KeyDisplay", "presets.json")


def set_presets_path(path):
    """测试注入：覆盖 presets.json 路径；传 None 恢复默认。"""
    global _path_override
    _path_override = path


def _empty():
    """返回空结构的新副本（避免调用方篡改共享常量）。"""
    return {"version": EMPTY_STRUCT["version"],
            "themePresets": list(EMPTY_STRUCT["themePresets"]),
            "layoutPresets": list(EMPTY_STRUCT["layoutPresets"])}


def _backup_bad(path):
    """把损坏文件改名为 presets.json.bak（尽力而为，备份失败也不抛）。"""
    try:
        os.replace(path, path + ".bak")
    except Exception:
        pass


def load():
    """读取 presets.json 并解析为 dict。

    - 文件不存在 → 空结构 {"version": 1, "themePresets": [], "layoutPresets": []}
    - 文件损坏 / 顶层不是 JSON 对象 → 改名为 .bak 后返回空结构（进程不崩溃）
    """
    path = get_presets_path()
    try:
        with open(path, "r", encoding="utf-8") as f:
            obj = json.load(f)
        if not isinstance(obj, dict):
            raise ValueError("presets.json 顶层必须是 JSON 对象")
        return obj
    except FileNotFoundError:
        # 文件不存在：直接返回空结构，不产生 .bak
        return _empty()
    except Exception:
        # 损坏 / 权限等任何异常：备份坏文件后降级为空结构
        _backup_bad(path)
        return _empty()


def save(obj):
    """原子写入 presets.json（UTF-8 无 BOM）；失败抛异常，由调用方应答 RESP|ERR。

    先写同目录 presets.json.tmp，再 os.replace 原子替换（不会出现半截文件）。
    """
    if not isinstance(obj, dict):
        raise ValueError("presets 数据必须是 JSON 对象")
    path = get_presets_path()
    directory = os.path.dirname(path)
    if directory:
        os.makedirs(directory, exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)
    os.replace(tmp, path)