using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace TrayHider
{
    /// <summary>
    /// 窗口/弹窗拦截引擎。独立后台线程运行，与托盘扫描互不影响。
    ///
    /// 只用车轮询（默认 400ms 一次），不用 SetWinEventHook——实测全局事件钩子
    /// 在本工具「隐藏/显示主窗口」时会导致进程崩溃，而轮询已足够及时。
    ///
    /// 动作：只有「隐藏」——ShowWindow(SW_HIDE)，窗口藏起、程序后台继续运行。
    ///
    /// 可靠性设计（P0）：
    ///   1) 用 ShowWindow 的返回值 + IsWindowVisible 复核，才算真的拦住；
    ///   2) 拦不住时指数退避（1s→2s→…→60s），不再每轮重试、不再刷日志；
    ///   3) 每一次判定（拦住 / 为什么跳过 / 失败几次）都进诊断缓冲，必要时写运行日志；
    ///   4) 藏起来的窗口记进 hidden.json，异常退出后下次启动自动恢复。
    /// </summary>
    internal sealed class PopupGuard : IDisposable
    {
        private readonly Settings settings;
        private readonly BlockLog log;
        private Thread worker;
        private volatile bool running;
        private volatile bool disposed;

        // 进程名缓存：PID 会被系统复用，所以给缓存加个短时效（30 秒），
        // 过期就重查，避免某个窗口拿到上一个进程的名字。
        private struct CachedName
        {
            public string Name;
            public DateTime Until;
        }

        private static readonly Dictionary<uint, CachedName> nameCache = new Dictionary<uint, CachedName>();
        private static readonly object nameCacheGate = new object();
        private static readonly TimeSpan NameCacheTtl = TimeSpan.FromSeconds(30);

        // 被「隐藏」动作藏起来的窗口：句柄 → 藏起它的规则。
        // 同一时刻只有一条规则会藏着某个窗口（藏完它就被扫描跳过了），单值就够。
        private readonly Dictionary<IntPtr, WindowRule> hiddenWindows = new Dictionary<IntPtr, WindowRule>();

        // 本工具进程名 / 进程号：拦截时永远不碰自己，避免「把工具自己拦掉」。
        private readonly string selfProcessName = Process.GetCurrentProcess().ProcessName;
        private readonly uint selfProcessId = (uint)Process.GetCurrentProcess().Id;

        private IntPtr ownWindow;

        // 系统窗口与自身，永不拦截。
        private static readonly HashSet<string> SystemClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "TopLevelWindowForOverflowXamlIsland",
            "NotifyIconOverflowWindow", "DesktopWindowClass", "Progman", "WorkerW",
            "Windows.UI.Core.CoreWindow", "IME", "MSCTFIME UI",
        };

        // ---------------- P0：失败退避 ----------------

        private sealed class Attempt
        {
            public int FailCount;
            public DateTime NextRetry;
        }

        private readonly Dictionary<IntPtr, Attempt> attempts = new Dictionary<IntPtr, Attempt>();
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

        /// <summary>指数退避：1s, 2s, 4s, 8s, 16s, 32s, 60s…</summary>
        private static TimeSpan Backoff(int failCount)
        {
            int sec = 1;
            for (int i = 1; i < failCount && sec < 60; i++) sec *= 2;
            return TimeSpan.FromSeconds(Math.Min(60, sec));
        }

        // ---------------- P2：规则健康度 ----------------

        private sealed class RuleHealth
        {
            public readonly Queue<int> Rates = new Queue<int>();   // 最近若干次截图命中率
            public int BelowCount;                                 // 连续"临界偏下"的次数
            public int MissStreak;                                 // 连续"真的没拦住"的次数（结果导向的健康度）
            public int Blocks;                                     // 拦住过几次
            public int TotalDropped;                               // 自动校准累计下调了多少
            public bool CapAnnounced;                              // 已提示过"下调到顶"
            public bool MissingSizeAnnounced;                      // 已提示过"缺采集尺寸，跳过比对"
            public readonly Queue<int> SuccessRates = new Queue<int>();   // 真的拦住时的分数（供阈值自学习）
        }

        private readonly Dictionary<string, RuleHealth> health = new Dictionary<string, RuleHealth>(StringComparer.Ordinal);
        private readonly object healthGate = new object();

        public event EventHandler SettingsChanged;   // 自动校准改了阈值，通知界面保存

        // ---------------- P0：隐藏窗口清单（崩溃/退出善后） ----------------

        private readonly HiddenJournal journal = new HiddenJournal();

        public event Action<BlockRecord> Blocked;
        public event EventHandler DiagnosticsChanged;

        public PopupGuard(Settings settings, BlockLog log)
        {
            this.settings = settings;
            this.log = log;
            // 构造时就读隐藏清单：MainForm 是在 Start 之前做崩溃恢复的，
            // 放到 Start 里会让恢复永远拿到空清单。
            journal.Load();
        }

        public bool IsRunning { get { return running; } }

        public void Start()
        {
            if (running || disposed) return;
            running = true;
            worker = new Thread(WorkerLoop) { IsBackground = true, Name = "PopupGuardWorker" };
            worker.Start();
        }

        public void Stop()
        {
            running = false;
            if (worker != null) worker.Join(1500);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Stop();
        }

        // ---------------- 工作线程：轮询 ----------------

        private void WorkerLoop()
        {
            // 启动先扫一遍，处理引擎启动前就已存在的窗口（也要兜住异常：
            // 后台线程上的未处理异常会直接终止整个进程）
            try { ScanAll(); }
            catch { }

            while (running)
            {
                // 最小 300ms：再密也没有意义（一次全量扫描约 2ms），只是白耗 CPU
                Thread.Sleep(Math.Max(300, settings.PopupGuardIntervalMs));
                if (!running) break;
                try { ScanAll(); }
                catch { }
            }
        }

        /// <summary>枚举所有可见顶层窗口并逐个处理。</summary>
        private void ScanAll()
        {
            PruneHidden();

            // 每轮只拍一次快照、只算一次"是否需要标题"：规则表可能很大，
            // 逐窗口重复遍历会白白吃掉 CPU。
            List<WindowRule> rules = SnapshotRules();
            bool needTitle = NeedsTitle(rules);

            var windows = new List<IntPtr>(256);
            Native.EnumWindows(delegate(IntPtr h, IntPtr l) { windows.Add(h); return true; }, IntPtr.Zero);
            foreach (IntPtr hwnd in windows)
            {
                if (!running) return;
                try { ProcessWindow(hwnd, rules, needTitle); }
                catch { }
            }
        }

        /// <summary>取规则快照。UI 会增删规则，遍历必须用副本。</summary>
        private List<WindowRule> SnapshotRules()
        {
            var rules = settings.WindowRules;
            if (rules == null || rules.Count == 0) return new List<WindowRule>();
            lock (SettingsStore.RulesGate) return rules.ToList();
        }

        /// <summary>取窗口信息 → 匹配规则 → 执行动作。</summary>
        private void ProcessWindow(IntPtr hwnd, List<WindowRule> rules, bool needTitle)
        {
            if (!Native.IsWindow(hwnd)) return;
            if (!Native.IsWindowVisible(hwnd)) return;
            if (hwnd == ownWindow)
            {
                Note(GuardDecision.Skipped(null, GuardSkip.OwnWindow, null, -1, "本工具窗口，永不拦截"));
                return;
            }

            var info = Describe(hwnd, needTitle);
            if (info == null) return;
            if (SystemClasses.Contains(info.ClassName))
            {
                Note(GuardDecision.Skipped(null, GuardSkip.SystemClass, info, -1, "系统类名 " + info.ClassName));
                return;
            }
            if (IsWhitelisted(info))
            {
                Note(GuardDecision.Skipped(null, GuardSkip.Whitelist, info, -1, "白名单（本工具 / 资源管理器）"));
                return;
            }
            if (rules.Count == 0) return;

            WindowRule hit = null;
            GuardSkip relatedSkip = GuardSkip.None;
            foreach (var rule in rules)
            {
                // 先看这条规则跟当前窗口有没有关系：无关的规则（比如别家软件的规则）
                // 停没停用都不该算到这个窗口头上，否则满屏都是"有规则已停用"。
                if (!rule.Matches(info)) continue;
                if (!rule.Enabled)
                {
                    // 纯截图规则（无属性条件）对任何窗口都"匹配"，
                    // 别因此把"有规则已停用"刷到每一个窗口上
                    if (HasAttributes(rule)) relatedSkip = GuardSkip.RuleDisabled;
                    continue;
                }
                if (rule.IsEmpty()) { relatedSkip = GuardSkip.RuleEmpty; continue; }
                hit = rule;
                break;
            }
            if (hit == null)
            {
                if (relatedSkip != GuardSkip.None)
                    Note(GuardDecision.Skipped(null, relatedSkip, info, -1, SkipText(relatedSkip)));
                // 「没有规则命中」只对"属于已知软件、却没规则可用"的窗口记录，否则全是噪音
                else if (IsRelevantWindow(info, rules))
                    Note(GuardDecision.Skipped(null, GuardSkip.NoRuleMatched, info, -1, "没有规则命中"));
                return;
            }

            // 尺寸范围过滤（规则设了宽高限制时才生效）
            if (hit.WidthMin.HasValue || hit.WidthMax.HasValue ||
                hit.HeightMin.HasValue || hit.HeightMax.HasValue)
            {
                Native.RECT rect;
                if (!Native.GetWindowRect(hwnd, out rect)) return;
                int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
                if (!hit.SizeInRange(w, h))
                {
                    Note(GuardDecision.Skipped(hit, GuardSkip.SizeOutOfRange, info, -1,
                        "尺寸 " + w + "×" + h + " 不在规则范围内"));
                    return;
                }
            }

            // 截图规则：属性命中后再比对画面（标题/进程名会变、画面不变的弹窗靠这个）。
            int rate = -1;
            if (hit.UseImage && hit.HasImagePoints())
            {
                // 纯截图规则（没有标题/进程/类名）对任何窗口都成立，
                // 先用"采集时的尺寸"粗筛掉明显不相关的窗口，否则每个可见窗口都要截图。
                if (!HasAttributes(hit))
                {
                    string sizeNote;
                    if (!SizeCloseToCapture(hit, hwnd, out sizeNote))
                    {
                        // 缺采集尺寸属于规则自身的配置问题：每条规则只提示一次，
                        // 否则每个可见窗口都会写一条，把日志顶满还每轮写盘。
                        bool missing = !(hit.CapturedWidth.HasValue && hit.CapturedHeight.HasValue);
                        if (missing)
                        {
                            var h = HealthOf(hit);
                            bool already;
                            lock (healthGate) { already = h.MissingSizeAnnounced; h.MissingSizeAnnounced = true; }
                            if (!already)
                            {
                                Announce(hit, info, -1, hit.EffectiveThreshold(),
                                    "这条截图规则没有采集时的窗口尺寸，已跳过画面比对（避免对每个窗口截图）。" +
                                    "建议给它补上进程名 / 类名 / 标题条件，或重新采集");
                            }
                        }
                        else
                        {
                            // 尺寸不符只是粗筛优化，不算"本该拦却没拦"，不进运行日志
                            Note(GuardDecision.Skipped(hit, GuardSkip.PrefilterSkipped, info, -1, sizeNote));
                        }
                        return;
                    }
                }

                rate = ImageMatcher.MatchRate(hit, hwnd);
                if (rate < 0)
                {
                    Note(GuardDecision.Skipped(hit, GuardSkip.CaptureFailed, info, -1, "截图失败（PrintWindow 不支持该窗口）"));
                    return;   // 截图失败：宁可漏拦也不误杀
                }
                int threshold = hit.EffectiveThreshold();
                int lowLine = Math.Max(0, threshold - settings.BorderBand);
                bool confirmed = rate >= threshold;

                // 双阈值：落在临界区（低于高线、但还在低线之上）时不立刻放弃，
                // 隔一会儿复检一次——单次采样很容易正好赶上画面刷新/滚动。
                if (!confirmed && rate >= lowLine)
                {
                    int delay = settings.RecheckMs;
                    if (delay > 0) Thread.Sleep(delay);
                    if (!running) return;
                    int rate2 = ImageMatcher.MatchRate(hit, hwnd);
                    if (rate2 >= 0)
                    {
                        if (rate2 > rate) rate = rate2;
                        if (rate2 >= lowLine) confirmed = true;
                    }
                }

                RecordRate(hit, rate);
                if (!confirmed)
                {
                    Note(GuardDecision.Skipped(hit, GuardSkip.BelowThreshold, info, rate,
                        "命中率 " + rate + "% < 阈值 " + threshold + "%（低线 " + lowLine + "%）"));
                    RecordMiss(hit);                 // 结果导向：真的没拦住，记一次漏拦
                    TryAutoCalibrate(hit, info, rate);
                    return;
                }
            }

            // 不做「同一窗口短时间内只处理一次」的去重：窗口被关闭后
            // IsWindow / IsWindowVisible 自然会过滤掉后续命中。
            ApplyActions(hit, hwnd, info, rate);
        }

        /// <summary>跳过原因的用户可读文案。</summary>
        private static string SkipText(GuardSkip skip)
        {
            switch (skip)
            {
                case GuardSkip.RuleDisabled: return "有规则已停用";
                case GuardSkip.RuleEmpty: return "规则没有可用条件，已跳过";
                default: return "没有规则命中";
            }
        }

        /// <summary>窗口是否属于"已知软件"（进程名或类名出现在任一规则里）。</summary>
        private static bool IsRelevantWindow(WindowInfo info, List<WindowRule> rules)
        {
            foreach (var rule in rules)
            {
                if (!rule.Enabled) continue;
                if (!string.IsNullOrEmpty(rule.ProcessName) &&
                    string.Equals(info.ProcessName, rule.ProcessName, StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.IsNullOrEmpty(rule.WindowClass) &&
                    string.Equals(info.ClassName, rule.WindowClass, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>规则是否带属性条件（标题/进程/类名）。纯截图规则没有属性，会匹配所有窗口。</summary>
        internal static bool HasAttributes(WindowRule rule)
        {
            return rule != null && (!string.IsNullOrEmpty(rule.TitleKeyword) ||
                                    !string.IsNullOrEmpty(rule.ProcessName) ||
                                    !string.IsNullOrEmpty(rule.WindowClass));
        }

        /// <summary>
        /// 纯截图规则的廉价粗筛：窗口尺寸与采集时相差超过一倍就跳过画面比对。
        /// 没有属性条件的规则匹配所有窗口，不粗筛的话每个可见窗口都要 PrintWindow + 逐点取色。
        /// 没有采集尺寸的（旧配置 / 手写 JSON）返回 false，由调用方跳过比对。
        ///
        /// 已知取舍：0.5~2 倍这个区间对"同一个窗口最大化 / 还原"或跨 DPI 显示器可能误判，
        /// 宁可漏拦也不把 CPU 吃满——这是刻意选择的。
        /// </summary>
        private static bool SizeCloseToCapture(WindowRule rule, IntPtr hwnd, out string note)
        {
            note = "";
            if (rule == null) return true;
            if (!rule.CapturedWidth.HasValue || !rule.CapturedHeight.HasValue)
            {
                note = "规则缺少采集时的窗口尺寸，未做画面比对（避免对每个可见窗口截图）";
                return false;
            }
            int cw = rule.CapturedWidth.Value, ch = rule.CapturedHeight.Value;
            if (cw <= 0 || ch <= 0) return true;
            Native.RECT rect;
            if (!Native.GetWindowRect(hwnd, out rect)) return true;
            int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
            bool ok = w * 2 >= cw && w <= cw * 2 && h * 2 >= ch && h <= ch * 2;
            note = ok ? "" : ("窗口尺寸 " + w + "×" + h + " 与采集时 " + cw + "×" + ch + " 相差过大，跳过画面比对");
            return ok;
        }

        private bool IsWhitelisted(WindowInfo info)
        {
            return !string.IsNullOrEmpty(WhitelistReason(info));
        }

        /// <summary>
        /// 该窗口是否永远不该被拦截，返回原因（空 = 可以拦截）。
        /// 这是刻意的设计：把桌面 / 任务栏 / 文件夹窗口藏了会让系统不可用。
        /// 但必须让用户在加入规则时就看见，否则会以为"规则失效了"。
        /// </summary>
        internal string WhitelistReason(WindowInfo info)
        {
            if (info == null) return "";
            if (info.ProcessId == selfProcessId) return "本工具窗口";
            if (string.Equals(info.ProcessName, selfProcessName, StringComparison.OrdinalIgnoreCase)) return "本工具进程";
            if (string.Equals(info.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
                return "Windows 资源管理器（桌面 / 文件夹窗口）";
            if (SystemClasses.Contains(info.ClassName)) return "系统窗口（" + info.ClassName + "）";
            return "";
        }

        /// <summary>
        /// 规则是否需要窗口标题。若所有启用的规则都只按进程 / 类名匹配，
        /// 就不必调用 GetWindowTextW——它个别窗口上会卡住扫描线程。
        /// </summary>
        private static bool NeedsTitle(List<WindowRule> rules)
        {
            // 停用的规则也要看标题：否则"这条停用的规则本来能不能命中"就判断不出来
            foreach (var rule in rules)
                if (!string.IsNullOrEmpty(rule.TitleKeyword)) return true;
            return false;
        }

        /// <summary>
        /// 执行动作。动作只有「隐藏」一种（窗口藏起、程序后台继续运行）。
        ///
        /// 关键：ShowWindow 的返回值 + IsWindowVisible 复核都通过才算拦住；
        /// 否则记一条失败并进入指数退避，避免同一个窗口把日志刷满。
        /// </summary>
        private void ApplyActions(WindowRule rule, IntPtr hwnd, WindowInfo info, int rate)
        {
            var actions = rule.Actions;
            if (actions == null || actions.Count == 0) return;

            Attempt attempt = null;
            lock (attempts) { attempts.TryGetValue(hwnd, out attempt); }
            if (settings.FailureBackoff && attempt != null &&
                attempt.FailCount > 0 && DateTime.Now < attempt.NextRetry)
            {
                Note(GuardDecision.Skipped(rule, GuardSkip.BackingOff, info, rate,
                    "上次隐藏失败，退避至 " + attempt.NextRetry.ToString("HH:mm:ss") +
                    "（第 " + attempt.FailCount + " 次）"));
                return;
            }

            // 注意：ShowWindow 的返回值是"之前是否可见"，不是成功与否，不能拿来判断。
            // 真正的判据是调用后窗口是否已经不可见。
            Native.ShowWindow(hwnd, Native.SW_HIDE);
            bool invisible = !Native.IsWindowVisible(hwnd);

            if (invisible)
            {
                lock (attempts) { if (attempt != null) attempts.Remove(hwnd); }
                lock (hiddenWindows) { hiddenWindows[hwnd] = rule; }
                journal.Add(rule, info);
                RecordSuccess(rule, rate);   // 确实藏住了才算学习样本（藏不住说明阈值再低也没用）
                Record(info, rule, "隐藏", true, rate, HitNote(rule, rate), 0);
                Note(GuardDecision.Hide(rule, info, rate));
                RuleHealthHit(rule);
                return;
            }

            // 失败：记一条 + 指数退避
            if (attempt == null) { attempt = new Attempt(); lock (attempts) attempts[hwnd] = attempt; }
            attempt.FailCount++;
            attempt.NextRetry = DateTime.Now + Backoff(attempt.FailCount);
            string reason = "ShowWindow 未生效（第 " + attempt.FailCount + " 次失败）";
            Record(info, rule, "隐藏失败", false, rate, reason, attempt.FailCount);
            Note(GuardDecision.Fail(rule, info, rate, reason));
        }

        private static string HitNote(WindowRule rule, int rate)
        {
            return rate >= 0
                ? "命中率 " + rate + "% / 阈值 " + rule.EffectiveThreshold() + "%"
                : "属性命中";
        }

        private void Record(WindowInfo info, WindowRule rule, string action, bool success,
                            int rate, string note, int failCount)
        {
            var record = BlockRecord.Make("窗口拦截", info.Title, info.ProcessName, action,
                                          success, note, rate, rule == null ? "" : RuleFingerprint.Of(rule));
            try
            {
                // 只有"真的多了一行"才需要通知界面；合并计数不算，否则同一个窗口
                // 每 400ms 记一次"命中率不足"就会把日志页刷到停不下来。
                if (log != null && log.Add(record))
                {
                    var handler = Blocked;
                    if (handler != null) handler(record);
                }
            }
            catch { }
        }

        // ---------------- 诊断 ----------------

        // 同一条规则对同一个进程的同一种判定，短时间内只写一次日志、只刷一次界面。
        // 否则"某个窗口一直差一点拦不住"（或者同一软件的多个窗口都差一点）
        // 会让日志页每秒重建好几次。内存环形缓冲里仍然每条都留，供排查。
        private readonly Dictionary<string, DateTime> lastDecisionLog = new Dictionary<string, DateTime>();
        private readonly object decisionGate = new object();
        private static readonly TimeSpan DecisionLogInterval = TimeSpan.FromSeconds(15);

        private bool AllowDecisionLog(string ruleId, string processName, string result)
        {
            string key = (ruleId ?? "") + "|" + (processName ?? "") + "|" + (result ?? "");
            lock (decisionGate)
            {
                DateTime last;
                if (lastDecisionLog.TryGetValue(key, out last) && DateTime.Now - last < DecisionLogInterval)
                    return false;
                lastDecisionLog[key] = DateTime.Now;
                if (lastDecisionLog.Count > 500) lastDecisionLog.Clear();
                return true;
            }
        }

        /// <summary>
        /// 记一条判定：写进运行日志，并在确实新增了一行时通知界面刷新。
        /// （内存环形缓冲已移除——只有写没有读，属废代码。）
        /// </summary>
        private void Note(GuardDecision d)
        {
            if (d == null) return;
            if (!settings.DiagnosticsEnabled) return;   // 总开关：关掉后不再写判定日志
            if (!d.WorthLogging || log == null) return;

            // 同一规则对同一进程的同一种判定，15 秒内只写一次（多个窗口轮流命中也算同一种）
            if (!AllowDecisionLog(d.RuleId, d.ProcessName, d.Result)) return;

            bool added = log.Add(BlockRecord.Make("判定", d.Title, d.ProcessName,
                                                  string.IsNullOrEmpty(d.Action) ? d.Result : d.Action,
                                                  d.Success, d.Reason, d.Rate, d.RuleId));
            if (!added) return;

            var handler = DiagnosticsChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        // ---------------- 健康度与自动校准 ----------------

        private static string HealthKey(WindowRule rule)
        {
            if (rule == null) return "";
            return string.IsNullOrEmpty(rule.Fingerprint) ? RuleFingerprint.Of(rule) : rule.Fingerprint;
        }

        private RuleHealth HealthOf(WindowRule rule)
        {
            if (rule == null) return null;
            // 指纹一般由存取时算好；这里兜底，避免空指纹的规则全挤进同一个学习槽
            string id = HealthKey(rule);
            lock (healthGate)
            {
                RuleHealth h;
                if (!health.TryGetValue(id, out h)) { h = new RuleHealth(); health[id] = h; }
                return h;
            }
        }

        /// <summary>记一次截图命中率（滑动窗口，最多 20 个）。</summary>
        internal void RecordRate(WindowRule rule, int rate)   // internal：供健康度用例直接喂样本
        {
            if (rule == null || rate < 0) return;
            var h = HealthOf(rule);
            lock (healthGate)
            {
                h.Rates.Enqueue(rate);
                while (h.Rates.Count > 20) h.Rates.Dequeue();
            }
        }

        /// <summary>
        /// 清掉一条规则的健康度历史与累计校准量：手动重新校准 / 改阈值后调用。
        /// 否则「临界 / 已失配」是拿旧阈值下的命中率算的，自动校准也可能一上来就顶到上限。
        /// </summary>
        public void ResetHealth(WindowRule rule)
        {
            if (rule == null) return;
            lock (healthGate) { health.Remove(HealthKey(rule)); }
            // 旧的学习值也不能留：重新校准/改条件后，学习要从新阈值重新开始
            if (rule.LearnedRatio.HasValue)
            {
                rule.LearnedRatio = null;
                var handler = SettingsChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        private void RuleHealthHit(WindowRule rule)
        {
            if (rule == null) return;
            var h = HealthOf(rule);
            lock (healthGate) { h.Blocks++; h.BelowCount = 0; h.MissStreak = 0; }
        }

        /// <summary>记一次"真的没拦住"（比对条件不满足）。用于结果导向的健康标记。</summary>
        internal void RecordMiss(WindowRule rule)   // internal：供健康度用例直接构造漏拦场景
        {
            if (rule == null) return;
            var h = HealthOf(rule);
            lock (healthGate) { h.MissStreak++; }
        }

        /// <summary>
        /// 记录一次"真的拦住了"时的画面分数，并据此学习阈值。
        ///
        /// 学习规则（用户完全不参与）：
        ///   高线 = 历史成功分的 10% 分位（代表"这个窗口最差也能长这样"），
        ///   但夹在 [采集阈值 − 10, 采集阈值] 之间——只放松、不收紧，防止越学越拦不住。
        /// 至少攒够 5 个成功样本才开始学习；学习值变化时通知界面落盘。
        /// </summary>
        internal void RecordSuccess(WindowRule rule, int rate)
        {
            if (rule == null || rate < 0) return;
            int capture = rule.MatchRatio > 0 ? rule.MatchRatio : 80;
            int floor = Math.Max(0, capture - 10);

            var h = HealthOf(rule);
            bool changed = false;
            lock (healthGate)
            {
                h.MissStreak = 0;          // 拦住了，漏拦计数清零（与是否自学习无关）
                if (!rule.AutoThreshold) return;   // 用户接管了阈值就不要再自动改

                h.SuccessRates.Enqueue(rate);
                while (h.SuccessRates.Count > 20) h.SuccessRates.Dequeue();
                if (h.SuccessRates.Count < 5) return;   // 样本太少，先按采集阈值工作

                int learned = Percentile10(h.SuccessRates);
                learned = Math.Max(learned, floor);     // 最多放松 10
                learned = Math.Min(learned, capture);   // 不收紧
                if (learned > 90) learned = 90;

                // 规则字段是共享对象，写它要和 UI/保存用同一把锁
                if (rule.LearnedRatio != learned)
                {
                    lock (SettingsStore.RulesGate) { rule.LearnedRatio = learned; }
                    changed = true;
                }
            }
            if (changed)
            {
                var handler = SettingsChanged;   // 让主窗把学习结果落盘
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        /// <summary>10% 分位（不需要引 LINQ，直接排序取下标）。</summary>
        private static int Percentile10(Queue<int> values)
        {
            var arr = values.ToArray();
            Array.Sort(arr);
            int idx = (int)Math.Floor(0.10 * (arr.Length - 1));
            return arr[Math.Max(0, idx)];
        }

        /// <summary>
        /// 规则健康标记（给列表显示）：样本太少返回空；
        /// 中位数低于阈值为「已失配」，低于阈值+10 为「临界」。
        /// </summary>
        /// <summary>
        /// 规则的显示标记，与守卫的拦截判定保持同一口径：
        ///   「已失配」= 真的拦不住 —— 要么连续多次漏拦（结果导向），要么分数连临界区都进不去；
        ///   「临界」  = 能拦，但靠的是临界区复检（余量不足，提示用户留意）。
        /// 分数够、也没漏拦 → 不显示任何标记。
        /// </summary>
        public string HealthMark(WindowRule rule)
        {
            if (rule == null || !rule.UseImage || rule.Points == null || rule.Points.Count == 0) return "";
            int[] arr;
            int miss;
            lock (healthGate)
            {
                RuleHealth h;
                if (!health.TryGetValue(HealthKey(rule), out h)) return "";
                arr = h.Rates.ToArray();
                miss = h.MissStreak;
            }
            if (arr.Length < 3) return "";   // 样本不够，不评判（也避免"一重启就报失配"）
            Array.Sort(arr);
            int median = arr[arr.Length / 2];
            int threshold = rule.EffectiveThreshold();
            int lowLine = Math.Max(0, threshold - settings.BorderBand);

            if (miss >= 3) return "（已失配）";        // 连续漏拦：真的拦不住
            if (median < lowLine) return "（已失配）"; // 连临界区都进不去：肯定拦不住
            if (median < threshold) return "（临界）"; // 临界区复检后能拦，但余量不足
            return "";
        }

        /// <summary>
        /// 可选的自动校准：连续多次命中率"就差一点"才下调阈值。
        /// 两条安全约束：单次最多降 5（且被夹在 40~90），**累计下调不超过
        /// PopupGuardAutoCalibrateMaxDrop（默认 10）**——到顶就停手并提示重新采集，
        /// 避免阈值被一路降到很低、误杀面越放越大。
        /// 默认关闭，由用户决定。
        ///
        /// 并发说明：这里直接改的是共享的 rule 对象，编辑窗是模态的、保存也在 UI 线程；
        /// 理论上可能出现"刚校准完、编辑窗又保存了旧阈值"互相覆盖。实际窗口期极短，
        /// 且 SettingsChanged 会立刻把校准结果存盘，属可接受范围。
        /// </summary>
        private void TryAutoCalibrate(WindowRule rule, WindowInfo info, int rate)
        {
            if (!settings.PopupGuardAutoCalibrate) return;
            if (rate < 40) return;   // 差得太多说明画面已经变了，不适合自动校准

            int threshold = rule.EffectiveThreshold();
            int tolerance = settings.PopupGuardAutoCalibrateTolerance;
            int need = settings.PopupGuardAutoCalibrateCount;
            int maxDrop = settings.PopupGuardAutoCalibrateMaxDrop;

            var h = HealthOf(rule);
            lock (healthGate)
            {
                if (rate >= threshold - tolerance) h.BelowCount++;
                else h.BelowCount = 0;
                if (h.BelowCount < need) return;
                h.BelowCount = 0;
            }

            int updated = Math.Max(40, Math.Min(90, rate - 5));
            if (updated >= threshold) return;

            int droppedSoFar, cappedAlready;
            lock (healthGate)
            {
                droppedSoFar = h.TotalDropped;
                cappedAlready = h.CapAnnounced ? 1 : 0;
            }
            int room = maxDrop - droppedSoFar;          // 还剩多少下调额度
            if (room <= 0)
            {
                if (cappedAlready == 0)
                {
                    lock (healthGate) { h.CapAnnounced = true; }
                    Announce(rule, info, rate, threshold,
                        "自动校准已累计下调 " + droppedSoFar + "% 到顶（上限 " + maxDrop +
                        "%），不再自动下调；这条规则建议重新采集");
                }
                return;
            }
            if (threshold - updated > room) updated = threshold - room;   // 夹到剩余额度内

            lock (SettingsStore.RulesGate) { rule.MatchRatio = updated; }
            rule.AutoThreshold = true;
            lock (healthGate) { h.TotalDropped += threshold - updated; }

            string reason = "阈值 " + threshold + "% → " + updated + "%（连续命中率 " + rate +
                            "% 略低于阈值，已自动校准；累计下调 " + (droppedSoFar + threshold - updated) +
                            "% / 上限 " + maxDrop + "%）";
            Note(new GuardDecision
            {
                Time = DateTime.Now,
                Title = info.Title ?? "",
                ProcessName = info.ProcessName ?? "",
                Result = "提示",
                Action = "自动校准",
                Reason = reason,
                Rate = rate,
                Threshold = updated,
                RuleName = GuardDecision.DescribeRule(rule),
                RuleId = rule.Fingerprint,
                Success = true,
            });
            var handler = SettingsChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>只提示、不改阈值（写一条日志并通知界面）。</summary>
        private void Announce(WindowRule rule, WindowInfo info, int rate, int threshold, string reason)
        {
            Note(new GuardDecision
            {
                Time = DateTime.Now,
                Title = info.Title ?? "",
                ProcessName = info.ProcessName ?? "",
                Result = "提示",
                Action = "自动校准",
                Reason = reason,
                Rate = rate,
                Threshold = threshold,
                RuleName = GuardDecision.DescribeRule(rule),
                RuleId = rule.Fingerprint,
                Success = true,
            });
        }

        // ---------------- 对外工具方法 ----------------

        /// <summary>告诉引擎哪个是本工具窗口，避免误拦自己。</summary>
        public void SetOwnWindow(Form form)
        {
            if (form != null && !form.IsDisposed) ownWindow = form.Handle;
        }

        // ---------------- 被隐藏窗口的恢复 ----------------

        /// <summary>清理已经自行恢复显示、或已经被销毁的句柄。</summary>
        private void PruneHidden()
        {
            lock (hiddenWindows)
            {
                if (hiddenWindows.Count == 0) return;
                var bad = new List<IntPtr>();
                foreach (IntPtr h in hiddenWindows.Keys)
                {
                    if (!Native.IsWindow(h) || Native.IsWindowVisible(h)) bad.Add(h);
                }
                foreach (IntPtr h in bad) hiddenWindows.Remove(h);
            }
            // 退避记录只在窗口消失时清（窗口还在时要继续保持退避，否则又会每轮重试）
            lock (attempts)
            {
                if (attempts.Count == 0) return;
                var bad = new List<IntPtr>();
                foreach (IntPtr h in attempts.Keys)
                    if (!Native.IsWindow(h)) bad.Add(h);
                foreach (IntPtr h in bad) attempts.Remove(h);
            }
        }

        /// <summary>恢复某条规则藏起来的窗口（停用/删除规则时用）。</summary>
        public int RestoreWindowsOf(WindowRule rule)
        {
            int restored = 0;
            lock (hiddenWindows)
            {
                var list = hiddenWindows.Where(delegate(KeyValuePair<IntPtr, WindowRule> kv)
                                         { return kv.Value == rule; })
                                         .Select(delegate(KeyValuePair<IntPtr, WindowRule> kv) { return kv.Key; })
                                         .ToList();
                foreach (IntPtr h in list)
                {
                    hiddenWindows.Remove(h);
                    if (Native.IsWindow(h))
                    {
                        Native.ShowWindow(h, Native.SW_SHOWNORMAL);
                        restored++;
                    }
                }
            }
            journal.RemoveOfRule(rule);
            return restored;
        }

        /// <summary>
        /// 恢复所有被本引擎藏起来的窗口。
        /// 同时把命中这些窗口的规则停用：规则还开着的话，下一轮扫描（≤400ms）
        /// 就会把它们再藏回去，"恢复"等于没做。停用后界面上能看出来，
        /// 想恢复拦截把开关拨回去即可。
        /// </summary>
        public int RestoreAllHidden()
        {
            int restored = 0;
            var affected = new List<WindowRule>();
            lock (hiddenWindows)
            {
                foreach (var kv in hiddenWindows.ToList())
                {
                    IntPtr h = kv.Key;
                    hiddenWindows.Remove(h);
                    if (!Native.IsWindow(h)) continue;
                    Native.ShowWindow(h, Native.SW_SHOWNORMAL);
                    restored++;
                    if (kv.Value != null && !affected.Contains(kv.Value)) affected.Add(kv.Value);
                }
            }
            if (affected.Count > 0)
            {
                lock (SettingsStore.RulesGate)
                    foreach (var r in affected) r.Enabled = false;
            }
            journal.Clear();
            return restored;
        }

        /// <summary>
        /// 异常退出后的善后：读 hidden.json，把上次没来得及恢复的窗口显示出来。
        /// 正常退出会清空清单，所以这里只会处理"非正常退出"的情况。
        /// </summary>
        public int RecoverFromLastCrash()
        {
            var items = journal.Snapshot();
            if (items.Count == 0) return 0;

            var windows = new List<IntPtr>(256);
            Native.EnumWindows(delegate(IntPtr h, IntPtr l) { windows.Add(h); return true; }, IntPtr.Zero);

            int restored = 0;
            foreach (var item in items)
            {
                foreach (IntPtr h in windows)
                {
                    if (!Native.IsWindow(h)) continue;
                    // 只处理"还藏着"的窗口：已经可见的（用户可能自己叫回来了）不用管，
                    // 也不计入恢复数量，避免虚报
                    if (Native.IsWindowVisible(h)) continue;
                    var info = Describe(h, true);
                    if (info == null) continue;
                    if (!string.Equals(info.ProcessName, item.ProcessName ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                    // 进程名 + （类名 或 标题）匹配才恢复，避免误显示别的窗口
                    bool sameClass = !string.IsNullOrEmpty(item.ClassName) &&
                                     string.Equals(info.ClassName, item.ClassName, StringComparison.OrdinalIgnoreCase);
                    bool sameTitle = !string.IsNullOrEmpty(item.Title) &&
                                     string.Equals(info.Title, item.Title, StringComparison.OrdinalIgnoreCase);
                    if (!sameClass && !sameTitle) continue;
                    // 同上：ShowWindow 的返回值不可靠（藏过的窗口返回 FALSE）；
                    // 窗口显示是异步的，马上查 IsWindowVisible 可能还没生效，所以以"尝试恢复"计数
                    Native.ShowWindow(h, Native.SW_SHOWNORMAL);
                    restored++;
                }
            }
            journal.Clear();   // 无论有没有找到，清单都不该留到下一次
            return restored;
        }

        /// <summary>枚举当前所有可见顶层窗口（供界面列出）。</summary>
        public static List<WindowInfo> EnumerateVisibleWindows()
        {
            var result = new List<WindowInfo>();
            var self = (uint)Process.GetCurrentProcess().Id;
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (Native.IsWindowVisible(h))
                {
                    var info = Describe(h, true);
                    if (info != null && info.Title.Length > 0 && info.ProcessId != self) result.Add(info);
                }
                return true;
            }, IntPtr.Zero);
            return result.OrderBy(x => x.ProcessName).ThenBy(x => x.Title).ToList();
        }

        /// <summary>点选采集：返回鼠标所在窗口的信息（本工具自身窗口不采集）。</summary>
        public static WindowInfo PickWindow()
        {
            Native.POINT point;
            if (!Native.GetCursorPos(out point)) return null;
            IntPtr hwnd = Native.WindowFromPoint(point);
            if (hwnd == IntPtr.Zero) return null;
            var selfPid = (uint)Process.GetCurrentProcess().Id;
            var info = Describe(hwnd, true);
            if (info != null && info.ProcessId == selfPid) return null;
            return info;
        }

        /// <summary>读取窗口的标题 / 类名 / 进程（进程名带缓存，避免频繁 OpenProcess）。</summary>
        public static WindowInfo Describe(IntPtr hwnd, bool withTitle = true)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return null;
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            return new WindowInfo
            {
                Handle = hwnd,
                // 规则不靠标题匹配时就不读标题：GetWindowTextW 个别窗口上会卡住扫描线程
                Title = withTitle ? (Native.WindowTitle(hwnd) ?? "") : "",
                ClassName = Native.WindowClass(hwnd) ?? "",
                ProcessName = ProcessNameOf(pid) ?? "",
                ProcessId = pid,
            };
        }

        private static string ProcessNameOf(uint pid)
        {
            if (pid == 0) return "";
            lock (nameCacheGate)
            {
                CachedName cached;
                if (nameCache.TryGetValue(pid, out cached) && DateTime.Now < cached.Until) return cached.Name;
            }
            // 用 OpenProcess + QueryFullProcessImageName，而不是 Process.GetProcessById：
            // 后者在受保护进程上会阻塞扫描线程，前者对拒绝访问的进程快速失败。
            string name = "";
            string path = Native.ProcessPath(pid);
            if (path.Length > 0) name = System.IO.Path.GetFileNameWithoutExtension(path);
            lock (nameCacheGate)
            {
                nameCache[pid] = new CachedName { Name = name, Until = DateTime.Now + NameCacheTtl };
                if (nameCache.Count > 2000) nameCache.Clear();
            }
            return name;
        }
    }
}
