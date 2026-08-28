"""伴生进程调试日志：原生输入与坐标链路监控（写入 %LOCALAPPDATA%\\KeyDisplay\\pipe-debug.log）。

hooks.py / pipe_server.py 共用；日志文件位于用户数据目录（非 exe 同目录）——
0.8.3 修复：以普通权限运行（开机自启/协议拉起）时 Program Files 目录不可写，
日志打开失败会静默丢失；LOCALAPPDATA 普通权限可写，与 presets.json 同目录。
"""
import os
import threading
import time

_lock = threading.Lock()
_handle = None

# 0.8.3：日志轮转上限——常驻开机自启长期运行，防止 pipe-debug.log 无界增长
_MAX_BYTES = 1 * 1024 * 1024


def _path():
    base = os.environ.get("LOCALAPPDATA", "")
    return os.path.join(base, "KeyDisplay", "pipe-debug.log")


def log(msg):
    global _handle
    try:
        with _lock:
            if _handle is None:
                _handle = open(_path(), "a", encoding="utf-8")
            # 超过上限：轮转到 pipe-debug.log.1（最多留 2 份，旧份删除）
            if _handle.tell() > _MAX_BYTES:
                _handle.close()
                _handle = None
                try:
                    if os.path.exists(_path() + ".1"):
                        os.remove(_path() + ".1")
                    os.replace(_path(), _path() + ".1")
                except Exception:
                    pass
                _handle = open(_path(), "a", encoding="utf-8")
            _handle.write("%s %s\n" % (time.strftime("%H:%M:%S"), msg))
            _handle.flush()
    except Exception:
        pass
