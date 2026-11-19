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
            timer.Interval = 2500;
            Text = "TrayHider · Windows 11 托盘图标隐藏工具";
            Icon = LoadAppIcon();
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
            trayIcon = LoadAppIcon();
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

        private static Icon LoadAppIcon()
        {
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null) return icon;
            }
            catch { }
            return SystemIcons.Application;
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
            if (settings.MinimizeToTrayOnStart) Hide();
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
            try { items = TrayCatalog.Scan(SelfPid); }
            catch (Exception ex) { statusLabel.Text = "扫描失败: " + ex.Message; return; }

            int changed = RuleEngine.Apply(settings, items);
            lastItems = items;

            string signature = string.Join("|", items.Select(x => x.Key + ":" + x.State + ":" + x.Tooltip + ":" + x.Name));
            if (force || signature != lastSignature)
            {
                lastSignature = signature;
                RefreshList(items);
            }
            else
            {
                UpdateStatus(items, changed);
            }
        }

        private void RefreshList(List<TrayItem> items)
        {
            if (items == null) return;
            var selected = new HashSet<string>(
                list.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag).OfType<TrayItem>().Select(x => x.Key));
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

            if (old != null) old.Dispose();

            UpdateStatus(items, 0);
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
            int changed = RuleEngine.RestoreAll(settings, lastItems);
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
