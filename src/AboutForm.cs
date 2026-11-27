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
            KeyPreview = true; // 没有按钮，Esc 关闭

            // 与程序其他界面保持一致：用系统默认字体（本机的「宋体」9pt），
            // 只有标题加粗，其余全部继承同一个字体，不再单独指定 Segoe UI。
            Font = SystemFonts.DefaultFont;

            const int pad = 14;
            const int contentWidth = 410; // 容纳一整行「TrayHider v2.0.0 · 藏起一切打扰 · 作者 wanvfx · 开源（MIT）」
            var titleFont = new Font(Font.FontFamily, Font.Size, FontStyle.Bold);
            int y = pad;

            // 标题、版本号、作者信息同一行：TrayHider v2.0.0 · 藏起一切打扰 · 作者 wanvfx · 开源（MIT）
            var title = new Label();
            title.Text = "TrayHider";
            title.Font = titleFont;
            title.AutoSize = true;
            title.Location = new Point(pad, y);

            string versionText = "v" + BuildInfo.Version;
            int x = pad + TextRenderer.MeasureText(title.Text, titleFont).Width + 6;

            // 版本号：外观与普通文字一致（同字体同字号、不下划线、不改颜色），双击打开项目主页。
            var version = new Label();
            version.Text = versionText;
            version.AutoSize = true;
            version.Cursor = Cursors.Hand;
            version.Location = new Point(x, y);
            version.DoubleClick += delegate { OpenProjectUrl(); };
            x += TextRenderer.MeasureText(versionText, Font).Width + 8;

            var meta = new Label();
            meta.Text = "· 藏起一切打扰 · 作者 wanvfx · 开源（MIT）";
            meta.AutoSize = true;
            meta.Location = new Point(x, y);

            y += title.GetPreferredSize(Size.Empty).Height + 12;

            // 正文高度必须用布局引擎自己的计算：TextRenderer 量出来的高度会少一行，
            // 直接用它会让最后一行被裁掉。
            var body = new Label();
            body.Text = Summary;
            body.Location = new Point(pad, y);
            body.Size = new Size(contentWidth,
                body.GetPreferredSize(new Size(contentWidth, 0)).Height + 2);
            y += body.Height;

            ClientSize = new Size(contentWidth + pad * 2, y + pad);

            Controls.Add(title);
            Controls.Add(version);
            Controls.Add(meta);
            Controls.Add(body);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            base.OnKeyDown(e);
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
