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
            "被隐藏的程序都在后台正常运行，随时可以恢复，不结束进程、不注入其他软件。";

        public AboutForm()
        {
            Text = "关于 TrayHider";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            // 与程序其他界面保持一致：用系统默认字体（本机的「宋体」9pt），
            // 只有标题加粗，其余全部继承同一个字体，不再单独指定 Segoe UI。
            Font = SystemFonts.DefaultFont;

            const int pad = 12;
            const int contentWidth = 400;
            var titleFont = new Font(Font.FontFamily, Font.Size, FontStyle.Bold);
            int y = pad;

            var title = new Label();
            title.Text = "TrayHider";
            title.Font = titleFont;
            title.AutoSize = true;
            title.Location = new Point(pad, y);

            // 版本号：外观与普通文字一致（同字体同字号、不下划线、不改颜色），双击打开项目主页。
            var version = new Label();
            version.Text = "v" + BuildInfo.Version;
            version.AutoSize = true;
            version.Cursor = Cursors.Hand;
            version.Location = new Point(
                pad + TextRenderer.MeasureText(title.Text, titleFont).Width + 6, y);
            version.DoubleClick += delegate { OpenProjectUrl(); };

            y += title.GetPreferredSize(Size.Empty).Height + 2;

            var tagline = new Label();
            tagline.Text = "藏起一切打扰 · 作者 wanvfx · 开源（MIT）";
            tagline.AutoSize = true;
            tagline.Location = new Point(pad, y);
            y += tagline.GetPreferredSize(Size.Empty).Height + 10;

            var body = new Label();
            body.Text = Summary;
            body.Location = new Point(pad, y);
            body.Size = new Size(contentWidth,
                TextRenderer.MeasureText(Summary, Font, new Size(contentWidth, 0),
                    TextFormatFlags.WordBreak).Height + 2);
            y += body.Height + 10;

            var close = new Button();
            close.Text = "关闭";
            close.Size = new Size(96, 26);
            close.Location = new Point(pad + (contentWidth - close.Width) / 2, y);
            y += close.Height;

            ClientSize = new Size(contentWidth + pad * 2, y + pad);

            Controls.Add(title);
            Controls.Add(version);
            Controls.Add(tagline);
            Controls.Add(body);
            Controls.Add(close);

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
