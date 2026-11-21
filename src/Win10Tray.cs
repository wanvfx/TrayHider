using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TrayHider
{
    /// <summary>
    /// Windows 10 的通知区域是传统的 Explorer 工具栏（ToolbarWindow32）：
    /// 每个图标是工具栏上的一个按钮。隐藏 = 把按钮移到「显示隐藏的图标」溢出区，
    /// 这是 Windows 10 的原生行为（图标仍在运行，不结束进程）。
    /// </summary>
    internal static class Win10Tray
    {
        private const uint TB_HIDEBUTTON = 0x404;        // WM_USER + 4
        private const uint TB_ISBUTTONHIDDEN = 0x40C;    // WM_USER + 12
        private const uint TB_GETBUTTON = 0x417;         // WM_USER + 23
        private const uint TB_BUTTONCOUNT = 0x418;       // WM_USER + 24
        private const uint TB_AUTOSIZE = 0x421;          // WM_USER + 33

        private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_FREE = 0x10000;
        private const uint PAGE_READWRITE = 4;
        private const uint PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct ToolbarButton
        {
            public int iBitmap;
            public int idCommand;
            public byte fsState, fsStyle, reserved1, reserved2;
            public long dwData;
            public IntPtr iString;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrayData
        {
            public IntPtr hWnd;
            public uint uID;
            public uint uCallbackMessage;
        }

        // 溢出窗口只在用户点开过「显示隐藏的图标」后才存在；自动扫描时按需点开一次，
        // 但必须节流，否则溢出窗口会被反复弹出。
        private const int OverflowRefreshIntervalSeconds = 15;
        private static DateTime lastOverflowRefresh = DateTime.MinValue;

        public static List<TrayItem> Scan(uint excludePid, bool refreshOverflow)
        {
            var result = new List<TrayItem>();
            AddToolbar(FindNormalTrayWindow(), "TrayNotifyWnd", excludePid, result);

            IntPtr overflow = FindOverflowTrayWindow();
            if (overflow != IntPtr.Zero)
            {
                AddToolbar(overflow, "NotifyIconOverflowWindow", excludePid, result);
            }
            else if (refreshOverflow &&
                     (DateTime.UtcNow - lastOverflowRefresh).TotalSeconds >= OverflowRefreshIntervalSeconds)
            {
                lastOverflowRefresh = DateTime.UtcNow;
                ForceOverflowRefresh();
                overflow = FindOverflowTrayWindow();
                if (overflow != IntPtr.Zero)
                    AddToolbar(overflow, "NotifyIconOverflowWindow", excludePid, result);
            }
            return result;
        }

        public static IntPtr FindNormalTrayWindow()
        {
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return IntPtr.Zero;
            tray = Native.FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray == IntPtr.Zero) return IntPtr.Zero;
            tray = Native.FindWindowExW(tray, IntPtr.Zero, "SysPager", null);
            if (tray == IntPtr.Zero) return IntPtr.Zero;
            return Native.FindWindowExW(tray, IntPtr.Zero, "ToolbarWindow32", null);
        }

        public static IntPtr FindOverflowTrayWindow()
        {
            IntPtr overflow = Native.FindWindow("NotifyIconOverflowWindow", null);
            if (overflow == IntPtr.Zero) return IntPtr.Zero;
            return Native.FindWindowExW(overflow, IntPtr.Zero, "ToolbarWindow32", null);
        }

        private static void AddToolbar(IntPtr toolbar, string area, uint excludePid, List<TrayItem> result)
        {
            if (toolbar == IntPtr.Zero) return;
            uint explorerPid;
            Native.GetWindowThreadProcessId(toolbar, out explorerPid);
            if (explorerPid == 0) return;

            IntPtr process = Native.OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, explorerPid);
            if (process == IntPtr.Zero) return;
            try
            {
                int buttonSize = Marshal.SizeOf(typeof(ToolbarButton));
                IntPtr buffer = Native.VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)buttonSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (buffer == IntPtr.Zero) return;
                try
                {
                    int count = Native.SendMessage(toolbar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    for (int i = 0; i < count; i++)
                    {
                        UIntPtr read;
                        byte[] raw;
                        var button = default(ToolbarButton);
                        var data = default(TrayData);

                        if (Native.SendMessage(toolbar, TB_GETBUTTON, (IntPtr)i, buffer) == IntPtr.Zero) continue;
                        raw = new byte[buttonSize];
                        if (!Native.ReadProcessMemory(process, buffer, raw, (UIntPtr)raw.Length, out read) ||
                            read.ToUInt64() != (ulong)raw.Length) continue;
                        button = BytesToStruct<ToolbarButton>(raw);
                        if (button.dwData == 0) continue;

                        raw = new byte[1024];
                        if (!Native.ReadProcessMemory(process, (IntPtr)button.dwData, raw, (UIntPtr)raw.Length, out read) ||
                            read.ToUInt64() < 16) continue;
                        data = BytesToStruct<TrayData>(new byte[] { raw[0], raw[1], raw[2], raw[3],
                                                                   raw[4], raw[5], raw[6], raw[7],
                                                                   raw[8], raw[9], raw[10], raw[11] });
                        if (data.hWnd == IntPtr.Zero) continue;

                        uint ownerPid;
                        Native.GetWindowThreadProcessId(data.hWnd, out ownerPid);
                        if (ownerPid == 0 || ownerPid == excludePid) continue;
                        string path = Native.ProcessPath(ownerPid);
                        if (path.Length == 0) continue;

                        // iString 指向的缓冲没有可靠结尾，容易读出一长串乱码。
                        // 优先读 TRAYDATA 里的 szTip（固定 128 个 WCHAR）；
                        // 用同一结构里的 szExePath 与进程路径比对，校验字段偏移是否正确。
                        string tooltip = ReadTooltip(process, button.iString);
                        string trayPath = DecodeUtf16(raw, 44, 520);
                        if (trayPath.Length > 0 && TrayCatalog.SamePath(trayPath, path))
                        {
                            string trayTip = DecodeUtf16(raw, 564, 256);
                            if (trayTip.Length > 0) tooltip = trayTip;
                        }
                        bool hidden = Native.SendMessage(toolbar, TB_ISBUTTONHIDDEN, (IntPtr)button.idCommand, IntPtr.Zero) != IntPtr.Zero;

                        result.Add(new TrayItem
                        {
                            Path = path,
                            Name = TrayCatalog.Describe(path, tooltip),
                            Tooltip = tooltip,
                            Window = data.hWnd,
                            Pid = ownerPid,
                            Id = data.uID,
                            Guid = Guid.Empty,
                            WindowClass = area,
                            State = hidden ? 1 : 0,
                            Toolbar = toolbar,
                            Command = button.idCommand,
                        });
                    }
                }
                finally { Native.VirtualFreeEx(process, buffer, UIntPtr.Zero, MEM_FREE); }
            }
            finally { Native.CloseHandle(process); }
        }

        /// <summary>从内存字节里解出一段以 0 结尾的 UTF-16 字符串。</summary>
        private static string DecodeUtf16(byte[] data, int offset, int maxBytes)
        {
            if (offset < 0 || offset >= data.Length) return "";
            int end = Math.Min(data.Length, offset + maxBytes);
            int i = offset;
            while (i + 1 < end && (data[i] != 0 || data[i + 1] != 0)) i += 2;
            if (i <= offset) return "";
            return Encoding.Unicode.GetString(data, offset, i - offset);
        }

        private static string ReadTooltip(IntPtr process, IntPtr address)
        {
            if (address == IntPtr.Zero) return "";
            byte[] raw = new byte[1024];
            UIntPtr read;
            if (!Native.ReadProcessMemory(process, address, raw, (UIntPtr)raw.Length, out read) || read.ToUInt64() < 2) return "";
            int n = (int)read.ToUInt64();
            int i = 0;
            while (i + 1 < n && (raw[i] != 0 || raw[i + 1] != 0)) i += 2;
            string text = i > 0 ? Encoding.Unicode.GetString(raw, 0, i) : "";
            return CleanTooltip(text);
        }

        /// <summary>
        /// iString 指向的缓冲尾部常跟着空格填充和下一段内存数据，
        /// 真实提示一般较短，截到第一段连续空白（空格 / 制表符）即可。
        /// </summary>
        private static string CleanTooltip(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int run = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == ' ' || text[i] == '\t')
                {
                    if (++run >= 2) return text.Substring(0, i - 1).TrimEnd();
                }
                else run = 0;
            }
            return text.TrimEnd();
        }

        private static T BytesToStruct<T>(byte[] bytes) where T : struct
        {
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(T)));
            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                return (T)Marshal.PtrToStructure(ptr, typeof(T));
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        public static int GetState(TrayItem item)
        {
            if (TryFindButton(item.Toolbar, item.Window, item.Id, item.Tooltip, out _, out byte state))
                return (state & 8) != 0 ? 1 : 0;   // TBSTATE_HIDDEN
            return 1;
        }

        public static bool SetHidden(TrayItem item, bool hidden)
        {
            // 发完 TB_HIDEBUTTON 后回读状态确认，失败重试一次。
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!TryFindButton(item.Toolbar, item.Window, item.Id, item.Tooltip,
                                   out uint commandId, out _))
                    return false;
                item.Command = (int)commandId;
                Native.SendMessage(item.Toolbar, TB_HIDEBUTTON, (IntPtr)commandId, (IntPtr)(hidden ? 1 : 0));
                Native.SendMessage(item.Toolbar, TB_AUTOSIZE, IntPtr.Zero, IntPtr.Zero);

                if (TryFindButton(item.Toolbar, item.Window, item.Id, item.Tooltip, out _, out byte after))
                    if (((after & 8) != 0) == hidden) return true;
            }
            return false;
        }

        /// <summary>
        /// 按 (窗口句柄, uID) 重新定位按钮：工具栏重建后 commandId 会变，
        /// 直接用扫描时缓存的 commandId 可能点错按钮。若同一 (窗口, uID) 有多个按钮，
        /// 再用提示文字进一步区分。
        /// </summary>
        public static bool TryFindButton(IntPtr toolbar, IntPtr window, uint id, string tooltip,
                                         out uint commandId, out byte buttonState)
        {
            commandId = 0;
            buttonState = 0;
            uint processId;
            Native.GetWindowThreadProcessId(toolbar, out processId);
            IntPtr process = Native.OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, processId);
            if (process == IntPtr.Zero) return false;
            try
            {
                IntPtr buffer = Native.VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (buffer == IntPtr.Zero) return false;
                try
                {
                    int count = Native.SendMessage(toolbar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    var candidates = new List<KeyValuePair<uint, byte>>();
                    var tips = new List<string>();
                    for (int i = 0; i < count; i++)
                    {
                        UIntPtr read;
                        byte[] raw;
                        byte[] data;
                        long dwData;

                        if (Native.SendMessage(toolbar, TB_GETBUTTON, (IntPtr)i, buffer) == IntPtr.Zero) continue;
                        raw = new byte[32];
                        if (!Native.ReadProcessMemory(process, buffer, raw, (UIntPtr)raw.Length, out read) ||
                            read.ToUInt64() < 32) continue;
                        dwData = BitConverter.ToInt64(raw, 16);
                        if (dwData == 0) continue;
                        data = new byte[16];
                        if (!Native.ReadProcessMemory(process, (IntPtr)dwData, data, (UIntPtr)data.Length, out read) ||
                            read.ToUInt64() < 12) continue;
                        if ((IntPtr)BitConverter.ToInt64(data, 0) != window ||
                            BitConverter.ToUInt32(data, 8) != id)
                            continue;

                        candidates.Add(new KeyValuePair<uint, byte>((uint)BitConverter.ToInt32(raw, 4), raw[8]));
                        tips.Add(ReadTooltip(process, (IntPtr)BitConverter.ToInt64(raw, 24)));
                    }
                    if (candidates.Count == 0) return false;

                    // 同一 (窗口, uID) 有多个按钮时，用提示文字进一步区分。
                    int chosen = 0;
                    if (candidates.Count > 1 && !string.IsNullOrEmpty(tooltip))
                        for (int i = 0; i < tips.Count; i++)
                            if (tips[i] == tooltip) { chosen = i; break; }

                    commandId = candidates[chosen].Key;
                    buttonState = candidates[chosen].Value;
                    return true;
                }
                finally { Native.VirtualFreeEx(process, buffer, UIntPtr.Zero, MEM_FREE); }
            }
            finally { Native.CloseHandle(process); }
        }

        /// <summary>
        /// 点击任务栏上的「显示隐藏的图标」按钮，强制溢出窗口出现，
        /// 这样连溢出区里的图标也能被枚举到。
        /// </summary>
        public static void ForceOverflowRefresh()
        {
            try
            {
                IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) return;
                IntPtr notify = Native.FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                if (notify == IntPtr.Zero) return;
                IntPtr overflow = Native.FindWindow("NotifyIconOverflowWindow", null);
                if (overflow != IntPtr.Zero && Native.IsWindowVisible(overflow)) return;
                IntPtr chevron = Native.FindWindowExW(notify, IntPtr.Zero, "Button", null);
                if (chevron == IntPtr.Zero) return;
                Native.SendMessage(chevron, 0x00F5u /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero);
                for (int i = 0; i < 20; i++)
                {
                    overflow = Native.FindWindow("NotifyIconOverflowWindow", null);
                    if (overflow != IntPtr.Zero) { Thread.Sleep(150); return; }
                    Thread.Sleep(100);
                }
            }
            catch { }
        }
    }
}
