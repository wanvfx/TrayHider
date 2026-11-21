using System;
using System.Drawing;
using System.Windows.Forms;

namespace TrayHider
{
    internal sealed class AboutForm : Form
    {
        private const string Description =
            "TrayHider v1.0.2\r\n" +
            "作者：wanvfx\r\n" +
            "\r\n" +
            "支持平台\r\n" +
            "Windows 10 / Windows 11。不支持 Windows 7 及更早版本。\r\n" +
            "\r\n" +
            "功能\r\n" +
            "隐藏通知区域（托盘）里指定的图标，被隐藏的程序继续在后台正常运行。\r\n" +
            "\r\n" +
            "随时可恢复\r\n" +
            "隐藏只是改变显示状态：在主窗口点「恢复选中 / 恢复全部」，或命令行\r\n" +
            "TrayHider.exe --show <关键字>，图标就会回到托盘。\r\n" +
            "\r\n" +
            "工作原理（Windows 11）\r\n" +
            "Windows 11 把每个托盘图标的记录放在注册表\r\n" +
            "HKCU\\Control Panel\\NotifyIconSettings\r\n" +
            "其中包含程序路径（ExecutablePath）、UID / IconGuid、提示文字和图标快照。\r\n" +
            "\r\n" +
            "本工具据此枚举出正在运行的图标（用进程路径匹配活动窗口，并用\r\n" +
            "Shell_NotifyIconGetRect 过滤掉已退出程序的缓存记录），然后调用\r\n" +
            "Shell_NotifyIcon(NIM_MODIFY)，把图标状态设为 NIS_HIDDEN（隐藏）。\r\n" +
            "\r\n" +
            "NIS_HIDDEN 是 Windows 11 官方识别的状态，资源管理器会让图标同时从任务栏和\r\n" +
            "「显示隐藏的图标」溢出窗口中消失，而图标所属的程序本身完全不受影响\r\n" +
            "（不结束进程、不修改其他软件、不需要管理员权限）。\r\n" +
            "\r\n" +
            "工作原理（Windows 10）\r\n" +
            "Windows 10 的通知区域是传统的 Explorer 工具栏（ToolbarWindow32），\r\n" +
            "每个图标是工具栏上的一个按钮。本工具读取这些按钮及其提示文字，\r\n" +
            "用 TB_HIDEBUTTON 把指定按钮移入「显示隐藏的图标」溢出区，\r\n" +
            "这是 Windows 10 的原生隐藏方式，同样不结束进程、不注入 Explorer。\r\n" +
            "\r\n" +
            "两台系统的隐藏规则都保存在设置文件并在后台定时重新应用，因此程序重启、\r\n" +
            "或资源管理器重建托盘之后，隐藏状态依然保持。\r\n" +
            "\r\n" +
            "快捷键\r\n" +
            "在“选项 → 快捷键设置”里自定义。按一次呼出主窗口，再按一次隐藏。\r\n" +
            "两种触发方式，都是被动监听、不占用按键，其他软件的快捷键照常生效：\r\n" +
            "· 组合键：Ctrl / Alt / Shift / Win 任意组合加任意按键（默认 Ctrl + Alt + T）；\r\n" +
            "· 按键序列：点“录制按键”后按下一串键，自动识别出按了哪些键、各按几次；\r\n" +
            "  也可以用下拉框手动逐个添加。支持不同键、不同次数，例如 T→T→NumLock、\r\n" +
            "  A→B→B→C，并可设置整段序列的时间窗口。NumLock、F10 等任意键都可以用。\r\n" +
            "\r\n" +
            "“选项 → 隐藏本工具自己的托盘图标”可以把自己的托盘图标也隐藏掉，让托盘更干净。\r\n" +
            "被隐藏的图标不会出现在「显示隐藏的图标」溢出窗口里，所以隐藏自身图标后\r\n" +
            "请用快捷键呼出主窗口，或重新运行本程序。\r\n" +
            "\r\n" +
            "设置文件：%LOCALAPPDATA%\\TrayHider\\settings.json";

        public AboutForm()
        {
            Text = "关于 TrayHider";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(600, 460);

            var body = new TextBox();
            body.Multiline = true;
            body.ReadOnly = true;
            body.ScrollBars = ScrollBars.Vertical;
            body.Dock = DockStyle.Fill;
            body.BackColor = SystemColors.Window;
            body.Font = new Font("Segoe UI", 9f);
            body.Text = Description;

            var close = new Button();
            close.Text = "关闭";
            close.Dock = DockStyle.Bottom;
            close.Height = 34;
            close.Click += delegate { Close(); };

            Controls.Add(body);
            Controls.Add(close);

            Shown += delegate { close.Focus(); };
        }
    }
}
