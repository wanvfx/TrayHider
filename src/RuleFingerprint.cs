using System;
using System.Text;

namespace TrayHider
{
    /// <summary>
    /// 规则指纹：属性条件 + 点阵量化摘要。改名、改显示名都不影响它，
    /// 供去重、导入合并、崩溃恢复、日志合并共用。
    /// 不参与序列化——它由条件算出来，存下来反而可能过期。
    /// </summary>
    internal static class RuleFingerprint
    {
        public static string Of(WindowRule r)
        {
            if (r == null) return "";
            var sb = new StringBuilder();
            sb.Append(Nz(r.TitleKeyword)).Append('|').Append(Nz(r.ProcessName)).Append('|')
              .Append(Nz(r.WindowClass)).Append('|').Append(r.AndMode ? 'A' : 'O');
            if (r.UseImage && r.HasImagePoints())
            {
                int tpl = r.Templates == null ? 1 : Math.Max(1, r.Templates.Count);
                sb.Append("|tpl=").Append(tpl)
                  .Append("|pts=").Append(r.Points == null ? 0 : r.Points.Count);
                int n = Math.Min(16, r.Points.Count);
                for (int i = 0; i < n; i++)
                {
                    var p = r.Points[i];
                    sb.Append(',').Append((int)(p.X * 1000)).Append(':').Append((int)(p.Y * 1000))
                      .Append(':').Append(p.R / 8).Append(':').Append(p.G / 8).Append(':').Append(p.B / 8);
                }
            }
            // FNV-1a（32 位）：够短，做去重键足够
            uint hash = 2166136261u;
            string text = sb.ToString();
            for (int i = 0; i < text.Length; i++)
            {
                hash ^= text[i];
                hash *= 16777619u;
            }
            return hash.ToString("X8");
        }

        private static string Nz(string s) { return s == null ? "" : s.Trim(); }
    }
}
