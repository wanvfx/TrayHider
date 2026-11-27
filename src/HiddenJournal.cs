using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace TrayHider
{
    /// <summary>
    /// 本工具藏起来的窗口清单（%LOCALAPPDATA%\TrayHider\hidden.json）。
    ///
    /// 用途：正常退出时清空；若上次是异常退出（进程被杀），下次启动据此把窗口恢复显示，
    /// 避免"工具没了、窗口还一直藏着"。
    ///
    /// 只存可跨重启匹配的特征（进程名 / 类名 / 标题 / 路径），不存句柄——句柄重启后无意义。
    /// </summary>
    internal sealed class HiddenJournal
    {
        [DataContract]
        public sealed class Item
        {
            [DataMember] public string ProcessName { get; set; }
            [DataMember] public string Title { get; set; }
            [DataMember] public string ClassName { get; set; }
            [DataMember] public string Path { get; set; }
            [DataMember] public string RuleId { get; set; }
            [DataMember] public string Since { get; set; }

            public Item() { }

            public Item(WindowRule rule, WindowInfo info)
            {
                ProcessName = info.ProcessName ?? "";
                Title = info.Title ?? "";
                ClassName = info.ClassName ?? "";
                Path = Native.ProcessPath(info.ProcessId) ?? "";
                RuleId = rule == null ? "" : RuleFingerprint.Of(rule);
                Since = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
        }

        [DataContract]
        public sealed class JournalFile
        {
            [DataMember] public List<Item> Items { get; set; }

            public JournalFile()
            {
                Items = new List<Item>();
            }
        }

        public static string FilePath { get { return Path.Combine(SettingsStore.Folder, "hidden.json"); } }

        private readonly List<Item> items = new List<Item>();
        private readonly object gate = new object();

        public int Count { get { lock (gate) return items.Count; } }

        /// <summary>读盘。文件不存在或损坏都当空清单处理（不能因为它让启动失败）。</summary>
        public void Load()
        {
            lock (gate)
            {
                items.Clear();
                try
                {
                    if (!File.Exists(FilePath)) return;
                    using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var file = (JournalFile)new DataContractJsonSerializer(typeof(JournalFile)).ReadObject(stream);
                        if (file != null && file.Items != null)
                            foreach (var it in file.Items)
                                if (it != null && !string.IsNullOrEmpty(it.ProcessName)) items.Add(it);
                    }
                }
                catch { items.Clear(); }
            }
        }

        public void Add(WindowRule rule, WindowInfo info)
        {
            if (info == null) return;
            lock (gate)
            {
                string id = rule == null ? "" : RuleFingerprint.Of(rule);
                foreach (var it in items)
                    if (it.RuleId == id &&
                        string.Equals(it.ProcessName, info.ProcessName ?? "", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(it.Title, info.Title ?? "", StringComparison.OrdinalIgnoreCase))
                        return;   // 已记录，不重复
                items.Add(new Item(rule, info));
                SaveLocked();
            }
        }

        /// <summary>移除某条规则对应的记录（该规则的窗口已被恢复）。</summary>
        public void RemoveOfRule(WindowRule rule)
        {
            if (rule == null) return;
            string id = RuleFingerprint.Of(rule);
            lock (gate)
            {
                int n = items.Count;
                items.RemoveAll(delegate(Item it) { return it != null && it.RuleId == id; });
                if (items.Count != n) SaveLocked();
            }
        }

        /// <summary>清空清单（所有窗口都已恢复显示，或正常退出）。</summary>
        public void Clear()
        {
            lock (gate)
            {
                items.Clear();
                SaveLocked();
            }
        }

        public List<Item> Snapshot()
        {
            lock (gate) return new List<Item>(items);
        }

        private void SaveLocked()
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.Folder);
                string temp = FilePath + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(JournalFile)).WriteObject(stream, new JournalFile { Items = new List<Item>(items) });
                    stream.Flush(true);
                }
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
                else File.Move(temp, FilePath);
            }
            catch (Exception ex) { SettingsStore.LogStoreError("journal-error.log", "隐藏窗口清单写入失败", ex); }
        }
    }
}
