"""测试客户端：连接命名管道，读取并打印状态快照。

用法：先启动 companion.py，再运行 python test_client.py
"""
import ctypes
import ctypes.wintypes as wt
import time

from state import KEY_ORDER, MOUSE_ORDER, parse_snapshot, SNAPSHOT_SIZE

GENERIC_READ = 0x80000000
OPEN_EXISTING = 3
FILE_FLAG_OVERLAPPED = 0x40000000
ERROR_PIPE_BUSY = 231
PIPE_READMODE_MESSAGE = 0x2
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
kernel32.CreateFileW.restype = ctypes.c_void_p


def connect(path):
    while True:
        handle = kernel32.CreateFileW(
            path, GENERIC_READ, 0, None, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED, None)
        if handle != INVALID_HANDLE_VALUE:
            mode = wt.DWORD(PIPE_READMODE_MESSAGE)
            kernel32.SetNamedPipeHandleState(handle, ctypes.byref(mode),
                                             None, None)
            return handle
        err = ctypes.get_last_error()
        if err == ERROR_PIPE_BUSY:
            kernel32.WaitNamedPipeW(path, 5000)
            continue
        raise OSError("无法连接管道，错误码 %d" % err)


def main():
    handle = connect(r"\\.\pipe\KeyDisplayState")
    print("已连接管道，开始读取状态（Ctrl+C 退出）...")
    buf = (ctypes.c_char * SNAPSHOT_SIZE)()
    read = wt.DWORD()
    last = None
    try:
        while True:
            kernel32.ReadFile(handle, buf, SNAPSHOT_SIZE,
                              ctypes.byref(read), None)
            if read.value == SNAPSHOT_SIZE:
                snap = parse_snapshot(buf.raw)
                if snap:
                    keys = [k for k in KEY_ORDER
                            if snap["keys"] & (1 << KEY_ORDER.index(k))]
                    mouse = [m for m in MOUSE_ORDER
                             if snap["mouse"] & (1 << MOUSE_ORDER.index(m))]
                    line = ("seq=%-6d pos=(%-6d,%-6d) keys=[%s] mouse=[%s]"
                            % (snap["seq"], snap["mx"], snap["my"],
                               ",".join(keys) or "-", ",".join(mouse) or "-"))
                    if line != last:
                        print(line)
                        last = line
            time.sleep(1.0 / 30.0)
    except KeyboardInterrupt:
        pass
    finally:
        kernel32.CloseHandle(handle)
    print("已退出")


if __name__ == "__main__":
    main()