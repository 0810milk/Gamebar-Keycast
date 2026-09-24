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


def _optimize_process():
    """0.8.4 底层优化：消除包身份进程被电源节流/低优先级调度导致的唤醒抖动。

    背景：伴生进程改由包内完整信任进程启动后，Windows 可能对带包身份的进程启用
    EcoQoS（效率模式，降低执行速度），使 4ms 级 sleep 的唤醒时间不均匀——平均帧率
    看似正常，但单帧延迟出现尖峰，体感即"光标延迟增加、按键反馈不及时"。
    三重加固：关闭电源节流 + 提高进程优先级 + 提升系统计时器精度。
    返回是否成功提升了计时器精度（退出时需对应 timeEndPeriod）。
    """
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    try:
        class PROCESS_POWER_THROTTLING_STATE(ctypes.Structure):
            _fields_ = [("Version", wt.ULONG), ("ControlMask", wt.ULONG),
                        ("StateMask", wt.ULONG)]

        kernel32.SetProcessInformation.argtypes = [
            ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, wt.DWORD]
        # ProcessPowerThrottling = 4；EXECUTION_SPEED = 0x1；StateMask=0 → 关闭该节流
        st = PROCESS_POWER_THROTTLING_STATE(1, 0x1, 0)
        kernel32.SetProcessInformation(kernel32.GetCurrentProcess(), 4,
                                       ctypes.byref(st), ctypes.sizeof(st))
    except Exception:
        pass
    try:
        # ABOVE_NORMAL_PRIORITY_CLASS (0x8000)：保证推送线程及时被调度
        kernel32.SetPriorityClass.argtypes = [ctypes.c_void_p, wt.DWORD]
        kernel32.SetPriorityClass(kernel32.GetCurrentProcess(), 0x00008000)
    except Exception:
        pass
    try:
        # 计时器精度 1ms：默认 15.6ms 粒度下 240Hz 的 sleep(4.17ms) 会被取整到 ~15.6ms
        # （实际只有 ~64Hz 且抖动明显）；不能依赖游戏等前台程序替我们提升
        ctypes.WinDLL("winmm").timeBeginPeriod(1)
        return True
    except Exception:
        return False


def _restore_timer(raised):
    """进程退出时恢复计时器精度（避免长期占用高精度计时器影响系统耗电）。"""
    if raised:
        try:
            ctypes.WinDLL("winmm").timeEndPeriod(1)
        except Exception:
            pass


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
    # 0.8.4 底层优化：关闭电源节流（EcoQoS）+ 提升优先级 + 计时器精度 1ms
    timer_raised = _optimize_process()
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

    server = PipeServer(state, stop, pfn, fps=int(config.get("fps", 240)))
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
        _restore_timer(timer_raised)

    server_thread.join(timeout=2)
    hooks_thread.join(timeout=2)
    return 0


if __name__ == "__main__":
    sys.exit(main())