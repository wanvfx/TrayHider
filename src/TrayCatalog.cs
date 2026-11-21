using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TrayHider
{
    /// <summary>A live notification-area icon.</summary>
    public sealed class TrayItem
    {
        public string Path = "";
        public string Name = "";
        public string Tooltip = "";
        public IntPtr Window;
        public uint Pid;
        public uint Id;
        public Guid Guid;
        public string WindowClass = "";
        public int State;
        public IntPtr Toolbar;
        public int Command;

        public string Key
        {
            get
            {
                return Guid != Guid.Empty
                    ? Guid.ToString()
                    : Path + "|" + Id + "|" + Window.ToInt64();
            }
        }

        internal byte[] snapshot;
        MemoryStream imageStream;
        Image image;

        /// <summary>PNG snapshot recorded by Explorer, used as a display image.</summary>
        public Image Image
        {
            get
            {
                if (image != null) return image;
                if (snapshot != null && snapshot.Length > 0)
                {
                    try
                    {
                        imageStream = new MemoryStream(snapshot);
                        image = Image.FromStream(imageStream);
                    }
                    catch { image = null; }
                }
                if (image == null && !string.IsNullOrEmpty(Path))
                {
                    // Win10 没有注册表快照，退回可执行文件自身的图标。
                    try
                    {
                        using (var associated = Icon.ExtractAssociatedIcon(Path))
                        {
                            if (associated != null) image = associated.ToBitmap();
                        }
                    }
                    catch { image = null; }
                }
                return image;
            }
        }

        public int SnapshotLength { get { return snapshot == null ? 0 : snapshot.Length; } }
    }

    /// <summary>Reads the notification-area icon table and changes icon visibility.</summary>
    public static class TrayCatalog
    {
        private const string SettingsKey = @"Control Panel\NotifyIconSettings";
        private static readonly string ExplorerPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        /// <summary>
        /// Enumerates live tray icons. Entries whose owning process is not running are skipped,
        /// as are shell-owned controls and (optionally) one process id.
        /// </summary>
        public static List<TrayItem> Scan(uint excludePid, bool refreshOverflow = false)
        {
            // Win10 与 Win11 的通知区域实现完全不同，走两条独立路径。
            if (!OSInfo.IsWindows11)
            {
                var items = Win10Tray.Scan(excludePid, refreshOverflow);
                return items.GroupBy(x => x.Key).Select(g => g.First())
                            .OrderBy(x => x.Name).ThenBy(x => x.Pid).ToList();
            }
            return ScanWindows11(excludePid);
        }

        private static List<TrayItem> ScanWindows11(uint excludePid)
        {
            var windows = new List<IntPtr>(256);
            Native.EnumProc collect = delegate(IntPtr h, IntPtr l) { windows.Add(h); return true; };
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                windows.Add(h);
                Native.EnumChildWindows(h, collect, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);

            // Message-only windows (HWND_MESSAGE owner) host many tray icons.
            IntPtr message = IntPtr.Zero;
            var seen = new HashSet<IntPtr>();
            while (true)
            {
                message = Native.FindWindowExW((IntPtr)(-3), message, null, null);
                if (message == IntPtr.Zero || !seen.Add(message)) break;
                windows.Add(message);
                Native.EnumChildWindows(message, collect, IntPtr.Zero);
            }

            var owners = new Dictionary<string, List<KeyValuePair<IntPtr, uint>>>(StringComparer.OrdinalIgnoreCase);
            foreach (IntPtr window in windows)
            {
                uint pid;
                Native.GetWindowThreadProcessId(window, out pid);
                string path = Native.ProcessPath(pid);
                if (path.Length == 0) continue;
                List<KeyValuePair<IntPtr, uint>> list;
                if (!owners.TryGetValue(path, out list)) owners[path] = list = new List<KeyValuePair<IntPtr, uint>>();
                list.Add(new KeyValuePair<IntPtr, uint>(window, pid));
            }

            var result = new List<TrayItem>();
            using (RegistryKey root = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                if (root == null) return result;
                foreach (string subkeyName in root.GetSubKeyNames())
                {
                    try
                    {
                        using (RegistryKey key = root.OpenSubKey(subkeyName))
                        {
                            if (key == null) continue;

                            string raw = key.GetValue("ExecutablePath") as string ?? "";
                            string path = ExpandPath(raw);
                            if (path.Length == 0) continue;
                            if (SamePath(path, ExplorerPath)) continue; // shell controls are left alone
                            if (!owners.ContainsKey(path)) continue;

                            Guid guid;
                            Guid.TryParse(key.GetValue("IconGuid") as string ?? "", out guid);
                            string tooltip = key.GetValue("InitialTooltip") as string ?? "";
                            uint uid = key.GetValue("UID") is int ? unchecked((uint)(int)key.GetValue("UID")) : 0;
                            byte[] snapshot = key.GetValue("IconSnapshot") as byte[];
                            string name = Describe(path, tooltip);

                            foreach (KeyValuePair<IntPtr, uint> owner in owners[path])
                            {
                                if (owner.Value == excludePid) continue;
                                var item = new TrayItem
                                {
                                    Path = path,
                                    Name = name,
                                    Tooltip = tooltip,
                                    Window = owner.Key,
                                    Pid = owner.Value,
                                    Id = uid,
                                    Guid = guid,
                                    WindowClass = Native.WindowClass(owner.Key),
                                    snapshot = snapshot,
                                };
                                int state = TrayApi.GetState(item);
                                if (state != 0 && state != 1) continue; // no live icon for this window
                                item.State = state;
                                result.Add(item);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!(ex is IOException) && !(ex is UnauthorizedAccessException) &&
                            !(ex is ArgumentException)) throw;
                    }
                }
            }

            return result.GroupBy(x => x.Key).Select(g => g.First())
                         .OrderBy(x => x.Name).ThenBy(x => x.Pid).ToList();
        }

        public static string ExpandPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            path = Environment.ExpandEnvironmentVariables(path.Trim());
            int end = path.StartsWith("{") ? path.IndexOf('}') : -1;
            if (end > 0)
            {
                Guid folder;
                if (Guid.TryParse(path.Substring(0, end + 1), out folder))
                {
                    IntPtr known;
                    if (Native.SHGetKnownFolderPath(folder, 0, IntPtr.Zero, out known) == 0)
                    {
                        try
                        {
                            string prefix = Marshal.PtrToStringUni(known) ?? "";
                            if (prefix.Length > 0) path = prefix + path.Substring(end + 1);
                        }
                        finally { Marshal.FreeCoTaskMem(known); }
                    }
                }
            }
            return path;
        }

        public static string Describe(string path, string tooltip)
        {
            try
            {
                string description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description;
            }
            catch { }
            string name = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(name) ? tooltip : name;
        }

        public static bool SamePath(string first, string second)
        {
            try
            {
                return string.Equals(Path.GetFullPath(first.Trim()).Replace('/', '\\'),
                                     Path.GetFullPath(second.Trim()).Replace('/', '\\'),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException) && !(ex is NotSupportedException) &&
                    !(ex is PathTooLongException)) throw;
                return string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        public static bool ContainsIgnoreCase(string source, string value)
        {
            return source != null && value != null &&
                   source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>Visibility operations on a single icon.</summary>
    public static class TrayApi
    {
        /// <returns>0 = shown, 1 = hidden, anything else = no live icon.</returns>
        public static int GetState(TrayItem item)
        {
            if (!OSInfo.IsWindows11) return Win10Tray.GetState(item);
            var id = new Native.NotifyIconIdentifier
            {
                cbSize = Native.NotifyIconIdentifierSize,
                hWnd = item.Window,
                uID = item.Id,
                guidItem = item.Guid,
            };
            return Native.Shell_NotifyIconGetRect(ref id, out _);
        }

        public static bool SetHidden(TrayItem item, bool hidden)
        {
            if (!OSInfo.IsWindows11) return Win10Tray.SetHidden(item, hidden);
            var data = new Native.NotifyIconData
            {
                cbSize = Native.NotifyIconDataSize,
                hWnd = item.Window,
                uID = item.Id,
                guidItem = item.Guid,
                uFlags = Native.NIF_STATE | (item.Guid != Guid.Empty ? Native.NIF_GUID : 0u),
                dwStateMask = Native.NIS_HIDDEN,
                dwState = hidden ? Native.NIS_HIDDEN : 0u,
            };
            return Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref data);
        }
    }
}
