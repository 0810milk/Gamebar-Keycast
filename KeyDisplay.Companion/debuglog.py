"""伴生进程调试日志：原生输入与坐标链路监控（写入 pipe-debug.log）。

hooks.py / pipe_server.py 共用；日志文件位于可执行文件同目录，
即使进程由 Start-Process 启动（无 stdout 重定向）也能可靠落盘。
"""
import os
import sys
import threading
import time

_lock = threading.Lock()
_handle = None


def _path():
    if getattr(sys, "frozen", False):
        base = os.path.dirname(sys.executable)
    else:
        base = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(base, "pipe-debug.log")


def log(msg):
    global _handle
    try:
        with _lock:
            if _handle is None:
                _handle = open(_path(), "a", encoding="utf-8")
            _handle.write("%s %s\n" % (time.strftime("%H:%M:%S"), msg))
            _handle.flush()
    except Exception:
        pass
