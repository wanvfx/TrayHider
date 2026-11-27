using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrayHider
{
    internal sealed class MainForm : Form
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private static readonly uint SelfPid = (uint)Process.GetCurrentProcess().Id;
        // 开机后一段时间内用更快的扫描频率，尽快把「已隐藏」的图标藏掉，避免逐个闪现。
        private const int NormalScanInterval = 2500;  // 稳定期扫描间隔（毫秒）
        private const int BoostScanInterval = 350;    // 开机快速阶段扫描间隔（毫秒）
        private const int BoostDurationSeconds = 60;  // 开机快速阶段持续时长（秒）

        private HotkeyEngine hotkey;
        private bool hotkeyHookReady;
        private bool startHidden;
        private PopupGuard popupGuard;
        private BlockLog blockLog;

        private readonly ListView list = new ListView();
        private readonly TextBox filter = new TextBox();
        private readonly ListView logList = new ListView();
        private TabControl tabs;
        private readonly StatusStrip status = new StatusStrip();
        private readonly ToolStripStatusLabel statusLabel = new ToolStripStatusLabel() { Spring = true };
        private NotifyIcon tray = null;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly Settings settings;
        private ImageList images = null;
        private Icon trayIcon = null;
        private string lastSignature = "";
        private bool forceClose;
        private bool initialized;
        private bool wasHiddenStart;
        private DateTime boostUntil = DateTime.MinValue;
        private List<TrayItem> lastItems = new List<TrayItem>();

        // 列宽自适应：默认按内容取宽；用户手动拖过的列保持他的宽度
        private readonly HashSet<int> traySized = new HashSet<int>();
        private readonly HashSet<int> logSized = new HashSet<int>();

        // 日志页刷新节流：把短时间内的多次刷新合并成一次
        private readonly System.Windows.Forms.Timer logRefreshTimer =
            new System.Windows.Forms.Timer { Interval = 300 };
        private bool logRefreshPending;

        public MainForm(Settings settings)
        {
            this.settings = settings;
            // 归一化：隐藏自身托盘必须以快捷键为逃生通道。
            // 历史配置若「隐藏了自身」却没开快捷键，这里把关掉，避免窗口隐藏后无法找回。
            if (settings.HideSelfIcon && !settings.HotkeyEnabled)
                settings.HideSelfIcon = false;
            // 是否开机自启以注册表为准。
            startHidden = IsAutoStart() && settings.MinimizeToTrayOnStart;
            timer.Interval = NormalScanInterval;
            Text = "TrayHider-藏起一切打扰";
            Icon = CreateAppIcon();
            Size = new Size(1020, 720);
            // 不限制最小尺寸：用户可以自由拖拽窗口大小（内容超出时由面板滚动）
            StartPosition = FormStartPosition.CenterScreen;

            // 拦截记录与引擎：构造时就建好，规则页始终可用；是否跑引擎由 PopupGuardEnabled 决定。
            blockLog = new BlockLog();
            popupGuard = new PopupGuard(settings, blockLog);
            // 拦到窗口、或出现新的「判定」记录时刷新界面（见 OnGuardActivity / OnGuardDiagnosticsChanged）
            popupGuard.Blocked += delegate { BeginInvoke(new Action(OnGuardActivity)); };
            popupGuard.DiagnosticsChanged += delegate { BeginInvoke(new Action(OnGuardDiagnosticsChanged)); };
            // 自动校准改了阈值：落盘并刷新列表
            popupGuard.SettingsChanged += delegate
            {
                BeginInvoke(new Action(delegate
                {
                    SettingsStore.Save(settings);
                    if (rulePanelRefresh != null) rulePanelRefresh();
                }));
            };

            MenuStrip menu = BuildMenu();
            tabs = BuildTabs();
            BuildTray();

            status.Items.Add(statusLabel);

            // Dock 顺序：后添加的先布局，菜单在最上，标签页占满中间，状态栏在最下。
            Controls.Add(tabs);
            Controls.Add(status);
            Controls.Add(menu);

            Load += MainForm_Load;
            FormClosing += MainForm_FormClosing;
            list.MouseDoubleClick += List_MouseDoubleClick;
            list.KeyDown += List_KeyDown;
            list.MouseDown += List_MouseDown;
            filter.TextChanged += delegate { RefreshList(lastItems); };

            logRefreshTimer.Tick += delegate
            {
                logRefreshTimer.Stop();
                if (logRefreshPending) { logRefreshPending = false; RefreshLogList(); }
            };
        }

        /// <summary>合并短时间内的多次日志页刷新（守卫可能连续产生判定）。</summary>
        private void QueueLogRefresh()
        {
            if (tabs == null || tabs.SelectedIndex != 2) return;   // 没停在日志页就不必刷
            logRefreshPending = true;
            if (!logRefreshTimer.Enabled) logRefreshTimer.Start();
        }

        /// <summary>构建两个切换页：托盘隐藏 / 窗口规则。</summary>
        private TabControl BuildTabs()
        {
            var tabs = new TabControl() { Dock = DockStyle.Fill };

            // 页一：托盘隐藏
            var trayPage = new TabPage("托盘隐藏");
            var top = new Panel() { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8) };
            const string hint = "按名称 / 提示 / 路径筛选";
            filter.Width = 300;
            filter.Height = 24;
            filter.Left = 8;
            filter.Top = 9;
            filter.Text = hint;
            filter.ForeColor = Color.Gray;
            filter.Enter += delegate
            {
                if (filter.Text == hint) { filter.Text = ""; filter.ForeColor = SystemColors.WindowText; }
            };
            filter.Leave += delegate
            {
                if (filter.Text.Length == 0) { filter.Text = hint; filter.ForeColor = Color.Gray; }
            };
            top.Controls.Add(filter);
            trayPage.Controls.Add(BuildList());
            trayPage.Controls.Add(top);

            // 页二：窗口规则
            var rulePage = new TabPage("窗口规则");
            var rules = new WindowRulePanel(settings, popupGuard, blockLog);
            rulePanelRefresh = rules.RefreshCallback;
            rules.CaptureChanged += delegate(bool on) { ApplyCaptureAffinity(); };
            rules.StatusMessage += delegate(string message)
            {
                if (IsDisposed) return;
                BeginInvoke(new Action(delegate
                {
                    // 只有停留在规则页时才覆盖主状态栏（构造期 tabs 还为 null，不能覆盖）
                    if (tabs != null && tabs.SelectedIndex == 1) statusLabel.Text = message;
                }));
            };
            rulePage.Controls.Add(rules);

            // 页三：运行日志（窗口拦截 + 托盘隐藏）
            var logPage = new TabPage("运行日志");
            logPage.Controls.Add(BuildLogPanel());

            tabs.TabPages.Add(trayPage);
            tabs.TabPages.Add(rulePage);
            tabs.TabPages.Add(logPage);
            tabs.SelectedIndexChanged += delegate
            {
                UpdateStatusForCurrentTab();
                if (tabs.SelectedIndex == 0) ScanAndRefresh(false);
                else if (tabs.SelectedIndex == 2) RefreshLogList();
            };
            return tabs;
        }

        /// <summary>按当前标签页刷新底部主状态栏：托盘页显示图标统计，规则页显示规则统计。</summary>
        private void UpdateStatusForCurrentTab()
        {
            if (tabs == null) return;
            if (tabs.SelectedIndex == 1)
            {
                int rules = settings.WindowRules == null ? 0 : settings.WindowRules.Count;
                int today = blockLog.Snapshot().Count(delegate(BlockRecord r)
                {
                    DateTime t;
                    return r.Category == "窗口拦截" && DateTime.TryParse(r.Time, out t) && t.Date == DateTime.Today;
                });
                statusLabel.Text = (settings.PopupGuardEnabled ? "监控中" : "已停用") +
                                   " · 规则 " + rules + " 条 · 今日已拦截 " + today + " 次";
            }
            else if (tabs.SelectedIndex == 2)
            {
                var snapshot = blockLog.Snapshot();
                int wins = snapshot.Count(delegate(BlockRecord r) { return r.Category == "窗口拦截"; });
                int judge = snapshot.Count(delegate(BlockRecord r) { return r.Category == "判定"; });
                int tray = snapshot.Count(delegate(BlockRecord r) { return r.Category == "托盘隐藏"; });
                statusLabel.Text = "运行日志 · 窗口拦截 " + wins + " · 判定 " + judge + " · 托盘隐藏 " + tray;
            }
            else
            {
                UpdateStatus(lastItems, 0);
            }
        }

        /// <summary>拦截记录页：只读列表 + 清空。</summary>
        private Panel BuildLogPanel()
        {
            var panel = new Panel() { Dock = DockStyle.Fill };
            var top = new Panel() { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8) };
            var clear = new Button() { Text = "清空记录", Width = 110, Height = 28, Left = 8, Top = 9 };
            clear.Click += delegate { blockLog.Clear(); RefreshLogList(); };
            var export = new Button() { Text = "导出日志", Width = 110, Height = 28, Left = 130, Top = 9 };
            export.Click += delegate { ExportLog(); };
            top.Controls.Add(clear);
            top.Controls.Add(export);

            logList.Dock = DockStyle.Fill;
            logList.View = View.Details;
            logList.FullRowSelect = true;
            logList.MultiSelect = false;
            ListViewUtil.WireAutoFit(logList, logSized);
            logList.Columns.Add("时间", 140);
            logList.Columns.Add("类别", 70);
            logList.Columns.Add("名称", 150);
            logList.Columns.Add("进程", 110);
            logList.Columns.Add("动作", 70);
            logList.Columns.Add("结果", 60);
            logList.Columns.Add("次数", 50);
            logList.Columns.Add("备注", 150);
            panel.Controls.Add(logList);
            panel.Controls.Add(top);
            return panel;
        }

        private void RefreshLogList()
        {
            logList.Items.Clear();
            var records = blockLog.Snapshot();
            for (int i = records.Count - 1; i >= 0; i--)
            {
                var r = records[i];
                var row = new ListViewItem(r.Time);
                row.SubItems.Add(r.Category);
                row.SubItems.Add(r.Title);
                row.SubItems.Add(r.ProcessName);
                row.SubItems.Add(r.Action);
                row.SubItems.Add(r.Success ? "成功" : "未生效");
                row.SubItems.Add(Math.Max(1, r.Count).ToString());
                row.SubItems.Add(r.Note ?? "");
                logList.Items.Add(row);
            }
            ListViewUtil.AutoFit(logList, logSized, new[] { 140, 70, 150, 110, 70, 60, 50, 150 });
        }


        /// <summary>列表右键菜单：选中项上隐藏/显示，空白处刷新。</summary>
        private ContextMenuStrip BuildListMenu()
        {
            var menu = new ContextMenuStrip();
            var hide = new ToolStripMenuItem("隐藏选中(&H)", null, delegate { HideSelected(); });
            var restore = new ToolStripMenuItem("显示选中(&S)", null, delegate { RestoreSelected(); });
            var restoreAll = new ToolStripMenuItem("全部显示(&A)", null, delegate { RestoreAll(); });
            var refresh = new ToolStripMenuItem("刷新(&R)", null, delegate { ScanAndRefresh(true); });
            menu.Items.Add(hide);
            menu.Items.Add(restore);
            menu.Items.Add(restoreAll);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(refresh);
            menu.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                bool hasSelection = list.SelectedItems.Count > 0;
                hide.Enabled = hasSelection;
                restore.Enabled = hasSelection;
                restoreAll.Enabled = true;
            };
            return menu;
        }

        /// <summary>Ctrl+A 全选（窗体级拦截，避免 ListView 不响应）。</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.A))
            {
                if (tabs == null || tabs.SelectedIndex == 0)
                {
                    foreach (ListViewItem item in list.Items) item.Selected = true;
                }
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                foreach (ListViewItem item in list.Items) item.Selected = true;
                e.Handled = true;
            }
        }

        /// <summary>右键在空白处时只保留「刷新」（其余项在 Opening 里已按选中状态启用）。</summary>
        private void List_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = list.HitTest(e.Location);
            // 空白处：清空选择，让菜单只剩可用的「刷新 / 全部显示」
            if (hit.Item == null) list.SelectedItems.Clear();
        }

        private MenuStrip BuildMenu()
        {
            var menu = new MenuStrip();
            var file = new ToolStripMenuItem("操作(&O)");
            file.DropDownItems.Add("刷新(&R)", null, delegate { ScanAndRefresh(true); });
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("隐藏选中图标(&H)", null, delegate { HideSelected(); });
            file.DropDownItems.Add("恢复选中图标(&S)", null, delegate { RestoreSelected(); });
            file.DropDownItems.Add("恢复全部图标(&A)", null, delegate { RestoreAll(); });
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("退出(&X)", null, delegate { ForceExit(); });

            var options = new ToolStripMenuItem("选项(&T)");

            // 1. 快捷键设置
            var hotkey = new ToolStripMenuItem("快捷键设置(&K)...");
            hotkey.Click += delegate
            {
                using (var form = new HotkeyForm(settings))
                {
                    if (form.ShowDialog(this) == DialogResult.OK) ApplyHotkey();
                }
            };
            options.DropDownItems.Add(hotkey);

            // 2. 跟随开机启动
            var autoStart = new ToolStripMenuItem("跟随开机启动(&A)") { CheckOnClick = true };
            autoStart.Checked = IsAutoStart();

            // 3. 开机启动隐藏窗口到托盘（仅当“跟随开机启动”勾选时显示）
            var minimizeOnStart = new ToolStripMenuItem("开机启动隐藏窗口到托盘(&M)") { CheckOnClick = true };
            minimizeOnStart.Checked = settings.MinimizeToTrayOnStart;
            minimizeOnStart.Visible = autoStart.Checked;
            minimizeOnStart.CheckedChanged += delegate
            {
                settings.MinimizeToTrayOnStart = minimizeOnStart.Checked;
                SettingsStore.Save(settings);
            };

            autoStart.CheckedChanged += delegate
            {
                SetAutoStart(autoStart.Checked);
                minimizeOnStart.Visible = autoStart.Checked;
            };

            options.DropDownItems.Add(autoStart);
            options.DropDownItems.Add(minimizeOnStart);

            // 4. 隐藏本工具托盘（要求已启用快捷键，否则窗口隐藏后将无路找回）
            var hideSelf = new ToolStripMenuItem("隐藏本工具托盘(&S)") { CheckOnClick = true };
            hideSelf.Checked = settings.HideSelfIcon;
            hideSelf.CheckedChanged += delegate
            {
                if (hideSelf.Checked)
                {
                    if (!SetHideSelfIcon(true)) hideSelf.Checked = false;
                }
                else SetHideSelfIcon(false);
            };
            options.DropDownItems.Add(hideSelf);

            // 5. 扫描时包含溢出区图标
            var scanOverflow = new ToolStripMenuItem("扫描时包含溢出区图标(&O)") { CheckOnClick = true };
            scanOverflow.Checked = settings.ScanOverflow;
            scanOverflow.CheckedChanged += delegate
            {
                settings.ScanOverflow = scanOverflow.Checked;
                SettingsStore.Save(settings);
            };
            options.DropDownItems.Add(scanOverflow);

            // 6. 窗口拦截：自动校准阈值（默认关——自动改阈值有误杀风险，由用户决定）
            var autoCalibrate = new ToolStripMenuItem("窗口拦截自动校准阈值(&C)") { CheckOnClick = true };
            autoCalibrate.Checked = settings.PopupGuardAutoCalibrate;
            autoCalibrate.CheckedChanged += delegate
            {
                settings.PopupGuardAutoCalibrate = autoCalibrate.Checked;
                SettingsStore.Save(settings);
            };
            options.DropDownItems.Add(autoCalibrate);

            var help = new ToolStripMenuItem("帮助(&H)");
            help.DropDownItems.Add("关于 TrayHider(&A)", null, delegate { ShowAbout(); });

            // 「窗口规则」改为下拉：切换规则页 + 导入/导出规则
            var windowRules = new ToolStripMenuItem("窗口规则(&W)");
            windowRules.DropDownItems.Add("窗口规则设置(&R)", null, delegate { SwitchToRulePage(); });
            windowRules.DropDownItems.Add(new ToolStripSeparator());
            windowRules.DropDownItems.Add("恢复被隐藏窗口(&H)", null, delegate
            {
                int n = popupGuard != null ? popupGuard.RestoreAllHidden() : 0;
                UpdateStatusForCurrentTab();
                tray.ShowBalloonTip(3000, "TrayHider",
                    n > 0 ? ("已恢复 " + n + " 个被隐藏的窗口") : "当前没有被隐藏的窗口", ToolTipIcon.Info);
            });
            windowRules.DropDownItems.Add(new ToolStripSeparator());
            windowRules.DropDownItems.Add("导出运行日志…", null, delegate { ExportLog(); });
            windowRules.DropDownItems.Add(new ToolStripSeparator());
            windowRules.DropDownItems.Add("导入规则…", null, delegate { ImportRules(); });
            windowRules.DropDownItems.Add("导出规则…", null, delegate { ExportRules(); });

            menu.Items.Add(file);
            menu.Items.Add(options);
            menu.Items.Add(windowRules);
            menu.Items.Add(help);
            MainMenuStrip = menu;
            return menu;
        }

        private ListView BuildList()
        {
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = true;
            list.HideSelection = false;
            list.Columns.Add("名称", 200);
            list.Columns.Add("提示文字", 240);
            list.Columns.Add("进程路径", 340);
            list.Columns.Add("状态", 90);
            list.ContextMenuStrip = BuildListMenu();
            ListViewUtil.WireAutoFit(list, traySized);
            images = new ImageList() { ImageSize = new Size(24, 24), ColorDepth = ColorDepth.Depth32Bit };
            list.SmallImageList = images;
            return list;
        }

        private void BuildTray()
        {
            trayIcon = CreateAppIcon();
            tray = new NotifyIcon();
            tray.Icon = trayIcon;
            tray.Text = "TrayHider · 藏起一切打扰";
            tray.Visible = !settings.HideSelfIcon;
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开主窗口", null, delegate { ShowWindow(); });
            menu.Items.Add("恢复全部图标", null, delegate { RestoreAll(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ForceExit(); });
            tray.ContextMenuStrip = menu;
            tray.MouseDoubleClick += delegate { ShowWindow(); };
        }

        private static readonly List<Bitmap> keptIcons = new List<Bitmap>();

        /// <summary>
        /// 运行时绘制程序图标（显示器 + 被划掉的“眼睛”）。
        /// 不依赖 exe 内嵌图标资源，避免在某些系统上取不到图标而回退成默认 exe 图标。
        /// </summary>
        private static Icon CreateAppIcon()
        {
            var bmp = new Bitmap(64, 64);
            keptIcons.Add(bmp);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                using (var body = new SolidBrush(Color.FromArgb(0x3B, 0x7B, 0xFF)))
                    g.FillRectangle(body, 5, 6, 54, 36);
                using (var screen = new SolidBrush(Color.FromArgb(0xF6, 0xFA, 0xFF)))
                    g.FillRectangle(screen, 10, 11, 44, 26);
                using (var stand = new SolidBrush(Color.FromArgb(0x17, 0x3F, 0x8C)))
                    g.FillRectangle(stand, 28, 42, 8, 11);
                using (var foot = new SolidBrush(Color.FromArgb(0x3B, 0x7B, 0xFF)))
                    g.FillRectangle(foot, 15, 53, 34, 6);

                using (var pen = new Pen(Color.FromArgb(0x1E, 0x56, 0xC8), 4f))
                {
                    pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    g.DrawEllipse(pen, 16, 19, 32, 12);
                    using (var pupil = new SolidBrush(Color.FromArgb(0x1E, 0x56, 0xC8)))
                        g.FillEllipse(pupil, 28, 21, 8, 8);
                    g.DrawLine(pen, 14, 31, 50, 17);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private void ShowAbout()
        {
            using (var form = new AboutForm())
            {
                form.StartPosition = FormStartPosition.CenterScreen;
                form.ShowDialog(this);
            }
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            Initialize();
        }

        /// <summary>
        /// 启动扫描与定时器、注册快捷键。放在 OnHandleCreated 里执行而不是 Form.Load，
        /// 是因为开机隐藏启动时窗口从不 Show，Load 根本不会触发；而句柄只要创建（即使隐藏）
        /// 就会触发 OnHandleCreated，这样开机后扫描隐藏依然生效。
        /// </summary>
        private void Initialize()
        {
            if (initialized) return;
            initialized = true;

            ScanAndRefresh(true);
            timer.Tick += OnTimerTick;
            // 只有配置了隐藏规则时才进入开机快速扫描，没有规则时直接按正常频率，避免白耗 CPU。
            if (settings.Hidden.Count > 0)
            {
                timer.Interval = BoostScanInterval;
                boostUntil = DateTime.UtcNow.AddSeconds(BoostDurationSeconds);
            }
            timer.Start();
            ApplyHotkey();
            if (tray != null) tray.Visible = !settings.HideSelfIcon;

            // 窗口拦截引擎：独立线程运行，与托盘扫描互不影响。
            // 引擎在构造时已创建，这里只决定要不要跑起来。
            popupGuard.SetOwnWindow(this);
            // 上次若是被强杀/崩溃，hidden.json 里会留着没恢复的窗口，这里补显示
            int recovered = popupGuard.RecoverFromLastCrash();
            if (recovered > 0 && blockLog != null)
            {
                blockLog.Add(new BlockRecord
                {
                    Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Category = "窗口拦截",
                    Title = "异常退出善后",
                    ProcessName = "",
                    Action = "恢复",
                    Success = true,
                    Note = "上次未正常退出，已自动恢复 " + recovered + " 个被藏起的窗口",
                    Count = recovered,
                });
            }
            if (settings.PopupGuardEnabled && wasHiddenStart) popupGuard.Start();
            if (settings.HideSelfFromCapture) ApplyCaptureAffinity();

            // Win10 会把新程序的图标放进「显示隐藏的图标」溢出区，稍等片刻后自查并提示。
            if (!OSInfo.IsWindows11)
            {
                var selfCheck = new System.Windows.Forms.Timer { Interval = 3000 };
                selfCheck.Tick += delegate { selfCheck.Stop(); CheckSelfIcon(); };
                selfCheck.Start();
            }
        }

        /// <summary>
        /// 「截图/录屏时隐藏本工具窗口」：SetWindowDisplayAffinity 只能作用于本进程窗口，
        /// 所以只对自己的窗口设置；跨进程无效（实测 err=5）。
        /// </summary>
        private void ApplyCaptureAffinity()
        {
            try
            {
                if (!IsHandleCreated) return;
                Native.SetWindowDisplayAffinity(Handle,
                    settings.HideSelfFromCapture ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
            }
            catch { }
        }

        /// <summary>切到「窗口规则」页。</summary>
        private void SwitchToRulePage()
        {
            if (tabs != null && tabs.TabPages.Count > 1) tabs.SelectedIndex = 1;
        }

        private void ExportRules()
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "TrayHider 规则文件(*.json)|*.json|所有文件(*.*)|*.*";
                dialog.FileName = "TrayHider规则.json";
                dialog.DefaultExt = "json";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var exported = settings.WindowRules ?? new List<WindowRule>();
                    RuleIo.Export(exported, dialog.FileName);
                    MessageBox.Show(this, "已导出 " + exported.Count + " 条规则。", "TrayHider",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "导出失败：" + ex.Message, "TrayHider",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        /// <summary>把运行日志（含「判定」记录）导出为 JSON，便于排查时发出来。</summary>
        private void ExportLog()
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "JSON 文件(*.json)|*.json|所有文件(*.*)|*.*";
                dialog.FileName = "TrayHider运行日志.json";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var records = blockLog.Snapshot();
                    var ms = new System.IO.MemoryStream();
                    new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(List<BlockRecord>))
                        .WriteObject(ms, records);
                    System.IO.File.WriteAllText(dialog.FileName,
                        System.Text.Encoding.UTF8.GetString(ms.ToArray()), System.Text.Encoding.UTF8);
                    MessageBox.Show(this, "已导出 " + records.Count + " 条记录。", "TrayHider",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "导出失败：" + ex.Message, "TrayHider",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void ImportRules()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "TrayHider 规则文件(*.json)|*.json|所有文件(*.*)|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var rules = RuleIo.Import(dialog.FileName);
                    if (rules == null) { MessageBox.Show(this, "规则文件格式不正确。", "TrayHider", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    if (settings.WindowRules == null) settings.WindowRules = new List<WindowRule>();
                    int skipped = 0;
                    lock (SettingsStore.RulesGate)
                    {
                        // 同指纹视为同一条规则，默认跳过重复项
                        foreach (var r in rules)
                        {
                            string fp = RuleFingerprint.Of(r);
                            bool dup = false;
                            foreach (var e in settings.WindowRules)
                                if (RuleFingerprint.Of(e) == fp) { dup = true; break; }
                            if (dup) { skipped++; continue; }
                            settings.WindowRules.Add(r);
                        }
                    }
                    SettingsStore.Save(settings);
                    if (tabs != null && tabs.TabPages.Count > 1) tabs.SelectedIndex = 1;
                    MessageBox.Show(this, skipped > 0
                        ? ("已导入 " + (rules.Count - skipped) + " 条规则，跳过 " + skipped + " 条重复。")
                        : ("已导入 " + rules.Count + " 条规则。"), "TrayHider",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "导入失败：" + ex.Message, "TrayHider",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }


        /// <summary>外部（如命令行）请求切到某一页：0=托盘隐藏，1=窗口规则。</summary>
        public void SelectTab(int index)
        {
            if (tabs != null && index >= 0 && index < tabs.TabPages.Count) tabs.SelectedIndex = index;
        }

        /// <summary>守卫拦到窗口后：刷新日志页、规则页计数、状态栏。</summary>
        private void OnGuardActivity()
        {
            if (IsDisposed) return;
            if (tabs != null && tabs.SelectedIndex == 2) QueueLogRefresh();
            if (tabs != null && tabs.SelectedIndex == 1 && rulePanelRefresh != null) rulePanelRefresh();
            UpdateStatusForCurrentTab();
        }

        /// <summary>出现新的「判定」记录：刷新日志页与状态栏（规则页计数不受影响）。</summary>
        private void OnGuardDiagnosticsChanged()
        {
            if (IsDisposed) return;
            if (tabs != null && tabs.SelectedIndex == 2) QueueLogRefresh();
            if (tabs != null && tabs.SelectedIndex == 1) UpdateStatusForCurrentTab();
        }

        /// <summary>规则面板注册的刷新回调（拦截后更新「拦截次数」列）。</summary>
        public Action rulePanelRefresh;

        private void OnTimerTick(object sender, EventArgs e)
        {
            ScanAndRefresh(false);
            // 停留在规则页/日志页时，主状态栏显示的是对应统计，需要跟着刷新
            if (tabs != null && tabs.SelectedIndex == 1) UpdateStatusForCurrentTab();
            else if (tabs != null && tabs.SelectedIndex == 2) UpdateStatusForCurrentTab();
            // 开机快速阶段结束，回到正常频率，减少常驻时的 CPU 占用。
            if (DateTime.UtcNow >= boostUntil && timer.Interval != NormalScanInterval)
                timer.Interval = NormalScanInterval;
        }

        protected override void SetVisibleCore(bool value)
        {
            // 开机自启时默认不显示主窗口（缩在托盘里），只生效一次。
            if (startHidden)
            {
                startHidden = false;
                wasHiddenStart = true;
                // 窗口虽然不可见，句柄仍要创建：否则 Load / OnHandleCreated 不触发，
                // 全局快捷键注册不上，用户就没有把窗口呼出来的逃生通道了。
                if (!IsHandleCreated) _ = Handle;
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Initialize();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 窗口真正显示后再应用一次防截图状态（句柄刚创建时设可能不生效）。
            // 注意：SetWindowDisplayAffinity 在个别环境下会导致窗口内容不渲染，
            // 只在用户真正勾选「截图/录屏时隐藏本工具窗口」时才设置。
            if (settings.HideSelfFromCapture) ApplyCaptureAffinity();
            // 窗口拦截引擎在显示时启动，避开 OnHandleCreated 的句柄创建期（实测那时启动会漏拦截）。
            if (settings.PopupGuardEnabled && popupGuard != null && !popupGuard.IsRunning) popupGuard.Start();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (hotkey != null) hotkey.Dispose();
            base.OnHandleDestroyed(e);
        }

        private void ApplyHotkey()
        {
            if (IsDisposed) return;

            if (hotkey == null)
            {
                hotkey = new HotkeyEngine();
                hotkey.Triggered += ToggleWindow;
            }
            hotkey.Enabled = settings.HotkeyEnabled;
            hotkey.Mode = settings.HotkeyMode;
            hotkey.Modifiers = settings.HotkeyModifiers;
            hotkey.Key = settings.HotkeyKey;
            hotkey.Sequence = settings.Sequence ?? new List<int>();
            hotkey.WindowMs = settings.SequenceWindowMs;

            if (settings.HotkeyEnabled)
            {
                if (!hotkeyHookReady) { hotkey.Start(); hotkeyHookReady = true; }
            }
            else hotkey.Stop();
        }

        /// <summary>The hotkey toggles: show the window when it is hidden, hide it when visible.</summary>
        private void ToggleWindow()
        {
            if (IsDisposed) return;
            if (Visible) Hide();
            else ShowWindow();
        }

        /// <summary>
        /// Windows 10 默认把新图标放进「显示隐藏的图标」溢出区，任务栏上看不到，
        /// 容易被误认为“图标丢了”。这里自查一次并提示用户。
        /// </summary>
        private void CheckSelfIcon()
        {
            if (IsDisposed || OSInfo.IsWindows11) return;
            try
            {
                var all = TrayCatalog.Scan(0, false);
                var self = all.FirstOrDefault(x => TrayCatalog.SamePath(x.Path, Application.ExecutablePath));
                if (self == null)
                {
                    // 图标可能还在没打开过的溢出区里，点开一次再找。
                    Win10Tray.ForceOverflowRefresh();
                    all = TrayCatalog.Scan(0, false);
                    self = all.FirstOrDefault(x => TrayCatalog.SamePath(x.Path, Application.ExecutablePath));
                }
                if (self == null)
                {
                    statusLabel.Text = "未在托盘找到本工具图标：可能被其他软件隐藏，或资源管理器尚未刷新";
                    return;
                }
                if (self.WindowClass != "NotifyIconOverflowWindow") return;
                if (tray != null)
                    tray.ShowBalloonTip(6000, "TrayHider",
                        "Windows 10 把新图标放在「显示隐藏的图标」溢出区里。\n" +
                        "点任务栏右下角的 ^ 就能看到本工具图标，也可以把它拖到任务栏上常驻显示。",
                        ToolTipIcon.Info);
                statusLabel.Text = "提示：本工具图标当前在「显示隐藏的图标」溢出区（点任务栏的 ^ 可见）";
            }
            catch { }
        }

        private bool SetHideSelfIcon(bool hide)
        {
            // 隐藏自身托盘图标必须以快捷键为逃生通道，否则窗口一旦隐藏就再也找不回来。
            if (hide && !settings.HotkeyEnabled)
            {
                MessageBox.Show(this,
                    "需要先启用快捷键才能隐藏本工具托盘。\n\n" +
                    "否则窗口一旦隐藏（点 × 或开机隐藏启动），将没有任何方式找回工具。\n" +
                    "请先到「选项 → 快捷键设置」启用快捷键，再勾选本项。",
                    "TrayHider", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            settings.HideSelfIcon = hide;
            SettingsStore.Save(settings);
            if (tray != null) tray.Visible = !hide;
            if (hide)
                MessageBox.Show(this,
                    "已隐藏本工具托盘。\n\n" +
                    "按你设置的快捷键可随时呼出主窗口；\n" +
                    "想恢复托盘图标，请取消“选项 → 隐藏本工具托盘”。",
                    "TrayHider", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        private void ScanAndRefresh(bool force)
        {
            List<TrayItem> items;
            try { items = TrayCatalog.Scan(SelfPid, settings.ScanOverflow); }
            catch (Exception ex) { statusLabel.Text = "扫描失败: " + ex.Message; return; }

            int changed = RuleEngine.Apply(settings, items, blockLog);
            lastItems = items;

            // 只在“有哪些图标、是否隐藏”变化时才重建列表。
            // tooltip 在 Win10 上很多软件每秒都在改，若计入签名会导致列表反复重建、
            // 表现为明显闪烁、滚动位置被重置。
            string signature = string.Join("|", items.Select(x => x.Key + ":" + x.State));
            if (force || signature != lastSignature)
            {
                lastSignature = signature;
                RefreshList(items);
            }
            else
            {
                UpdateRowsInPlace(items);
                UpdateStatus(items, changed);
            }
        }

        private void RefreshList(List<TrayItem> items)
        {
            if (items == null) return;
            var selected = new HashSet<string>(
                list.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag).OfType<TrayItem>().Select(x => x.Key));
            // 记住当前滚动位置，重建列表后还原，避免“刷一下就被弹回顶部”。
            string topKey = list.TopItem != null && list.TopItem.Tag is TrayItem top ? top.Key : null;
            string filterText = filter.Text == "按名称 / 提示 / 路径筛选" ? "" : filter.Text.Trim();

            // Fill the image list first and attach it before creating rows: the ListView
            // resolves ImageKey against whichever list is assigned at row-creation time.
            var newImages = new ImageList() { ImageSize = new Size(24, 24), ColorDepth = ColorDepth.Depth32Bit };
            foreach (var item in items)
            {
                if (filterText.Length > 0 && !Matches(item, filterText)) continue;
                Image image = item.Image;
                if (image != null) newImages.Images.Add(item.Key, image);
            }

            ImageList old = list.SmallImageList;
            list.SmallImageList = newImages;
            images = newImages;

            list.BeginUpdate();
            list.Items.Clear();
            foreach (var item in items)
            {
                if (filterText.Length > 0 && !Matches(item, filterText)) continue;

                var row = new ListViewItem(item.Name);
                row.Tag = item;
                row.SubItems.Add(item.Tooltip);
                row.SubItems.Add(item.Path);
                row.SubItems.Add(item.State == 1 ? "已隐藏" : "显示中");
                if (item.Image != null) row.ImageKey = item.Key;
                if (selected.Contains(item.Key)) row.Selected = true;
                list.Items.Add(row);
            }
            list.EndUpdate();

            if (topKey != null)
            {
                for (int i = 0; i < list.Items.Count; i++)
                {
                    if (list.Items[i].Tag is TrayItem item && item.Key == topKey)
                    {
                        list.Items[i].EnsureVisible();
                        break;
                    }
                }
            }

            if (old != null) old.Dispose();

            ListViewUtil.AutoFit(list, traySized, new[] { 160, 180, 260, 70 });
            UpdateStatus(items, 0);
        }

        /// <summary>列表结构没变时，只更新变化的文本，不重建整张表。</summary>
        private void UpdateRowsInPlace(List<TrayItem> items)
        {
            if (list.Items.Count == 0) return;
            var byKey = new Dictionary<string, TrayItem>();
            foreach (var item in items) byKey[item.Key] = item;
            foreach (ListViewItem row in list.Items)
            {
                if (!(row.Tag is TrayItem old)) continue;
                if (!byKey.TryGetValue(old.Key, out var fresh)) continue;
                if (row.SubItems[1].Text != fresh.Tooltip) row.SubItems[1].Text = fresh.Tooltip;
                string state = fresh.State == 1 ? "已隐藏" : "显示中";
                if (row.SubItems[3].Text != state) row.SubItems[3].Text = state;
            }
        }

        private static bool Matches(TrayItem item, string text)
        {
            return TrayCatalog.ContainsIgnoreCase(item.Name, text) ||
                   TrayCatalog.ContainsIgnoreCase(item.Tooltip, text) ||
                   TrayCatalog.ContainsIgnoreCase(item.Path, text);
        }

        private void UpdateStatus(List<TrayItem> items, int changed)
        {
            int hidden = items.Count(x => x.State == 1);
            statusLabel.Text = "共 " + items.Count + " 个图标 · 显示中 " + (items.Count - hidden) +
                               " · 已隐藏 " + hidden + (changed > 0 ? " · 本次隐藏 " + changed : "");
        }

        private void List_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var selected = list.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag).OfType<TrayItem>().ToList();
            if (selected.Count == 0) return;
            if (selected.Any(x => x.State == 0)) HideSelected();
            else { RuleEngine.Restore(settings, lastItems, selected, blockLog); ScanAndRefresh(true); }
        }

        private void HideSelected()
        {
            var selected = list.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag).OfType<TrayItem>().ToList();
            int changed = 0;
            foreach (var item in selected)
            {
                if (!settings.Hidden.Exists(delegate(IconRule r) { return r.Matches(item); }))
                    settings.Hidden.Add(IconRule.From(item));
                if (item.State == 0 && TrayApi.SetHidden(item, true))
                {
                    item.State = 1;
                    changed++;
                    if (!settings.Recovery.Exists(delegate(IconRule r) { return r.Matches(item); }))
                        settings.Recovery.Add(IconRule.From(item));
                    if (blockLog != null)
                        blockLog.Add(new BlockRecord
                        {
                            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            Category = "托盘隐藏",
                            Title = item.Name,
                            ProcessName = item.Path,
                            Action = "隐藏",
                            Success = true,
                        });
                }
            }
            SettingsStore.Save(settings);
            ScanAndRefresh(true);
        }

        private void RestoreSelected()
        {
            var selected = list.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag).OfType<TrayItem>().ToList();
            RuleEngine.Restore(settings, lastItems, selected, blockLog);
            ScanAndRefresh(true);
        }

        private void RestoreAll()
        {
            // Win10 的溢出窗口只在被点开后才会创建；恢复全部时先把它打开，
            // 否则溢出区里的图标（很可能就是旧版本隐藏的那些）枚举不到。
            if (!OSInfo.IsWindows11) Win10Tray.ForceOverflowRefresh();
            var items = TrayCatalog.Scan(SelfPid, !OSInfo.IsWindows11);
            int changed = RuleEngine.RestoreAll(settings, items, blockLog);
            lastItems = items;
            ScanAndRefresh(true);
            MessageBox.Show(this, "已恢复 " + changed + " 个图标。", "TrayHider",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ForceExit()
        {
            forceClose = true;
            Close();
            Application.Exit();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // 点窗口右上角 ×（或 Alt+F4）只缩到托盘、不退出；
            // 真正退出走托盘右键「退出」或菜单「操作 → 退出」。
            if (!forceClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            timer.Stop();
            try { logRefreshTimer.Stop(); logRefreshTimer.Dispose(); } catch { }
            // 真退出（不是缩到托盘）时，把藏起来的窗口都放回来
            try { if (popupGuard != null) popupGuard.RestoreAllHidden(); } catch { }
            if (popupGuard != null) { popupGuard.Dispose(); popupGuard = null; }
            if (hotkey != null) hotkey.Dispose();
            if (tray != null) tray.Dispose();
            if (trayIcon != null) trayIcon.Dispose();
        }

        private static bool IsAutoStart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return key != null && key.GetValue("TrayHider") != null;
                }
            }
            catch { return false; }
        }

        private void SetAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (enable) key.SetValue("TrayHider", "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                    else key.DeleteValue("TrayHider", false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(null, "写入开机启动项失败: " + ex.Message, "TrayHider",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
