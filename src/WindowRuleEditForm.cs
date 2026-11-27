using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TrayHider
{
    /// <summary>
    /// 编辑窗口规则。上方只读区显示采集时识别到的窗口名 / 程序 / 路径；
    /// 匹配条件先按识别结果预填并只读，点「修改」后才可改。
    /// 动作固定为「隐藏窗口、程序后台继续运行」。
    /// </summary>
    internal sealed class WindowRuleEditForm : Form
    {
        private readonly WindowRule rule;

        private readonly TextBox name;      // 只读：采集到的窗口名
        private readonly ComboBox family;   // 归属软件（用户自定义，决定分组）
        private readonly TextBox title = new TextBox();
        private readonly TextBox process = new TextBox();
        private readonly TextBox className = new TextBox();
        private readonly NumericUpDown threshold;   // 匹配阈值（仅截图规则）
        private readonly RadioButton orMode = new RadioButton();
        private readonly RadioButton andMode = new RadioButton();
        private readonly CheckBox enabled = new CheckBox();
        private readonly Label preview = new Label();
        private readonly Label thresholdNote = new Label();
        private readonly Button editBtn;

        /// <param name="knownFamilies">已有的归属软件名，供下拉选择</param>
        public WindowRuleEditForm(WindowRule rule, List<string> knownFamilies)
        {
            this.rule = rule;

            Text = "编辑窗口规则";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            name = new TextBox { ReadOnly = true, BackColor = SystemColors.Control };
            family = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
            foreach (var f in knownFamilies) family.Items.Add(f);
            threshold = new NumericUpDown { Minimum = 30, Maximum = 100, Value = 80, Size = new Size(80, 24) };
            editBtn = new Button { Text = "修改", Size = new Size(70, 26) };
            editBtn.Click += delegate
            {
                title.ReadOnly = process.ReadOnly = className.ReadOnly = false;
                editBtn.Visible = false;
                title.Focus();
            };

            BuildLayout();
            LoadRule();
        }

        private void BuildLayout()
        {
            int y = 16;

            var l0 = new Label { Text = "弹窗名称：", AutoSize = true, Location = new Point(16, y + 4) };
            name.Location = new Point(110, y); name.Size = new Size(400, 24);
            y += 32;

            var lf = new Label { Text = "归属软件：", AutoSize = true, Location = new Point(16, y + 4) };
            family.Location = new Point(110, y); family.Size = new Size(220, 24);
            y += 32;

            var l1 = new Label { Text = "标题包含：", AutoSize = true, Location = new Point(16, y + 4) };
            title.Location = new Point(110, y); title.Size = new Size(380, 24);
            y += 34;

            var l2 = new Label { Text = "进程名：", AutoSize = true, Location = new Point(16, y + 4) };
            process.Location = new Point(110, y); process.Size = new Size(220, 24);
            y += 34;

            int yClass = y;
            var l3 = new Label { Text = "窗口类名：", AutoSize = true, Location = new Point(16, y + 4) };
            className.Location = new Point(110, y); className.Size = new Size(300, 24);
            editBtn.Location = new Point(420, y);
            y += 36;

            Controls.Add(l0); Controls.Add(name);
            Controls.Add(lf); Controls.Add(family);
            Controls.Add(l1); Controls.Add(title);
            Controls.Add(l2); Controls.Add(process);
            Controls.Add(l3); Controls.Add(className);
            Controls.Add(editBtn);

            // 截图规则才有「匹配阈值」：画面命中率 ≥ 该值才拦（窗口内容会动的得调低）
            if (rule.UseImage)
            {
                var lt = new Label { Text = "匹配阈值：", AutoSize = true, Location = new Point(16, y + 4) };
                threshold.Location = new Point(110, y);
                // 显示"当前真正生效的阈值"：自动模式下可能是自学习出来的值
                int shown = rule.EffectiveThreshold();
                threshold.Value = Math.Max(threshold.Minimum, Math.Min(threshold.Maximum, shown));
                thresholdNote.Text = !rule.AutoThreshold
                    ? "% 命中率 ≥ 该值才拦（手动）"
                    : (rule.LearnedRatio.HasValue
                        ? ("% 命中率 ≥ 该值才拦（自动学习中，当前 " + rule.LearnedRatio + "%）")
                        : "% 命中率 ≥ 该值才拦（采集时按画面抖动自动定）");
                thresholdNote.AutoSize = true;
                thresholdNote.Location = new Point(196, y + 4);
                thresholdNote.ForeColor = Color.Gray;
                Controls.Add(lt); Controls.Add(threshold); Controls.Add(thresholdNote);
                y += 34;
            }

            orMode.Text = "或（任一字段命中）";
            orMode.Location = new Point(110, y); orMode.Size = new Size(160, 24);
            andMode.Text = "且（所有非空字段都命中）";
            andMode.Location = new Point(290, y); andMode.Size = new Size(200, 24);
            y += 34;

            enabled.Text = "启用此规则";
            enabled.Location = new Point(110, y); enabled.Size = new Size(120, 24);
            y += 32;

            var hint = new Label
            {
                Text = "说明：命中后「隐藏窗口」，程序在后台继续运行。\n" +
                       "匹配条件默认按识别结果预填，点右侧「修改」后再改。",
                ForeColor = Color.Gray, AutoSize = true, Location = new Point(110, y)
            };
            y += 46;

            preview.Location = new Point(16, y); preview.Size = new Size(500, 40);
            y += 48;

            var ok = new Button { Text = "确定", Size = new Size(110, 30) };
            ok.Location = new Point(280, y);
            ok.Click += delegate { SaveAndClose(); };
            var cancel = new Button { Text = "取消", Size = new Size(110, 30) };
            cancel.Location = new Point(404, y);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(orMode); Controls.Add(andMode);
            Controls.Add(enabled);
            Controls.Add(hint);
            Controls.Add(preview);
            Controls.Add(ok); Controls.Add(cancel);

            ClientSize = new Size(540, y + 46);

            EventHandler changed = delegate { UpdatePreview(); };
            title.TextChanged += changed;
            process.TextChanged += changed;
            className.TextChanged += changed;
            orMode.CheckedChanged += changed;
            andMode.CheckedChanged += changed;
            threshold.ValueChanged += delegate
            {
                // 用户一改阈值就接管控制权：不再自学习，并显示手动值
                rule.AutoThreshold = false;
                rule.LearnedRatio = null;
                thresholdNote.Text = "% 命中率 ≥ 该值才拦（手动）";
                UpdatePreview();
            };
            family.TextChanged += changed;
        }

        private void LoadRule()
        {
            // 只读区：告诉用户这条规则对应哪个窗口/程序（截图规则只有这些信息）
            string who = string.IsNullOrEmpty(rule.DisplayName)
                ? TextUtil.FirstNonEmpty(rule.SoftwareName, rule.ProcessName)
                : rule.DisplayName;
            if (!string.IsNullOrEmpty(rule.ProgramPath)) who += "（" + rule.ProgramPath + "）";
            name.Text = who;
            family.Text = TextUtil.FirstNonEmpty(rule.Family, rule.SoftwareName, rule.ProcessName);

            // 匹配条件按识别结果预填（旧规则没有识别数据时退回已有值），默认只读
            title.Text = TextUtil.FirstNonEmpty(rule.TitleKeyword, rule.RecognizedTitle, rule.DisplayName);
            process.Text = TextUtil.FirstNonEmpty(rule.ProcessName, rule.SoftwareName);
            className.Text = TextUtil.FirstNonEmpty(rule.WindowClass, rule.RecognizedClass);
            title.ReadOnly = process.ReadOnly = className.ReadOnly = true;

            orMode.Checked = !rule.AndMode;
            andMode.Checked = rule.AndMode;
            enabled.Checked = rule.Enabled;
            UpdatePreview();
        }


        private void UpdatePreview()
        {
            string extra = rule.UseImage
                ? "截图采样 " + (rule.Points == null ? 0 : rule.Points.Count) + " 点，阈值 " + (int)threshold.Value + "%"
                : "";
            preview.Text = "预览：" + BuildRule().Describe()
                         + (extra.Length > 0 ? " · " + extra : "");
        }

        private WindowRule BuildRule()
        {
            return new WindowRule
            {
                TitleKeyword = title.Text.Trim(),
                ProcessName = process.Text.Trim(),
                WindowClass = className.Text.Trim(),
                AndMode = andMode.Checked,
                // 动作固定为「隐藏」——对齐火绒：窗口藏起、程序后台运行
                Actions = new List<WindowAction> { WindowAction.Hide },
                Enabled = enabled.Checked,
                // 截图规则的相关数据原样保留，避免一编辑就退化成纯属性规则
                UseImage = rule.UseImage,
                Points = rule.Points ?? new List<CapturePoint>(),
                Templates = rule.Templates ?? new List<List<CapturePoint>>(),
                MatchRatio = (int)threshold.Value,
                LearnedRatio = rule.LearnedRatio,
                ColorTolerance = rule.ColorTolerance,
                CapturedWidth = rule.CapturedWidth,
                CapturedHeight = rule.CapturedHeight,
                Thumbnail = rule.Thumbnail,
                DisplayName = rule.DisplayName,
                SoftwareName = rule.SoftwareName,
                ProgramPath = rule.ProgramPath,
                RecognizedTitle = rule.RecognizedTitle,
                RecognizedClass = rule.RecognizedClass,
                Family = family.Text.Trim(),
            };
        }

        private void SaveAndClose()
        {
            var updated = BuildRule();
            if (!updated.IsUsable())
            {
                MessageBox.Show(this, "没有可用的匹配条件：请至少填写标题 / 进程 / 类名，或保留截图采样点。",
                    "窗口规则", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 截图规则必须有属性条件限定范围：否则它匹配所有窗口，
            // 拦截时会对每个可见窗口截图（PrintWindow + 逐点取色），把工具拖慢。
            if (updated.UseImage && updated.Points != null && updated.Points.Count > 0 &&
                string.IsNullOrEmpty(updated.TitleKeyword) &&
                string.IsNullOrEmpty(updated.ProcessName) &&
                string.IsNullOrEmpty(updated.WindowClass))
            {
                MessageBox.Show(this,
                    "截图规则需要至少一个匹配条件（标题包含 / 进程名 / 窗口类名）来限定范围。\n\n" +
                    "否则它会匹配所有窗口，拦截时对每个可见窗口截图，明显拖慢工具。",
                    "窗口规则", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            rule.TitleKeyword = updated.TitleKeyword;
            rule.ProcessName = updated.ProcessName;
            rule.WindowClass = updated.WindowClass;
            rule.AndMode = updated.AndMode;
            rule.Actions = updated.Actions;
            rule.Enabled = updated.Enabled;
            rule.UseImage = updated.UseImage;
            rule.Points = updated.Points;
            rule.Templates = updated.Templates ?? new List<List<CapturePoint>>();
            rule.MatchRatio = updated.MatchRatio;
            rule.LearnedRatio = updated.LearnedRatio;
            rule.ColorTolerance = updated.ColorTolerance;
            rule.Thumbnail = updated.Thumbnail;
            rule.DisplayName = updated.DisplayName;
            rule.SoftwareName = updated.SoftwareName;
            rule.ProgramPath = updated.ProgramPath;
            rule.RecognizedTitle = updated.RecognizedTitle;
            rule.RecognizedClass = updated.RecognizedClass;
            rule.Family = updated.Family;

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
