using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

        // 预设协议（0.7.0）应答事件：读循环线程触发，参数 = RESP 帧体（"OK" / "ERR|<msg>" / "DATA|<json>"）
        public event EventHandler<string> PresetResponse;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _task;
        private int _failCount;

        // 预设请求/应答（0.7.0）：应答由读循环线程经 PresetResponse 回调，请求间用 _requestLock 互斥（防应答错配）
        private readonly SemaphoreSlim _requestLock = new SemaphoreSlim(1, 1);
        private volatile bool _connected;          // 当前是否已连上管道（未连时写请求直接返回 null）
        private FileStream _stream;                // 当前连接的读写流（读循环维护，写请求经它发送）
        private const int MsgBufSize = 65536;      // 单次读缓冲上限（状态帧 36/68B；RESP 按 Peek 实际长度读取）
        private const int MaxResponseBytes = 2 * 1024 * 1024;   // RESP 应答长度上限（2MB，防异常消息无限读）

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

        /// <summary>
        /// 发送 CMD 请求帧并等待应答（0.7.0 预设协议）。与快照读取共用同一管道：
        /// 写 CMD 帧、应答由读循环线程经 PresetResponse 事件回调（读循环天然串行，无需与 60Hz 读取竞争）。
        /// 管道未连接 / 超时 / 写失败 → 返回 null（调用方降级，不影响主功能）。
        /// </summary>
        /// <param name="cmd">请求命令，如 GET_PRESETS / PUT_PRESETS</param>
        /// <param name="payload">命令载荷（GET_PRESETS 传空串）；非空时拼接到 CMD| 帧后</param>
        /// <param name="timeoutMs">应答超时（默认 2000ms）</param>
        /// <returns>应答帧体：OK / ERR|&lt;msg&gt; / DATA|&lt;json&gt;；失败返回 null</returns>
        public async Task<string> RequestPresetAsync(string cmd, string payload, int timeoutMs = 2000)
        {
            if (!_connected) return null;
            string frame = "CMD|" + cmd + (string.IsNullOrEmpty(payload) ? "" : "|" + payload);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<string> handler = null;
            handler = (s, resp) => tcs.TrySetResult(resp);
            PresetResponse += handler;
            try
            {
                await _requestLock.WaitAsync().ConfigureAwait(false);
                if (!_connected) return null;   // 等锁期间管道可能已断开
                var bytes = Encoding.UTF8.GetBytes(frame);
                try
                {
                    // 消息模式管道下一次 WriteAsync = 一条完整 CMD 消息（阻塞写，全量写入）。
                    // 远端不读（旧版伴生进程）时大消息会阻塞：写也套超时，超时直接放弃（返回 null）。
                    var writeTask = _stream.WriteAsync(bytes, 0, bytes.Length);
                    var writeWinner = await Task.WhenAny(writeTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                    if (writeWinner != writeTask) return null;   // 写超时
                    await writeTask.ConfigureAwait(false);        // 写失败（管道断开等）→ 抛异常 → 返回 null
                }
                catch
                {
                    return null;   // 写失败（管道断开等）
                }
                var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (winner != tcs.Task) return null;   // 超时：伴生进程旧版本不支持预设协议/未响应
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                PresetResponse -= handler;
                _requestLock.Release();
            }
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
                        NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
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
                    using (var stream = new FileStream(new SafeFileHandle(handle, true), FileAccess.ReadWrite))
                    {
                        _stream = stream;
                        _connected = true;
                        var buf = new byte[MsgBufSize];
                        try
                        {
                            while (!ct.IsCancellationRequested)
                            {
                                // 消息模式管道：先 PeekNamedPipe 取当前消息实际长度（lpBytesLeftThisMessage），
                                // 按实际长度一次读完整条消息，避免缓冲区小于消息时的 ERROR_MORE_DATA / 截断问题。
                                uint avail = 0, left = 0;
                                bool peekOk;
                                try { peekOk = NativeMethods.PeekNamedPipe(handle, null, 0, IntPtr.Zero, out avail, out left); }
                                catch { peekOk = false; }
                                if (!peekOk)
                                {
                                    // peek 失败（管道异常）：让 ReadAsync 读到 0 触发重连
                                    int nz = await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                                    if (nz == 0) break;
                                    continue;
                                }
                                if (left == 0)
                                {
                                    // 当前无完整消息（伴生进程推送间隙）：稍候轮询，避免按错误长度读取
                                    await Task.Delay(4, ct).ConfigureAwait(false);
                                    continue;
                                }
                                if (left > (uint)MaxResponseBytes) left = (uint)MaxResponseBytes;   // 应答长度上限保护
                                if (buf.Length < left) buf = new byte[(int)left];
                                int n = await stream.ReadAsync(buf, 0, (int)left, ct).ConfigureAwait(false);
                                if (n == 0) break; // 管道断开，重连

                                if (n >= 4 && buf[0] == (byte)'R' && buf[1] == (byte)'E' &&
                                    buf[2] == (byte)'S' && buf[3] == (byte)'P')
                                {
                                    // 应答帧（0.7.0 预设协议）：RESP|OK / RESP|ERR|<msg> / RESP|DATA|<json>
                                    string body = Encoding.UTF8.GetString(buf, 4, n - 4).TrimEnd('\0', '\r', '\n');
                                    PresetResponse?.Invoke(this, body);
                                }
                                else if (n >= 36 && buf[0] == (byte)'K' && buf[1] == (byte)'D' &&
                                         buf[2] == (byte)'S' && buf[3] == (byte)'P')
                                {
                                    // 状态帧：协议 v2（36 字节）与 v3（68 字节），现有解析逻辑不变
                                    var snap = Parse(buf, n);
                                    Snapshot?.Invoke(this, snap);
                                }
                            }
                        }
                        finally
                        {
                            _stream = null;
                            _connected = false;
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