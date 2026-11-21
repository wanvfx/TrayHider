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

        private HotkeyEngine hotkey;
        private bool hotkeyHookReady;
        private bool startHidden;

        private readonly ListView list = new ListView();
        private readonly TextBox filter = new TextBox();
        private readonly StatusStrip status = new StatusStrip();
        private readonly ToolStripStatusLabel statusLabel = new ToolStripStatusLabel() { Spring = true };
        private NotifyIcon tray = null;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly Settings settings;
        private ImageList images = null;
        private Icon trayIcon = null;
        private string lastSignature = "";
        private bool forceClose;
        private List<TrayItem> lastItems = new List<TrayItem>();

        public MainForm(Settings settings)
        {
            this.settings = settings;
            startHidden = settings.AutoStart && settings.MinimizeToTrayOnStart;
            timer.Interval = 2500;
            Text = "TrayHider-托盘图标隐藏工具";
            Icon = CreateAppIcon();
            Size = new Size(1000, 620);
            MinimumSize = new Size(720, 420);
            StartPosition = FormStartPosition.CenterScreen;

            MenuStrip menu = BuildMenu();
            Panel toolbar = BuildToolbar();
            ListView view = BuildList();
            BuildTray();

            status.Items.Add(statusLabel);

            // Dock layout follows the reverse z-order: the last added control is laid out first,
            // so the menu ends up above the toolbar and the list fills the remaining area.
            Controls.Add(view);
            Controls.Add(status);
            Controls.Add(toolbar);
            Controls.Add(menu);

            Load += MainForm_Load;
            FormClosing += MainForm_FormClosing;
            list.MouseDoubleClick += List_MouseDoubleClick;
            filter.TextChanged += delegate { RefreshList(lastItems); };
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
            var autoStart = new ToolStripMenuItem("登录 Windows 时自动启动(&L)") { CheckOnClick = true };
            autoStart.Checked = IsAutoStart();
            autoStart.CheckedChanged += delegate { SetAutoStart(autoStart.Checked); };
            var closeToTray = new ToolStripMenuItem("关闭窗口时缩到托盘(&C)") { CheckOnClick = true };
            closeToTray.Checked = settings.CloseToTray;
            closeToTray.CheckedChanged += delegate { settings.CloseToTray = closeToTray.Checked; };
            options.DropDownItems.Add(autoStart);
            var minimizeOnStart = new ToolStripMenuItem("开机启动时最小化到托盘(&M)") { CheckOnClick = true };
            minimizeOnStart.Checked = settings.MinimizeToTrayOnStart;
            minimizeOnStart.CheckedChanged += delegate
            {
                settings.MinimizeToTrayOnStart = minimizeOnStart.Checked;
                SettingsStore.Save(settings);
            };
            options.DropDownItems.Add(closeToTray);
            var hotkey = new ToolStripMenuItem("快捷键设置(&K)...");
            hotkey.Click += delegate
            {
                using (var form = new HotkeyForm(settings))
                {
                    if (form.ShowDialog(this) == DialogResult.OK) ApplyHotkey();
                }
            };
            var hideSelf = new ToolStripMenuItem("隐藏本工具自己的托盘图标(&S)") { CheckOnClick = true };
            hideSelf.Checked = settings.HideSelfIcon;
            hideSelf.CheckedChanged += delegate { SetHideSelfIcon(hideSelf.Checked); };
            options.DropDownItems.Add(hotkey);
            options.DropDownItems.Add(hideSelf);
            var scanOverflow = new ToolStripMenuItem("扫描时包含溢出区图标（会弹出溢出窗口）(&O)") { CheckOnClick = true };
            scanOverflow.Checked = settings.ScanOverflow;
            scanOverflow.CheckedChanged += delegate
            {
                settings.ScanOverflow = scanOverflow.Checked;
                SettingsStore.Save(settings);
            };
            options.DropDownItems.Add(scanOverflow);

            var help = new ToolStripMenuItem("帮助(&H)");
            help.DropDownItems.Add("关于 TrayHider(&A)", null, delegate { ShowAbout(); });

            menu.Items.Add(file);
            menu.Items.Add(options);
            menu.Items.Add(help);
            MainMenuStrip = menu;
            return menu;
        }

        private Panel BuildToolbar()
        {
            var panel = new Panel() { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8) };
            var refresh = new Button() { Text = "刷新", Width = 84, Height = 28, Left = 8, Top = 7 };
            var hide = new Button() { Text = "隐藏选中", Width = 96, Height = 28, Left = 100, Top = 7 };
            var restore = new Button() { Text = "恢复选中", Width = 96, Height = 28, Left = 204, Top = 7 };
            var restoreAll = new Button() { Text = "恢复全部", Width = 96, Height = 28, Left = 308, Top = 7 };
            const string hint = "按名称 / 提示 / 路径筛选";
            filter.Width = 300;
            filter.Height = 24;
            filter.Left = 424;
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

            refresh.Click += delegate { ScanAndRefresh(true); };
            hide.Click += delegate { HideSelected(); };
            restore.Click += delegate { RestoreSelected(); };
            restoreAll.Click += delegate { RestoreAll(); };

            panel.Controls.AddRange(new Control[] { refresh, hide, restore, restoreAll, filter });
            return panel;
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
            images = new ImageList() { ImageSize = new Size(24, 24), ColorDepth = ColorDepth.Depth32Bit };
            list.SmallImageList = images;
            return list;
        }

        private void BuildTray()
        {
            trayIcon = CreateAppIcon();
            tray = new NotifyIcon();
            tray.Icon = trayIcon;
            tray.Text = "TrayHider · 托盘图标隐藏工具";
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
            ScanAndRefresh(true);
            timer.Tick += delegate { ScanAndRefresh(false); };
            timer.Start();
            ApplyHotkey();
            if (tray != null) tray.Visible = !settings.HideSelfIcon;
            // Win10 会把新程序的图标放进「显示隐藏的图标」溢出区，稍等片刻后自查并提示。
            if (!OSInfo.IsWindows11)
            {
                var selfCheck = new System.Windows.Forms.Timer { Interval = 3000 };
                selfCheck.Tick += delegate { selfCheck.Stop(); CheckSelfIcon(); };
                selfCheck.Start();
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            // 开机自启时默认不显示主窗口（缩在托盘里），只生效一次。
            if (startHidden)
            {
                startHidden = false;
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyHotkey();
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

        private void SetHideSelfIcon(bool hide)
        {
            settings.HideSelfIcon = hide;
            SettingsStore.Save(settings);
            if (tray != null) tray.Visible = !hide;
            if (!hide) return;
            if (!settings.HotkeyEnabled)
                MessageBox.Show(this,
                    "已隐藏本工具自己的托盘图标，且未启用快捷键。\n" +
                    "此时托盘里将找不到本工具，只能重新运行程序才能打开主窗口。",
                    "TrayHider", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                MessageBox.Show(this,
                    "已隐藏本工具自己的托盘图标。\n\n" +
                    "按你设置的快捷键可随时呼出主窗口；\n" +
                    "想恢复托盘图标，请取消“选项 → 隐藏本工具自己的托盘图标”。",
                    "TrayHider", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ScanAndRefresh(bool force)
        {
            List<TrayItem> items;
            try { items = TrayCatalog.Scan(SelfPid, settings.ScanOverflow); }
            catch (Exception ex) { statusLabel.Text = "扫描失败: " + ex.Message; return; }

            int changed = RuleEngine.Apply(settings, items);
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
            else { RuleEngine.Restore(settings, lastItems, selected); ScanAndRefresh(true); }
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
                }
            }
            SettingsStore.Save(settings);
            ScanAndRefresh(true);
        }

        private void RestoreSelected()
        {
            var selected = list.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag).OfType<TrayItem>().ToList();
            RuleEngine.Restore(settings, lastItems, selected);
            ScanAndRefresh(true);
        }

        private void RestoreAll()
        {
            // Win10 的溢出窗口只在被点开后才会创建；恢复全部时先把它打开，
            // 否则溢出区里的图标（很可能就是旧版本隐藏的那些）枚举不到。
            if (!OSInfo.IsWindows11) Win10Tray.ForceOverflowRefresh();
            var items = TrayCatalog.Scan(SelfPid, !OSInfo.IsWindows11);
            int changed = RuleEngine.RestoreAll(settings, items);
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
            if (!forceClose && settings.CloseToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            timer.Stop();
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

        private static void SetAutoStart(bool enable)
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
