using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace TrayHider
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
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
                    case "--list": return Cli.RunList(args);
                    case "--hide": return Cli.RunHide(args, true);
                    case "--show": return Cli.RunHide(args, false);
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

        public static uint SelfPid { get { return (uint)Process.GetCurrentProcess().Id; } }

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
                if (form.ShowDialog() == DialogResult.OK) ApplyHotkeyAfterDialog(settings);
            }
            return 0;
        }

        private static void ApplyHotkeyAfterDialog(Settings settings)
        {
            // The dialog already persisted the settings; nothing else to do here,
            // but keep the entry point symmetric with the GUI.
        }
    }
}
