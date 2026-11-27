using System;
using System.Diagnostics;
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

        // z 序遍历顶层窗口：GetTopWindow 拿最上层，再用 GW_HWNDNEXT 往下走
        [DllImport("user32.dll")]
        public static extern IntPtr GetTopWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint direction);

        public const uint GW_HWNDFIRST = 0;
        public const uint GW_HWNDLAST = 1;
        public const uint GW_HWNDNEXT = 2;
        public const uint GW_HWNDPREV = 3;

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

        // ---- 窗口拦截（PopupGuard）所需的 Win32 ----

        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;

        // SetWindowDisplayAffinity 只能作用于本进程窗口；跨进程会被拒绝（err=5）。
        public const uint WDA_NONE = 0x00000000;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        public static extern bool IsHungAppWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr WindowFromPoint(POINT point);

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TRANSPARENT = 0x00000020;

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int index);

        // ---- 截图拦截：截窗口图 + 采样 ----

        // PW_RENDERFULLCONTENT = 2，能截到 DirectUI/硬件合成的窗口内容
        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern uint GetPixel(IntPtr hdc, int x, int y);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr hdc, int x, int y, int width, int height,
                                         IntPtr hdcSrc, int x1, int y1, uint rop);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        /// <summary>截取窗口到指定位图；失败返回 false（部分窗口 PrintWindow 不支持）。</summary>
        public static bool CaptureWindow(IntPtr hwnd, IntPtr hdcDest, int width, int height)
        {
            if (width <= 0 || height <= 0) return false;
            // 先试 PW_RENDERFULLCONTENT（Win8+），失败再试普通模式
            if (PrintWindow(hwnd, hdcDest, 2)) return true;
            return PrintWindow(hwnd, hdcDest, 0);
        }

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT point);

        // 物理按键状态：轮询用，不依赖消息队列（点击落到哪里都能检测到）
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
        }

        private const uint PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020;
        private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_FREE = 0x10000;
        private const uint PAGE_READWRITE = 4;

        /// <summary>
        /// 读窗口标题。用 GetWindowTextW（对 WinForms 等窗口有效，实测比手发 WM_GETTEXT 可靠）。
        /// 先跳过「挂起」的窗口（IsHungAppWindow）：个别窗口会一直等 WM_GETTEXT，
        /// 宁可少读一个标题，也不能把扫描线程卡死。
        /// 注意：不要用「起子线程 + Join 超时」的方式调 GetWindowTextW——实测会让进程崩溃。
        /// </summary>
        public static string WindowTitle(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return "";
            if (IsHungAppWindow(hWnd)) return "";
            var sb = new StringBuilder(512);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }

    /// <summary>一个顶层窗口的采集信息，用于匹配与展示。</summary>
    public sealed class WindowInfo
    {
        public IntPtr Handle;
        public string Title = "";
        public string ProcessName = "";
        public string ClassName = "";
        public uint ProcessId;

        public string Describe()
        {
            string title = Title.Length > 30 ? Title.Substring(0, 30) + "…" : Title;
            return title + "  |  " + ProcessName;
        }
    }
}
