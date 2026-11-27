using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

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

    /// <summary>窗口拦截的动作。只有「隐藏」一种：窗口藏起、程序在后台正常运行。</summary>
    [DataContract]
    public enum WindowAction
    {
        // 只留 Hide，且值保持 1：旧配置里 Actions=[1] 仍是隐藏
        [EnumMember] Hide = 1,
    }

    /// <summary>截图采样点：相对坐标（0~1）+ 颜色。用于「截图拦截」的画面比对。</summary>
    [DataContract]
    public sealed class CapturePoint
    {
        [DataMember] public double X { get; set; }
        [DataMember] public double Y { get; set; }
        [DataMember] public int R { get; set; }
        [DataMember] public int G { get; set; }
        [DataMember] public int B { get; set; }

        public CapturePoint() { }
        public CapturePoint(double x, double y, int r, int g, int b) { X = x; Y = y; R = r; G = g; B = b; }
    }

    /// <summary>一条窗口拦截规则：匹配条件 + 动作集合。</summary>
    [DataContract]
    public sealed class WindowRule
    {
        [DataMember] public string TitleKeyword { get; set; }
        [DataMember] public string ProcessName { get; set; }
        [DataMember] public string WindowClass { get; set; }
        [DataMember] public string DisplayName { get; set; }      // 列表显示名（采集到的窗口标题/软件名）
        [DataMember] public string SoftwareName { get; set; }     // 显示用的软件名（不参与匹配，用于分组）
        [DataMember] public string ProgramPath { get; set; }      // 被拦截程序的完整路径（用于判断是不是同一个软件）
        [DataMember] public string RecognizedTitle { get; set; }  // 采集时识别到的窗口标题（只用于界面展示）
        [DataMember] public string RecognizedClass { get; set; }  // 采集时识别到的窗口类名（只用于界面展示）
        [DataMember] public string Family { get; set; }           // 归属软件（用户指定，留空则自动按路径判断）
        [DataMember] public bool AndMode { get; set; }          // false=任一字段命中，true=都要命中
        [DataMember] public List<WindowAction> Actions { get; set; }
        [DataMember] public bool Enabled { get; set; }

        // ---- 截图拦截相关 ----
        [DataMember] public bool UseImage { get; set; }         // 是否启用截图比对
        [DataMember] public List<CapturePoint> Points { get; set; }  // 采样点阵（相对坐标+颜色）
        // 多套画面模板：同一个窗口在不同内容状态下的点阵。匹配时任一套达标即命中，
        // 解决"窗口内容会变、单套模板覆盖不住"导致的间歇漏拦。为空时退回单套 Points。
        [DataMember] public List<List<CapturePoint>> Templates { get; set; }
        [DataMember] public int MatchRatio { get; set; }        // 匹配阈值（百分比，采集时按画面抖动自动定，也可手动改）
        // 自学习阈值：AutoThreshold 为 true 时，守卫根据"历史成功拦住时的分数"自动算出来。
        // 用户不需要碰这个数字；一旦手动改阈值（AutoThreshold=false）就失效。
        [DataMember] public int? LearnedRatio { get; set; }
        [DataMember] public bool AutoThreshold { get; set; }     // 阈值是否为采集时自动算出（手动改后转手动）
        [DataMember] public int ColorTolerance { get; set; }    // 颜色容差（每通道，默认 16）
        // 采集时窗口的尺寸：给"纯截图规则"做廉价粗筛（没有属性条件时用它跳过明显不相关的窗口）
        [DataMember] public int? CapturedWidth { get; set; }
        [DataMember] public int? CapturedHeight { get; set; }
        [DataMember] public int? WidthMin { get; set; }         // 尺寸范围（可空）
        [DataMember] public int? WidthMax { get; set; }
        [DataMember] public int? HeightMin { get; set; }
        [DataMember] public int? HeightMax { get; set; }
        // 缩略图（采集时保存，悬停预览用）。JSON 里以 base64 存成 ThumbnailB64：
        // byte[] 直接序列化会写成逐字节数字数组，体积膨胀约 4.5 倍。
        public byte[] Thumbnail { get; set; }
        [DataMember] public string ThumbnailB64 { get; set; }   // 新格式：base64

        // 旧格式（逐字节数组）只读兼容：老配置里键名也是 Thumbnail，新配置不再写出
        [DataMember(Name = "Thumbnail", EmitDefaultValue = false)]
        public byte[] ThumbnailLegacy { get; set; }

        // 规则指纹：不参与序列化（由条件算出来），Load/Save 时刷新
        public string Fingerprint { get; set; }
        [DataMember] public string CreatedAt { get; set; }   // 创建时间，用于"从未生效"提示

        public WindowRule()
        {
            TitleKeyword = "";
            ProcessName = "";
            WindowClass = "";
            DisplayName = "";
            SoftwareName = "";
            ProgramPath = "";
            RecognizedTitle = "";
            RecognizedClass = "";
            Family = "";
            AndMode = false;
            // 默认动作 = 隐藏（窗口藏起、程序在后台正常运行），不是关闭
            Actions = new List<WindowAction> { WindowAction.Hide };
            Enabled = true;
            UseImage = false;
            Points = new List<CapturePoint>();
            Templates = new List<List<CapturePoint>>();
            MatchRatio = 80;
            AutoThreshold = false;
            LearnedRatio = null;
            ColorTolerance = 16;
            Fingerprint = "";
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        /// <summary>
        /// 生效阈值（高线）：自动模式下优先用自学习值，否则用采集/手动的 MatchRatio。
        /// 自学习只会"放松"（最多比采集阈值低 10），不会收紧，避免越学越拦不住。
        /// </summary>
        public int EffectiveThreshold()
        {
            if (AutoThreshold && LearnedRatio.HasValue && LearnedRatio.Value > 0)
                return Math.Min(90, LearnedRatio.Value);
            return MatchRatio > 0 ? MatchRatio : 80;
        }

        /// <summary>由界面采集到的一条信息生成默认规则（默认动作=隐藏）。</summary>
        public static WindowRule From(WindowInfo info)
        {
            string name = string.IsNullOrEmpty(info.Title) ? info.ProcessName : info.Title;
            return new WindowRule
            {
                TitleKeyword = info.Title ?? "",
                ProcessName = info.ProcessName ?? "",
                WindowClass = info.ClassName ?? "",
                DisplayName = name ?? "",
                SoftwareName = info.ProcessName ?? "",
                ProgramPath = Native.ProcessPath(info.ProcessId),
            };
        }

        /// <summary>尺寸范围是否包含给定宽高（未设置的边界不限制）。</summary>
        public bool SizeInRange(int width, int height)
        {
            if (WidthMin.HasValue && width < WidthMin.Value) return false;
            if (WidthMax.HasValue && width > WidthMax.Value) return false;
            if (HeightMin.HasValue && height < HeightMin.Value) return false;
            if (HeightMax.HasValue && height > HeightMax.Value) return false;
            return true;
        }

        public bool Matches(WindowInfo info)
        {
            int hit = 0, total = 0;

            if (!string.IsNullOrEmpty(TitleKeyword))
            {
                total++;
                if (info.Title != null &&
                    info.Title.IndexOf(TitleKeyword, StringComparison.OrdinalIgnoreCase) >= 0) hit++;
            }
            if (!string.IsNullOrEmpty(ProcessName))
            {
                total++;
                if (string.Equals(info.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase)) hit++;
            }
            if (!string.IsNullOrEmpty(WindowClass))
            {
                total++;
                if (string.Equals(info.ClassName, WindowClass, StringComparison.OrdinalIgnoreCase)) hit++;
            }
            if (total == 0) return UseImage;   // 纯截图规则：属性无条件，交由截图比对决定
            return AndMode ? hit == total : hit > 0;
        }

        /// <summary>
        /// 规则是否真的能拦：要么有属性条件，要么有可用来比对的截图点阵。
        /// （只有 UseImage 而没有采样点的规则永远比对不出结果，等于死规则。）
        /// </summary>
        public bool IsUsable()
        {
            bool hasAttr = !string.IsNullOrEmpty(TitleKeyword) || !string.IsNullOrEmpty(ProcessName) ||
                           !string.IsNullOrEmpty(WindowClass);
            return hasAttr || (UseImage && HasImagePoints());
        }

        /// <summary>有没有可用的画面模板（多套模板或单套 Points 都算）。</summary>
        public bool HasImagePoints()
        {
            if (Points != null && Points.Count > 0) return true;
            if (Templates != null)
                foreach (var t in Templates)
                    if (t != null && t.Count > 0) return true;
            return false;
        }

        /// <summary>所有画面模板（多套优先，否则把 Points 当作唯一一套）。</summary>
        public List<List<CapturePoint>> GetTemplates()
        {
            var result = new List<List<CapturePoint>>();
            if (Templates != null)
                foreach (var t in Templates)
                    if (t != null && t.Count > 0) result.Add(t);
            if (result.Count == 0 && Points != null && Points.Count > 0)
                result.Add(Points);
            return result;
        }

        /// <summary>规则没有可匹配条件，属于无效规则。</summary>
        public bool IsEmpty()
        {
            return !IsUsable();
        }

        public string Describe()
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(TitleKeyword)) parts.Add("标题含“" + TitleKeyword + "”");
            if (!string.IsNullOrEmpty(ProcessName)) parts.Add("进程=" + ProcessName);
            if (!string.IsNullOrEmpty(WindowClass)) parts.Add("类=" + WindowClass);
            if (WidthMin.HasValue || WidthMax.HasValue || HeightMin.HasValue || HeightMax.HasValue)
            {
                parts.Add("尺寸 " + FmtRange(WidthMin, WidthMax) + "×" + FmtRange(HeightMin, HeightMax));
            }
            string match = parts.Count == 0
                ? (string.IsNullOrEmpty(DisplayName) ? SoftwareName : DisplayName)
                : string.Join(AndMode ? " 且 " : " 或 ", parts.ToArray());
            if (string.IsNullOrEmpty(match))
                match = UseImage
                    ? ((Points != null && Points.Count > 0) ? "（仅截图）" : "（仅截图，缺采样点）")
                    : "（无匹配条件）";
            var acts = (Actions != null && Actions.Count > 0) ? ActionName() : "无动作";
            int tplCount = Templates != null ? Templates.Count : 0;
            int pointCount = Points != null ? Points.Count : 0;
            string image = UseImage && (tplCount > 0 || pointCount > 0)
                ? "截图" + pointCount + "点" + (tplCount > 1 ? "×" + tplCount + "套" : "")
                  + "/" + EffectiveThreshold() + "%"
                : "";
            return match + " → " + acts + (image.Length > 0 ? " · " + image : "");
        }

        private static string FmtRange(int? min, int? max)
        {
            if (min.HasValue && max.HasValue) return min + "~" + max;
            if (min.HasValue) return "≥" + min;
            if (max.HasValue) return "≤" + max;
            return "?";
        }

        /// <summary>动作只有「隐藏」一种；旧配置里残留的关闭/最小化也按隐藏处理。</summary>
        public static string ActionName()
        {
            return "隐藏";
        }
    }

    /// <summary>一条运行日志：窗口拦截 或 托盘隐藏/恢复。</summary>
    [DataContract]
    public sealed class BlockRecord
    {
        [DataMember] public string Time { get; set; }
        [DataMember] public string Category { get; set; }   // 窗口拦截 / 托盘隐藏
        [DataMember] public string Title { get; set; }
        [DataMember] public string ProcessName { get; set; }
        [DataMember] public string Action { get; set; }
        [DataMember] public bool Success { get; set; }
        [DataMember] public string Note { get; set; }      // 备注：失败原因 / 命中率 / 自动校准说明
        [DataMember] public int Count { get; set; }        // 合并计数：同因重复只累加，不新增行
        [DataMember] public int Rate { get; set; }         // 截图命中率（未比对为 -1）
        [DataMember] public string RuleId { get; set; }    // 规则指纹，用于合并与归属

        /// <summary>统一构造，避免各处漏字段。</summary>
        public static BlockRecord Make(string category, string title, string processName,
                                        string action, bool success, string note, int rate, string ruleId)
        {
            return new BlockRecord
            {
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Category = category ?? "",
                Title = title ?? "",
                ProcessName = processName ?? "",
                Action = action ?? "",
                Success = success,
                Note = note ?? "",
                Count = 1,
                Rate = rate,
                RuleId = ruleId ?? "",
            };
        }

        public BlockRecord()
        {
            Time = "";
            Category = "";
            Title = "";
            ProcessName = "";
            Action = "";
            Note = "";
            Count = 1;
            Rate = -1;
            RuleId = "";
        }
    }

    [DataContract]
    public sealed class Settings
    {
        [DataMember] public int Version { get; set; }
        [DataMember] public List<IconRule> Hidden { get; set; }
        [DataMember] public List<IconRule> Recovery { get; set; }
        [DataMember] public bool MinimizeToTrayOnStart { get; set; }
        [DataMember] public bool HideSelfIcon { get; set; }
        [DataMember] public bool ScanOverflow { get; set; }
        [DataMember] public bool HotkeyEnabled { get; set; }
        [DataMember] public int HotkeyMode { get; set; }
        [DataMember] public uint HotkeyModifiers { get; set; }
        [DataMember] public int HotkeyKey { get; set; }
        [DataMember] public List<int> Sequence { get; set; }
        [DataMember] public int SequenceWindowMs { get; set; }
        [DataMember] public bool PopupGuardEnabled { get; set; }
        [DataMember] public List<WindowRule> WindowRules { get; set; }
        [DataMember] public int PopupGuardIntervalMs { get; set; }
        [DataMember] public bool HideSelfFromCapture { get; set; }
        [DataMember] public bool HideUntriggeredRules { get; set; }   // 规则页「隐藏未触发规则」

        // ---- P2：守卫的智能化开关 ----
        // 可空：旧配置缺这个键时取期望值（true），显式写 false 则尊重用户
        [DataMember] public bool? PopupGuardDiagnosticsEnabled { get; set; }     // 记录判定（可关）
        [DataMember] public bool? PopupGuardFailureBackoff { get; set; }          // 失败退避（可空，空=开）
        [DataMember] public bool PopupGuardAutoCalibrate { get; set; }            // 自动校准阈值（默认关）
        [DataMember] public int PopupGuardAutoCalibrateCount { get; set; }        // 触发所需连续次数
        [DataMember] public int PopupGuardAutoCalibrateTolerance { get; set; }    // 距阈值多近才算"临界"
        [DataMember] public int PopupGuardAutoCalibrateMaxDrop { get; set; }      // 累计最多下调多少（防误杀）
        // 临界区宽度：分数低于阈值但在"阈值−该值"以上时，隔 150ms 复检一次再决定。
        // 可空 = 自动（默认 5）。设为 0 表示关闭复检。
        [DataMember] public int? PopupGuardBorderBand { get; set; }
        [DataMember] public int? PopupGuardRecheckMs { get; set; }               // 复检间隔（毫秒，可空，空=150）

        /// <summary>是否记录判定日志。可空字段：缺失（旧配置）= 开。</summary>
        internal bool DiagnosticsEnabled { get { return PopupGuardDiagnosticsEnabled ?? true; } }

        /// <summary>是否开启失败退避。可空字段：缺失（旧配置）= 开。</summary>
        internal bool FailureBackoff { get { return PopupGuardFailureBackoff ?? true; } }

        /// <summary>临界区宽度：分数低于阈值但在"阈值 − 该值"以上时，隔一次复检再决定。空值取 5。</summary>
        internal int BorderBand
        {
            get { int v = PopupGuardBorderBand ?? 5; return v >= 0 && v <= 50 ? v : 5; }   // 上限 50：再大等于全放行
        }

        /// <summary>复检间隔（毫秒），夹在 0~2000。</summary>
        internal int RecheckMs
        {
            get { return PopupGuardRecheckMs.HasValue && PopupGuardRecheckMs.Value >= 0 && PopupGuardRecheckMs.Value <= 2000 ? PopupGuardRecheckMs.Value : 150; }
        }

        public Settings()
        {
            Version = 3;
            Hidden = new List<IconRule>();
            Recovery = new List<IconRule>();
            MinimizeToTrayOnStart = true;
            HideSelfIcon = false;
            ScanOverflow = false;
            HotkeyEnabled = true;
            HotkeyMode = 0;
            HotkeyModifiers = VirtualKeys.MOD_CONTROL | VirtualKeys.MOD_ALT;
            HotkeyKey = 0x54; // T
            Sequence = new List<int>();
            SequenceWindowMs = 800;
            PopupGuardEnabled = false;
            WindowRules = new List<WindowRule>();
            // 轮询间隔：一次全量扫描约 2ms，400ms 一次占用很低，拦截够快
            PopupGuardIntervalMs = 400;
            HideSelfFromCapture = false;
            HideUntriggeredRules = false;
            PopupGuardAutoCalibrate = false;          // 默认关：自动改阈值有误杀风险
            PopupGuardAutoCalibrateCount = 5;
            PopupGuardAutoCalibrateTolerance = 5;
            PopupGuardAutoCalibrateMaxDrop = 10;      // 累计下调超过这个值就停手，提示用户重新采集
            PopupGuardBorderBand = null;              // 空 = 自动（5）
        }
    }

    /// <summary>
    /// 配置迁移。幂等：每一步都看版本号，重复执行没有副作用。
    /// 每次读配置都会跑一遍，顺带补齐新字段的默认值。
    /// </summary>
    internal static class SettingsMigration
    {
        public static void Apply(Settings s)
        {
            if (s == null) return;
            if (s.Version < 2)
            {
                // v1→v2：预留（暂无需要改的结构）
                s.Version = 2;
            }
            if (s.Version < 3)
            {
                // v2→v3：规则补指纹与创建时间
                foreach (var r in s.WindowRules)
                {
                    if (string.IsNullOrEmpty(r.CreatedAt))
                        r.CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    r.Fingerprint = RuleFingerprint.Of(r);
                }
                s.Version = 3;
            }
            // 新字段缺失时（极旧的配置）补默认值，避免 0/false 让功能"消失"
            // 极旧配置升级：Popuard 相关的可空字段缺失时会取期望值，无需在此补；
            // 这里只处理 int 型新字段的兜底（缺字段时默认 0，而 0 不是负数）
            if (s.PopupGuardAutoCalibrateCount <= 0) s.PopupGuardAutoCalibrateCount = 5;
            if (s.PopupGuardAutoCalibrateTolerance <= 0) s.PopupGuardAutoCalibrateTolerance = 5;
            if (s.PopupGuardAutoCalibrateMaxDrop <= 0) s.PopupGuardAutoCalibrateMaxDrop = 10;
        }
    }

    /// <summary>JSON settings under %LOCALAPPDATA%\TrayHider.</summary>
    public sealed class SettingsStore
    {
        /// <summary>持久化失败时留个痕迹（不能静默，否则用户以为存上了）。</summary>
        internal static void LogStoreError(string logFile, string title, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + title;
                if (ex != null) line += Environment.NewLine + ex.ToString();
                File.AppendAllText(Path.Combine(Folder, logFile), line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private static readonly string DefaultFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayHider");

        /// <summary>
        /// 配置目录，默认 %LOCALAPPDATA%\TrayHider。
        /// 自动化测试可改写到临时目录，避免测试数据覆盖真实配置。
        /// </summary>
        internal static string Folder = DefaultFolder;

        private static string FilePath { get { return Path.Combine(Folder, "settings.json"); } }

        /// <summary>
        /// 规则列表的读写锁：UI 线程增删改，守卫线程遍历。
        /// 守卫侧每轮拍快照遍历，避免边改边遍历导致半途作废。
        /// </summary>
        internal static readonly object RulesGate = new object();

        /// <summary>
        /// 读配置。三层兜底，任何情况下都能返回可用配置——配置坏掉不能让用户打不开工具。
        ///   1) 正常反序列化；2) 宽容读（先洗掉坏字段）；3) 默认配置。
        /// </summary>
        public static Settings Load()
        {
            Settings settings = null;
            Exception first = null;

            try { settings = ReadCore(); }
            catch (Exception ex) { first = ex; }

            if (settings == null)
            {
                try { settings = ReadLenient(); }
                catch (Exception ex) { WriteConfigError("宽容读取失败", first ?? ex); }
            }

            if (settings == null)
            {
                settings = new Settings();
                WriteConfigError("配置无法解析，已回退到默认配置", first);
            }
            return settings;
        }

        private static Settings ReadCore()
        {
            if (!File.Exists(FilePath)) return new Settings();
            using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var settings = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
                if (settings == null) return new Settings();
                Normalize(settings);
                return settings;
            }
        }

        /// <summary>宽容读：先把已知会抛异常的字段（缩略图 base64）洗掉，再交给反序列化。</summary>
        private static Settings ReadLenient()
        {
            string text = File.ReadAllText(FilePath, Encoding.UTF8);
            text = SanitizeThumbnailB64(text);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(text)))
            {
                var settings = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(ms);
                if (settings == null) return new Settings();
                Normalize(settings);
                return settings;
            }
        }

        private static readonly Regex ThumbnailB64Field = new Regex(
            "\"ThumbnailB64\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);

        /// <summary>把非法的 ThumbnailB64 值置空。坏 base64 只该丢缩略图，不该让整个配置读不出来。</summary>
        private static string SanitizeThumbnailB64(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            return ThumbnailB64Field.Replace(json, delegate(Match m)
            {
                string value = m.Groups[1].Value;
                if (value.Length == 0) return m.Value;
                try { Convert.FromBase64String(value); return m.Value; }
                catch { return "\"ThumbnailB64\":null"; }
            });
        }

        /// <summary>
        /// 把规则的缩略图统一成字节（兼容 base64 / 旧字节数组两种 JSON 来源），
        /// 并刷新 ThumbnailB64、清掉 ThumbnailLegacy。导出、导入、存取共用这一处。
        /// </summary>
        internal static void EncodeThumbnail(WindowRule r)
        {
            if (r == null) return;
            // 内存里已有字节就直接用；没有再从 base64 / 旧数组里解出来
            if (r.Thumbnail == null || r.Thumbnail.Length == 0) r.Thumbnail = DecodeThumbnail(r);
            r.ThumbnailB64 = r.Thumbnail == null ? null : Convert.ToBase64String(r.Thumbnail);
            r.ThumbnailLegacy = null;
            r.Fingerprint = RuleFingerprint.Of(r);
        }

        private static void Normalize(Settings settings)
        {
            if (settings.Hidden == null) settings.Hidden = new List<IconRule>();
            if (settings.Recovery == null) settings.Recovery = new List<IconRule>();
            if (settings.WindowRules == null) settings.WindowRules = new List<WindowRule>();
            foreach (var r in settings.WindowRules) EncodeThumbnail(r);
            SettingsMigration.Apply(settings);
        }

        private static void WriteConfigError(string title, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, "config-error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + title + "\r\n" +
                    (ex == null ? "" : ex.ToString()) + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        private static bool reportedBadThumbnail;

        /// <summary>把规则里的缩略图统一解成字节：优先新格式 base64，其次旧格式的字节数组。</summary>
        private static byte[] DecodeThumbnail(WindowRule r)
        {
            if (r == null) return null;
            if (!string.IsNullOrEmpty(r.ThumbnailB64))
            {
                try { return Convert.FromBase64String(r.ThumbnailB64); }
                catch (FormatException)
                {
                    // 坏 base64 只丢缩略图，不影响规则与启动；记一次日志便于排查
                    r.ThumbnailB64 = null;
                    if (!reportedBadThumbnail)
                    {
                        reportedBadThumbnail = true;
                        WriteConfigError("缩略图 base64 无法解析，已忽略该缩略图", null);
                    }
                }
            }
            return r.ThumbnailLegacy;
        }

        /// <summary>
        /// 写盘串行锁：避免两个 Save 同时写同一个 .tmp（会互相覆盖，可能写出坏文件）。
        /// </summary>
        private static readonly object SaveGate = new object();

        public static void Save(Settings settings)
        {
            try
            {
                lock (SaveGate)
                {
                    // 序列化必须在 RulesGate 内：UI 会增删规则，守卫线程会改学习阈值，
                    // 锁外序列化可能抛"集合已修改"，被下面的 catch 吞掉 → 改动静默丢失。
                    lock (RulesGate)
                    {
                        if (settings.WindowRules != null)
                            foreach (var r in settings.WindowRules) EncodeThumbnail(r);

                        Directory.CreateDirectory(Folder);
                        string temp = FilePath + ".tmp";
                        try
                        {
                            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                new DataContractJsonSerializer(typeof(Settings)).WriteObject(stream, settings);
                                stream.Flush(true);
                            }
                            // File.Replace 自带 .bak；失败退化为直接覆盖（不把异常抛给界面）
                            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
                            else File.Move(temp, FilePath);
                        }
                        catch { if (File.Exists(temp)) File.Delete(temp); throw; }
                    }
                }
            }
            catch (Exception ex)
            {
                // 存不下来也不能让界面崩：写个日志便于排查（比如磁盘/权限问题）
                try
                {
                    File.AppendAllText(Path.Combine(Folder, "save-error.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + "\r\n");
                }
                catch { }
            }
        }

    }

    /// <summary>拦截记录，单独存 blocklog.json，与设置文件解耦。</summary>
    public sealed class BlockLog
    {
        private const int MaxPerCategory = 200;   // 每类各自限量，窗口拦截不会把托盘记录整体挤掉
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

        private static string Folder { get { return SettingsStore.Folder; } }
        private static string FilePath { get { return Path.Combine(Folder, "blocklog.json"); } }

        private readonly List<BlockRecord> records = new List<BlockRecord>();
        private readonly object gate = new object();
        private readonly object dropGate = new object();
        private int droppedRecords;        // 因锁竞争被丢弃的条数（>0 说明日志不完整）


        public BlockLog()
        {
            Load();
        }

        public List<BlockRecord> Snapshot()
        {
            if (!Monitor.TryEnter(gate, 2000)) return new List<BlockRecord>();
            try { return records.ToList(); }
            finally { Monitor.Exit(gate); }
        }

        /// <summary>
        /// 追加一条记录并裁剪。返回 true 表示"新增了一行"；
        /// 返回 false 表示只是把同类重复合并进了已有行（Count+1），界面不必为此重建。
        /// </summary>
        public bool Add(BlockRecord record)
        {
            if (record == null) return false;
            if (record.Count <= 0) record.Count = 1;
            if (string.IsNullOrEmpty(record.Time)) record.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            // 用带超时的锁：UI 线程也会来读记录，锁竞争时不能把后台线程卡死。
            if (!Monitor.TryEnter(gate, 2000))
            {
                lock (dropGate)
                {
                    if (droppedRecords == 0)
                        SettingsStore.LogStoreError("blocklog-error.log", "运行日志因锁竞争被丢弃（第一条）", null);
                    droppedRecords++;
                }
                return false;
            }
            try
            {
                // 同类别/同规则/同进程/同标题/同动作，且相隔不到 60 秒：只累加计数，不新增行。
                // 这样"同一个窗口每次都拦不住"不会把日志刷满。
                if (records.Count > 0)
                {
                    var last = records[records.Count - 1];
                    if (SameCategory(last.Category, record.Category) &&
                        string.Equals(last.RuleId ?? "", record.RuleId ?? "", StringComparison.Ordinal) &&
                        string.Equals(last.ProcessName ?? "", record.ProcessName ?? "", StringComparison.Ordinal) &&
                        string.Equals(last.Title ?? "", record.Title ?? "", StringComparison.Ordinal) &&
                        string.Equals(last.Action ?? "", record.Action ?? "", StringComparison.Ordinal) &&
                        WithinSeconds(last.Time, record.Time, 60))
                    {
                        last.Count += record.Count;
                        last.Success = record.Success;
                        last.Rate = record.Rate;
                        last.Note = record.Note;
                        last.Time = record.Time;
                        SaveLocked();
                        return false;
                    }
                }
                records.Add(record);
                TrimLocked();
                SaveLocked();
                return true;
            }
            catch { return false; }
            finally
            {
                Monitor.Exit(gate);
            }
        }

        /// <summary>历史记录里类别为空时按动作归类，避免出现一堆空白行。</summary>
        private static bool SameCategory(string a, string b)
        {
            return string.Equals(NormalizeCategory(a), NormalizeCategory(b), StringComparison.Ordinal);
        }

        private static string NormalizeCategory(string category)
        {
            if (!string.IsNullOrEmpty(category)) return category;
            return "托盘隐藏";   // 早期版本没写类别，基本都是托盘隐藏
        }

        private static bool WithinSeconds(string older, string newer, int seconds)
        {
            DateTime a, b;
            if (!DateTime.TryParse(older, out a) || !DateTime.TryParse(newer, out b)) return false;
            return Math.Abs((b - a).TotalSeconds) <= seconds;
        }

        public void Clear()
        {
            lock (gate)
            {
                records.Clear();
                SaveLocked();
            }
        }

        /// <summary>
        /// 一次性压实历史日志：类别为空的归到「托盘隐藏」，并把相邻的同因记录合并成一条（计数累加）。
        /// 早期版本留下的几百条重复记录会在这里被压掉。
        /// </summary>
        private void CompactLocked()
        {
            foreach (var r in records)
            {
                if (string.IsNullOrEmpty(r.Category)) r.Category = "托盘隐藏";
                if (r.Count <= 0) r.Count = 1;
            }
            var merged = new List<BlockRecord>();
            foreach (var r in records)
            {
                if (merged.Count > 0)
                {
                    var last = merged[merged.Count - 1];
                    if (string.Equals(last.Category ?? "", r.Category ?? "", StringComparison.Ordinal) &&
                        string.Equals(last.RuleId ?? "", r.RuleId ?? "", StringComparison.Ordinal) &&
                        string.Equals(last.ProcessName ?? "", r.ProcessName ?? "", StringComparison.Ordinal) &&
                        string.Equals(last.Title ?? "", r.Title ?? "", StringComparison.Ordinal) &&
                        string.Equals(last.Action ?? "", r.Action ?? "", StringComparison.Ordinal) &&
                        WithinSeconds(last.Time, r.Time, 60))
                    {
                        last.Count += r.Count;
                        last.Time = r.Time;
                        continue;
                    }
                }
                merged.Add(r);
            }
            records.Clear();
            records.AddRange(merged);
        }

        private void TrimLocked()
        {
            var cutoff = DateTime.Now - MaxAge;
            var expired = new List<BlockRecord>();
            records.RemoveAll(delegate(BlockRecord r)
            {
                DateTime time;
                if (!DateTime.TryParse(r.Time, out time)) return true;
                if (time < cutoff) { expired.Add(r); return true; }
                return false;
            });
            if (expired.Count > 0) ArchiveLocked(expired);
            // 按类别分别限量：从最旧的开始删，直到该类不超限
            var totals = new Dictionary<string, int>();
            foreach (var r in records)
            {
                string cat = NormalizeCategory(r.Category);
                int c;
                totals.TryGetValue(cat, out c);
                totals[cat] = c + 1;
            }
            foreach (var kv in totals)
            {
                int excess = kv.Value - MaxPerCategory;
                if (excess <= 0) continue;
                for (int i = 0; i < records.Count && excess > 0; i++)
                {
                    if (NormalizeCategory(records[i].Category) == kv.Key)
                    {
                        records.RemoveAt(i);
                        i--;
                        excess--;
                    }
                }
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var loaded = (List<BlockRecord>)new DataContractJsonSerializer(typeof(List<BlockRecord>)).ReadObject(stream);
                    if (loaded != null)
                    {
                        records.Clear();
                        records.AddRange(loaded);
                        CompactLocked();   // 压实历史：清洗空类别、合并相邻重复
                        TrimLocked();
                    }
                }
            }
            catch (Exception ex)
            {
                // 日志文件损坏不能静默：清空会让用户以为"从没拦过"
                records.Clear();
                SettingsStore.LogStoreError("blocklog-error.log", "运行日志读取失败（已按空处理）", ex);
            }
        }

        /// <summary>把过期记录归档到 blocklog-archive.json，再从主日志里删掉。</summary>
        private void ArchiveLocked(List<BlockRecord> expired)
        {
            try
            {
                string archivePath = Path.Combine(Folder, "blocklog-archive.json");
                var all = new List<BlockRecord>();
                if (File.Exists(archivePath))
                {
                    try
                    {
                        using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            var old = (List<BlockRecord>)new DataContractJsonSerializer(typeof(List<BlockRecord>)).ReadObject(stream);
                            if (old != null) all.AddRange(old);
                        }
                    }
                    catch { }
                }
                all.AddRange(expired);
                if (all.Count > 1000) all.RemoveRange(0, all.Count - 1000);
                using (var stream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    new DataContractJsonSerializer(typeof(List<BlockRecord>)).WriteObject(stream, all);
            }
            catch { }
        }

        private void SaveLocked()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string temp = FilePath + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(List<BlockRecord>)).WriteObject(stream, records);
                    stream.Flush(true);
                }
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
                else File.Move(temp, FilePath);
            }
            catch (Exception ex) { SettingsStore.LogStoreError("blocklog-error.log", "运行日志写入失败", ex); }
        }
    }

    /// <summary>Applies and reverses hide rules.</summary>
    public static class RuleEngine
    {
        public static bool MatchesAny(IEnumerable<IconRule> rules, TrayItem item)
        {
            foreach (var rule in rules) if (rule.Matches(item)) return true;
            return false;
        }

        private static void Log(BlockLog log, string category, string title, string process,
                                string action, bool success)
        {
            if (log == null) return;
            log.Add(new BlockRecord
            {
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Category = category,
                Title = title ?? "",
                ProcessName = process ?? "",
                Action = action,
                Success = success,
            });
        }

        /// <summary>Hides every live icon matched by a rule. Returns the number actually hidden.</summary>
        public static int Apply(Settings settings, List<TrayItem> items, BlockLog log)
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
                    Log(log, "托盘隐藏", item.Name, item.Path, "隐藏", true);
                }
            }
            if (changed > 0) SettingsStore.Save(settings);
            return changed;
        }

        public static int Restore(Settings settings, List<TrayItem> items, IList<TrayItem> only, BlockLog log)
        {
            int changed = 0;
            foreach (var item in only)
            {
                // 不依赖 Recovery 记录：只要当前是隐藏状态就恢复。
                // 否则用旧版本隐藏的、或删过设置文件后，恢复会被静默跳过。
                if (item.State == 1 && TrayApi.SetHidden(item, false))
                {
                    item.State = 0;
                    changed++;
                    Log(log, "托盘隐藏", item.Name, item.Path, "恢复", true);
                }
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

        public static int RestoreAll(Settings settings, List<TrayItem> items, BlockLog log)
        {
            int changed = 0;
            // 同上：把所有当前处于隐藏状态的图标都恢复，不看 Recovery 记录。
            foreach (var item in items)
            {
                if (item.State == 1 && TrayApi.SetHidden(item, false))
                {
                    item.State = 0;
                    changed++;
                    Log(log, "托盘隐藏", item.Name, item.Path, "恢复", true);
                }
            }
            settings.Hidden.Clear();
            settings.Recovery.Clear();
            SettingsStore.Save(settings);
            return changed;
        }
    }
}
