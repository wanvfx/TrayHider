using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TrayHider
{
    /// <summary>
    /// 窗口规则面板：一个列表里分两组——「当前可见窗口」（勾选加入）和「已保存规则」（右键编辑）。
    /// 与托盘隐藏主业解耦，规则存在 Settings.WindowRules。
    /// </summary>
    internal sealed class WindowRulePanel : Panel
    {
        private readonly Settings settings;
        private readonly PopupGuard guard;
        private readonly BlockLog blockLog;

        private readonly Button pick = new Button();
        private readonly Button refreshWindows = new Button();
        private readonly Button capture = new Button();
        private readonly ListView combined = new ListView();
        private readonly ListViewGroup groupWindows;
        private readonly Button addSelected = new Button();
        private readonly CheckBox hideSelfFromCapture = new CheckBox();
        private readonly CheckBox hideUntriggered = new CheckBox();
        private bool _hideUntriggered;

        /// <summary>拦截后需要刷新列表时由主窗调用。</summary>
        public Action RefreshCallback { get; set; }
        private readonly ListView ruleList = new ListView();
        private readonly ImageList ruleIcons = new ImageList();

        public WindowRulePanel(Settings settings, PopupGuard guard, BlockLog blockLog)
        {
            this.settings = settings;
            this.guard = guard;
            this.blockLog = blockLog;

            Dock = DockStyle.Fill;
            groupWindows = new ListViewGroup("当前可见窗口（勾选后加入规则）", HorizontalAlignment.Left);
            // 供主窗在拦截后回调刷新「拦截次数」列
            RefreshCallback = delegate { if (IsHandleCreated) LoadRules(); };
            BuildLayout();
            // 规则列表和可见窗口枚举都放到句柄创建后做（OnHandleCreated），
            // 避免构造期枚举进程/窗口导致主窗口延迟显示（表现为“白屏”）。
        }


        private void BuildLayout()
        {
            hideSelfFromCapture.Text = "截图/录屏时隐藏本工具窗口";
            hideSelfFromCapture.Checked = settings.HideSelfFromCapture;
            hideSelfFromCapture.CheckedChanged += delegate
            {
                settings.HideSelfFromCapture = hideSelfFromCapture.Checked;
                SettingsStore.Save(settings);
                if (CaptureChanged != null) CaptureChanged(hideSelfFromCapture.Checked);
            };

            hideUntriggered.Text = "隐藏未触发规则";
            _hideUntriggered = settings.HideUntriggeredRules;
            hideUntriggered.Checked = _hideUntriggered;
            hideUntriggered.CheckedChanged += delegate
            {
                _hideUntriggered = hideUntriggered.Checked;
                settings.HideUntriggeredRules = _hideUntriggered;   // 与其它选项一样持久化
                SettingsStore.Save(settings);
                LoadRules();
            };

            pick.Text = "● 点选窗口";
            pick.Click += delegate { PickWindow(); };

            refreshWindows.Text = "刷新列表";
            refreshWindows.Click += delegate { RefreshWindows(); };

            addSelected.Text = "把勾选窗口加入规则";
            addSelected.Click += delegate { AddSelected(); };

            capture.Text = "截图拦截…";
            capture.Click += delegate { CaptureImageRule(); };

            combined.View = View.Details;
            combined.CheckBoxes = true;
            combined.FullRowSelect = true;
            combined.HideSelection = false;
            combined.Columns.Add("", 26);
            combined.Columns.Add("标题 / 关键字", 280);
            combined.Columns.Add("进程", 150);
            combined.Columns.Add("类名", 190);
            combined.Columns.Add("模式", 50);
            combined.Columns.Add("动作", 140);
            combinedSizes = new HashSet<int>();
            ListViewUtil.WireAutoFit(combined, combinedSizes);
            combined.Groups.Add(groupWindows);
            combined.ContextMenuStrip = BuildWindowMenu();

            // 已保存规则：按软件分组（列：软件弹窗名称 / 拦截次数 / 操作）——对齐火绒，位置/类型自动判断
            ruleIcons.ImageSize = new Size(20, 20);
            ruleIcons.ColorDepth = ColorDepth.Depth32Bit;
            ruleList.Dock = DockStyle.Fill;
            ruleList.View = View.Details;
            ruleList.FullRowSelect = true;
            ruleList.HideSelection = false;
            ruleList.SmallImageList = ruleIcons;
            ruleList.Columns.Add("软件弹窗名称", 240);
            ruleList.Columns.Add("拦截次数", 90);
            ruleList.Columns.Add("操作", 90);
            ruleList.CheckBoxes = false;   // 不用行勾选框：启用/停用统一走「操作」列的开关
            ruleList.OwnerDraw = true;     // 自己画：行首图标 + 操作列的开关按钮
            ruleList.MultiSelect = false;  // 与火绒一致：一次只操作一条（菜单按选中项生效）
            ListViewUtil.WireAutoFit(ruleList, ruleSized);   // 用户拖过的列保持他的宽度
            ruleList.DrawItem += RuleList_DrawItem;
            ruleList.DrawColumnHeader += RuleList_DrawColumnHeader;   // 自绘后列标题要自己画
            ruleList.MouseClick += RuleList_MouseClick;
            ruleList.MouseMove += RuleList_MouseMove;
            ruleList.ContextMenuStrip = BuildRuleMenu();


            // 用 Dock 布局（和托盘隐藏页一致）：顶部两行固定，列表填满剩余空间，状态行在最下。
            // 这样窗口变宽/变高时列表自动贴合，不会左侧留白或右侧超出。
            var masterEnable = new CheckBox { Text = "全部启用", Width = 90, Height = 28 };
            masterEnable.Location = new Point(4, 6);   // 放在「点选窗口」左侧（buttonPanel 内左对齐）
            masterEnable.Checked = _allEnabled = settings.PopupGuardEnabled;
            masterEnable.CheckedChanged += delegate
            {
                _allEnabled = masterEnable.Checked;
                // 「全部启用」只控制引擎总开关，不改写各规则的启用状态：
                // 否则关掉再打开会把用户单独停用的规则一起打开
                settings.PopupGuardEnabled = masterEnable.Checked;
                if (!masterEnable.Checked) guard.RestoreAllHidden();   // 总开关关掉：把藏起的窗口都放回来
                SettingsStore.Save(settings);
                SyncGuardState();
                ruleList.Invalidate();   // 只重绘开关，不重建整张列表（避免下面文字整体刷一遍）
                Say(masterEnable.Checked ? "已启用窗口拦截（各规则的开关保持不变）"
                                        : "已停用窗口拦截（各规则的开关保持不变）");
            };

            var buttonPanel = new Panel() { Dock = DockStyle.Top, Height = 42, Padding = new Padding(12, 7, 0, 0) };
            pick.Location = new Point(108, 6); pick.Size = new Size(110, 28);
            refreshWindows.Location = new Point(226, 6); refreshWindows.Size = new Size(100, 28);
            addSelected.Location = new Point(334, 6); addSelected.Size = new Size(160, 28);
            capture.Location = new Point(506, 6); capture.Size = new Size(110, 28);
            // 两个复选框放在「截图拦截」按钮右侧（与火绒一致）
            hideSelfFromCapture.Location = new Point(624, 8); hideSelfFromCapture.Size = new Size(230, 24);
            hideUntriggered.Location = new Point(860, 8); hideUntriggered.Size = new Size(150, 24);
            buttonPanel.Controls.Add(masterEnable);
            buttonPanel.Controls.Add(pick);
            buttonPanel.Controls.Add(refreshWindows);
            buttonPanel.Controls.Add(addSelected);
            buttonPanel.Controls.Add(capture);
            buttonPanel.Controls.Add(hideSelfFromCapture);
            buttonPanel.Controls.Add(hideUntriggered);

            // 已保存规则（分组列表）放下面，占满剩余空间；当前可见窗口放上面固定高度
            combined.Dock = DockStyle.Top;
            combined.Height = 200;
            ruleList.Dock = DockStyle.Fill;

            Controls.Add(ruleList);
            Controls.Add(combined);
            Controls.Add(buttonPanel);

            UpdateStatus();
        }

        /// <summary>本工具窗口防截图开关变化时通知主窗口。</summary>
        public event Action<bool> CaptureChanged;

        /// <summary>规则页想显示的状态/提示文字（由主窗统一显示在底部状态栏）。</summary>
        public event Action<string> StatusMessage;

        private void Say(string message)
        {
            if (StatusMessage != null) StatusMessage(message);
        }

        private ContextMenuStrip BuildRuleMenu()
        {
            var menu = new ContextMenuStrip();

            // —— 子项（单条规则）——
            menu.Items.Add("启用 / 停用", null, delegate { ToggleSelectedRule(); });
            menu.Items.Add("编辑…", null, delegate { EditSelectedRule(); });
            menu.Items.Add("按当前画面重新校准…", null, delegate { RecalibrateSelectedRule(); });
            menu.Items.Add("删除", null, delegate { DeleteSelectedRule(); });
            menu.Items.Add("恢复此规则藏起的窗口", null, delegate { RestoreSelectedRuleWindows(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("上移", null, delegate { MoveRule(-1); });
            menu.Items.Add("下移", null, delegate { MoveRule(1); });

            // —— 组标题行（整个软件）——
            var gEnable = new ToolStripMenuItem("启用该组全部", null, delegate { EnableGroup(true); });
            var gDisable = new ToolStripMenuItem("停用该组全部", null, delegate { EnableGroup(false); });
            var gRestore = new ToolStripMenuItem("恢复该组藏起的窗口", null, delegate { RestoreGroupWindows(); });
            var gDelete = new ToolStripMenuItem("删除该组", null, delegate { DeleteGroup(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(gEnable);
            menu.Items.Add(gDisable);
            menu.Items.Add(gRestore);
            menu.Items.Add(gDelete);

            menu.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                bool onRule = ruleList.SelectedItems.Count > 0 &&
                              ruleList.SelectedItems[0].Tag is RuleRow;
                bool onGroup = ruleList.SelectedItems.Count > 0 &&
                               ruleList.SelectedItems[0].Tag is RuleIo.PopupGroup;
                foreach (ToolStripItem item in menu.Items)
                {
                    bool groupItem = object.ReferenceEquals(item, gEnable) || object.ReferenceEquals(item, gDisable) ||
                                     object.ReferenceEquals(item, gRestore) || object.ReferenceEquals(item, gDelete);
                    item.Visible = groupItem ? onGroup : onRule;
                }
                if (!onRule && !onGroup) e.Cancel = true;
            };
            return menu;
        }

        /// <summary>「当前可见窗口」列表的右键菜单（和规则列表的菜单分开，避免误操作）。</summary>
        private ContextMenuStrip BuildWindowMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("把勾选窗口加入规则", null, delegate { AddSelected(); });
            menu.Items.Add("刷新列表", null, delegate { RefreshWindows(); });
            return menu;
        }

        /// <summary>右键「启用 / 停用」：切换选中规则的启用状态。</summary>
        private void ToggleSelectedRule()
        {
            var rule = SelectedRule();
            if (rule == null) return;
            lock (SettingsStore.RulesGate) { rule.Enabled = !rule.Enabled; }
            if (!rule.Enabled) guard.RestoreWindowsOf(rule);   // 停用就把它藏起来的窗口放回来
            SettingsStore.Save(settings);
            LoadRules();
            Say((rule.Enabled ? "已启用：" : "已停用：") + RuleDisplayName(rule));
        }

        // ---------------- 采集 ----------------

        private bool picking;
        private System.Windows.Forms.Timer pickTimer;

        /// <summary>进入点选模式：计时器轮询鼠标下的窗口并显示；单击确认，Esc 取消。</summary>
        private void PickWindow()
        {
            if (picking) return;
            picking = true;
            pick.Text = "移动鼠标选择，单击确认，Esc 取消…";
            if (pickTimer == null)
            {
                pickTimer = new System.Windows.Forms.Timer { Interval = 120 };
                pickTimer.Tick += delegate { PickTick(); };
            }
            pickTimer.Start();
        }

        private void PickTick()
        {
            // Esc：直接轮询物理按键，不额外挂全局钩子
            if ((Native.GetAsyncKeyState(0x1B) & 0x8000) != 0)
            {
                EndPick();
                Say("已取消点选");
                return;
            }
            var info = PopupGuard.PickWindow();
            if (info != null) Say("当前指向：" + info.Title + "  |  " + info.ProcessName);
            if ((Control.MouseButtons & MouseButtons.Left) == MouseButtons.Left)
            {
                EndPick();
                if (info != null) AddRule(WindowRule.From(info), guard.WhitelistReason(info));
            }
        }

        private void EndPick()
        {
            if (pickTimer != null) pickTimer.Stop();
            picking = false;
            pick.Text = "● 点选窗口";
        }

        private void AddSelected()
        {
            int added = 0;
            foreach (ListViewItem row in combined.Items)
            {
                if (row.Group != groupWindows || !row.Checked) continue;
                if (!(row.Tag is WindowInfo info)) continue;
                AddRule(WindowRule.From(info), guard.WhitelistReason(info));
                added++;
            }
            if (added == 0) Say("请先勾选要加入的窗口。");
        }

        private void AddRule(WindowRule rule, string warn = null)
        {
            // 同指纹视为同一条规则，别重复添加
            string fp = RuleFingerprint.Of(rule);
            WindowRule existing = null;
            lock (SettingsStore.RulesGate)
            {
                foreach (var r in settings.WindowRules)
                    if (RuleFingerprint.Of(r) == fp) { existing = r; break; }
            }
            if (existing != null)
            {
                Say("该窗口已经在规则里（" + RuleDisplayName(existing) + "），未重复添加");
                LoadRules();
                return;
            }
            lock (SettingsStore.RulesGate) { settings.WindowRules.Add(rule); }
            SettingsStore.Save(settings);
            LoadRules();
            UpdateStatus();
            Say(string.IsNullOrEmpty(warn)
                ? ("已加入规则：" + rule.Describe())
                : ("已加入规则，但" + warn + "在白名单内，这条规则永远不会生效：" + rule.Describe()));
        }

        // 采集/校准时采几次样：越多越稳，等待时间也越长
        private const int SampleCount = 5;

        /// <summary>
        /// 由多次采样算出推荐阈值：最低互比 − 自适应余量（抖动越大余量越大），夹在 40~90。
        /// 采集与「重新校准」共用。tolerance 用规则的 ColorTolerance（缺省 16）。
        /// </summary>
        private static void ComputeThreshold(List<CapturePoint[]> samples, int tolerance,
                                             out int threshold, out int minRate, out int jitter)
        {
            threshold = 80; minRate = 100; jitter = 0;
            if (samples == null || samples.Count < 2) return;
            int tol = tolerance > 0 ? tolerance : 16;
            var rates = new List<int>();
            for (int a = 0; a < samples.Count; a++)
                for (int b = a + 1; b < samples.Count; b++)
                    rates.Add(ImageMatcher.ComparePoints(samples[a], samples[b], tol));
            rates.Sort();
            minRate = rates[0];
            jitter = rates[rates.Count - 1] - minRate;
            // 多套模板取最大值会抬高分数（比较次数变多），余量要相应加大，避免误杀
            int margin = Math.Max(3, Math.Min(12, 3 + jitter / 10)) + Math.Max(0, samples.Count - 1);
            threshold = Math.Max(40, Math.Min(90, minRate - margin));
        }

        /// <summary>把采样结果（数组列表）转成模板列表。</summary>
        private static List<List<CapturePoint>> ToTemplateList(List<CapturePoint[]> samples)
        {
            var list = new List<List<CapturePoint>>(samples.Count);
            foreach (var s in samples) list.Add(new List<CapturePoint>(s));
            return list;
        }

        /// <summary>对指定窗口连续采样 SampleCount 次（间隔 300ms）。</summary>
        private static List<CapturePoint[]> SampleWindow(IntPtr hwnd)
        {
            var samples = new List<CapturePoint[]>();
            if (hwnd == IntPtr.Zero) return samples;
            for (int i = 0; i < SampleCount; i++)
            {
                try
                {
                    var p = ScreenCapture.CaptureWindowPoints(hwnd, 8);
                    if (p != null && p.Length > 0) samples.Add(p);
                }
                catch { }
                if (i < SampleCount - 1) System.Threading.Thread.Sleep(300);
            }
            return samples;
        }

        /// <summary>
        /// 交互点选一个窗口（用于"重新校准"这种无法自动判断目标的场景）。
        /// 返回 null 表示取消。
        /// </summary>
        private WindowInfo PickWindowInteractive()
        {
            var dlg = new Form
            {
                Text = "点选要校准的窗口",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(460, 92),
                TopMost = true
            };
            var label = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "移动鼠标指向要校准的窗口，单击确认（Esc 取消）"
            };
            dlg.Controls.Add(label);

            var timer = new System.Windows.Forms.Timer { Interval = 120 };
            WindowInfo picked = null;
            timer.Tick += delegate
            {
                if ((Native.GetAsyncKeyState(0x1B) & 0x8000) != 0)
                {
                    dlg.DialogResult = DialogResult.Cancel;
                    dlg.Close();
                    return;
                }
                var info = PopupGuard.PickWindow();
                if (info != null) label.Text = "当前指向：" + info.Title + "  |  " + info.ProcessName;
                if ((Control.MouseButtons & MouseButtons.Left) == MouseButtons.Left && info != null)
                {
                    picked = info;
                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                }
            };
            dlg.FormClosed += delegate { timer.Stop(); timer.Dispose(); };

            using (dlg)
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                return picked;
            }
        }

        /// <summary>
        /// 按当前窗口画面重新校准阈值（用户主动触发，不自动改）。
        /// 采集同样的 5 次采样 + 抖动自适应，只改阈值，不动其它条件。
        /// </summary>
        private void RecalibrateSelectedRule()
        {
            var rule = SelectedRule();
            if (rule == null) return;
            if (!rule.UseImage || !rule.HasImagePoints())
            {
                Say("这条规则没有截图采样点，无法按画面校准");
                return;
            }

            IntPtr best = IntPtr.Zero;
            string bestTitle = "", bestClass = "";
            if (PopupGuard.HasAttributes(rule))
            {
                foreach (var info in PopupGuard.EnumerateVisibleWindows())
                {
                    if (!rule.Matches(info)) continue;
                    if (info.ProcessId == (uint)Process.GetCurrentProcess().Id) continue;
                    best = info.Handle;
                    bestTitle = info.Title ?? "";
                    bestClass = info.ClassName ?? "";
                    break;
                }
                if (best == IntPtr.Zero)
                {
                    Say("没有找到匹配的可见窗口：请先打开要拦截的窗口，再校准");
                    return;
                }
            }
            else
            {
                // 纯截图规则对任何窗口都成立，自动选会选到错的窗口——让用户点选
                Say("这条规则没有匹配条件，请点选要校准的窗口");
                var picked = PickWindowInteractive();
                if (picked == null) { Say("已取消校准"); return; }
                best = picked.Handle;
                bestTitle = picked.Title ?? "";
                bestClass = picked.ClassName ?? "";
            }

            var samples = SampleWindow(best);
            if (samples.Count < 2) { Say("截图失败，无法校准"); return; }

            int threshold, minRate, jitter;
            ComputeThreshold(samples, rule.ColorTolerance, out threshold, out minRate, out jitter);
            int old = rule.EffectiveThreshold();
            rule.MatchRatio = threshold;
            rule.AutoThreshold = true;
            rule.Points = new List<CapturePoint>(samples[0]);
            rule.Templates = ToTemplateList(samples);   // 多套模板：任一套达标即命中
            // 顺手更新识别信息（窗口可能已经换了版本/标题）
            rule.RecognizedTitle = bestTitle;
            rule.RecognizedClass = bestClass;
            guard.ResetHealth(rule);   // 阈值变了，旧的健康度样本不再有参考价值
            SettingsStore.Save(settings);
            LoadRules();
            Say("已按当前画面重新校准「" + bestTitle + "」：阈值 " + old + "% → " + threshold
                + "%（" + samples.Count + " 套模板，最低互比 " + minRate + "%，抖动 " + jitter + "%）");
        }

        /// <summary>窗口是否永远拦不了（白名单）。是则给出提示后缀。</summary>
        private string WarnSuffix(WindowInfo info)
        {
            string reason = guard.WhitelistReason(info);
            return string.IsNullOrEmpty(reason) ? "" : "（注意：" + reason + "在白名单内，这条规则不会生效）";
        }

        /// <summary>规则是否命中白名单（命中就永远不会被拦），是则返回原因。</summary>
        private string BlockedRuleReason(WindowRule r)
        {
            if (r == null) return "";
            return guard.WhitelistReason(new WindowInfo
            {
                ProcessName = r.ProcessName ?? "",
                ClassName = r.WindowClass ?? "",
            });
        }

        /// <summary>框选屏幕区域 → 生成截图拦截规则（点阵特征）。</summary>
        private void CaptureImageRule()
        {
            using (var overlay = new CaptureOverlayForm())
            {
                if (overlay.ShowDialog(this) != DialogResult.OK) return;

                // 先确认能识别到这个窗口的身份信息，再花时间采样
                string title = "", proc = "", progPath = "", cls = "";
                var captured = PopupGuard.Describe(overlay.SelectedHwnd, true);
                if (captured != null)
                {
                    title = captured.Title ?? "";
                    proc = captured.ProcessName ?? "";
                    cls = captured.ClassName ?? "";
                    progPath = Native.ProcessPath(captured.ProcessId);
                }

                // 截图规则必须有属性条件兜底（进程名最理想）：否则它会对任何窗口都成立，
                // 守卫每轮要给每个可见窗口截图，CPU 会被吃满。
                if (string.IsNullOrEmpty(proc) && string.IsNullOrEmpty(cls) && string.IsNullOrEmpty(title))
                {
                    Say("识别不到该窗口的标题 / 类名 / 进程名，无法生成截图规则（缺了限定条件会拖慢拦截）");
                    return;
                }

                int capW = 0, capH = 0;
                Native.RECT capRect;
                if (overlay.SelectedHwnd != IntPtr.Zero && Native.GetWindowRect(overlay.SelectedHwnd, out capRect))
                {
                    capW = capRect.Right - capRect.Left;
                    capH = capRect.Bottom - capRect.Top;
                }

                // 连续采样 5 次：窗口内容会动（会话列表、头像、时间），
                // 按实测到的抖动自动定阈值，比写死 80% 稳得多。
                var samples = SampleWindow(overlay.SelectedHwnd);
                if (samples.Count == 0) { Say("截图失败，请重试"); return; }

                int threshold, minRate, jitter;
                // 新规则的容差固定 16（见下面 rule.ColorTolerance = 16）
                ComputeThreshold(samples, 16, out threshold, out minRate, out jitter);

                var rule = new WindowRule
                {
                    UseImage = true,
                    Points = new List<CapturePoint>(samples[0]),
                    // 每套采样都存成模板：窗口内容会变（列表滚动/不同聊天），
                    // 匹配时任一套达标即命中，避免"同一窗口换个内容就漏拦"
                    Templates = ToTemplateList(samples),
                    MatchRatio = threshold,
                    AutoThreshold = true,
                    ColorTolerance = 16,
                    // 进程名作为程序身份：别的程序即使画面偶然相似也不会被误拦
                    ProcessName = proc,
                    SoftwareName = proc,
                    ProgramPath = progPath,
                    RecognizedTitle = title,
                    RecognizedClass = cls,
                    DisplayName = RuleDisplayName(title, proc),
                    CapturedWidth = capW > 0 ? (int?)capW : null,
                    CapturedHeight = capH > 0 ? (int?)capH : null,
                };
                // 保存缩略图（供列表悬停预览）
                try
                {
                    rule.Thumbnail = ScreenCapture.CaptureThumbnail(overlay.SelectedHwnd, 160);
                }
                catch { }
                lock (SettingsStore.RulesGate) { settings.WindowRules.Add(rule); }
                SettingsStore.Save(settings);
                LoadRules();
                UpdateStatus();
                Say("已生成截图规则（" + rule.Points.Count + " 点 × " + samples.Count + " 套模板，最低互比 "
                    + minRate + "%，抖动 " + jitter + "%，阈值 " + threshold + "%）"
                    + (proc.Length > 0 ? "，限进程 " + proc : "") + "）→ " + rule.Describe()
                    + WarnSuffix(captured));
            }
        }

        // ---------------- 列表 ----------------

        private void RefreshWindows()
        {
            // 句柄还没创建时不能 BeginInvoke，等 OnHandleCreated 再刷
            if (!IsHandleCreated) return;
            SetPlaceholder();
            var thread = new System.Threading.Thread(delegate()
            {
                List<WindowInfo> windows;
                try { windows = PopupGuard.EnumerateVisibleWindows(); }
                catch { windows = new List<WindowInfo>(); }
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(new Action(delegate { ShowWindows(windows); }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>枚举期间先显示一行提示，但不影响已保存的规则组。</summary>
        private void SetPlaceholder()
        {
            if (!IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke(new Action(SetPlaceholder)); return; }
            RemoveGroupItems(groupWindows);
            var row = new ListViewItem("正在枚举窗口…");
            row.Group = groupWindows;
            combined.Items.Add(row);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            LoadRules();
            RefreshWindows();
        }

        private void ShowWindows(List<WindowInfo> windows)
        {
            if (IsDisposed) return;
            RemoveGroupItems(groupWindows);
            foreach (var info in windows)
            {
                var row = new ListViewItem("");
                row.SubItems.Add(info.Title);
                row.SubItems.Add(info.ProcessName);
                row.SubItems.Add(info.ClassName);
                row.SubItems.Add("");
                row.SubItems.Add("");
                row.Tag = info;
                row.Group = groupWindows;
                combined.Items.Add(row);
            }
            ListViewUtil.AutoFit(combined, combinedSizes, new[] { 26, 280, 150, 190, 50, 140 });
            UpdateStatus();
        }

        private void RemoveGroupItems(ListViewGroup group)
        {
            var removed = new List<ListViewItem>();
            foreach (ListViewItem item in combined.Items)
                if (item.Group == group) removed.Add(item);
            foreach (ListViewItem item in removed) combined.Items.Remove(item);
        }

        /// <summary>拦截次数：某分组（含其所有子程序）被「窗口拦截」的次数。</summary>
        private int CountBlocks(RuleIo.PopupGroup g) { return CountBlocks(g.Rules); }

        private int CountBlocks(List<WindowRule> rules)
        {
            // 按“进程名”归属：标题会重名（微信主界面/视频号都叫“微信”），按标题算会把别家算进来
            var procs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rules)
            {
                if (!string.IsNullOrEmpty(r.SoftwareName)) procs.Add(r.SoftwareName);
                if (!string.IsNullOrEmpty(r.ProcessName)) procs.Add(r.ProcessName);
                if (!string.IsNullOrEmpty(r.DisplayName)) titles.Add(r.DisplayName);
                if (!string.IsNullOrEmpty(r.TitleKeyword)) titles.Add(r.TitleKeyword);
            }
            int count = 0;
            foreach (var rec in blockLog.Snapshot())
            {
                if (rec.Category != "窗口拦截") continue;
                // 同因重复会合并成一行（Count 累加），按 Count 累加才不等于少算
                int n = Math.Max(1, rec.Count);
                if (procs.Count > 0)
                {
                    if (!string.IsNullOrEmpty(rec.ProcessName) && procs.Contains(rec.ProcessName)) count += n;
                }
                else if (titles.Contains(rec.Title)) count += n;   // 实在不知道进程的旧规则才退回按标题
            }
            return count;
        }

        /// <summary>分组的图标路径：优先主程序（所在目录等于分组根）的完整路径。</summary>
        private static string GroupIconPath(RuleIo.PopupGroup g)
        {
            foreach (var r in g.Rules)
            {
                if (string.IsNullOrEmpty(r.ProgramPath)) continue;
                string d = System.IO.Path.GetDirectoryName(r.ProgramPath.TrimEnd('\\', '/')) ?? "";
                if (string.Equals(d, g.Path, StringComparison.OrdinalIgnoreCase)) return r.ProgramPath;
            }
            foreach (var r in g.Rules)
                if (!string.IsNullOrEmpty(r.ProgramPath)) return r.ProgramPath;
            return RuleIo.FindProcessPath(g.ProcessName);
        }

        /// <summary>填充分组规则列表：组标题行可折叠、带启用开关；组内是规则。</summary>
        private void LoadRules()
        {
            if (!IsHandleCreated) return;
            ruleList.BeginUpdate();
            ruleList.Items.Clear();
            ruleList.Groups.Clear();
            ruleIcons.Images.Clear();

            var rules = settings.WindowRules;
            if (rules != null)
            {
                bool filledPath;
                List<RuleIo.PopupGroup> groups;
                lock (SettingsStore.RulesGate)
                {
                    // GroupBySoftware 会顺手补齐缺失的 ProgramPath（写规则对象），所以要加锁
                    groups = RuleIo.GroupBySoftware(rules, delegate(List<WindowRule> rs) { return CountBlocks(rs); },
                                                     out filledPath);
                }
                if (filledPath) SettingsStore.Save(settings);   // 补到的路径写回，下次直接可用
                foreach (var g in groups)
                {
                    // 勾了「隐藏未触发规则」：按「单条规则」判断，没拦过的规则不占地方
                    List<WindowRule> visibleRules = g.Rules;
                    if (_hideUntriggered)
                    {
                        visibleRules = new List<WindowRule>();
                        foreach (var r in g.Rules)
                        {
                            if (!r.Enabled) continue;
                            if (CountBlocks(new List<WindowRule> { r }) > 0) visibleRules.Add(r);
                        }
                    }
                    if (visibleRules.Count == 0) continue;

                    string swKey = "sw_" + g.Name;
                    try
                    {
                        string path = GroupIconPath(g);
                        if (!string.IsNullOrEmpty(path))
                        {
                            using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(path))
                            {
                                if (icon != null) ruleIcons.Images.Add(swKey, icon.ToBitmap());
                            }
                        }
                    }
                    catch { }
                    if (!ruleIcons.Images.ContainsKey(swKey)) ruleIcons.Images.Add(swKey, DefaultIcon());

                    // 整组拦截次数只算一次（日志最多 200 条 × 规则数，逐行重算很浪费）
                    int groupBlocks = CountBlocks(g);

                    var lg = new ListViewGroup(g.Name);
                    ruleList.Groups.Add(lg);

                    // 组标题行：点击折叠/展开，右键管理整组，操作列是开关
                    bool groupEnabled = GroupAllOn(g);
                    string groupState = groupEnabled ? "已启用" : (GroupAnyOn(g) ? "部分启用" : "已停用");
                    var head = new ListViewItem();
                    head.Text = (_collapsed.Contains(g.Name) ? "▶ " : "▼ ") + g.Name + "（共 " + visibleRules.Count + " 项）";
                    head.Tag = g;                       // Tag = 组 → 点击折叠、右键管整组
                    head.Group = lg;
                    head.ImageKey = swKey;
                    head.SubItems.Add("");
                    head.SubItems.Add("");
                    head.SubItems.Add(groupState);
                    ruleList.Items.Add(head);

                    if (!_collapsed.Contains(g.Name))
                    {
                        foreach (var r in visibleRules)
                        {
                            // 被拦截程序的图标：可能是软件主程序，也可能是它下面的子程序
                            string procKey = "";
                            string proc = TextUtil.FirstNonEmpty(r.ProcessName, r.SoftwareName);
                            if (!string.IsNullOrEmpty(proc))
                            {
                                procKey = "proc_" + proc;
                                if (!ruleIcons.Images.ContainsKey(procKey))
                                {
                                    try
                                    {
                                        string path = TextUtil.FirstNonEmpty(r.ProgramPath, RuleIo.FindProcessPath(proc));
                                        if (!string.IsNullOrEmpty(path))
                                            using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(path))
                                            {
                                                if (icon != null) ruleIcons.Images.Add(procKey, icon.ToBitmap());
                                            }
                                    }
                                    catch { }
                                    if (!ruleIcons.Images.ContainsKey(procKey)) ruleIcons.Images.Add(procKey, DefaultIcon());
                                }
                            }

                            string thumbKey = "";
                            if (r.Thumbnail != null && r.Thumbnail.Length > 0)
                            {
                                thumbKey = "thumb_" + rules.IndexOf(r);
                                if (!ruleIcons.Images.ContainsKey(thumbKey))
                                {
                                    try
                                    {
                                        // 拷一份再入 ImageList：Image.FromStream 要求流在图片存活期间不释放
                                        using (var ms = new System.IO.MemoryStream(r.Thumbnail))
                                        using (var src = Image.FromStream(ms))
                                            ruleIcons.Images.Add(thumbKey, new Bitmap(src));
                                    }
                                    catch { thumbKey = ""; }
                                }
                            }

                            string mark = HealthMark(r);
                            string blocked = BlockedRuleReason(r);
                            if (blocked.Length > 0) mark = (mark.Length > 0 ? mark + " " : "") + "（永不生效）";
                            var row = new ListViewItem(mark.Length > 0 ? RuleDisplayName(r) + " " + mark
                                                                     : RuleDisplayName(r));
                            row.SubItems.Add(groupBlocks.ToString());
                            row.SubItems.Add(r.Enabled ? "已启用" : "已停用");
                            row.Tag = new RuleRow { Rule = r, ThumbKey = thumbKey, ProcKey = procKey, SwKey = swKey };
                            row.Group = lg;
                            row.ImageKey = thumbKey.Length > 0 ? thumbKey
                                         : (procKey.Length > 0 ? procKey : swKey);
                            ruleList.Items.Add(row);
                        }
                    }
                }
            }
            ruleList.EndUpdate();
            ListViewUtil.AutoFit(ruleList, ruleSized, new[] { 220, 70, 70 });
            UpdateStatus();
        }

        private readonly HashSet<string> _collapsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _allEnabled = true;
        private readonly HashSet<int> ruleSized = new HashSet<int>();
        private HashSet<int> combinedSizes = new HashSet<int>();

        /// <summary>鼠标悬停在带缩略图的规则上时，显示弹窗预览大图（火绒效果）。</summary>
        private PreviewForm previewForm;
        private WindowRule lastPreviewRule;   // 同一条规则不重复刷新，避免狂建 Bitmap

        /// <summary>行首截图缩略图的右边界；只有压在这里才弹悬停预览。</summary>
        private int ThumbZoneRight
        {
            get { return 2 + IndentPx + IconSize; }
        }

        private void RuleList_MouseMove(object sender, MouseEventArgs e)
        {
            ListViewItem item = ruleList.GetItemAt(e.X, e.Y);
            var row = item == null ? null : item.Tag as RuleRow;
            if (row == null || row.ThumbKey.Length == 0 || e.X > ThumbZoneRight)
            {
                if (previewForm != null) previewForm.Hide();
                lastPreviewRule = null;
                return;
            }
            var rule = row.Rule;
            if (ReferenceEquals(rule, lastPreviewRule) && previewForm != null && previewForm.Visible) return;
            lastPreviewRule = rule;
            try
            {
                if (previewForm == null)
                    previewForm = new PreviewForm();
                previewForm.ShowImage(rule.Thumbnail);
            }
            catch { }
        }

        // ---------------- 自绘：行首图标 + 操作列开关 ----------------

        private const int IconSize = 20;   // 与 ruleIcons.ImageSize 一致

        /// <summary>子项缩进量：两个全角文字的宽度。</summary>
        private int IndentPx
        {
            get
            {
                int one = TextRenderer.MeasureText("　", ruleList.Font).Width;   // 全角空格 ≈ 一个汉字宽
                return one * 2;
            }
        }

        /// <summary>子项行的附加信息：规则本身 + 各类图标在 ruleIcons 里的键。</summary>
        private sealed class RuleRow
        {
            public WindowRule Rule;
            public string ThumbKey = "";   // 截图缩略图（悬停看大图）
            public string ProcKey = "";    // 被拦截程序（可能是子程序）的图标
            public string SwKey = "";      // 所属软件的图标
        }

        private void RuleList_DrawItem(object sender, DrawListViewItemEventArgs e)
        {
            // 整行内容都在这里画：实测悬停时系统可能不再重画子项（次数和开关会被“吃掉”），
            // 所以图标、名称、次数、开关一律放在行级事件里，不依赖 DrawSubItem。
            bool hl = IsRowHighlighted(e.Item);
            Rectangle row = e.Bounds;
            e.DrawDefault = false;   // 整行由我自己画，别让系统再拿默认内容盖上来
            using (var bg = new SolidBrush(hl ? SystemColors.Highlight : ruleList.BackColor))
                e.Graphics.FillRectangle(bg, row);

            var popupGroup = e.Item.Tag as RuleIo.PopupGroup;
            if (popupGroup != null)   // 组标题行：软件图标 + 名称 + 整组开关
            {
                int x = DrawIcon(e.Graphics, "sw_" + popupGroup.Name, row.X + 2, row);
                bool on = GroupAllOn(popupGroup);
                string text = e.Item.Text;
                if (!on && GroupAnyOn(popupGroup) && text.EndsWith("）"))
                    text = text.Insert(text.Length - 1, "，部分启用");   // 合并进"（共 N 项）"里，避免两段括号
                DrawCellText(e.Graphics, text, x + 4, ColumnRect(0, row), hl);
                DrawSwitch(e.Graphics, ColumnRect(2, row), on);
                return;
            }

            var item = e.Item.Tag as RuleRow;
            if (item == null) return;

            int rx = row.X + 2 + IndentPx;   // 子项整体向右错两个文字位
            if (item.ThumbKey.Length > 0) rx = DrawIcon(e.Graphics, item.ThumbKey, rx, row);
            if (item.ProcKey.Length > 0) rx = DrawIcon(e.Graphics, item.ProcKey, rx, row);
            if (item.ThumbKey.Length == 0 && item.ProcKey.Length == 0)
                rx = DrawIcon(e.Graphics, item.SwKey, rx, row);
            DrawCellText(e.Graphics, e.Item.Text, rx + 4, ColumnRect(0, row), hl);

            string count = e.Item.SubItems.Count > 1 ? e.Item.SubItems[1].Text : "";
            DrawCellText(e.Graphics, count, ColumnRect(1, row).X + 4, ColumnRect(1, row), hl);

            DrawSwitch(e.Graphics, ColumnRect(2, row), item.Rule.Enabled);
        }

        /// <summary>自绘模式下拉列标题不再由系统画，这里补上（否则列名全消失）。</summary>
        /// <summary>两套采样点是否一致（判断编辑有没有真的改到画面条件）。</summary>
        private static bool SamePoints(System.Collections.Generic.List<CapturePoint> a,
                                       System.Collections.Generic.List<CapturePoint> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (Math.Abs(a[i].X - b[i].X) > 1e-9 || Math.Abs(a[i].Y - b[i].Y) > 1e-9 ||
                    a[i].R != b[i].R || a[i].G != b[i].G || a[i].B != b[i].B) return false;
            return true;
        }

        private void RuleList_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            e.DrawBackground();
            TextRenderer.DrawText(e.Graphics, e.Header.Text, ruleList.Font,
                new Rectangle(e.Bounds.X + 4, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 8), e.Bounds.Height),
                ruleList.ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        /// <summary>第 index 列在行矩形里的位置（按当前列宽累加）。</summary>
        private Rectangle ColumnRect(int index, Rectangle row)
        {
            int x = row.X;
            for (int i = 0; i < index && i < ruleList.Columns.Count; i++)
                x += ruleList.Columns[i].Width;
            int w = index < ruleList.Columns.Count ? ruleList.Columns[index].Width : Math.Max(0, row.Right - x);
            return new Rectangle(x, row.Y, w, row.Height);
        }

        private bool IsRowHighlighted(ListViewItem item)
        {
            if (item == null || !item.Selected) return false;
            if (ruleList.HideSelection && !ruleList.Focused) return false;
            return true;
        }

        private void DrawCellText(Graphics g, string text, int x, Rectangle cell, bool highlight)
        {
            if (x >= cell.Right || string.IsNullOrEmpty(text)) return;
            // 用整行高度做垂直居中：用点当左上角会让文字贴着行顶，下半截被裁掉
            TextRenderer.DrawText(g, text, ruleList.Font,
                new Rectangle(x, cell.Y, Math.Max(1, cell.Right - x), cell.Height),
                highlight ? SystemColors.HighlightText : ruleList.ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        /// <summary>在行首画一个图标，返回下一个可用 x。</summary>
        private int DrawIcon(Graphics g, string key, int x, Rectangle row)
        {
            if (ruleIcons.Images.ContainsKey(key))
            {
                int y = row.Y + Math.Max(0, (row.Height - IconSize) / 2);
                g.DrawImage(ruleIcons.Images[key], x, y, IconSize, IconSize);
                return x + IconSize + 4;
            }
            return x;
        }

        /// <summary>火绒式开关：蓝=开，灰=关，白色圆钮。尺寸做小，避免把行高撑得错位。
        /// partial=true 表示"部分启用"，圆钮画在中间。</summary>
        /// <summary>
        /// 开关只有"开 / 关"两态（旋钮只落在左右两端）。
        /// 组内"部分启用"不再画成中间态——那看着像卡住了；改成在标题后面写"（部分启用）"。
        /// </summary>
        /// <summary>
        /// 开关在单元格里的实际矩形。绘制与点击命中共用这一处，
        /// 否则列宽变了会出现"画在一个地方、点在另一个地方"。
        /// </summary>
        private static Rectangle SwitchRect(Rectangle bounds)
        {
            const int w = 32, h = 16;
            int x = bounds.X + Math.Max(2, (bounds.Width - w) / 2);
            int y = bounds.Y + Math.Max(0, (bounds.Height - h) / 2);
            return Rectangle.FromLTRB(x, y, x + w, y + h);
        }

        private void DrawSwitch(Graphics g, Rectangle bounds, bool on)
        {
            Rectangle r = SwitchRect(bounds);
            using (var path = RoundSwitch(r, 8))
            using (var brush = new SolidBrush(on ? Color.FromArgb(0xB0, 0xB0, 0xB8)      // 开：浅灰
                                                : Color.FromArgb(0xDA, 0xDA, 0xDE)))   // 关：更浅的灰
            using (var edge = new Pen(Color.FromArgb(0x96, 0x96, 0x9E)))              // 描边，保证任何底色上都看得见
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillPath(brush, path);
                g.DrawPath(edge, path);
            }
            int d = 16 - 4;
            int kx = on ? r.Right - d - 2 : r.Left + 2;
            using (var knob = new SolidBrush(Color.White))
                g.FillEllipse(knob, kx, r.Top + 2, d, d);
        }

        /// <summary>整组是否全部启用（决定开关画"开"还是"关"）。</summary>
        private static bool GroupAllOn(RuleIo.PopupGroup g)
        {
            return g.Rules.Count > 0 && g.Rules.TrueForAll(delegate(WindowRule r) { return r.Enabled; });
        }

        private static bool GroupAnyOn(RuleIo.PopupGroup g)
        {
            return g.Rules.Exists(delegate(WindowRule r) { return r.Enabled; });
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundSwitch(Rectangle r, int radius)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private void RuleList_MouseClick(object sender, MouseEventArgs e)
        {
            ListViewItem item = ruleList.GetItemAt(e.X, e.Y);
            if (item == null) return;

            // 点击「操作」列（第 3 列，索引 2）：切换启用/停用（组标题行=整组，规则行=单条）
            var row = item;
            int col = ColumnIndexAt(e.X);
            if (col == 2)
            {
                // 只有点中开关本身才算：这一列宽 90、开关只有 32 宽，
                // 整列都能拨会误触（点到列的上下空白也跳）。
                if (!SwitchRect(ColumnRect(2, row.Bounds)).Contains(e.X, e.Y)) return;

                var popupGroup = row.Tag as RuleIo.PopupGroup;
                if (popupGroup != null)
                {
                    bool enable = !GroupAllOn(popupGroup);   // 全开 → 全关；混合/全关 → 全开
                    lock (SettingsStore.RulesGate)
                        foreach (var r in popupGroup.Rules) r.Enabled = enable;
                    if (!enable) foreach (var r in popupGroup.Rules) guard.RestoreWindowsOf(r);
                    SettingsStore.Save(settings);
                    // 只重绘，不重建：重建会让下面所有行的文字整体刷一遍
                    ruleList.Invalidate();
                    Say((enable ? "已启用组：" : "已停用组：") + popupGroup.Name);
                    return;
                }
                var ruleRow = row.Tag as RuleRow;
                var rule = ruleRow != null ? ruleRow.Rule : null;
                if (rule != null)
                {
                    rule.Enabled = !rule.Enabled;
                    if (!rule.Enabled) guard.RestoreWindowsOf(rule);
                    SettingsStore.Save(settings);
                    ruleList.Invalidate();   // 只重绘这一片，不重建整张列表
                    Say((rule.Enabled ? "已启用：" : "已停用：") + RuleDisplayName(rule));
                }
                return;
            }

            var group = item.Tag as RuleIo.PopupGroup;
            if (group == null) return;
            if (_collapsed.Contains(group.Name)) _collapsed.Remove(group.Name);
            else _collapsed.Add(group.Name);
            LoadRules();
        }

        /// <summary>返回 x 处所在的列索引；非详情视图返回 -1。</summary>
        private int ColumnIndexAt(int x)
        {
            int acc = 0;
            for (int i = 0; i < ruleList.Columns.Count; i++)
            {
                acc += ruleList.Columns[i].Width;
                if (x < acc) return i;
            }
            return ruleList.Columns.Count - 1;
        }

        /// <summary>采集到的窗口 → 显示名：优先窗口标题，其次软件名，都没有就写「未知程序」。</summary>
        private static string RuleDisplayName(string title, string processName)
        {
            if (!string.IsNullOrEmpty(title)) return title;
            if (!string.IsNullOrEmpty(processName)) return processName;
            return "未知程序";
        }

        private static string RuleDisplayName(WindowRule r)
        {
            if (!string.IsNullOrEmpty(r.DisplayName)) return r.DisplayName;
            if (!string.IsNullOrEmpty(r.SoftwareName)) return r.SoftwareName + "（截图）";
            if (!string.IsNullOrEmpty(r.TitleKeyword)) return r.TitleKeyword;
            if (!string.IsNullOrEmpty(r.WindowClass)) return r.WindowClass;
            if (!string.IsNullOrEmpty(r.ProcessName)) return r.ProcessName + "（截图）";
            return "截图规则";
        }

        private static System.Drawing.Image DefaultIcon()
        {
            var bmp = new System.Drawing.Bitmap(20, 20);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Transparent);
                using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x3B, 0x7B, 0xFF)))
                    g.FillRectangle(brush, 2, 2, 16, 16);
            }
            return bmp;
        }

        // 进程名 → 路径的缓存：列表刷新时每个软件都会查一次，缓存 5 分钟够用
        private static readonly Dictionary<string, string> procPathCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// 规则健康标记（给列表显示）：守卫算出的「失配 / 临界」，
        /// 或者"创建超过 3 天却一次都没拦到"的「从未生效」。
        /// </summary>
        private string HealthMark(WindowRule r)
        {
            if (r == null) return "";
            string mark = guard.HealthMark(r);
            if (mark.Length > 0) return mark;
            DateTime created;
            if (DateTime.TryParse(r.CreatedAt ?? "", out created) &&
                DateTime.Now - created > TimeSpan.FromDays(3) &&
                CountBlocks(new List<WindowRule> { r }) == 0)
                return "（从未生效）";
            return "";
        }

        private void UpdateStatus()
        {
            int rules = settings.WindowRules == null ? 0 : settings.WindowRules.Count;
            int today = blockLog.Snapshot().Count(delegate(BlockRecord r)
            {
                DateTime t;
                return r.Category == "窗口拦截" && DateTime.TryParse(r.Time, out t) && t.Date == DateTime.Today;
            });
            Say((settings.PopupGuardEnabled ? "监控中" : "已停用") +
                " · 规则 " + rules + " 条 · 今日已拦截 " + today + " 次");
        }

        private void SyncGuardState()
        {
            if (settings.PopupGuardEnabled) guard.Start();
            else guard.Stop();
        }

        // ---------------- 规则编辑 ----------------

        /// <summary>当前右键选中的规则（子项行）。</summary>
        private WindowRule SelectedRule()
        {
            if (ruleList.SelectedItems.Count == 0) return null;
            var rr = ruleList.SelectedItems[0].Tag as RuleRow;
            return rr != null ? rr.Rule : null;
        }

        /// <summary>当前右键选中的组（软件标题行）。</summary>
        private RuleIo.PopupGroup SelectedGroup()
        {
            if (ruleList.SelectedItems.Count == 0) return null;
            return ruleList.SelectedItems[0].Tag as RuleIo.PopupGroup;
        }

        private void EditSelectedRule()
        {
            var rule = SelectedRule();
            if (rule == null) return;
            // 下拉候选：已有的归属软件 + 现有哪些软件组
            var families = new List<string>();
            foreach (var r in settings.WindowRules)
                if (!string.IsNullOrEmpty(r.Family) && !families.Contains(r.Family, StringComparer.OrdinalIgnoreCase))
                    families.Add(r.Family);
            foreach (var g in RuleIo.GroupBySoftware(settings.WindowRules,
                                                       delegate(List<WindowRule> rs) { return 0; },
                                                       out _))
            {
                if (!families.Contains(g.Name, StringComparer.OrdinalIgnoreCase)) families.Add(g.Name);
            }
            // 先记下会影响判定的字段：只有这些变了才需要清掉旧的健康度样本
            string oldTitle = rule.TitleKeyword, oldProc = rule.ProcessName, oldClass = rule.WindowClass;
            bool oldAnd = rule.AndMode, oldImg = rule.UseImage;
            int oldRatio = rule.MatchRatio;
            System.Collections.Generic.List<CapturePoint> oldPoints = rule.Points;

            using (var dialog = new WindowRuleEditForm(rule, families))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                bool matchChanged =
                    oldTitle != rule.TitleKeyword || oldProc != rule.ProcessName || oldClass != rule.WindowClass ||
                    oldAnd != rule.AndMode || oldImg != rule.UseImage || oldRatio != rule.MatchRatio ||
                    !SamePoints(oldPoints, rule.Points);

                SettingsStore.Save(settings);      // 这里会重算指纹
                if (matchChanged) guard.ResetHealth(rule);   // 只改归属软件之类的不必清
                LoadRules();
                UpdateStatus();
            }
        }

        private void DeleteSelectedRule()
        {
            var rule = SelectedRule();
            if (rule == null) return;
            guard.RestoreWindowsOf(rule);
            lock (SettingsStore.RulesGate) { settings.WindowRules.Remove(rule); }
            SettingsStore.Save(settings);
            LoadRules();
            UpdateStatus();
        }

        /// <summary>把选中规则藏起来的窗口放回来（规则本身保留、保持启用）。</summary>
        private void RestoreSelectedRuleWindows()
        {
            var rule = SelectedRule();
            if (rule == null) return;
            int n = guard.RestoreWindowsOf(rule);
            Say(n > 0 ? ("已恢复 " + n + " 个窗口：" + RuleDisplayName(rule))
                      : "这条规则当前没有藏起窗口");
        }

        // ---------------- 组（软件）管理 ----------------

        private void EnableGroup(bool enable)
        {
            var g = SelectedGroup();
            if (g == null) return;
            lock (SettingsStore.RulesGate)
                foreach (var r in g.Rules) r.Enabled = enable;
            if (!enable) foreach (var r in g.Rules) guard.RestoreWindowsOf(r);
            SettingsStore.Save(settings);
            LoadRules();
            Say((enable ? "已启用组：" : "已停用组：") + g.Name);
        }

        private void RestoreGroupWindows()
        {
            var g = SelectedGroup();
            if (g == null) return;
            int n = 0;
            foreach (var r in g.Rules) n += guard.RestoreWindowsOf(r);
            Say(n > 0 ? ("已恢复 " + n + " 个窗口：" + g.Name) : "该组当前没有藏起窗口");
        }

        private void DeleteGroup()
        {
            var g = SelectedGroup();
            if (g == null) return;
            foreach (var r in g.Rules) guard.RestoreWindowsOf(r);
            lock (SettingsStore.RulesGate)
                foreach (var r in g.Rules) settings.WindowRules.Remove(r);
            SettingsStore.Save(settings);
            LoadRules();
            Say("已删除该组：" + g.Name);
        }

        private void MoveRule(int delta)
        {
            var rule = SelectedRule();
            if (rule == null) return;
            int index = settings.WindowRules.IndexOf(rule);
            int target = index + delta;
            if (target < 0 || target >= settings.WindowRules.Count) return;
            lock (SettingsStore.RulesGate)
            {
                settings.WindowRules.RemoveAt(index);
                settings.WindowRules.Insert(target, rule);
            }
            SettingsStore.Save(settings);
            LoadRules();
        }
    }
}
