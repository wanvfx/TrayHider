using System;
using System.Collections.Generic;
using System.Drawing;

namespace TrayHider
{
    /// <summary>
    /// 截图比对：把规则里的点阵（相对坐标 + 颜色）与窗口当前画面比对。
    /// 支持多套模板：一次截图，逐套打分，取最高分——任一套达标即算命中，
    /// 用来应对"窗口内容会变、单套模板覆盖不住"的情况。
    /// </summary>
    internal static class ImageMatcher
    {
        /// <summary>
        /// 窗口当前画面与规则的最佳匹配率（0~100）。截图失败返回 -1。
        /// 有多套模板时返回"最有利的那一套"的分数。
        /// </summary>
        public static int MatchRate(WindowRule rule, IntPtr hwnd)
        {
            if (rule == null) return -1;
            List<List<CapturePoint>> templates = rule.GetTemplates();
            if (templates.Count == 0) return -1;

            int tolerance = rule.ColorTolerance > 0 ? rule.ColorTolerance : 16;
            using (var bmp = ScreenCapture.CaptureWindowBitmap(hwnd))
            {
                if (bmp == null) return -1;
                int best = -1;
                foreach (var t in templates)
                {
                    int rate = Score(t, bmp, tolerance);
                    if (rate > best) best = rate;
                }
                return best;
            }
        }

        /// <summary>单套点阵对给定位图的匹配率（0~100）。</summary>
        private static int Score(IList<CapturePoint> points, Bitmap bmp, int tolerance)
        {
            if (points == null || points.Count == 0 || bmp == null) return -1;
            int match = 0, total = 0;
            foreach (var p in points)
            {
                int x = (int)(p.X * bmp.Width);
                int y = (int)(p.Y * bmp.Height);
                if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) { total++; continue; }
                Color c = bmp.GetPixel(x, y);
                if (Math.Abs(c.R - p.R) <= tolerance &&
                    Math.Abs(c.G - p.G) <= tolerance &&
                    Math.Abs(c.B - p.B) <= tolerance) match++;
                total++;
            }
            if (total == 0) return -1;
            return match * 100 / total;
        }

        /// <summary>两份点阵的相互命中率（0~100）。用于采集时估算画面抖动，自动定阈值。</summary>
        public static int ComparePoints(IList<CapturePoint> a, IList<CapturePoint> b, int tolerance)
        {
            if (a == null || b == null || a.Count == 0 || a.Count != b.Count) return 0;
            int tol = tolerance > 0 ? tolerance : 16;
            int match = 0;
            for (int i = 0; i < a.Count; i++)
            {
                CapturePoint p = a[i], q = b[i];
                if (Math.Abs(p.R - q.R) <= tol &&
                    Math.Abs(p.G - q.G) <= tol &&
                    Math.Abs(p.B - q.B) <= tol) match++;
            }
            return match * 100 / a.Count;
        }
    }
}
