"""KeyDisplay 伴生进程主程序。

职责：安装全局键盘/鼠标钩子，收集状态并通过命名管道
\\\\.\\pipe\\KeyDisplayState 按 60Hz 推送给 Game Bar 小组件。

用法：
    python companion.py [--package-family <PFN>] [--config <json>]
        [--duration <秒>]   # 运行指定秒数后退出，用于测试

配置项（config.json）：
    { "packageFamilyName": "KeyDisplay.Widget_xxxxxxxxxxxx" }
    用于在管道安全描述符中放行 UWP 包 SID。
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import json
import os
import sys
import threading
import time

from hooks import start_hooks, reconcile
from pipe_server import PipeServer, StopFlag
from state import InputState

MUTEX_NAME = "Local\\KeyDisplayCompanionMutex"
ERROR_ALREADY_EXISTS = 183


def _acquire_mutex():
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateMutexW.restype = ctypes.c_void_p
    kernel32.CreateMutexW.argtypes = [ctypes.c_void_p, wt.BOOL, wt.LPCWSTR]
    handle = kernel32.CreateMutexW(None, False, MUTEX_NAME)
    if ctypes.get_last_error() == ERROR_ALREADY_EXISTS:
        return None
    return handle


def _load_config(explicit_path=None):
    if explicit_path and os.path.isfile(explicit_path):
        with open(explicit_path, "r", encoding="utf-8") as f:
            return json.load(f)

    candidates = []
    if getattr(sys, "frozen", False):
        base = os.path.dirname(sys.executable)
    else:
        base = os.path.dirname(os.path.abspath(__file__))
    candidates.append(os.path.join(base, "config.json"))
    program_data = os.environ.get("PROGRAMDATA", "")
    if program_data:
        candidates.append(os.path.join(program_data, "KeyDisplay", "config.json"))

    for path in candidates:
        if os.path.isfile(path):
            try:
                with open(path, "r", encoding="utf-8") as f:
                    return json.load(f)
            except Exception:
                pass
    return {}


def main():
    parser = argparse.ArgumentParser(description="KeyDisplay companion process")
    parser.add_argument("--package-family", default=None,
                        help="UWP 包 Family Name，用于管道 DACL 放行")
    parser.add_argument("--config", default=None, help="指定 config.json 路径")
    parser.add_argument("--duration", type=float, default=None,
                        help="运行指定秒数后退出（测试用）")
    parser.add_argument("uri", nargs="*",
                        help="忽略协议启动 URI（如 keydisplay://start）")
    args = parser.parse_args()

    config = _load_config(args.config)
    pfn = args.package_family or config.get("packageFamilyName")

    mutex = _acquire_mutex()
    if mutex is None:
        print("KeyDisplayCompanion：已有实例在运行", file=sys.stderr)
        return 0

    state = InputState()
    stop = StopFlag()
    hook_error = []

    def _hooks_entry():
        try:
            start_hooks(state, stop)
        except Exception as exc:  # noqa: BLE001
            hook_error.append(exc)

    hooks_thread = threading.Thread(target=_hooks_entry, daemon=True)
    hooks_thread.start()
    time.sleep(0.15)
    if hook_error:
        raise hook_error[0]

    server = PipeServer(state, stop, pfn)
    server_thread = threading.Thread(target=server.run, daemon=True)
    server_thread.start()

    print("KeyDisplayCompanion 已启动，管道：\\\\.\\pipe\\KeyDisplayState",
          file=sys.stderr)

    done = threading.Event()
    try:
        done.wait(args.duration if args.duration else None)
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()

    server_thread.join(timeout=2)
    hooks_thread.join(timeout=2)
    return 0


if __name__ == "__main__":
    sys.exit(main())