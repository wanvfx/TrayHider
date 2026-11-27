using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace TrayHider
{
    /// <summary>字符串小工具。</summary>
    internal static class TextUtil
    {
        /// <summary>返回第一个非空白项（全空返回 ""）。</summary>
        public static string FirstNonEmpty(params string[] values)
        {
            if (values == null) return "";
            foreach (var v in values)
                if (!string.IsNullOrEmpty(v)) return v;
            return "";
        }
    }

    /// <summary>
    /// 列表列宽自适应：默认按内容取宽；用户手动拖过的列保持他的宽度；总宽超出时等比压缩。
    /// 三个列表（托盘 / 规则 / 运行日志）共用这一份实现。
    /// </summary>
    internal static class ListViewUtil
    {
        private static bool fitting;   // 程序自己改列宽时置位，避免被当成"用户拖过"

        public static void WireAutoFit(ListView list, HashSet<int> sized)
        {
            list.ColumnWidthChanged += delegate(object s, ColumnWidthChangedEventArgs e)
            {
                if (fitting) return;
                sized.Add(e.ColumnIndex);
            };
        }

        /// <param name="fallback">各列的默认宽度</param>
        public static void AutoFit(ListView list, HashSet<int> sized, int[] fallback)
        {
            if (list == null || list.Columns.Count == 0 || !list.IsHandleCreated) return;
            int avail = list.ClientSize.Width - 6;
            if (avail <= 50) return;

            fitting = true;
            try
            {
                for (int i = 0; i < list.Columns.Count; i++)
                {
                    if (sized.Contains(i)) continue;
                    list.Columns[i].Width = fallback[Math.Min(i, fallback.Length - 1)];
                    list.AutoResizeColumn(i, list.Items.Count == 0
                        ? ColumnHeaderAutoResizeStyle.HeaderSize
                        : ColumnHeaderAutoResizeStyle.ColumnContent);
                }
                int total = 0;
                for (int i = 0; i < list.Columns.Count; i++) total += list.Columns[i].Width;
                if (total > avail)
                {
                    double scale = (double)avail / total;
                    for (int i = 0; i < list.Columns.Count; i++)
                        if (!sized.Contains(i))
                            list.Columns[i].Width = Math.Max(50, (int)(list.Columns[i].Width * scale));
                }
            }
            finally { fitting = false; }
        }
    }
}
