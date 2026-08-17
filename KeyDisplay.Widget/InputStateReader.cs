using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Windows.Storage;

namespace KeyDisplay
{
    /// <summary>
    /// 一份输入快照。协议 v3 为 68 字节（尾部 32 字节 = 256 位 VK 位图）；
    /// 兼容 v2 的 36 字节旧快照（ExtraKeys 为 null，自定义键降级为仅显示）。
    /// </summary>
    public sealed class InputSnapshot
    {
        public ushort Keys;  // 12 位：Q W E R A S D F Shift Ctrl Alt Space
        public byte Mouse;   // 5 位：L R M X1 X2
        public int MouseX;
        public int MouseY;
        public int VsX;      // 虚拟屏幕原点/尺寸，用于确定鼠标垫的纵横比与点映射
        public int VsY;
        public int VsW;
        public int VsH;
        public uint Seq;     // 帧序号，用于判断数据是否变化（未变化时跳过重绘）
        public byte[] ExtraKeys;   // 协议 v3：32 字节 = 256 位 VK 位图，按虚拟键码直接索引；
                                   // v2 旧快照为 null（自定义键降级为仅显示）
    }

    /// <summary>
    /// 通过命名管道 \\.\pipe\KeyDisplayState 读取伴生进程推送的输入快照。
    /// UWP 沙箱内 System.IO.Pipes 不可用，改用 CreateFileW + FileStream 读取。
    /// </summary>
    public sealed class InputStateReader : IDisposable
    {
        public event EventHandler<InputSnapshot> Snapshot;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _task;
        private int _failCount;

        public void Start()
        {
            if (_task != null) return;
            Log("reader start");
            _task = Task.Run(() => RunLoopAsync(_cts.Token));
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _task?.Wait(500); }
            catch
            {
            }
            _cts.Dispose();
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                IntPtr handle = IntPtr.Zero;
                try
                {
                    handle = NativeMethods.CreateFileW(
                        @"\\.\pipe\KeyDisplayState",
                        NativeMethods.GENERIC_READ,
                        0,
                        IntPtr.Zero,
                        NativeMethods.OPEN_EXISTING,
                        NativeMethods.FILE_FLAG_OVERLAPPED,
                        IntPtr.Zero);

                    if (handle.ToInt64() == NativeMethods.INVALID_HANDLE_VALUE)
                    {
                        int err = Marshal.GetLastWin32Error();
                        _failCount++;
                        if (_failCount <= 3 || _failCount % 30 == 0)
                        {
                            Log("CreateFileW failed err=" + err);
                        }
                        // 伴生进程尚未启动，稍后重试
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                        continue;
                    }
                    _failCount = 0;

                    Log("connected");
                    // 与伴生进程的消息模式一致（byte 模式读消息管道在部分场景会导致
                    // 读返回 0 提前断开，表现为连接抖动/收不到数据）
                    try
                    {
                        uint mode = NativeMethods.PIPE_READMODE_MESSAGE;
                        NativeMethods.SetNamedPipeHandleState(handle, ref mode, IntPtr.Zero, IntPtr.Zero);
                    }
                    catch
                    {
                    }
                    using (var stream = new FileStream(new SafeFileHandle(handle, true), FileAccess.Read))
                    {
                        var buf = new byte[68];
                        while (!ct.IsCancellationRequested)
                        {
                            // 消息模式下一次 ReadAsync 即一条完整消息（缓冲区 >= 消息长度时）。
                            // 兼容协议 v2（36 字节）与 v3（68 字节）：按实际读到的长度分派，
                            // 非 KDSP 开头或长度 < 36 一律丢弃（防错位解析）。
                            int n = await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                            if (n == 0) break; // 管道断开，重连

                            if (n >= 36 && buf[0] == (byte)'K' && buf[1] == (byte)'D' &&
                                buf[2] == (byte)'S' && buf[3] == (byte)'P')
                            {
                                var snap = Parse(buf, n);
                                Snapshot?.Invoke(this, snap);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                }

                try { await Task.Delay(500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private static void Log(string msg)
        {
            try
            {
                var dir = ApplicationData.Current.LocalFolder.Path;
                File.AppendAllText(Path.Combine(dir, "diag.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n");
            }
            catch
            {
            }
        }

        private static InputSnapshot Parse(byte[] b, int len)
        {
            var snap = new InputSnapshot
            {
                Keys = (ushort)(b[5] | (b[6] << 8)),
                Mouse = b[7],
                MouseX = BitConverter.ToInt32(b, 8),
                MouseY = BitConverter.ToInt32(b, 12),
                VsX = BitConverter.ToInt32(b, 16),
                VsY = BitConverter.ToInt32(b, 20),
                VsW = BitConverter.ToInt32(b, 24),
                VsH = BitConverter.ToInt32(b, 28),
                Seq = BitConverter.ToUInt32(b, 32)
            };
            // 协议 v3：b[36..68] = 32 字节 256 位 VK 位图（小端，位 = (extra[vk>>3]>>(vk&7))&1）。
            // 长度 < 68（v2 旧快照）时 ExtraKeys 保持 null，调用方降级为仅显示，不崩溃。
            if (len >= 68)
            {
                snap.ExtraKeys = new byte[32];
                Buffer.BlockCopy(b, 36, snap.ExtraKeys, 0, 32);
            }
            return snap;
        }
    }
}