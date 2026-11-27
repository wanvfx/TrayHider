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

            const int pad = 18;          // 四周留白
            const int contentWidth = 524; // 正文可用宽度

            var titleFont = new Font("Segoe UI", 11f, FontStyle.Bold);
            var textFont = new Font("Segoe UI", 9f);

            var title = new Label();
            title.Text = "TrayHider";
            title.Font = titleFont;
            title.AutoSize = true;
            title.Location = new Point(pad, pad);

            // 版本号：外观与普通文字一致（不下划线、不改颜色），双击打开项目主页。
            var version = new Label();
            version.Text = "v" + BuildInfo.Version;
            version.Font = titleFont;
            version.AutoSize = true;
            version.Cursor = Cursors.Hand;
            version.Location = new Point(
                pad + TextRenderer.MeasureText(title.Text, titleFont).Width + 8, pad);
            version.DoubleClick += delegate { OpenProjectUrl(); };

            var tagline = new Label();
            tagline.Text = "藏起一切打扰 · 作者 wanvfx · 开源（MIT）";
            tagline.Font = textFont;
            tagline.AutoSize = true;
            tagline.Location = new Point(pad, pad + 30);

            var body = new Label();
            body.Text = Summary;
            body.Font = textFont;
            body.Location = new Point(pad, pad + 58);
            body.Size = new Size(contentWidth,
                TextRenderer.MeasureText(Summary, textFont, new Size(contentWidth, 0),
                    TextFormatFlags.WordBreak).Height + 2);

            var close = new Button();
            close.Text = "关闭";
            close.Size = new Size(120, 32);
            close.Location = new Point(pad + (contentWidth - close.Width) / 2, body.Bottom + 16);
            close.Click += delegate { Close(); };

            ClientSize = new Size(contentWidth + pad * 2, close.Bottom + pad);

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
