using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TrayHider
{
    /// <summary>Win32 declarations used to read and change the notification area.</summary>
    internal static class Native
    {
        public const uint NIM_MODIFY = 0x00000001;
        public const uint NIF_STATE = 0x00000008;
        public const uint NIF_GUID = 0x00000020;
        public const uint NIS_HIDDEN = 0x00000001;

        public static readonly uint NotifyIconDataSize = (uint)Marshal.SizeOf(typeof(NotifyIconData));
        public static readonly uint NotifyIconIdentifierSize = (uint)Marshal.SizeOf(typeof(NotifyIconIdentifier));

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NotifyIconData
        {
            public NotifyIconData() { }
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip = "";
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo = "";
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle = "";
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NotifyIconIdentifier
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid guidItem;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

        [DllImport("shell32.dll")]
        public static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier id, out Rect rect);

        [DllImport("shell32.dll")]
        public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
            uint flags, IntPtr token, out IntPtr path);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string window);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int maxCount);

        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)] public static extern bool DestroyIcon(IntPtr icon);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags,
            StringBuilder exeName, ref uint size);

        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint processId);

        [DllImport("kernel32.dll")] public static extern uint GetConsoleOutputCP();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetStdHandle(int index);

        // 必须显式指定 Unicode：DllImport 默认按 ANSI 封送 string，
        // 而 WriteConsoleW 的缓冲是 UTF-16，否则每个字节会被当成一个字符。
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool WriteConsoleW(IntPtr consoleOutput, string buffer,
            uint charsToWrite, out uint charsWritten, IntPtr reserved);

        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AllocConsole();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint type, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint type);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr read);

        public static string WindowClass(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassNameW(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string ProcessPath(uint processId)
        {
            if (processId == 0) return "";
            IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
            if (handle == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (QueryFullProcessImageNameW(handle, 0, sb, ref size)) return sb.ToString();
                if (Marshal.GetLastWin32Error() == 122 /* ERROR_INSUFFICIENT_BUFFER */)
                {
                    sb.EnsureCapacity(32768);
                    size = (uint)sb.Capacity;
                    if (QueryFullProcessImageNameW(handle, 0, sb, ref size)) return sb.ToString();
                }
                return "";
            }
            finally { CloseHandle(handle); }
        }
    }
}
