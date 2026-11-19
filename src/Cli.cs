using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace TrayHider
{
    /// <summary>Command line surface for scripting and verification.</summary>
    internal static class Cli
    {
        private static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "TrayHider_cli.txt");
        private static readonly List<string> Lines = new List<string>();
        private static readonly uint SelfPid = (uint)Process.GetCurrentProcess().Id;

        private static void Say(string line)
        {
            Console.WriteLine(line);
            Lines.Add(line);
        }

        private static void Flush()
        {
            try { File.WriteAllText(LogPath, string.Join(Environment.NewLine, Lines)); }
            catch { }
        }

        public static int RunList(string[] args)
        {
            Native.AttachConsole(unchecked((uint)-1));
            var items = TrayCatalog.Scan(SelfPid);
            Say("共 " + items.Count + " 个活动托盘图标:");
            foreach (var item in items)
                Say("  [" + (item.State == 1 ? "已隐藏" : "显示中") + "] " + item.Name + " | " +
                    item.Tooltip + " | " + item.Path + " | pid=" + item.Pid + " uid=" + item.Id +
                    " guid=" + item.Guid + " | 快照=" + item.SnapshotLength + "B");
            Flush();
            return 0;
        }

        public static int RunHide(string[] args, bool hide)
        {
            Native.AttachConsole(unchecked((uint)-1));
            if (args.Length < 2)
            {
                Say("用法: TrayHider --" + (hide ? "hide" : "show") + " <进程名|提示文字|路径关键字>");
                Flush();
                return 2;
            }

            string match = args[1];
            var items = TrayCatalog.Scan(SelfPid);
            var hits = items.FindAll(delegate(TrayItem x)
            {
                return TrayCatalog.ContainsIgnoreCase(x.Path, match) ||
                       TrayCatalog.ContainsIgnoreCase(x.Tooltip, match) ||
                       TrayCatalog.ContainsIgnoreCase(x.Name, match);
            });
            if (hits.Count == 0)
            {
                Say("未找到匹配的托盘图标: " + match);
                Flush();
                return 1;
            }

            var settings = SettingsStore.Load();
            int changed = 0;
            foreach (var item in hits)
            {
                if (hide)
                {
                    if (!settings.Hidden.Exists(delegate(IconRule r) { return r.Matches(item); }))
                        settings.Hidden.Add(IconRule.From(item));
                    if (item.State == 0 && TrayApi.SetHidden(item, true))
                    {
                        item.State = 1;
                        changed++;
                        if (!settings.Recovery.Exists(delegate(IconRule r) { return r.Matches(item); }))
                            settings.Recovery.Add(IconRule.From(item));
                    }
                }
                else
                {
                    settings.Hidden.RemoveAll(delegate(IconRule r) { return r.Matches(item); });
                    settings.Recovery.RemoveAll(delegate(IconRule r) { return r.Matches(item); });
                    if (item.State == 1 && TrayApi.SetHidden(item, false))
                    {
                        item.State = 0;
                        changed++;
                    }
                }
            }
            SettingsStore.Save(settings);
            Say(changed + " 个图标已" + (hide ? "隐藏" : "恢复") + "（匹配: " + match + "）");
            Flush();
            return 0;
        }

        private static string DescribeModifiers(uint modifiers)
        {
            var parts = new List<string>();
            if ((modifiers & VirtualKeys.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((modifiers & VirtualKeys.MOD_ALT) != 0) parts.Add("Alt");
            if ((modifiers & VirtualKeys.MOD_SHIFT) != 0) parts.Add("Shift");
            if ((modifiers & VirtualKeys.MOD_WIN) != 0) parts.Add("Win");
            return parts.Count == 0 ? "(无修饰键)" : string.Join(" + ", parts.ToArray());
        }

        private static List<string> SequenceNames(List<int> sequence)
        {
            var names = new List<string>();
            if (sequence != null) foreach (int vk in sequence) names.Add(VirtualKeys.Name(vk));
            return names;
        }

        /// <summary>Deterministic check of the configured trigger, without synthetic input.</summary>
        public static int RunHotkeyTest()
        {
            Native.AttachConsole(unchecked((uint)-1));
            var settings = SettingsStore.Load();
            var engine = new HotkeyEngine
            {
                Enabled = settings.HotkeyEnabled,
                Mode = settings.HotkeyMode,
                Modifiers = settings.HotkeyModifiers,
                Key = settings.HotkeyKey,
                Sequence = settings.Sequence ?? new List<int>(),
                WindowMs = settings.SequenceWindowMs,
            };
            int fired = 0;
            engine.Triggered += delegate { fired++; };

            if (!settings.HotkeyEnabled)
            {
                Say("快捷键未启用");
                Flush();
                return 1;
            }

            if (settings.HotkeyMode == 0)
            {
                Say("组合键测试：" + DescribeModifiers(settings.HotkeyModifiers) + " + " + VirtualKeys.Name(settings.HotkeyKey));
                if ((settings.HotkeyModifiers & VirtualKeys.MOD_CONTROL) != 0) engine.SimulateKey(0x11);
                if ((settings.HotkeyModifiers & VirtualKeys.MOD_ALT) != 0) engine.SimulateKey(0x12);
                if ((settings.HotkeyModifiers & VirtualKeys.MOD_SHIFT) != 0) engine.SimulateKey(0x10);
                if ((settings.HotkeyModifiers & VirtualKeys.MOD_WIN) != 0) engine.SimulateKey(0x5B);
                engine.SimulateKey(settings.HotkeyKey);
                Say("触发次数：" + fired + (fired == 1 ? "（通过）" : "（异常）"));
            }
            else
            {
                var sequence = settings.Sequence ?? new List<int>();
                Say("序列测试：" + string.Join(" → ", SequenceNames(sequence).ToArray()));
                if (sequence.Count == 0)
                {
                    Say("序列为空");
                    Flush();
                    return 1;
                }
                for (int i = 0; i < sequence.Count - 1; i++) engine.SimulateKey(sequence[i]);
                int prefix = fired;
                engine.SimulateKey(sequence[sequence.Count - 1]);
                Say("前缀触发=" + prefix + "（应为 0），完整序列触发=" + fired + "（应为 1）");
            }
            Flush();
            return fired > 0 ? 0 : 1;
        }

        public static int RunHelp()
        {            Native.AttachConsole(unchecked((uint)-1));
            Say("TrayHider - Windows 11 托盘图标隐藏工具（精简版）");
            Say("  TrayHider                  启动图形界面（常驻托盘）");
            Say("  TrayHider --list           列出所有活动托盘图标");
            Say("  TrayHider --hide <关键字>  按进程名 / 提示文字 / 路径关键字隐藏图标");
            Say("  TrayHider --show <关键字>  恢复已隐藏的图标");
            Say("  TrayHider --selftest       自检：创建测试图标并验证隐藏与恢复");
            Say("  TrayHider --about          打开“关于”对话框");
            Say("  TrayHider --hotkey         打开“快捷键设置”面板");
            Say("  TrayHider --hotkey-test    自检当前快捷键的触发逻辑");
            Flush();
            return 0;
        }
    }
}
