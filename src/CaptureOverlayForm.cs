using System;
using System.Drawing;
using System.Windows.Forms;

namespace TrayHider
{
    /// <summary>
    /// 火绒式「截图拦截」：
    /// 鼠标指向某个窗口时，只把那个窗口压暗（其余画面保持原样），并提示
    /// 「点击拦截此窗口，按 Esc 退出截图拦截」；点击该窗口后显示
    /// 「弹窗：xxx.exe」+「拦截」按钮；Esc 取消。
    /// 跟踪用 Cursor.Position 轮询，不依赖鼠标消息，因此遮罩只盖住目标窗口也不影响跟焦。
    /// </summary>
    internal sealed class CaptureOverlayForm : Form
    {
        private DimForm _dim;                 // 只压暗目标窗口
        private Timer _poll;                  // 轮询鼠标位置
        private IntPtr _hoverHwnd = IntPtr.Zero;
        private Rectangle _hoverRect;
        private IntPtr _lockedHwnd = IntPtr.Zero;
        private Rectangle _lockedRect;
        private bool _locked;
        private bool _escDown;                // Esc 沿触发，避免按住时反复取消
        private DateTime _lastEnum = DateTime.MinValue;

        private OverlayLabel lblHint;
        private OverlayLabel lblName;
        private BlockButton btnBlock;
        private OverlayLabel lblClose;

        /// <summary>识别到的窗体句柄（0 表示未识别到）。</summary>
        public IntPtr SelectedHwnd { get; private set; }

        // 透明色用黑：文字（白）抗锯齿出来的灰边和压暗后的窗口灰底接近，不会露出色边
        private readonly Color transKey = Color.Black;
        // 所有遮罩文字统一用工具本身的默认字体（和界面其它文字一致）
        private readonly Font textFont = SystemFonts.DefaultFont;

        public CaptureOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = transKey;
            TransparencyKey = transKey;
            Font = textFont;

            lblHint = new OverlayLabel
            {
                Text = "点击拦截此窗口，按 Esc 退出截图拦截",
                ForeColor = Color.White, Cursor = Cursors.Cross
            };
            // 标签占着窗口中间一大条：WinForms 的鼠标事件不会冒泡到窗体，必须自己接
            lblHint.MouseDown += delegate { LockHovered(); };
            lblName = new OverlayLabel
            {
                Text = "", Visible = false,
                ForeColor = Color.White, Cursor = Cursors.Cross
            };
            lblName.MouseDown += delegate { LockHovered(); };
            // 和工具界面「刷新列表」同款：白底圆角按钮（自绘，避免透明窗体下系统按钮填充丢失）
            btnBlock = new BlockButton { Text = "拦截" };
            btnBlock.Clicked += delegate { Confirm(); };

            // 关闭用纯文字「×」，不要背景框；位置由 LayoutChildren 放到目标窗口右上角
            lblClose = new OverlayLabel
            {
                Text = "×", Size = new Size(38, 30),
                ForeColor = Color.White, Cursor = Cursors.Hand
            };
            lblClose.MouseDown += delegate { Cancel(); };

            Controls.Add(lblHint);
            Controls.Add(lblName);
            Controls.Add(btnBlock);
            Controls.Add(lblClose);

            // 面板本体（含空白处）视为「点了窗口」
            MouseDown += delegate { LockHovered(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            _dim = new DimForm();
            _dim.Clicked += delegate { LockHovered(); };
            _dim.Show();

            _poll = new Timer { Interval = 50 };
            _poll.Tick += delegate { Poll(); };
            _poll.Start();

            FitToTarget();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_poll != null) { _poll.Stop(); _poll.Dispose(); _poll = null; }
            if (_dim != null) { _dim.Close(); _dim.Dispose(); _dim = null; }
            base.OnFormClosed(e);
        }

        // ---------------- 跟踪 ----------------

        private Point _lastPt;

        /// <summary>轮询鼠标：找到它下面的顶层窗口，只压暗那个窗口。鼠标没动时不重复枚举。</summary>
        private bool _lbtnDown;

