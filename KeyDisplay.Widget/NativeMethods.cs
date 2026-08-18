using System;
using System.Runtime.InteropServices;

namespace KeyDisplay
{
    internal static class NativeMethods
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const long INVALID_HANDLE_VALUE = -1;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        public const uint PIPE_READMODE_MESSAGE = 0x2;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetNamedPipeHandleState(
            IntPtr hNamedPipe,
            ref uint lpMode,
            IntPtr lpMaxCollectionCount,
            IntPtr lpCollectDataTimeout);

        // 预读管道下一条消息长度（消息模式下 lpBytesLeftThisMessage = 当前消息剩余字节数，即完整消息长度），
        // 用于按实际长度分配读缓冲，一次读完整条消息（规避缓冲区小于消息时的 ERROR_MORE_DATA/截断问题）。
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PeekNamedPipe(
            IntPtr hNamedPipe,
            byte[] lpBuffer,
            uint nBufferSize,
            IntPtr lpBytesRead,
            out uint lpTotalBytesAvail,
            out uint lpBytesLeftThisMessage);
    }
}