using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TrayHider
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                // 启动期异常会被这里接住并写日志，便于在别的机器上定位。
                try
                {
                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), "TrayHider_startup.log"),
                        ex.ToString(), Encoding.UTF8);
                }
                catch { }
                MessageBox.Show("TrayHider 启动失败：\r\n\r\n" + ex + "\r\n\r\n日志: " +
                                Path.Combine(Path.GetTempPath(), "TrayHider_startup.log"),
                                "TrayHider", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 记录未处理异常，便于定位崩溃
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                LogException("UI线程异常", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                LogException("未处理异常", e.ExceptionObject as Exception);
            };

            if (args.Length > 0)
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "--selftest": return SelfTest.Run();
                    case "--about": return ShowAbout();
                    case "--hotkey": return ShowHotkey();
                    case "--winrules": return ShowWindowRules();
                    case "--hotkey-test": return Cli.RunHotkeyTest();
                    case "--win10-probe": return Cli.RunWin10Probe();
                    case "--list": return Cli.RunList(args);
                    case "--hide": return Cli.RunHide(args, true);
                    case "--show": return Cli.RunHide(args, false);
                    case "--restore-all": return Cli.RunRestoreAll();
                    case "--restore-hidden-windows": return Cli.RunRestoreHiddenWindows();
                    case "--export-diagnostics": return Cli.RunExportDiagnostics();
                    case "--help":
                    case "-h":
                    case "/?": return Cli.RunHelp();
                    default:
                        Console.WriteLine("未知参数: " + args[0]);
                        return Cli.RunHelp();
                }
            }

            bool created;
            using (var mutex = new Mutex(true, @"Local\TrayHider_SingleInstance", out created))
            {
                if (!created)
                {
                    // 已有实例：把它窗口显示出来，而不是只弹提示（避免用户以为“打不开”）
                    if (TryActivateExistingWindow()) return 0;
                    MessageBox.Show("TrayHider 已经在运行（窗口在后台/托盘中）。", "TrayHider",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                Application.Run(new MainForm(SettingsStore.Load()));
            }
            return 0;
        }

        /// <summary>找到已有实例的窗口并显示到前台；没有可用窗口时返回 false。</summary>
        private static bool TryActivateExistingWindow()
        {
            try
            {
                string name = Process.GetCurrentProcess().ProcessName;
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    if (p.Id == Process.GetCurrentProcess().Id) continue;
                    IntPtr hwnd = p.MainWindowHandle;
                    if (hwnd == IntPtr.Zero)
                    {
                        // 隐藏启动的实例主窗口句柄可能为 0，枚举它的窗口找一下
                        hwnd = FindWindowOfProcess((uint)p.Id);
                    }
                    if (hwnd == IntPtr.Zero) continue;
                    Native.ShowWindow(hwnd, 9 /* SW_RESTORE */);
                    Native.ShowWindow(hwnd, 5 /* SW_SHOW */);
                    Native.SetForegroundWindow(hwnd);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static IntPtr FindWindowOfProcess(uint processId)
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pid == processId && Native.IsWindowVisible(h) && Native.GetWindowTextLength(h) > 0)
                {
                    found = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static void LogException(string title, Exception ex)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "TrayHider_exception.log"),
                    title + "\r\n" + (ex == null ? "(null)" : ex.ToString()) + "\r\n",
                    Encoding.UTF8);
            }
            catch { }
        }

        private static int ShowAbout()
        {
            using (var form = new AboutForm())
            {
                form.StartPosition = FormStartPosition.CenterScreen;
                form.ShowDialog();
            }
            return 0;
        }

        private static int ShowHotkey()
        {
            var settings = SettingsStore.Load();
            using (var form = new HotkeyForm(settings))
            {
                form.StartPosition = FormStartPosition.CenterScreen;
                form.ShowDialog();
            }
            return 0;
        }

        private static int ShowWindowRules()
        {
            // 规则页已经并入主窗口，这里只是打开主窗口并切到该页。
            var settings = SettingsStore.Load();
            var form = new MainForm(settings);
            form.SelectTab(1);
            Application.Run(form);
            return 0;
        }
    }
}