        private void Poll()
        {
            // Esc：直接轮询物理按键，不再额外挂一个全局键盘钩子（挂两个低级钩子会拖慢输入）
            short esc = Native.GetAsyncKeyState(0x1B);
            if ((esc & 0x8000) != 0)
            {
                if (!_escDown && Visible) { _escDown = true; Cancel(); return; }
                return;
            }
            _escDown = false;

            // 直接看物理左键状态：点击落到哪个窗口都能检测到，不依赖命中测试
            bool down = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (down && !_lbtnDown)
            {
                _lbtnDown = true;
                if (_hoverHwnd == IntPtr.Zero)   // 兜底：点的时候顺手按当前鼠标位置再认一次窗口
                {
                    IntPtr hh = WindowAtPoint(Cursor.Position);
                    if (hh != IntPtr.Zero)
                    {
                        Native.RECT nr;
                        if (Native.GetWindowRect(hh, out nr))
                        {
                            _hoverHwnd = hh;
                            _hoverRect = Rectangle.FromLTRB(nr.Left, nr.Top, nr.Right, nr.Bottom);
                        }
                    }
                }
                LockHovered();
            }
            else if (!down) _lbtnDown = false;

            if (_locked) return;
            Point pt = Cursor.Position;
            // 鼠标没动时也不完全空转：每 500ms 重算一次，窗口自己移动/缩放也能跟上
            if (pt == _lastPt && DateTime.Now - _lastEnum < TimeSpan.FromMilliseconds(500)) return;
            _lastPt = pt;
            _lastEnum = DateTime.Now;

            IntPtr h = WindowAtPoint(pt);
            Rectangle r = Rectangle.Empty;
            if (h != IntPtr.Zero)
            {
                Native.RECT nr;
                if (Native.GetWindowRect(h, out nr))
                    r = Rectangle.FromLTRB(nr.Left, nr.Top, nr.Right, nr.Bottom);
            }
            if (h == _hoverHwnd && r == _hoverRect) return;
            _hoverHwnd = h;
            _hoverRect = r;

            if (_hoverHwnd == IntPtr.Zero || _hoverRect.IsEmpty)
            {
                _hoverHwnd = IntPtr.Zero;
                if (_dim.Visible) _dim.Hide();
                if (Visible) Visible = false;   // 没指向窗口时不显示任何东西
                return;
            }
            if (!_dim.Visible) _dim.Show();
            if (_dim.Bounds != _hoverRect) _dim.Bounds = _hoverRect;
            if (!Visible) Visible = true;
            FitToTarget();
        }

        /// <summary>本体大小 = 目标窗口大小（透明且点击穿透），子控件按它排版。</summary>
        private void FitToTarget()
        {
            if (_hoverRect.IsEmpty) return;
            if (Bounds != _hoverRect) Bounds = _hoverRect;
            LayoutChildren();
        }

        /// <summary>排版：整块内容居中，× 在窗口右上角；所有控件都夹在窗口内，避免被裁切。</summary>
        private void LayoutChildren()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 1 || h <= 1) return;

            lblClose.Location = new Point(Math.Max(2, w - 40), 4);

            int hintH = textFont.Height + 10;
            lblHint.Size = new Size(w, hintH);

            if (!_locked)
            {
                lblHint.Location = new Point(0, Math.Max(0, (h - hintH) / 2));
                return;
            }

            lblName.Visible = true;
            btnBlock.Visible = true;

            int nameH = textFont.Height + 12;
            int btnH = btnBlock.Height;
            int gap = 8;
            int total = hintH + gap + nameH + gap + btnH;
            int y = Math.Max(4, (h - total) / 2);

            lblName.Size = new Size(w, nameH);
            lblHint.Location = new Point(0, y);
            lblName.Location = new Point(0, y + hintH + gap);

