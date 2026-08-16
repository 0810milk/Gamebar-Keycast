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
    /// 一份 36 字节输入快照。
    /// </summary>
    public sealed class InputSnapshot
    {
        public ushort Keys;  // 12 位：Q W E R A S D F Shift Ctrl Alt Space
        public byte Mouse;   // 5 位：L R M X1 X2
        public int MouseX;
        public int MouseY;
        public int VsX;      // 虚拟屏幕原点/尺寸，用于把坐标映射到鼠标垫
        public int VsY;
        public int VsW;
        public int VsH;
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
                    using (var stream = new FileStream(new SafeFileHandle(handle, true), FileAccess.Read))
                    {
                        var buf = new byte[36];
                        while (!ct.IsCancellationRequested)
                        {
                            int offset = 0;
                            while (offset < buf.Length)
                            {
                                int n = await stream.ReadAsync(buf, offset, buf.Length - offset, ct).ConfigureAwait(false);
                                if (n == 0) break;
                                offset += n;
                            }
                            if (offset < buf.Length) break; // 管道断开，重连

                            if (buf[0] == (byte)'K' && buf[1] == (byte)'D' &&
                                buf[2] == (byte)'S' && buf[3] == (byte)'P')
                            {
                                var snap = Parse(buf);
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

        private static InputSnapshot Parse(byte[] b)
        {
            return new InputSnapshot
            {
                Keys = (ushort)(b[5] | (b[6] << 8)),
                Mouse = b[7],
                MouseX = BitConverter.ToInt32(b, 8),
                MouseY = BitConverter.ToInt32(b, 12),
                VsX = BitConverter.ToInt32(b, 16),
                VsY = BitConverter.ToInt32(b, 20),
                VsW = BitConverter.ToInt32(b, 24),
                VsH = BitConverter.ToInt32(b, 28)
            };
        }
    }
}