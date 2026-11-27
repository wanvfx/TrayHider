using System;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Forms;

namespace TrayHider
{
    internal sealed class AboutForm : Form
    {
        private const string ProjectUrl = "https://github.com/wanvfx/TrayHider";

        // 一段话说清这个工具是做什么的：托盘图标 + 窗口/弹窗，两者都只是"藏起来"。
        private const string Summary =
            "TrayHider 帮你藏起两样东西：通知区域（托盘）里指定的图标，以及指定程序的窗口和弹窗。" +
            "两者都只是改变显示状态——被隐藏的程序都在后台正常运行，随时可以恢复，" +
            "不结束进程、不注入其他软件、不需要安装 .NET 运行时，打开即用。";

        private const string Details =
@"支持平台
Windows 10 / Windows 11。不支持 Windows 7 及更早版本。

随时可恢复
隐藏只是改变显示状态。托盘图标：在主窗口点「恢复选中 / 恢复全部」，或命令行
TrayHider.exe --show <关键字>，图标就会回到托盘。被窗口规则藏起的窗口：在规则页点
「恢复被隐藏窗口」，或运行 TrayHider.exe --restore-hidden-windows。

功能一 · 托盘图标隐藏
隐藏通知区域（托盘）里指定的图标，被隐藏的程序继续在后台正常运行。

Windows 11：本工具读取注册表 HKCU\Control Panel\NotifyIconSettings 里的图标记录
（程序路径、UID / IconGuid、提示文字、图标快照），枚举出正在运行的图标（用进程路径
匹配活动窗口，并用 Shell_NotifyIconGetRect 过滤掉已退出程序的缓存记录），然后调用
Shell_NotifyIcon(NIM_MODIFY) 把图标状态设为 NIS_HIDDEN。NIS_HIDDEN 是 Windows 11
官方识别的状态，资源管理器会让图标同时从任务栏和「显示隐藏的图标」溢出窗口中消失，
图标所属的程序本身完全不受影响。

Windows 10：通知区域是传统的 Explorer 工具栏（ToolbarWindow32），每个图标是工具栏上的
一个按钮。本工具跨进程读取这些按钮及其提示文字，再用 TB_HIDEBUTTON 把指定按钮移入
「显示隐藏的图标」溢出区——这是 Windows 10 原生的隐藏方式，同样不结束进程。

两台系统的隐藏规则都保存在设置文件并在后台定时重新应用，因此程序重启、或资源管理器
重建托盘之后，隐藏状态依然保持。

功能二 · 窗口规则（弹窗拦截）
把指定程序的窗口或弹窗藏起来，程序在后台继续运行（火绒式窗口规则）。

加规则：勾「当前可见窗口」后点「把勾选窗口加入规则」，或点「● 点选窗口」直接指一个
窗口，生成的都是属性规则（按标题 / 进程名 / 窗口类名匹配，可选「或」或「且」）。
截图拦截：点「截图拦截…」，工具只把鼠标指向的那一个窗口压暗，单击锁定后点「拦截」，
即可按画面采集采样点（连续 5 次、自动定阈值、保存多套模板，同一个窗口换内容状态也能
命中）；规则还可限定窗口尺寸范围。
后台每 400ms 检查一次：分数达到阈值就直接隐藏；落在临界区（低于阈值不到 5 分）会隔
150ms 复检一次，仍达标就拦；藏不住的按 1s→2s→…→60s 退避，不会刷个不停。本工具自身、
资源管理器和系统窗口永远不会被拦。阈值会按「真的拦住时的分数」自动放松（只放松、不
收紧），拦截结果和「为什么没拦」都写在「运行日志」页。

崩溃恢复
被规则藏起的窗口会记在 hidden.json，工具退出时自动恢复；即使工具被强行结束，
下次启动也会自动恢复并记一条「异常退出善后」。

快捷键
在「选项 → 快捷键设置」里自定义，按一次呼出主窗口，再按一次隐藏。支持组合键
（默认 Ctrl + Alt + T）和按键序列（可录制）两种方式，都是被动监听、不占用按键，
其他软件的快捷键照常生效。「选项 → 隐藏本工具托盘」可以把自己的托盘图标也隐藏掉；
被隐藏的图标不会出现在溢出窗口里，此时请用快捷键呼出主窗口，或重新运行本程序。

命令行
TrayHider.exe                  启动图形界面（常驻托盘）
TrayHider.exe --list           列出所有活动托盘图标
TrayHider.exe --hide <关键字>  隐藏图标（进程名 / 提示文字 / 路径关键字）
TrayHider.exe --show <关键字>  恢复已隐藏的图标
TrayHider.exe --restore-all    恢复当前所有处于隐藏状态的图标
TrayHider.exe --winrules       直接打开「窗口规则」页
TrayHider.exe --restore-hidden-windows   恢复被窗口规则藏起的窗口
TrayHider.exe --export-diagnostics       导出运行日志（含判定记录）
TrayHider.exe --about          打开本对话框

设置文件：%LOCALAPPDATA%\TrayHider\settings.json";

        public AboutForm()
        {
            Text = "关于 TrayHider";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(620, 520);

            var titleFont = new Font("Segoe UI", 16f, FontStyle.Bold);
            var versionFont = new Font("Segoe UI", 16f);

            var titleRow = new FlowLayoutPanel();
            titleRow.FlowDirection = FlowDirection.LeftToRight;
            titleRow.WrapContents = false;
            titleRow.Dock = DockStyle.Fill;

            var title = new Label();
            title.Text = "TrayHider";
            title.Font = titleFont;
            title.AutoSize = true;

            // 版本号：外观与普通文字一致（不下划线、不改颜色），双击打开项目主页。
            var version = new Label();
            version.Text = "v" + BuildInfo.Version;
            version.Font = versionFont;
            version.AutoSize = true;
            version.Cursor = Cursors.Hand;
            version.Margin = new Padding(8, 5, 0, 0);
            version.DoubleClick += delegate { OpenProjectUrl(); };

            titleRow.Controls.Add(title);
            titleRow.Controls.Add(version);

            var tagline = new Label();
            tagline.Text = "藏起一切打扰 · 作者 wanvfx · 开源（MIT）";
            tagline.Font = new Font("Segoe UI", 9f);
            tagline.Dock = DockStyle.Fill;

            var body = new TextBox();
            body.Multiline = true;
            body.ReadOnly = true;
            body.ScrollBars = ScrollBars.Vertical;
            body.WordWrap = true;
            body.BackColor = SystemColors.Window;
            body.Font = new Font("Segoe UI", 9f);
            body.Text = Summary + "\r\n\r\n" + Details;
            body.Dock = DockStyle.Fill;

            var close = new Button();
            close.Text = "关闭";
            close.Height = 32;
            close.Dock = DockStyle.Fill;
            close.Click += delegate { Close(); };

            var table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.ColumnCount = 1;
            table.RowCount = 4;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Padding = new Padding(10);
            table.Controls.Add(titleRow, 0, 0);
            table.Controls.Add(tagline, 0, 1);
            table.Controls.Add(body, 0, 2);
            table.Controls.Add(close, 0, 3);

            Controls.Add(table);

            Shown += delegate { close.Focus(); };
        }

        private void OpenProjectUrl()
        {
            try
            {
                Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开浏览器：" + ex.Message, "TrayHider",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