            int btnX = Math.Max(4, Math.Min(w / 2 - btnBlock.Width / 2, w - btnBlock.Width - 4));
            // 夹在窗口客户区内：超出下边缘会被裁掉，下面两个圆角就变成直角了
            int btnY = Math.Max(4, Math.Min(lblName.Bottom + gap, h - btnBlock.Height - 4));
            btnBlock.Location = new Point(btnX, btnY);
        }

        // ---------------- 交互 ----------------

        /// <summary>点击了窗口（遮罩）→ 锁定该窗口，显示「弹窗：xxx.exe」+「拦截」。</summary>
        private void LockHovered()
        {
            if (_locked || _hoverHwnd == IntPtr.Zero || _hoverRect.IsEmpty) return;
            _locked = true;
            _lockedHwnd = _hoverHwnd;
            _lockedRect = _hoverRect;

            lblName.Text = "弹窗：" + ProcessNameOf(_lockedHwnd);
            lblName.Visible = true;
            btnBlock.Visible = true;
            lblHint.Text = "按 Esc 退出截图拦截";
            LayoutChildren();
        }

        private void Confirm()
        {
            if (!_locked) return;
            SelectedHwnd = _lockedHwnd;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void Cancel()
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private static string ProcessNameOf(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            string path = Native.ProcessPath(pid);
            if (path.Length == 0) return "未知程序";
            string name = System.IO.Path.GetFileName(path);
            return name.Length == 0 ? "未知程序" : name;
        }

        /// <summary>
        /// 屏幕上某点最上层的可见窗口。
        /// 从 z 序最上层往下走（GetTopWindow + GW_HWNDNEXT），不依赖 EnumWindows 未文档化的顺序。
        /// 跳过本进程窗口、遮罩、点击穿透层和小工具窗口。
        /// </summary>
        private IntPtr WindowAtPoint(Point screenPt)
        {
            uint selfPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr h = Native.GetTopWindow(IntPtr.Zero);
            while (h != IntPtr.Zero)
            {
                if (IsCandidate(h, selfPid, screenPt)) return h;
                h = Native.GetWindow(h, Native.GW_HWNDNEXT);
            }
            return IntPtr.Zero;
        }

        /// <summary>该窗口是否可以作为拦截目标。</summary>
        private bool IsCandidate(IntPtr h, uint selfPid, Point screenPt)
        {
            if (h == Handle || (_dim != null && h == _dim.Handle)) return false;
            if (!Native.IsWindowVisible(h)) return false;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == selfPid) return false;
            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TRANSPARENT) != 0) return false;   // 点击穿透的浮层
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return false;    // 任务栏、输入法等
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return false;
            Rectangle wr = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (wr.Width < 20 || wr.Height < 20) return false;
            return wr.Contains(screenPt);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOPMOST = 0x00000008;
                const int WS_EX_TOOLWINDOW = 0x00000080;
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        /// <summary>
        /// 白底圆角「拦截」按钮（自绘 Control）。
        /// 不用 Button：透明窗体上系统按钮的默认绘制会盖住下边缘，下面两个圆角会变成直角。
        /// </summary>
        private sealed class BlockButton : Control
        {
            public BlockButton()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Size = new Size(120, 36);
                Cursor = Cursors.Hand;
                TabStop = false;
                Visible = false;
            }

            public event Action Clicked;

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Color.Black);   // 与窗体透明色一致 → 透明
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
                using (var fill = new SolidBrush(Color.White))
                using (var pen = new Pen(Color.FromArgb(185, 185, 190))
                           { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset })
                {
                    e.Graphics.FillPath(fill, path);
                    e.Graphics.DrawPath(pen, path);
                }
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (var fg = new SolidBrush(Color.FromArgb(28, 28, 28)))   // 近黑：纯黑会被透明色抠掉
                    e.Graphics.DrawString(Text, Font, fg, new Rectangle(0, 0, Width, Height), fmt);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && Clicked != null) Clicked();
            }
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int radius)
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

        /// <summary>
        /// 遮罩上的纯文字标签：只画白字，无阴影。自绘是为了让文字浮在透明窗体上
        /// （窗体透明色是纯黑，不能靠背景框托底）。
        /// </summary>
        private sealed class OverlayLabel : Label
        {
            public OverlayLabel()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
                TextAlign = ContentAlignment.MiddleCenter;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using (var fg = new SolidBrush(ForeColor))
                    e.Graphics.DrawString(Text, Font, fg, new Rectangle(0, 0, Width, Height), fmt);
            }
        }

        /// <summary>只压暗目标窗口的纯黑半透明层（无控件，避免控件被一起调暗）。</summary>
        private sealed class DimForm : Form
        {
            public Action Clicked;

            public DimForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = Color.Black;
                Opacity = 0.35;
                Cursor = Cursors.Cross;
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && Clicked != null) Clicked();
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_TOPMOST = 0x00000008;
                    const int WS_EX_TOOLWINDOW = 0x00000080;
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
                    return cp;
                }
            }
        }
    }

    /// <summary>截屏工具：把窗口抓成点阵 / 缩略图。</summary>
    internal static class ScreenCapture
    {
        /// <summary>
        /// 抓取窗口，生成 N×N 采样点。PrintWindow 失败时返回空数组。
        /// </summary>
        public static CapturePoint[] CaptureWindowPoints(IntPtr hwnd, int gridSize)
        {
            if (gridSize <= 0) return new CapturePoint[0];
            Native.RECT rect;
            if (!Native.GetWindowRect(hwnd, out rect)) return new CapturePoint[0];
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return new CapturePoint[0];

            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return new CapturePoint[0];
            try
            {
                IntPtr memDc = Native.CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero) return new CapturePoint[0];
                try
                {
                    IntPtr bmp = Native.CreateCompatibleBitmap(screenDc, width, height);
                    if (bmp == IntPtr.Zero) return new CapturePoint[0];
                    try
                    {
                        Native.SelectObject(memDc, bmp);
                        if (!Native.CaptureWindow(hwnd, memDc, width, height)) return new CapturePoint[0];

                        // 用 GDI+ 从内存 DC 读像素（GetPixel 太慢，逐点调用开销大）
                        using (var managed = new Bitmap(width, height))
                        using (var g = Graphics.FromImage(managed))
                        {
                            IntPtr gdi = g.GetHdc();
                            try
                            {
                                Native.BitBlt(gdi, 0, 0, width, height, memDc, 0, 0, 0x00CC0020 /* SRCCOPY */);
                            }
                            finally { g.ReleaseHdc(gdi); }
                            return SampleBitmap(managed, gridSize);
                        }
                    }
                    finally { Native.DeleteObject(bmp); }
                }
                finally { Native.DeleteDC(memDc); }
            }
            finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }
        }

        /// <summary>
        /// 采集窗口缩略图（PNG 字节）。最长边缩到 targetWidth，供列表悬停预览用。
        /// </summary>
        public static byte[] CaptureThumbnail(IntPtr hwnd, int targetWidth)
        {
            Bitmap bmp = CaptureWindowBitmap(hwnd);
            if (bmp == null) return new byte[0];
            try
            {
                int max = Math.Max(bmp.Width, bmp.Height);
                if (max > targetWidth)
                {
                    double scale = (double)targetWidth / max;
                    int w = Math.Max(1, (int)(bmp.Width * scale));
                    int h = Math.Max(1, (int)(bmp.Height * scale));
                    using (var small = new Bitmap(w, h))
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                        g.DrawImage(bmp, 0, 0, w, h);
                        using (var ms = new System.IO.MemoryStream())
                        {
                            small.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                            return ms.ToArray();
                        }
                    }
                }
                using (var ms = new System.IO.MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return ms.ToArray();
                }
            }
            finally { bmp.Dispose(); }
        }

        /// <summary>抓取窗口位图；PrintWindow 失败返回 null。</summary>
        public static Bitmap CaptureWindowBitmap(IntPtr hwnd)        {
            Native.RECT rect;
            if (!Native.GetWindowRect(hwnd, out rect)) return null;
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return null;

            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;
            try
            {
                IntPtr memDc = Native.CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero) return null;
                try
                {
                    IntPtr bmp = Native.CreateCompatibleBitmap(screenDc, width, height);
                    if (bmp == IntPtr.Zero) return null;
                    try
                    {
                        Native.SelectObject(memDc, bmp);
                        if (!Native.CaptureWindow(hwnd, memDc, width, height)) return null;

                        var managed = new Bitmap(width, height);
                        using (var g = Graphics.FromImage(managed))
                        {
                            IntPtr gdi = g.GetHdc();
                            try
                            {
                                Native.BitBlt(gdi, 0, 0, width, height, memDc, 0, 0, 0x00CC0020 /* SRCCOPY */);
                            }
                            finally { g.ReleaseHdc(gdi); }
                        }
                        return managed;
                    }
                    finally { Native.DeleteObject(bmp); }
                }
                finally { Native.DeleteDC(memDc); }
            }
            finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }
        }

        /// <summary>对位图按 N×N 网格采样（相对坐标 + 颜色）。</summary>
        private static CapturePoint[] SampleBitmap(Bitmap bmp, int gridSize)
        {
            var points = new CapturePoint[gridSize * gridSize];
            for (int gy = 0; gy < gridSize; gy++)
            {
                for (int gx = 0; gx < gridSize; gx++)
                {
                    int x = bmp.Width * (gx + 1) / (gridSize + 1);
                    int y = bmp.Height * (gy + 1) / (gridSize + 1);
                    Color c = bmp.GetPixel(x, y);
                    points[gy * gridSize + gx] = new CapturePoint(
                        (double)(gx + 1) / (gridSize + 1),
                        (double)(gy + 1) / (gridSize + 1),
                        c.R, c.G, c.B);
                }
            }
            return points;
        }
    }
}
