// KeyDisplay 输入接收器（原生采集层，P1 骨架）
//
// 设计要点（与讨论定稿一致）：
//   · 只用 Raw Input（RIDEV_INPUTSINK）接收键盘/鼠标：事件级、无钩子、
//     没有"因为慢而被系统静默摘除"这种失效模式，因此不需要任何自检/看门狗。
//   · GetAsyncKeyState 高速轮询是**常开的冗余数据源**，不是自检：
//     两条源按位 OR，谁异常都不影响结果，所以不需要判断谁死了。
//   · 最小保持锁存：按下事件至少保持 N 毫秒（键/鼠标键 60ms、滚轮 150ms），
//     保证 240Hz 采样下任何捕获到的事件都必然可见。
//   · 无界面：编译为 winexe（无控制台），RawInput 用**消息专用窗口**(HWND_MESSAGE)，
//     不显示、不进任务栏、不进 Alt+Tab；日志只写文件。
//   · 单实例：命名互斥体；重复启动直接退出。
//   · 协议与现有一致：76 字节 v4 帧；收到 CMD|PROTO|92 后该连接改发 92 字节
//     （前 76 字节逐字节不变 + 16 字节手柄块）。支持 CMD|VERSION 自报版本与权限。
//
// 用法：
//   KeyDisplayInput.exe [--pipe=名] [--hz=240] [--acl=包族名] [--latch-ms=60]
//   默认管道名 KeyDisplayState（与小组件现用一致）；测试时建议用别的名字避免互相干扰。

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class Program
{
    internal const string Version = "1.0.0-p1";
    private const int VK_COUNT = 256;
    private const int MAX_CLIENTS = 8;

    // ---------------- 配置 ----------------
    internal static string PipeName = "KeyDisplayState";
    internal static int PushHz = 240;
    // 生产默认：小组件所在 UWP 包的族名。管道 ACL 必须放行它，否则 Game Bar 里连不上。
    internal static string AclFamilyName = "KeyDisplay.Widget_hdjf4fqmxxv8g";
    internal static int LatchMs = 60;
    internal static int WheelLatchMs = 150;
    internal static int PollSweepPerTick = 64;     // 每 tick 轮询 64 个 VK → 4 tick 扫完 256
    internal static string LogPath;

    // ---------------- 全局状态 ----------------
    private static readonly object StateLock = new object();
    private static readonly byte[] ExtraKeys = new byte[32];          // 256 位 VK 位图（权威）
    private static readonly bool[] Down = new bool[VK_COUNT];         // 事件源按下状态
    private static readonly int[] LatchUntil = new int[VK_COUNT];     // 锁存到期（NowMs()）
    private static readonly bool[] PollDown = new bool[VK_COUNT];     // 轮询源按下状态
    private static int _pollCursor;
    private static ushort _keysMask;                                  // 12 内置键掩码
    private static byte _mouseMask;
    private static readonly int[] MouseLatchUntil = new int[8];
    private static int _mouseX, _mouseY, _vsX, _vsY, _vsW = 1920, _vsH = 1080;
    private static uint _seq;

    // 手柄
    private static readonly byte[] Gamepad = new byte[16];            // 92 字节帧的尾块

    // 统计（只用于日志/自报，不参与健康判定）
    private static long _rawKeyEvents, _rawMouseEvents, _frames;

    // 高精度单调毫秒。NowMs() 分辨率是系统计时器节拍（默认 15.6ms），
    // 用它做节拍会把推送压到 ~64Hz、锁存也不准 —— 这是"鼠标一顿一顿"的真正原因。
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static int NowMs() { return unchecked((int)Clock.ElapsedMilliseconds); }

    private static IntPtr _hwnd = IntPtr.Zero;
    private static readonly WndProcDelegate WndProcRef = WndProc;     // 防 GC

    // ---------------- P/Invoke：基础 ----------------
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public IntPtr lpszMenuName, lpszClassName, hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateMutexW(IntPtr attr, bool initialOwner, string name);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    // ---------------- P/Invoke：Raw Input ----------------
    private const uint WM_INPUT = 0x00FF;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_QUIT = 0x0012;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEKEYBOARD = 1, RIM_TYPEMOUSE = 0;
    private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey; public uint Message; public uint ExtraInformation; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWMOUSE
    {
        public ushort usFlags, usPad;
        public ushort usButtonFlags, usButtonData;
        public uint ulRawButtons;
        public int lLastX, lLastY;
        public uint ulExtraInformation;
    }

    private const ushort RI_KEY_BREAK = 0x01;
    private const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001, RI_MOUSE_LEFT_BUTTON_UP = 0x0002;
    private const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004, RI_MOUSE_RIGHT_BUTTON_UP = 0x0008;
    private const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010, RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020;
    private const ushort RI_MOUSE_BUTTON_4_DOWN = 0x0040, RI_MOUSE_BUTTON_4_UP = 0x0080;
    private const ushort RI_MOUSE_BUTTON_5_DOWN = 0x0100, RI_MOUSE_BUTTON_5_UP = 0x0200;
    private const ushort RI_MOUSE_WHEEL = 0x0400;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint num, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    // ---------------- P/Invoke：命名管道 ----------------
    private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
    private const uint PIPE_TYPE_MESSAGE = 0x00000004;
    private const uint PIPE_READMODE_MESSAGE = 0x00000002;
    private const uint PIPE_WAIT = 0x00000000;
    private const uint PIPE_UNLIMITED_INSTANCES = 255;
    private const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
    private static bool _firstInstance = true;   // 只有第一个实例能带 FIRST_PIPE_INSTANCE   // 首实例独占：名字被别的服务占用时明确失败
    private const uint PIPE_NOWAIT = 0x00000001;
    private const int ERROR_PIPE_CONNECTED = 535;
    private const int ERROR_BROKEN_PIPE = 109;
    private const int ERROR_NO_DATA = 232;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public uint nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outSize, uint inSize, uint timeout, ref SECURITY_ATTRIBUTES sec);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ConnectNamedPipe(IntPtr h, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DisconnectNamedPipe(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(IntPtr h, IntPtr buf, uint size, out uint read, out uint avail, out uint left);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr h, byte[] buf, uint toRead, out uint read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr h, byte[] buf, uint toWrite, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetNamedPipeHandleState(IntPtr h, ref uint mode, IntPtr maxCollectionCount, IntPtr collectTimeout);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint rev, out IntPtr sd, out uint size);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr str);
    [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

    // ---------------- P/Invoke：XInput ----------------
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons; public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION { public byte BatteryType, BatteryLevel; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_CAPABILITIES { public byte Type, SubType; public ushort Flags; public XINPUT_GAMEPAD Gamepad; public uint Vibration; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint idx, out XINPUT_STATE state);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern uint XInputGetBatteryInformation(uint idx, byte type, out XINPUT_BATTERY_INFORMATION info);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetCapabilities")]
    private static extern uint XInputGetCapabilities(uint idx, uint flags, out XINPUT_CAPABILITIES caps);

    // ---------------- 日志 ----------------
    internal static void Log(string msg)
    {
        try
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + msg + Environment.NewLine;
            File.AppendAllText(LogPath, line, Encoding.UTF8);
        }
        catch { }
    }

    // ---------------- 键盘：把事件与轮询合并进位图 ----------------
    private static void ApplyKey(int vk, bool isDown, bool fromEvent)
    {
        if (vk < 0 || vk >= VK_COUNT) return;
        int now = NowMs();
        lock (StateLock)
        {
            if (fromEvent)
            {
                Down[vk] = isDown;
                if (isDown) LatchUntil[vk] = now + LatchMs;
            }
            else
            {
                PollDown[vk] = isDown;
            }
            bool effective = Down[vk] || PollDown[vk] || (LatchUntil[vk] - now) > 0;
            int idx = vk >> 3, bit = 1 << (vk & 7);
            if (effective) ExtraKeys[idx] |= (byte)bit; else ExtraKeys[idx] &= (byte)~bit;
        }
    }

    // 12 个内置键（与协议 keys 位序一致：Q W E R A S D F Shift Ctrl Alt Space）
    private static readonly int[] BuiltinVks = { 0x51, 0x57, 0x45, 0x52, 0x41, 0x53, 0x44, 0x46, 0x10, 0x11, 0x12, 0x20 };

    private static void UpdateBuiltinMask()
    {
        ushort mask = 0;
        for (int i = 0; i < BuiltinVks.Length; i++)
        {
            int vk = BuiltinVks[i];
            int now = NowMs();
            bool down;
            lock (StateLock) { down = Down[vk] || PollDown[vk] || (LatchUntil[vk] - now) > 0; }
            if (vk == 0x10) down = down || IsAnyDown(0xA0, 0xA1);        // 左右 Shift
            else if (vk == 0x11) down = down || IsAnyDown(0xA2, 0xA3);   // 左右 Ctrl
            else if (vk == 0x12) down = down || IsAnyDown(0xA4, 0xA5);   // 左右 Alt
            if (down) mask |= (ushort)(1 << i);
        }
        lock (StateLock) { _keysMask = mask; }
    }

    private static bool IsAnyDown(int a, int b)
    {
        int now = NowMs();
        lock (StateLock)
        {
            return Down[a] || PollDown[a] || (LatchUntil[a] - now) > 0
                || Down[b] || PollDown[b] || (LatchUntil[b] - now) > 0;
        }
    }

    private static void SetMouseBit(int bit, bool down)
    {
        int now = NowMs();
        lock (StateLock)
        {
            if (down)
            {
                _mouseMask |= (byte)(1 << bit);
                MouseLatchUntil[bit] = now + LatchMs;
            }
            else if (MouseLatchUntil[bit] - now <= 0)
            {
                _mouseMask &= (byte)~(1 << bit);
            }
            else
            {
                MouseLatchUntil[bit] = now + 1;   // 锁存未到期：等它自然过期
            }
        }
    }

    private static void ExpireMouseLatch()
    {
        int now = NowMs();
        lock (StateLock)
        {
            for (int bit = 0; bit < 6; bit++)
                if ((_mouseMask & (1 << bit)) != 0 && MouseLatchUntil[bit] - now <= 0) { /* 事件未按下则由轮询决定 */ }
        }
    }

    // ---------------- 消息窗口 ----------------
    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            try { HandleRawInput(lParam); } catch (Exception ex) { Log("WM_INPUT 异常: " + ex.Message); }
            return IntPtr.Zero;
        }
        if (msg == WM_CLOSE) { PostMessageW(hWnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero); return IntPtr.Zero; }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static byte[] _rawBuffer = new byte[256];

    private static void HandleRawInput(IntPtr hRawInput)
    {
        uint size = (uint)_rawBuffer.Length;
        uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
        uint got = GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size > _rawBuffer.Length) _rawBuffer = new byte[size];
        IntPtr pinned = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, pinned, ref size, headerSize) != size) return;
            RAWINPUTHEADER header = (RAWINPUTHEADER)Marshal.PtrToStructure(pinned, typeof(RAWINPUTHEADER));
            IntPtr body = new IntPtr(pinned.ToInt64() + headerSize);
            if (header.dwType == RIM_TYPEKEYBOARD)
            {
                RAWKEYBOARD kb = (RAWKEYBOARD)Marshal.PtrToStructure(body, typeof(RAWKEYBOARD));
                bool isDown = (kb.Flags & RI_KEY_BREAK) == 0;
                ApplyKey(kb.VKey, isDown, true);
                _rawKeyEvents++;
            }
            else if (header.dwType == RIM_TYPEMOUSE)
            {
                RAWMOUSE ms = (RAWMOUSE)Marshal.PtrToStructure(body, typeof(RAWMOUSE));
                ushort f = ms.usButtonFlags;
                if ((f & RI_MOUSE_LEFT_BUTTON_DOWN) != 0) SetMouseBit(0, true);
                if ((f & RI_MOUSE_LEFT_BUTTON_UP) != 0) SetMouseBit(0, false);
                if ((f & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0) SetMouseBit(1, true);
                if ((f & RI_MOUSE_RIGHT_BUTTON_UP) != 0) SetMouseBit(1, false);
                if ((f & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0) SetMouseBit(2, true);
                if ((f & RI_MOUSE_MIDDLE_BUTTON_UP) != 0) SetMouseBit(2, false);
                if ((f & RI_MOUSE_BUTTON_4_DOWN) != 0) SetMouseBit(3, true);
                if ((f & RI_MOUSE_BUTTON_4_UP) != 0) SetMouseBit(3, false);
                if ((f & RI_MOUSE_BUTTON_5_DOWN) != 0) SetMouseBit(4, true);
                if ((f & RI_MOUSE_BUTTON_5_UP) != 0) SetMouseBit(4, false);
                if ((f & RI_MOUSE_WHEEL) != 0)
                {
                    // 合成 VK：0x07 滚轮上 / 0x08 滚轮下（与现有协议一致），带锁存
                    short delta = (short)ms.usButtonData;
                    int vk = delta > 0 ? 0x07 : 0x08;
                    int now = NowMs();
                    lock (StateLock)
                    {
                        Down[vk] = true; LatchUntil[vk] = now + WheelLatchMs;
                        int idx = vk >> 3, bit = 1 << (vk & 7);
                        ExtraKeys[idx] |= (byte)bit;
                    }
                }
                // 相对模式（普通鼠标）：立即把增量累加进坐标，让两次 tick 之间也在动
                if ((ms.usFlags & 0x01) == 0)
                {
                    _mouseX += ms.lLastX; _mouseY += ms.lLastY;
                }
                _rawMouseEvents++;
            }
        }
        finally { Marshal.FreeHGlobal(pinned); }
    }

    // ---------------- 轮询源（常开的冗余，不是自检） ----------------
    private static void PollSweep()
    {
        int start = _pollCursor;
        for (int n = 0; n < PollSweepPerTick; n++)
        {
            int vk = (start + n) & 0xFF;
            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            ApplyKey(vk, down, false);
        }
        _pollCursor = (start + PollSweepPerTick) & 0xFF;

        // 滚轮锁存到期后清位（轮询不管滚轮）
        int now = NowMs();
        lock (StateLock)
        {
            for (int vk = 0x07; vk <= 0x08; vk++)
            {
                if (LatchUntil[vk] - now <= 0)
                {
                    Down[vk] = false;
                    int idx = vk >> 3, bit = 1 << (vk & 7);
                    if (!PollDown[vk]) ExtraKeys[idx] &= (byte)~bit;
                }
            }
        }
        // 鼠标键：事件未按下且锁存到期 → 允许轮询接管（左/右/中由 GetAsyncKeyState 兜底）
        bool l = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        bool r = (GetAsyncKeyState(0x02) & 0x8000) != 0;
        bool m = (GetAsyncKeyState(0x04) & 0x8000) != 0;
        ApplyMousePoll(0, l); ApplyMousePoll(1, r); ApplyMousePoll(2, m);
        UpdateBuiltinMask();
    }

    private static void ApplyMousePoll(int bit, bool down)
    {
        int now = NowMs();
        lock (StateLock)
        {
            if (down) _mouseMask |= (byte)(1 << bit);
            else if (MouseLatchUntil[bit] - now <= 0) _mouseMask &= (byte)~(1 << bit);
        }
    }

    // ---------------- 手柄 ----------------
    private static int _gpActive = 0xFF;
    private static byte _gpBattery = 0xFF, _gpSubtype = 0xFF;
    private static readonly uint[] _gpPackets = new uint[4];

    private static void PollGamepad()
    {
        byte connected = 0; int active = -1;
        for (uint i = 0; i < 4; i++)
        {
            XINPUT_STATE st;
            if (XInputGetState(i, out st) != 0) { _gpPackets[i] = 0; continue; }
            connected |= (byte)(1 << (int)i);
            if (st.dwPacketNumber != _gpPackets[i])
            {
                bool first = _gpPackets[i] == 0;
                _gpPackets[i] = st.dwPacketNumber;
                if (first)
                {
                    XINPUT_CAPABILITIES caps;
                    if (XInputGetCapabilities(i, 0, out caps) == 0) _gpSubtype = caps.SubType;
                    XINPUT_BATTERY_INFORMATION bi;
                    if (XInputGetBatteryInformation(i, 0, out bi) == 0 && bi.BatteryType != 0 && bi.BatteryLevel >= 1)
                        _gpBattery = bi.BatteryLevel; else _gpBattery = 0xFF;
                }
                if (HasRealInput(st.Gamepad)) _gpActive = (int)i;
            }
            if (active < 0) active = (int)i;
        }
        int chosen = (_gpActive != 0xFF && (connected & (1 << _gpActive)) != 0) ? _gpActive : active;
        if (active < 0) chosen = -1;
        else if ((connected & (1 << active)) != 0 && _gpActive == 0xFF) _gpActive = active;

        lock (StateLock)
        {
            Gamepad[0] = connected;
            Gamepad[1] = (byte)(chosen < 0 ? 0xFF : chosen);
            if (chosen >= 0)
            {
                XINPUT_STATE st;
                if (XInputGetState((uint)chosen, out st) == 0)
                {
                    ushort b = st.Gamepad.wButtons;
                    Gamepad[2] = (byte)(b & 0xFF); Gamepad[3] = (byte)(b >> 8);
                    Gamepad[4] = st.Gamepad.bLeftTrigger; Gamepad[5] = st.Gamepad.bRightTrigger;
                    WriteShort(Gamepad, 6, st.Gamepad.sThumbLX);
                    WriteShort(Gamepad, 8, st.Gamepad.sThumbLY);
                    WriteShort(Gamepad, 10, st.Gamepad.sThumbRX);
                    WriteShort(Gamepad, 12, st.Gamepad.sThumbRY);
                    Gamepad[14] = _gpBattery;
                    Gamepad[15] = _gpSubtype;
                }
            }
            else
            {
                for (int i = 2; i < 16; i++) Gamepad[i] = 0;
                Gamepad[14] = 0xFF; Gamepad[15] = 0xFF;
            }
        }
    }

    private static bool HasRealInput(XINPUT_GAMEPAD g)
    {
        if (g.wButtons != 0) return true;
        if (g.bLeftTrigger > 30 || g.bRightTrigger > 30) return true;
        if (Math.Abs((int)g.sThumbLX) > 7849 || Math.Abs((int)g.sThumbLY) > 7849) return true;
        if (Math.Abs((int)g.sThumbRX) > 8689 || Math.Abs((int)g.sThumbRY) > 8689) return true;
        return false;
    }

    private static void WriteShort(byte[] buf, int off, short v)
    {
        buf[off] = (byte)(v & 0xFF); buf[off + 1] = (byte)((v >> 8) & 0xFF);
    }

    private static void WriteUInt(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v & 0xFF); buf[off + 1] = (byte)((v >> 8) & 0xFF);
        buf[off + 2] = (byte)((v >> 16) & 0xFF); buf[off + 3] = (byte)((v >> 24) & 0xFF);
    }

    // ---------------- 帧序列化（与 Python 侧逐字节一致） ----------------
    private static byte[] BuildFrame(bool withGamepad)
    {
        int len = withGamepad ? 92 : 76;
        byte[] f = new byte[len];
        f[0] = (byte)'K'; f[1] = (byte)'D'; f[2] = (byte)'S'; f[3] = (byte)'P';
        f[4] = 4;
        lock (StateLock)
        {
            f[5] = (byte)(_keysMask & 0xFF); f[6] = (byte)(_keysMask >> 8);
            f[7] = _mouseMask;
            WriteInt(f, 8, _mouseX); WriteInt(f, 12, _mouseY);
            WriteInt(f, 16, _vsX); WriteInt(f, 20, _vsY);
            WriteInt(f, 24, _vsW); WriteInt(f, 28, _vsH);
            WriteUInt(f, 32, _seq);
            Buffer.BlockCopy(ExtraKeys, 0, f, 36, 32);
            WriteULong(f, 68, QpcNs());
            if (withGamepad) Buffer.BlockCopy(Gamepad, 0, f, 76, 16);
        }
        return f;
    }

    private static void WriteInt(byte[] b, int o, int v) { WriteUInt(b, o, (uint)v); }
    private static void WriteULong(byte[] b, int o, ulong v)
    {
        for (int i = 0; i < 8; i++) b[o + i] = (byte)((v >> (8 * i)) & 0xFF);
    }

    private static ulong QpcNs()
    {
        long freq = Stopwatch.Frequency;
        long c = Stopwatch.GetTimestamp();
        return (ulong)((double)c * 1e9 / freq);
    }

    // ---------------- 客户端连接 ----------------
    private sealed class Client
    {
        public IntPtr Handle;
        public int FrameLen = 76;
        public Thread Thread;
        public volatile bool Alive = true;
    }

    private static readonly Client[] Clients = new Client[MAX_CLIENTS];
    private static int _clientCount;

    private static void AcceptLoop()
    {
        SECURITY_ATTRIBUTES sec;
        IntPtr sd = IntPtr.Zero;
        sec.nLength = (uint)Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
        sec.bInheritHandle = false;
        sec.lpSecurityDescriptor = IntPtr.Zero;
        try
        {
        if (!string.IsNullOrEmpty(AclFamilyName))
        {
            IntPtr pkgSid;
            if (DeriveAppContainerSidFromAppContainerName(AclFamilyName, out pkgSid) == 0)
            {
                IntPtr sidStr;
                if (ConvertSidToStringSidW(pkgSid, out sidStr))
                {
                    string sid = Marshal.PtrToStringUni(sidStr);
                    LocalFree(sidStr);
                    string userSid = "";
                    try { userSid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value; } catch { }
                    string sddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;" + sid + ")"
                        + (userSid.Length > 0 ? "(A;;GA;;;" + userSid + ")" : "");
                    uint sz;
                    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out sd, out sz))
                    {
                        sec.lpSecurityDescriptor = sd;
                        Log("管道 ACL 已附加包 SID: " + sid);
                    }
                    else Log("ACL 构建失败 err=" + Marshal.GetLastWin32Error());
                }
                else Log("SID 转字符串失败 err=" + Marshal.GetLastWin32Error());
            }
            else Log("派生包 SID 失败 err=" + Marshal.GetLastWin32Error());
        }

        }
        catch (Exception exa) { Log("ACL 构建异常（将退回默认 ACL）: " + exa.Message); }

        while (true)
        {
            // 只有首个实例带 FIRST_PIPE_INSTANCE；后续实例再带它会把自己挡在门外（err=5）
            uint openMode = PIPE_ACCESS_DUPLEX | (_firstInstance ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0u);
            _firstInstance = false;
            IntPtr h = CreateNamedPipeW(@"\\.\pipe\" + PipeName, openMode,
                PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT, PIPE_UNLIMITED_INSTANCES,
                8192, 8192, 0, ref sec);
            if (h == IntPtr.Zero || h.ToInt64() == -1)
            {
                int perr = Marshal.GetLastWin32Error();
                Log("CreateNamedPipe 失败 err=" + perr
                    + (perr == 5 ? "（管道名已被另一个服务占用：多半是旧版伴生进程还在跑，等它退出后会自动重试）" : ""));
                Thread.Sleep(1000);
                continue;
            }
            if (!ConnectNamedPipe(h, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_PIPE_CONNECTED) { CloseHandle(h); Thread.Sleep(50); continue; }
            }
            Client c = new Client();
            c.Handle = h;
            Thread t = new Thread(() => ClientLoop(c));
            t.IsBackground = true;
            t.Name = "pipe-client";
            c.Thread = t;
            for (int i = 0; i < MAX_CLIENTS; i++) if (Clients[i] == null) { Clients[i] = c; break; }
            Interlocked.Increment(ref _clientCount);
            Log("客户端已连接 (" + _clientCount + ")");
            t.Start();
        }
    }

    private static void ClientLoop(Client c)
    {
        uint mode = PIPE_READMODE_MESSAGE;
        SetNamedPipeHandleState(c.Handle, ref mode, IntPtr.Zero, IntPtr.Zero);
        int interval = Math.Max(1, 1000 / Math.Max(1, PushHz));
        long next = 0;
        byte[] cmd = new byte[1024];
        while (!_shutdown && c.Alive)
        {
            // 1) 有命令就读一条（消息模式，一次一条）
            uint avail, left;
            if (PeekNamedPipe(c.Handle, IntPtr.Zero, 0, out avail0Placeholder, out avail, out left) && avail > 0)
            {
                uint read;
                if (ReadFile(c.Handle, cmd, (uint)cmd.Length, out read, IntPtr.Zero))
                {
                    string text = Encoding.UTF8.GetString(cmd, 0, (int)read).Trim();
                    HandleCommand(c, text);
                }
                else break;
            }
            // 2) 到点就发一帧
            long now = NowMs();
            if (now >= next)
            {
                next = now + interval;
                byte[] frame = BuildFrame(c.FrameLen == 92);
                uint written;
                if (!WriteFile(c.Handle, frame, (uint)frame.Length, out written, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_NO_DATA && err != ERROR_BROKEN_PIPE) Log("写失败 err=" + err);
                    break;
                }
                Interlocked.Increment(ref _frames);
            }
            Thread.Sleep(1);   // 不再空转烧 CPU（timeBeginPeriod(1) 已保证 1ms 精度）
        }
        c.Alive = false;
        try { DisconnectNamedPipe(c.Handle); } catch { }
        try { CloseHandle(c.Handle); } catch { }
        for (int i = 0; i < MAX_CLIENTS; i++) if (Clients[i] == c) Clients[i] = null;
        Interlocked.Decrement(ref _clientCount);
        Log("客户端断开");
    }

    private static uint avail0Placeholder;

    private static void HandleCommand(Client c, string text)
    {
        if (!text.StartsWith("CMD|", StringComparison.Ordinal)) return;
        string rest = text.Substring(4);
        if (rest == "PROTO|92") { c.FrameLen = 92; Reply(c, "RESP|OK"); return; }
        if (rest == "PROTO|76") { c.FrameLen = 76; Reply(c, "RESP|OK"); return; }
        if (rest == "VERSION")
        {
            bool elevated = IsElevated();
            Reply(c, "RESP|OK|KeyDisplayInput " + Version + "|elevated=" + (elevated ? "1" : "0")
                + "|rawkeys=" + Interlocked.Read(ref _rawKeyEvents)
                + "|rawmouse=" + Interlocked.Read(ref _rawMouseEvents));
            return;
        }
        // 其余命令（GET_PRESETS / PUT_PRESETS / GET_STATS / SET_OPT / OPEN_URL …）
        // 一律转发给命令服务（Python 伴生进程，管道 KeyDisplayCmd），
        // 这样小组件与设置窗口**完全不用改动**。
        ProxyCommand(c, text);
    }

    private const uint GENERIC_READ_W = 0x80000000, GENERIC_WRITE_W = 0x40000000, OPEN_EXISTING_W = 3;
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec,
        uint disposition, uint flags, IntPtr template);

    private static readonly string CommandPipe = @"\\.\pipe\KeyDisplayCmd";

    private static void ProxyCommand(Client c, string text)
    {
        IntPtr h = CreateFileW(CommandPipe, GENERIC_READ_W | GENERIC_WRITE_W, 0, IntPtr.Zero,
            OPEN_EXISTING_W, 0, IntPtr.Zero);
        if (h == IntPtr.Zero || h.ToInt64() == -1)
        {
            Reply(c, "RESP|ERR|command service unavailable（命令服务未运行）");
            return;
        }
        try
        {
            uint mode = PIPE_READMODE_MESSAGE;
            SetNamedPipeHandleState(h, ref mode, IntPtr.Zero, IntPtr.Zero);
            byte[] b = Encoding.UTF8.GetBytes(text);
            uint written;
            if (!WriteFile(h, b, (uint)b.Length, out written, IntPtr.Zero))
            {
                Reply(c, "RESP|ERR|写命令服务失败");
                return;
            }
            byte[] buf = new byte[262144];
            uint read;
            if (!ReadFile(h, buf, (uint)buf.Length, out read, IntPtr.Zero))
            {
                Reply(c, "RESP|ERR|命令服务无应答");
                return;
            }
            byte[] reply = new byte[read];
            Buffer.BlockCopy(buf, 0, reply, 0, (int)read);
            uint w2;
            WriteFile(c.Handle, reply, (uint)reply.Length, out w2, IntPtr.Zero);
        }
        finally { CloseHandle(h); }
    }

    private static void Reply(Client c, string text)
    {
        byte[] b = Encoding.UTF8.GetBytes(text);
        uint written;
        WriteFile(c.Handle, b, (uint)b.Length, out written, IntPtr.Zero);
    }

    private static bool IsElevated()
    {
        try
        {
            using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                return new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static volatile bool _shutdown;

    // ---------------- 主流程 ----------------
    private static void FrameLoop()
    {
        int interval = Math.Max(1, 1000 / Math.Max(1, PushHz));
        long next = 0;
        int tick = 0;
        while (!_shutdown)
        {
            long now = NowMs();
            if (now >= next)
            {
                next = now + interval;
                tick++;
                PollSweep();
                {
                    // 0.2：鼠标坐标每个 tick 都读真值（240Hz）。原来写成每 8 个 tick 读一次 = 30Hz，
                    // 鼠标点会明显一顿一顿 —— 这是"鼠标移动卡顿"的直接原因。GetCursorPos 极便宜，240Hz 无压力。
                    POINT p;
                    if (GetCursorPos(out p)) { _mouseX = p.X; _mouseY = p.Y; }
                    _vsX = GetSystemMetrics(76); _vsY = GetSystemMetrics(77);
                    int w = GetSystemMetrics(78), h = GetSystemMetrics(79);
                    if (w > 0) _vsW = w;
                    if (h > 0) _vsH = h;
                }
                if (tick % 2 == 0) PollGamepad();
                lock (StateLock) { _seq = (_seq + 1) & 0xFFFFFFFF; }
            }
            Thread.Sleep(1);   // 不再空转烧 CPU（timeBeginPeriod(1) 已保证 1ms 精度）
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        foreach (string a in args)
        {
            if (a.StartsWith("--pipe=", StringComparison.Ordinal)) PipeName = a.Substring(7);
            else if (a.StartsWith("--hz=", StringComparison.Ordinal)) int.TryParse(a.Substring(5), out PushHz);
            else if (a.StartsWith("--acl=", StringComparison.Ordinal)) AclFamilyName = a.Substring(6);
            else if (a.StartsWith("--latch-ms=", StringComparison.Ordinal)) int.TryParse(a.Substring(11), out LatchMs);
        }
        if (PushHz < 30) PushHz = 30;
        if (PushHz > 1000) PushHz = 1000;

        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyDisplay");
        try { Directory.CreateDirectory(dir); } catch { }
        LogPath = Path.Combine(dir, "input.log");
        try { if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024) File.Delete(LogPath); } catch { }

        // 单实例
        IntPtr mutex = CreateMutexW(IntPtr.Zero, false, @"Global\KeyDisplayInput_" + PipeName);
        if (Marshal.GetLastWin32Error() == 183 /*ERROR_ALREADY_EXISTS*/)
        {
            Log("已有实例在运行，退出");
            return 1;
        }

        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("未处理异常: " + e.ExceptionObject);
        Log("启动: version=" + Version + " pipe=" + PipeName + " hz=" + PushHz
            + " elevated=" + (IsElevated() ? "1" : "0") + " latch=" + LatchMs + "ms");

        timeBeginPeriod(1);

        // 消息专用窗口（不显示、不进任务栏）
        IntPtr inst = GetModuleHandleW(null);
        WNDCLASSEX wc = new WNDCLASSEX();
        wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcRef);
        wc.hInstance = inst;
        wc.lpszClassName = Marshal.StringToHGlobalUni("KeyDisplayInputWnd");
        if (RegisterClassExW(ref wc) == 0)
        {
            Log("RegisterClassEx 失败 err=" + Marshal.GetLastWin32Error());
            return 2;
        }
        _hwnd = CreateWindowExW(0, "KeyDisplayInputWnd", "KeyDisplayInput", 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, inst, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Log("CreateWindowEx 失败 err=" + Marshal.GetLastWin32Error());
            return 3;
        }

        // Raw Input：INPUTSINK（系统级、非前台也收）+ 键盘 + 鼠标
        RAWINPUTDEVICE[] devs = new RAWINPUTDEVICE[2];
        devs[0].usUsagePage = 0x01; devs[0].usUsage = 0x06; devs[0].dwFlags = RIDEV_INPUTSINK; devs[0].hwndTarget = _hwnd;
        devs[1].usUsagePage = 0x01; devs[1].usUsage = 0x02; devs[1].dwFlags = RIDEV_INPUTSINK; devs[1].hwndTarget = _hwnd;
        if (!RegisterRawInputDevices(devs, 2, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
        {
            Log("RegisterRawInputDevices 失败 err=" + Marshal.GetLastWin32Error());
            return 4;
        }
        Log("Raw Input 已注册（键盘+鼠标，INPUTSINK）");

        Thread fa = new Thread(AcceptLoop); fa.IsBackground = true; fa.Name = "pipe-accept"; fa.Start();
        Thread ff = new Thread(FrameLoop); ff.IsBackground = true; ff.Name = "frame"; ff.Start();

        // 主线程只跑消息泵
        MSG msg;
        while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        _shutdown = true;
        Log("退出");
        return 0;
    }
}
