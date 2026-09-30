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

import metrics
import options
from hooks import start_hooks, reconcile
from pipe_server import PipeServer, StopFlag
from state import InputState

MUTEX_NAME = "Local\\KeyDisplayCompanionMutex"
ERROR_ALREADY_EXISTS = 183


def _acquire_mutex():
    """尝试获取单实例互斥体；返回 None 表示已有实例在运行（正常退出）。"""
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateMutexW.restype = ctypes.c_void_p
    kernel32.CreateMutexW.argtypes = [ctypes.c_void_p, wt.BOOL, wt.LPCWSTR]
    handle = kernel32.CreateMutexW(None, False, MUTEX_NAME)
    if not handle:
        # 0.8.3：句柄创建失败（权限/资源）≠ 已有实例——报真实错误而不是静默当作单实例退出
        raise OSError("创建互斥体失败，错误码 %d" % ctypes.get_last_error())
    if ctypes.get_last_error() == ERROR_ALREADY_EXISTS:
        kernel32.CloseHandle(handle)
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
    # 低延迟优化/推送频率/鼠标加速等选项：由 options.json 持久化（默认 lowLatency=true 保持现状）
    options.load()
    options.apply_startup()
    # 0.8.3：钩子启动成功/失败用事件一次性通知（替代固定 sleep(0.15) 探测——慢速机器上
    # start_hooks 可能在 0.15s 后才失败，进程会带着"无钩子"状态继续运行）
    hooks_ready = threading.Event()
    hook_error = []

    def _hooks_entry():
        try:
            start_hooks(state, stop, hooks_ready)
        except Exception as exc:  # noqa: BLE001
            hook_error.append(exc)
        finally:
            hooks_ready.set()

    hooks_thread = threading.Thread(target=_hooks_entry, daemon=True)
    hooks_thread.start()
    if not hooks_ready.wait(timeout=5.0):
        raise TimeoutError("钩子安装超时")
    if hook_error:
        raise hook_error[0]

    # 1.2：XInput 手柄轮询（只读、120Hz）。DLL 不可用/没有手柄时内部空转，
    # 绝不影响键鼠采集；手柄状态由泵线程按需注入快照（仅对握手要 92 字节的客户端）。
    gamepad_poller = None
    try:
        import gamepad as _gpmod
        gamepad_poller = _gpmod.open_poller(hz=120.0)
        print("[gamepad] xinput available=%s" % gamepad_poller.available,
              file=sys.stderr)
    except Exception as exc:  # noqa: BLE001
        print("[gamepad] init failed: %s" % exc, file=sys.stderr)

    server = PipeServer(state, stop, pfn, fps=options.effective_push_hz())
    if gamepad_poller is not None:
        server.attach_gamepad(gamepad_poller)
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
        if gamepad_poller is not None:
            gamepad_poller.stop()
        metrics.apply_low_latency(False)  # 退出时恢复计时器精度/普通优先级

    server_thread.join(timeout=2)
    hooks_thread.join(timeout=2)
    return 0


if __name__ == "__main__":
    sys.exit(main())