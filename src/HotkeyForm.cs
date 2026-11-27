using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TrayHider
{
    /// <summary>
    /// Hotkey panel. Two trigger modes, both passive (other applications keep their own
    /// shortcuts; this tool only watches):
    ///   - combination: Ctrl/Alt/Shift/Win + a key
    ///   - sequence: an ordered list of key presses, recorded or built by hand
    /// </summary>
    internal sealed class HotkeyForm : Form
    {
        private readonly Settings settings;

        private readonly CheckBox enable = new CheckBox();
        private readonly Label modeLabel = new Label();
        private readonly RadioButton modeCombo = new RadioButton();
        private readonly RadioButton modeSeq = new RadioButton();
        private readonly Panel comboPanel = new Panel();
        private readonly Panel seqPanel = new Panel();

        private readonly CheckBox modCtrl = new CheckBox();
        private readonly CheckBox modAlt = new CheckBox();
        private readonly CheckBox modShift = new CheckBox();
        private readonly CheckBox modWin = new CheckBox();
        private readonly ComboBox comboKey = new ComboBox();

        private readonly List<int> sequence = new List<int>();
        private readonly Button recordButton = new Button();
        private readonly Label sequenceLabel = new Label();
        private readonly ComboBox addKey = new ComboBox();
        private readonly NumericUpDown window = new NumericUpDown();
        private KeyboardHook recordHook;
        private bool recording;

        private readonly Label preview = new Label();

        public HotkeyForm(Settings settings)
        {
            this.settings = settings;
            if (settings.Sequence != null) sequence.AddRange(settings.Sequence);

            Text = "快捷键设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            enable.Text = "启用快捷键（按一次呼出，再按一次隐藏）";
            enable.Checked = settings.HotkeyEnabled;
            enable.CheckedChanged += delegate { UpdateState(); };

            modeLabel.Text = "触发方式：";
            modeLabel.AutoSize = true;

            modeCombo.Text = "组合键（如 Ctrl + Alt + T）";
            modeCombo.Checked = settings.HotkeyMode == 0;
            modeCombo.CheckedChanged += delegate { UpdateState(); };

            modeSeq.Text = "按键序列（可录制，任意键、任意次数）";
            modeSeq.Checked = settings.HotkeyMode == 1;
            modeSeq.CheckedChanged += delegate { UpdateState(); };

            BuildComboPanel();
            BuildSequencePanel();

            var ok = new Button();
            ok.Text = "确定";
            ok.Size = new Size(120, 32);
            ok.Click += delegate { SaveAndClose(); };

            var cancel = new Button();
            cancel.Text = "取消";
            cancel.Size = new Size(120, 32);
            cancel.Click += delegate { StopRecord(); DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(enable);
            Controls.Add(modeLabel);
            Controls.Add(modeCombo);
            Controls.Add(modeSeq);
            Controls.Add(comboPanel);
            Controls.Add(seqPanel);
            Controls.Add(preview);
            Controls.Add(ok);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
            FormClosed += delegate { StopRecord(); };

            UpdateState();
        }

        private void BuildComboPanel()
        {
            var label = new Label { Text = "修饰键：", AutoSize = true, Location = new Point(8, 10) };

            modCtrl.Text = "Ctrl";
            modCtrl.Location = new Point(76, 8);
            modCtrl.Size = new Size(58, 24);
            modCtrl.Checked = (settings.HotkeyModifiers & VirtualKeys.MOD_CONTROL) != 0;

            modAlt.Text = "Alt";
            modAlt.Location = new Point(142, 8);
            modAlt.Size = new Size(52, 24);
            modAlt.Checked = (settings.HotkeyModifiers & VirtualKeys.MOD_ALT) != 0;

            modShift.Text = "Shift";
            modShift.Location = new Point(202, 8);
            modShift.Size = new Size(60, 24);
            modShift.Checked = (settings.HotkeyModifiers & VirtualKeys.MOD_SHIFT) != 0;

            modWin.Text = "Win";
            modWin.Location = new Point(270, 8);
            modWin.Size = new Size(56, 24);
            modWin.Checked = (settings.HotkeyModifiers & VirtualKeys.MOD_WIN) != 0;

            var keyLabel = new Label { Text = "按键：", AutoSize = true, Location = new Point(8, 46) };

            comboKey.DropDownStyle = ComboBoxStyle.DropDownList;
            comboKey.Location = new Point(76, 42);
            comboKey.Size = new Size(180, 24);
            BindKeys(comboKey, settings.HotkeyKey);

            var hint = new Label
            {
                Text = "至少勾选一个修饰键。只监听、不占用，其他软件的快捷键不受影响。",
                ForeColor = Color.Gray,
                AutoSize = true,
                Location = new Point(76, 78)
            };

            comboPanel.Controls.Add(label);
            comboPanel.Controls.Add(modCtrl);
            comboPanel.Controls.Add(modAlt);
            comboPanel.Controls.Add(modShift);
            comboPanel.Controls.Add(modWin);
            comboPanel.Controls.Add(keyLabel);
            comboPanel.Controls.Add(comboKey);
            comboPanel.Controls.Add(hint);
        }

        private void BuildSequencePanel()
        {
            var recordLabel = new Label { Text = "录制：", AutoSize = true, Location = new Point(8, 10) };

            recordButton.Text = "● 录制按键";
            recordButton.Location = new Point(76, 6);
            recordButton.Size = new Size(120, 30);
            recordButton.Click += delegate { if (recording) StopRecord(); else StartRecord(); };

            var undo = new Button { Text = "撤销一步", Location = new Point(206, 6), Size = new Size(88, 30) };
            undo.Click += delegate { if (sequence.Count > 0) { sequence.RemoveAt(sequence.Count - 1); UpdateSequenceLabel(); } };

            var clear = new Button { Text = "清空", Location = new Point(302, 6), Size = new Size(70, 30) };
            clear.Click += delegate { sequence.Clear(); UpdateSequenceLabel(); };

            var clearHint = new Label
            {
                Text = "点击“录制按键”后按下你想要的键（可混按不同键、可重复），Esc 或再点一次结束。",
                ForeColor = Color.Gray, AutoSize = true, Location = new Point(8, 42)
            };

            sequenceLabel.Location = new Point(8, 68);
            sequenceLabel.Size = new Size(512, 44);
            sequenceLabel.Font = new Font(sequenceLabel.Font.FontFamily, sequenceLabel.Font.Size, FontStyle.Bold);

            var addLabel = new Label { Text = "手动添加：", AutoSize = true, Location = new Point(8, 122) };

            addKey.DropDownStyle = ComboBoxStyle.DropDownList;
            addKey.Location = new Point(86, 118);
            addKey.Size = new Size(150, 24);
            BindKeys(addKey, settings.HotkeyKey);

            var add = new Button { Text = "添加", Location = new Point(246, 116), Size = new Size(70, 28) };
            add.Click += delegate
            {
                sequence.Add(SelectedKey(addKey));
                UpdateSequenceLabel();
            };

            var windowLabel = new Label { Text = "时间窗口：", AutoSize = true, Location = new Point(8, 158) };
            window.Location = new Point(86, 154);
            window.Size = new Size(90, 24);
            window.Minimum = 200;
            window.Maximum = 5000;
            window.Increment = 100;
            window.Value = Math.Max(200, Math.Min(5000, settings.SequenceWindowMs));

            var msLabel = new Label { Text = "毫秒（整段序列需在此时间内完成）", AutoSize = true, Location = new Point(186, 158) };

            var hint = new Label
            {
                Text = "只监听、不占用：其他软件的快捷键照常生效，按下你设置的序列时本工具才会弹出。",
                ForeColor = Color.Gray, AutoSize = true, Location = new Point(8, 190)
            };

            seqPanel.Controls.Add(recordLabel);
            seqPanel.Controls.Add(recordButton);
            seqPanel.Controls.Add(undo);
            seqPanel.Controls.Add(clear);
            seqPanel.Controls.Add(clearHint);
            seqPanel.Controls.Add(sequenceLabel);
            seqPanel.Controls.Add(addLabel);
            seqPanel.Controls.Add(addKey);
            seqPanel.Controls.Add(add);
            seqPanel.Controls.Add(windowLabel);
            seqPanel.Controls.Add(window);
            seqPanel.Controls.Add(msLabel);
            seqPanel.Controls.Add(hint);

            UpdateSequenceLabel();
        }

        private void StartRecord()
        {
            recording = true;
            recordButton.Text = "■ 停止录制";
            if (recordHook == null)
            {
                recordHook = new KeyboardHook();
                recordHook.KeyDown += delegate(int vk)
                {
                    if (vk == VirtualKeys.VK_ESCAPE) { StopRecord(); return; }
                    if (VirtualKeys.IsModifier(vk)) return;
                    sequence.Add(vk);
                    UpdateSequenceLabel();
                };
            }
            if (!recordHook.Installed) recordHook.Install();
            sequenceLabel.Text = "正在录制… 请按键（Esc 结束）";
        }

        private void StopRecord()
        {
            if (!recording && recordHook == null) return;
            recording = false;
            recordButton.Text = "● 录制按键";
            if (recordHook != null) recordHook.Uninstall();
            UpdateSequenceLabel();
        }

        private void UpdateSequenceLabel()
        {
            sequenceLabel.Text = "当前序列：" + DescribeSequence();
        }

        private string DescribeSequence()
        {
            if (sequence.Count == 0) return "（尚未设置）";
            var parts = new List<string>();
            int i = 0;
            while (i < sequence.Count)
            {
                int j = i + 1;
                while (j < sequence.Count && sequence[j] == sequence[i]) j++;
                int count = j - i;
                string name = VirtualKeys.Name(sequence[i]);
                parts.Add(count > 1 ? name + " ×" + count : name);
                i = j;
            }
            return string.Join(" → ", parts.ToArray());
        }

        private void UpdateState()
        {
            bool enabled = enable.Checked;
            modeCombo.Enabled = enabled;
            modeSeq.Enabled = enabled;
            comboPanel.Visible = enabled && modeCombo.Checked;
            seqPanel.Visible = enabled && modeSeq.Checked;

            // Lay the whole dialog out here so there is no blank area in either mode.
            enable.Location = new Point(16, 14);
            enable.Size = new Size(360, 24);

            modeLabel.Location = new Point(16, 48);
            modeCombo.Location = new Point(96, 46);
            modeCombo.Size = new Size(210, 24);
            modeSeq.Location = new Point(316, 46);
            modeSeq.Size = new Size(230, 24);

            comboPanel.Location = new Point(16, 78);
            comboPanel.Size = new Size(528, 132);
            seqPanel.Location = new Point(16, 78);
            seqPanel.Size = new Size(528, 224);

            int height = modeCombo.Checked ? 336 : 424;
            ClientSize = new Size(560, height);

            preview.Location = new Point(16, height - 108);
            preview.Size = new Size(528, 48);
            preview.Text = "当前：" + Describe();

            foreach (Control c in Controls)
            {
                if (c is Button && c.Text == "确定") { c.Left = 300; c.Top = height - 46; }
                if (c is Button && c.Text == "取消") { c.Left = 432; c.Top = height - 46; }
            }
        }

        private string Describe()
        {
            if (!enable.Checked) return "未启用";
            if (modeCombo.Checked)
            {
                var parts = new List<string>();
                if (modCtrl.Checked) parts.Add("Ctrl");
                if (modAlt.Checked) parts.Add("Alt");
                if (modShift.Checked) parts.Add("Shift");
                if (modWin.Checked) parts.Add("Win");
                if (parts.Count == 0) return "请至少选择一个修饰键";
                return string.Join(" + ", parts.ToArray()) + " + " + VirtualKeys.Name(SelectedKey(comboKey));
            }
            return DescribeSequence() + "（" + window.Value + " 毫秒内）";
        }

        private static void BindKeys(ComboBox combo, int vk)
        {
            combo.BeginUpdate();
            combo.Items.Clear();
            int select = -1;
            var choices = VirtualKeys.KeyChoices;
            for (int i = 0; i < choices.Count; i++)
            {
                combo.Items.Add(new KeyItem(choices[i].Key, choices[i].Value));
                if (choices[i].Value == vk) select = i;
            }
            combo.EndUpdate();
            combo.SelectedIndex = select < 0 ? 0 : select;
        }

        private static int SelectedKey(ComboBox combo)
        {
            var item = combo.SelectedItem as KeyItem;
            return item == null ? 0 : item.Vk;
        }

        private sealed class KeyItem
        {
            public KeyItem(string name, int vk) { Name = name; Vk = vk; }
            public string Name { get; private set; }
            public int Vk { get; private set; }
            public override string ToString() { return Name; }
        }

        private void SaveAndClose()
        {
            // 已隐藏本工具托盘时，快捷键是唯一的逃生通道，不允许关闭。
            if (!enable.Checked && settings.HideSelfIcon)
            {
                MessageBox.Show(this,
                    "已隐藏本工具托盘，不能关闭快捷键。\n\n" +
                    "否则窗口一旦隐藏将无法找回工具。请先恢复托盘图标（取消「选项 → 隐藏本工具托盘」），再关闭快捷键。",
                    "快捷键设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            settings.HotkeyEnabled = enable.Checked;
            settings.HotkeyMode = modeCombo.Checked ? 0 : 1;

            if (modeCombo.Checked)
            {
                uint modifiers = 0;
                if (modCtrl.Checked) modifiers |= VirtualKeys.MOD_CONTROL;
                if (modAlt.Checked) modifiers |= VirtualKeys.MOD_ALT;
                if (modShift.Checked) modifiers |= VirtualKeys.MOD_SHIFT;
                if (modWin.Checked) modifiers |= VirtualKeys.MOD_WIN;
                if (modifiers == 0)
                {
                    MessageBox.Show(this, "请至少勾选一个修饰键（Ctrl / Alt / Shift / Win）。",
                        "快捷键设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                settings.HotkeyModifiers = modifiers;
                settings.HotkeyKey = SelectedKey(comboKey);
            }
            else
            {
                StopRecord();
                if (sequence.Count == 0)
                {
                    MessageBox.Show(this, "请先录制或手动添加按键序列。",
                        "快捷键设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                settings.Sequence = new List<int>(sequence);
                settings.SequenceWindowMs = (int)window.Value;
            }

            try { SettingsStore.Save(settings); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存设置失败：" + ex.Message, "快捷键设置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
