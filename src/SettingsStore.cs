using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace TrayHider
{
    /// <summary>One icon identity that should stay hidden.</summary>
    [DataContract]
    public sealed class IconRule
    {
        [DataMember] public string Path { get; set; }
        [DataMember] public uint Id { get; set; }
        [DataMember] public Guid Guid { get; set; }
        [DataMember] public string WindowClass { get; set; }
        [DataMember] public string Tooltip { get; set; }

        public IconRule()
        {
            Path = "";
            WindowClass = "";
            Tooltip = "";
        }

        public static IconRule From(TrayItem item)
        {
            return new IconRule
            {
                Path = item.Path,
                Id = item.Id,
                Guid = item.Guid,
                WindowClass = item.WindowClass,
                Tooltip = item.Tooltip,
            };
        }

        public bool Matches(TrayItem item)
        {
            if (!TrayCatalog.SamePath(Path, item.Path)) return false;
            if (Guid != Guid.Empty) return Guid == item.Guid;
            return item.Guid == Guid.Empty && Id == item.Id &&
                   string.Equals(WindowClass, item.WindowClass, StringComparison.OrdinalIgnoreCase);
        }
    }

    [DataContract]
    public sealed class Settings
    {
        [DataMember] public int Version { get; set; }
        [DataMember] public List<IconRule> Hidden { get; set; }
        [DataMember] public List<IconRule> Recovery { get; set; }
        [DataMember] public bool AutoStart { get; set; }
        [DataMember] public bool CloseToTray { get; set; }
        [DataMember] public bool MinimizeToTrayOnStart { get; set; }
        [DataMember] public bool HideSelfIcon { get; set; }
        [DataMember] public bool HotkeyEnabled { get; set; }
        [DataMember] public int HotkeyMode { get; set; }
        [DataMember] public uint HotkeyModifiers { get; set; }
        [DataMember] public int HotkeyKey { get; set; }
        [DataMember] public List<int> Sequence { get; set; }
        [DataMember] public int SequenceWindowMs { get; set; }

        public Settings()
        {
            Version = 1;
            Hidden = new List<IconRule>();
            Recovery = new List<IconRule>();
            CloseToTray = true;
            HotkeyEnabled = true;
            HotkeyMode = 0;
            HotkeyModifiers = VirtualKeys.MOD_CONTROL | VirtualKeys.MOD_ALT;
            HotkeyKey = 0x54; // T
            Sequence = new List<int>();
            SequenceWindowMs = 800;
        }
    }

    /// <summary>JSON settings under %LOCALAPPDATA%\TrayHider.</summary>
    public sealed class SettingsStore
    {
        private static readonly string Folder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayHider");
        private static readonly string FilePath = Path.Combine(Folder, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var settings = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
                        if (settings != null)
                        {
                            if (settings.Hidden == null) settings.Hidden = new List<IconRule>();
                            if (settings.Recovery == null) settings.Recovery = new List<IconRule>();
                            return settings;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!(ex is InvalidOperationException) && !(ex is InvalidDataException) &&
                    !(ex is IOException) && !(ex is UnauthorizedAccessException)) throw;
            }
            return new Settings();
        }

        public static void Save(Settings settings)
        {
            Directory.CreateDirectory(Folder);
            string temp = FilePath + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(Settings)).WriteObject(stream, settings);
                stream.Flush(true);
            }
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
            else File.Move(temp, FilePath);
        }

        public static string SettingsPath { get { return FilePath; } }
    }

    /// <summary>Applies and reverses hide rules.</summary>
    public static class RuleEngine
    {
        public static bool MatchesAny(IEnumerable<IconRule> rules, TrayItem item)
        {
            foreach (var rule in rules) if (rule.Matches(item)) return true;
            return false;
        }

        /// <summary>Hides every live icon matched by a rule. Returns the number actually hidden.</summary>
        public static int Apply(Settings settings, List<TrayItem> items)
        {
            int changed = 0;
            foreach (var item in items)
            {
                if (item.State == 0 && MatchesAny(settings.Hidden, item) && TrayApi.SetHidden(item, true))
                {
                    item.State = 1;
                    changed++;
                    if (!settings.Recovery.Exists(delegate(IconRule r) { return r.Matches(item); }))
                        settings.Recovery.Add(IconRule.From(item));
                }
            }
            if (changed > 0) SettingsStore.Save(settings);
            return changed;
        }

        public static int Restore(Settings settings, List<TrayItem> items, IList<TrayItem> only)
        {
            int changed = 0;
            foreach (var item in only)
            {
                if (!settings.Recovery.Exists(delegate(IconRule r) { return r.Matches(item); })) continue;
                if (item.State == 1)
                {
                    item.State = TrayApi.SetHidden(item, false) ? 0 : item.State;
                }
                if (item.State == 0) changed++;
            }
            RemoveMatches(settings.Hidden, only);
            RemoveMatches(settings.Recovery, only);
            SettingsStore.Save(settings);
            return changed;
        }

        private static void RemoveMatches(List<IconRule> rules, IList<TrayItem> targets)
        {
            rules.RemoveAll(delegate(IconRule r)
            {
                foreach (var item in targets) if (r.Matches(item)) return true;
                return false;
            });
        }

        public static int RestoreAll(Settings settings, List<TrayItem> items)
        {
            int changed = 0;
            foreach (var item in items)
            {
                if (item.State == 1 && settings.Recovery.Exists(delegate(IconRule r) { return r.Matches(item); }))
                {
                    if (TrayApi.SetHidden(item, false)) { item.State = 0; changed++; }
                }
            }
            settings.Hidden.Clear();
            settings.Recovery.Clear();
            SettingsStore.Save(settings);
            return changed;
        }
    }
}
