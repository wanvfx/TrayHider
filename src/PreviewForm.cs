using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TrayHider
{
    /// <summary>鼠标悬停时显示弹窗预览（截图缩略图放大）。</summary>
    internal sealed class PreviewForm : Form
    {
        private readonly PictureBox picture = new PictureBox();

        public PreviewForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.White;
            Size = new Size(220, 190);

            picture.Dock = DockStyle.Fill;
            picture.SizeMode = PictureBoxSizeMode.Zoom;
            picture.BackColor = Color.White;

            Controls.Add(picture);
        }

        /// <summary>
        /// 显示一张缩略图。每次调用都会换掉旧图——旧图必须释放，
        /// 否则鼠标在缩略图规则上来回移动会持续累积 GDI 对象。
        /// </summary>
        public void ShowImage(byte[] png)
        {
            if (png == null || png.Length == 0) return;
            Image parsed = null;
            Image previous = null;
            try
            {
                using (var ms = new MemoryStream(png))
                {
                    parsed = Image.FromStream(ms);
                    var bmp = new Bitmap(parsed);   // 拷一份，随后即可释放流与源图
                    previous = picture.Image;
                    picture.Image = bmp;
                }

                // 定位到鼠标右下方，避免遮挡
                Point p = Control.MousePosition;
                Screen screen = Screen.FromPoint(p);
                int x = p.X + 16, y = p.Y + 16;
                if (x + Width > screen.WorkingArea.Right) x = p.X - Width - 8;
                if (y + Height > screen.WorkingArea.Bottom) y = p.Y - Height - 8;
                Location = new Point(x, y);
                if (!Visible) Show();
                else BringToFront();
            }
            catch { if (parsed != null) parsed.Dispose(); }
            finally { if (previous != null) previous.Dispose(); }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Hide();
        }

        protected override bool ShowWithoutActivation { get { return true; } }
    }
}
