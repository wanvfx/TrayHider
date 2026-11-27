using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace TrayHider
{
    /// <summary>
    /// 规则导入/导出（JSON），以及火绒自带规则库的转换。
    /// 导出/导入格式为本工具的 WindowRule 列表，便于用户之间分享。
    /// </summary>
    public static class RuleIo
    {
        /// <summary>本工具规则文件的格式版本。</summary>
        public const int FormatVersion = 1;

        [DataContract]
        public sealed class RuleFile
        {
            [DataMember] public int Version { get; set; }
            [DataMember] public string Generator { get; set; }
            [DataMember] public List<WindowRule> Rules { get; set; }

            public RuleFile()
            {
                Version = FormatVersion;
                Generator = "TrayHider";
                Rules = new List<WindowRule>();
            }
        }

        /// <summary>导出规则到 JSON 文件。</summary>
        public static void Export(IEnumerable<WindowRule> rules, string path)
        {
            var file = new RuleFile { Rules = rules.ToList() };
            // 先统一缩略图编码，否则导出的文件里只有 null，导入后预览就没了
            foreach (var r in file.Rules) SettingsStore.EncodeThumbnail(r);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(RuleFile)).WriteObject(stream, file);
                stream.Flush(true);
            }
        }

        /// <summary>从 JSON 文件导入规则；失败返回 null。</summary>
        public static List<WindowRule> Import(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var file = (RuleFile)new DataContractJsonSerializer(typeof(RuleFile)).ReadObject(stream);
                if (file == null || file.Rules == null) return null;
                foreach (var r in file.Rules)
                {
                    if (r.Actions == null || r.Actions.Count == 0) r.Actions = new List<WindowAction> { WindowAction.Hide };
                    if (r.Points == null) r.Points = new List<CapturePoint>();
                    // 兼容新格式（ThumbnailB64）与旧格式（Thumbnail 字节数组）
                    SettingsStore.EncodeThumbnail(r);
                }
                return file.Rules;
            }
        }

        /// <summary>导出的规则按「软件」分组，供界面展示。</summary>
        public sealed class PopupGroup
        {
            public string Name = "";
            public string ProcessName = "";
            public string Path = "";
            public List<WindowRule> Rules = new List<WindowRule>();
            public int BlockCount;   // 拦截次数
        }

        // 进程名 → 路径：分组时每条规则都会查一次，缓存 5 分钟（列表刷新在 UI 线程上）
        private static readonly Dictionary<string, string> resolvedPaths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static DateTime resolvedPathsUntil = DateTime.MinValue;

        /// <summary>
        /// 按进程名找一个正在运行的进程，取它的完整路径。
        /// 面板和分组共用这一份（带 5 分钟缓存），避免同一进程被反复 OpenProcess。
        /// </summary>
        internal static string FindProcessPath(string proc)
        {
            if (string.IsNullOrEmpty(proc)) return "";
            if (DateTime.Now < resolvedPathsUntil)
            {
                string cached;
                if (resolvedPaths.TryGetValue(proc, out cached)) return cached;
            }
            string found = "";
            try
            {
                foreach (Process p in Process.GetProcessesByName(proc))
                {
                    try
                    {
                        if (p.MainModule != null && !string.IsNullOrEmpty(p.MainModule.FileName))
                        { found = p.MainModule.FileName; break; }
                    }
                    catch { }
                    string path = Native.ProcessPath((uint)p.Id);
                    if (!string.IsNullOrEmpty(path)) { found = path; break; }
                }
            }
            catch { }
            if (DateTime.Now >= resolvedPathsUntil)
            {
                resolvedPaths.Clear();
                resolvedPathsUntil = DateTime.Now.AddMinutes(5);
            }
            resolvedPaths[proc] = found;
            return found;
        }

        /// <summary>
        /// 按「软件」给规则分组。
        /// 自动判定：某个程序的目录在另一个程序目录下面就归到上面那个（子程序作为子项）；
        /// 都没有就按软件名/标题等。用户在编辑框里指定了「归属软件」的，直接并入同名组。
        ///
        /// 注意副作用：会顺手补上缺失的 ProgramPath（写回规则对象），调用方需要保存设置。
        /// </summary>
        public static List<PopupGroup> GroupBySoftware(List<WindowRule> rules,
                                                       Func<List<WindowRule>, int> blockCountOf,
                                                       out bool filledPath)
        {
            filledPath = false;
            var groups = new List<PopupGroup>();
            var byKey = new Dictionary<string, PopupGroup>(StringComparer.OrdinalIgnoreCase);

            // 先补齐缺失的路径（旧规则当时没记路径）
            foreach (var r in rules)
            {
                if (!string.IsNullOrEmpty(r.ProgramPath)) continue;
                string p = FindProcessPath(TextUtil.FirstNonEmpty(r.SoftwareName, r.ProcessName));
                if (!string.IsNullOrEmpty(p)) { r.ProgramPath = p; filledPath = true; }
            }

            // 所有已知程序目录
            var dirs = new List<string>();
            foreach (var r in rules)
            {
                if (string.IsNullOrEmpty(r.ProgramPath)) continue;
                string d = System.IO.Path.GetDirectoryName(r.ProgramPath.TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(d) && !dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) dirs.Add(d);
            }

            foreach (var r in rules)
            {
                string dir = "";
                if (!string.IsNullOrEmpty(r.ProgramPath))
                    dir = System.IO.Path.GetDirectoryName(r.ProgramPath.TrimEnd('\\', '/')) ?? "";

                string root = dir;
                if (dir.Length > 0)
                {
                    foreach (string d in dirs)
                    {
                        if (d.Length > 0 && d.Length < root.Length &&
                            dir.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase))
                        {
                            root = d;   // 子目录归到主程序目录下
                        }
                    }
                }

                string nameKey = TextUtil.FirstNonEmpty(r.SoftwareName, r.DisplayName, r.ProcessName, r.WindowClass, r.TitleKeyword);
                string key = TextUtil.FirstNonEmpty(root, nameKey);
                if (key.Length == 0) key = "自定义规则";

                PopupGroup group;
                if (!byKey.TryGetValue(key, out group))
                {
                    // 组名：主程序（目录等于 root 的那条）的软件名，否则目录名
                    string mainName = "";
                    if (root.Length > 0)
                    {
                        foreach (var other in rules)
                        {
                            if (string.IsNullOrEmpty(other.ProgramPath)) continue;
                            string od = System.IO.Path.GetDirectoryName(other.ProgramPath.TrimEnd('\\', '/')) ?? "";
                            if (string.Equals(od, root, StringComparison.OrdinalIgnoreCase))
                            {
                                mainName = TextUtil.FirstNonEmpty(other.SoftwareName, other.ProcessName);
                                if (mainName.Length > 0) break;
                            }
                        }
                    }
                    string displayName = mainName.Length > 0 ? mainName
                                     : (root.Length > 0 ? System.IO.Path.GetFileName(root) : key);

                    group = new PopupGroup
                    {
                        Name = displayName,
                        ProcessName = TextUtil.FirstNonEmpty(r.SoftwareName, r.ProcessName),
                        Path = root
                    };
                    byKey[key] = group;
                    groups.Add(group);
                }
                group.Rules.Add(r);
            }

            // 用户指定的「归属软件」：并入同名组（没有就新建一个）
            foreach (var r in rules)
            {
                if (string.IsNullOrEmpty(r.Family)) continue;
                PopupGroup target = null;
                foreach (var g in groups)
                    if (string.Equals(g.Name, r.Family, StringComparison.OrdinalIgnoreCase)) { target = g; break; }
                if (target == null)
                {
                    target = new PopupGroup { Name = r.Family, ProcessName = TextUtil.FirstNonEmpty(r.SoftwareName, r.ProcessName), Path = "" };
                    groups.Add(target);
                }
                if (!target.Rules.Contains(r))
                {
                    // 从原来的组里挪过来
                    foreach (var g in groups)
                        g.Rules.Remove(r);
                    target.Rules.Add(r);
                }
            }

            // 清掉挪空的分组
            for (int i = groups.Count - 1; i >= 0; i--)
                if (groups[i].Rules.Count == 0) groups.RemoveAt(i);

            foreach (var g in groups) g.BlockCount = blockCountOf(g.Rules);

            // 「自定义规则」放最前（与火绒一致）
            groups.Sort(delegate(PopupGroup a, PopupGroup b)
            {
                if (a.Name == "自定义规则") return -1;
                if (b.Name == "自定义规则") return 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return groups;
        }
    }
}
