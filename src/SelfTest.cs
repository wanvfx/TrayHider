using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TrayHider
{
    /// <summary>
    /// Creates its own notification-area icon, then verifies that enumeration, hiding and
    /// restoring work on this Windows build. It never touches the user's real icons.
    /// </summary>
    internal static class SelfTest
    {
        public static int Run()
        {
            string logPath = Path.Combine(Path.GetTempPath(), "TrayHider_selftest.log");
            string marker = "TrayHider 自检图标";
            string self = Application.ExecutablePath;
            var log = new StringBuilder();
            int result = 1;

            Form form = null;
            NotifyIcon tray = null;
            Icon icon = null;
            IntPtr iconHandle = IntPtr.Zero;
            Bitmap bitmap = null;
            System.Windows.Forms.Timer timer = null;

            try
            {
                form = new Form();
                form.ShowInTaskbar = false;
                form.WindowState = FormWindowState.Minimized;
                form.Opacity = 0;
                form.Size = new Size(1, 1);
                form.FormBorderStyle = FormBorderStyle.FixedSingle;
                form.MaximizeBox = false;
                form.MinimizeBox = false;

                bitmap = new Bitmap(32, 32);
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.FillEllipse(new SolidBrush(Color.FromArgb(0x2F, 0x6F, 0xED)), 4, 4, 24, 24);
                    g.DrawEllipse(Pens.White, 9, 9, 14, 14);
                }
                iconHandle = bitmap.GetHicon();
                icon = Icon.FromHandle(iconHandle);
                tray = new NotifyIcon();
                tray.Icon = icon;
                tray.Text = marker;
                tray.Visible = true;
                tray.ContextMenuStrip = new ContextMenuStrip();

                log.AppendLine("已创建测试托盘图标: " + marker);
                log.AppendLine("本程序: " + self);
                int step = 0;
                int attempts = 0;
                TrayItem target = null;

                timer = new System.Windows.Forms.Timer();
                timer.Interval = 1200;
                timer.Tick += delegate(object s, EventArgs e)
                {
                    try
                    {
                        if (step == 0)
                        {
                            var items = TrayCatalog.Scan(0);
                            foreach (var candidate in items)
                            {
                                if (TrayCatalog.SamePath(candidate.Path, self)) { target = candidate; break; }
                            }
                            if (target == null)
                            {
                                if (attempts++ < 10)
                                {
                                    log.AppendLine("尚未发现测试图标，重试 " + attempts + "/10");
                                    return;
                                }
                                log.AppendLine("失败: 未找到测试图标（枚举机制不可用）");
                                Finish();
                                return;
                            }
                            log.AppendLine("找到测试图标: path=" + target.Path + " tooltip=" + target.Tooltip +
                                           " state=" + target.State + " uid=" + target.Id + " guid=" + target.Guid);
                            if (target.State != 0)
                            {
                                log.AppendLine("失败: 初始状态为 " + target.State + "，期望 0");
                                Finish();
                                return;
                            }

                            bool ok = TrayApi.SetHidden(target, true);
                            log.AppendLine("调用隐藏: " + (ok ? "Shell_NotifyIcon 返回成功" : "Shell_NotifyIcon 返回失败"));
                            int state = WaitState(target, 1);
                            log.AppendLine("隐藏后状态: " + state + "（期望 1）");
                            if (state != 1) { log.AppendLine("失败: 隐藏未生效"); Finish(); return; }
                            step = 1;
                            return;
                        }

                        step = 2;
                        bool show = TrayApi.SetHidden(target, false);
                        log.AppendLine("调用恢复: " + (show ? "Shell_NotifyIcon 返回成功" : "Shell_NotifyIcon 返回失败"));
                        int restored = WaitState(target, 0);
                        log.AppendLine("恢复后状态: " + restored + "（期望 0）");
                        result = restored == 0 ? 0 : 1;
                        log.AppendLine(result == 0 ? "自检通过" : "失败: 恢复未生效");
                        Finish();
                    }
                    catch (Exception ex)
                    {
                        log.AppendLine("异常: " + ex);
                        Finish();
                    }
                };

                void Finish()
                {
                    try { if (timer != null) timer.Stop(); } catch { }
                    try { File.WriteAllText(logPath, log.ToString(), Encoding.UTF8); } catch { }
                    try { if (tray != null) tray.Dispose(); } catch { }
                    try { if (icon != null) icon.Dispose(); } catch { }
                    try { if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle); } catch { }
                    try { if (bitmap != null) bitmap.Dispose(); } catch { }
                    try { if (form != null) form.Close(); } catch { }
                    Environment.Exit(result);
                }

                timer.Start();
                Application.Run(form);
            }
            catch (Exception ex)
            {
                log.AppendLine("异常: " + ex);
                try { File.WriteAllText(logPath, log.ToString(), Encoding.UTF8); } catch { }
                return 1;
            }
            return result;
        }

        private static int WaitState(TrayItem item, int expected, int timeoutMs = 2500)
        {
            int elapsed = 0;
            while (elapsed < timeoutMs)
            {
                int state = TrayApi.GetState(item);
                if (state == expected) return state;
                Thread.Sleep(50);
                elapsed += 50;
            }
            return TrayApi.GetState(item);
        }
    }
}
