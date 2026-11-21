using System;
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

            if (args.Length > 0)
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "--selftest": return SelfTest.Run();
                    case "--about": return ShowAbout();
                    case "--hotkey": return ShowHotkey();
                    case "--hotkey-test": return Cli.RunHotkeyTest();
                    case "--win10-probe": return Cli.RunWin10Probe();
                    case "--list": return Cli.RunList(args);
                    case "--hide": return Cli.RunHide(args, true);
                    case "--show": return Cli.RunHide(args, false);
                    case "--restore-all": return Cli.RunRestoreAll();
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
                    MessageBox.Show("TrayHider 已经在运行。", "TrayHider",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                Application.Run(new MainForm(SettingsStore.Load()));
            }
            return 0;
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
    }
}
