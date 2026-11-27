using System;

namespace TrayHider
{
    /// <summary>守卫一次扫描的结论类型。用于把"为什么没拦"讲清楚，不再静默。</summary>
    internal enum GuardSkip
    {
        None,
        OwnWindow,       // 本工具窗口
        Whitelist,       // 自身进程 / explorer 白名单
        SystemClass,     // 系统类名
        RuleDisabled,    // 规则已停用
        RuleEmpty,       // 规则没有可用条件
        NoRuleMatched,   // 没有规则命中
        SizeOutOfRange,  // 尺寸不在范围内
        CaptureFailed,   // 截图失败
        BelowThreshold,  // 命中率低于阈值
        BackingOff,      // 拦截失败，退避中
        PrefilterSkipped,// 纯截图规则的廉价粗筛（尺寸不符）——性能优化，不是"本该拦却没拦"
    }

    /// <summary>
    /// 一条守卫判定：拦住了 / 为什么跳过 / 失败了几次。
    /// 内存里保留最近若干条，并在必要时写入运行日志。
    /// </summary>
    internal sealed class GuardDecision
    {
        public DateTime Time = DateTime.Now;
        public string Title = "", ProcessName = "", ClassName = "";
        public string RuleName = "";
        public string RuleId = "";
        public string Result = "";     // 隐藏 / 跳过 / 失败 / 提示
        public string Action = "";     // 写进运行日志的"动作"列（留空则用 Result）
        public string Reason = "";
        public int Rate = -1;          // 截图命中率（未比对为 -1）
        public int Threshold;
        public bool Success;

        /// <summary>
        /// 是否需要写进运行日志。
        /// 「隐藏 / 失败」已经由 Record 以「窗口拦截」类别记录，这里只补"值得看的跳过"，
        /// 避免同一件事写两遍。
        /// </summary>
        public bool WorthLogging
        {
            get { return (Result == "跳过" && SkipIsInteresting) || Result == "提示"; }
        }

        // 跳过类里值得落盘的：都是"规则本该生效却没生效"的情况
        public bool SkipIsInteresting
        {
            get
            {
                switch (Skip)
                {
                    case GuardSkip.BelowThreshold:
                    case GuardSkip.CaptureFailed:
                    case GuardSkip.SizeOutOfRange:
                    case GuardSkip.RuleEmpty:
                    case GuardSkip.RuleDisabled:
                    case GuardSkip.BackingOff:
                        return true;
                    default:
                        return false;
                }
            }
        }

        public GuardSkip Skip = GuardSkip.None;

        public static GuardDecision Hide(WindowRule rule, WindowInfo info, int rate)
        {
            return new GuardDecision
            {
                Time = DateTime.Now,
                Title = info.Title ?? "",
                ProcessName = info.ProcessName ?? "",
                ClassName = info.ClassName ?? "",
                Result = "隐藏",
                Reason = "命中率 " + (rate >= 0 ? rate + "%" : "未比对"),
                Rate = rate,
                Threshold = EffectiveThreshold(rule),
                RuleName = DescribeRule(rule),
                RuleId = RuleFingerprint.Of(rule),
                Success = true,
            };
        }

        public static GuardDecision Skipped(WindowRule rule, GuardSkip skip, WindowInfo info, int rate, string reason)
        {
            return new GuardDecision
            {
                Time = DateTime.Now,
                Title = info == null ? "" : (info.Title ?? ""),
                ProcessName = info == null ? "" : (info.ProcessName ?? ""),
                ClassName = info == null ? "" : (info.ClassName ?? ""),
                Result = "跳过",
                Skip = skip,
                Reason = reason,
                Rate = rate,
                Threshold = EffectiveThreshold(rule),
                RuleName = DescribeRule(rule),
                RuleId = rule == null ? "" : RuleFingerprint.Of(rule),
                Success = false,
            };
        }

        public static GuardDecision Fail(WindowRule rule, WindowInfo info, int rate, string reason)
        {
            var d = Skipped(rule, GuardSkip.None, info, rate, reason);
            d.Result = "失败";
            d.Success = false;
            return d;
        }

        /// <summary>规则的有效阈值（规则里 0/未设置时回退 80）。</summary>
        public static int EffectiveThreshold(WindowRule rule)
        {
            return rule != null && rule.MatchRatio > 0 ? rule.MatchRatio : 80;
        }

        /// <summary>规则的显示名（与规则列表一致）。</summary>
        public static string DescribeRule(WindowRule rule)
        {
            if (rule == null) return "";
            if (!string.IsNullOrEmpty(rule.DisplayName)) return rule.DisplayName;
            if (!string.IsNullOrEmpty(rule.SoftwareName)) return rule.SoftwareName;
            if (!string.IsNullOrEmpty(rule.ProcessName)) return rule.ProcessName;
            if (!string.IsNullOrEmpty(rule.TitleKeyword)) return rule.TitleKeyword;
            return "（未命名规则）";
        }
    }
}
