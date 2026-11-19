using System;
using System.Collections.Generic;

namespace TrayHider
{
    /// <summary>Virtual-key constants, display names and the key list used by the hotkey panel.</summary>
    internal static class VirtualKeys
    {
        public const int VK_ESCAPE = 0x1B;
        public const int VK_NUMLOCK = 0x90;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        private static readonly Dictionary<int, string> Names = BuildNames();
        private static readonly List<KeyValuePair<string, int>> Choices = BuildChoices();

        private static Dictionary<int, string> BuildNames()
        {
            var d = new Dictionary<int, string>
            {
                { 0x08, "Backspace" }, { 0x09, "Tab" }, { 0x0C, "Clear" }, { 0x0D, "Enter" },
                { 0x10, "Shift" }, { 0x11, "Ctrl" }, { 0x12, "Alt" }, { 0x13, "Pause" },
                { 0x14, "CapsLock" }, { 0x1B, "Esc" }, { 0x20, "Space" }, { 0x21, "PageUp" },
                { 0x22, "PageDown" }, { 0x23, "End" }, { 0x24, "Home" }, { 0x25, "左方向键" },
                { 0x26, "上方向键" }, { 0x27, "右方向键" }, { 0x28, "下方向键" }, { 0x2C, "PrintScreen" },
                { 0x2D, "Insert" }, { 0x2E, "Delete" }, { 0x5B, "左 Win" }, { 0x5C, "右 Win" },
                { 0x6A, "小键盘 *" }, { 0x6B, "小键盘 +" }, { 0x6C, "小键盘 分隔" }, { 0x6D, "小键盘 -" },
                { 0x6E, "小键盘 ." }, { 0x6F, "小键盘 /" }, { 0x90, "NumLock" }, { 0x91, "ScrollLock" },
                { 0xA0, "左 Shift" }, { 0xA1, "右 Shift" }, { 0xA2, "左 Ctrl" }, { 0xA3, "右 Ctrl" },
                { 0xA4, "左 Alt" }, { 0xA5, "右 Alt" }
            };
            for (int vk = 0x30; vk <= 0x39; vk++) d[vk] = "数字 " + (char)(vk - 0x30 + '0');
            for (int vk = 0x41; vk <= 0x5A; vk++) d[vk] = ((char)vk).ToString();
            for (int vk = 0x60; vk <= 0x69; vk++) d[vk] = "小键盘 " + (vk - 0x60);
            for (int vk = 0x70; vk <= 0x87; vk++) d[vk] = "F" + (vk - 0x70 + 1);
            return d;
        }

        private static List<KeyValuePair<string, int>> BuildChoices()
        {
            var list = new List<KeyValuePair<string, int>>();
            for (int vk = 0x41; vk <= 0x5A; vk++) list.Add(new KeyValuePair<string, int>(((char)vk).ToString(), vk));
            for (int vk = 0x30; vk <= 0x39; vk++) list.Add(new KeyValuePair<string, int>("数字 " + (char)(vk - 0x30 + '0'), vk));
            for (int vk = 0x70; vk <= 0x87; vk++) list.Add(new KeyValuePair<string, int>("F" + (vk - 0x70 + 1), vk));
            for (int vk = 0x60; vk <= 0x69; vk++) list.Add(new KeyValuePair<string, int>("小键盘 " + (vk - 0x60), vk));
            int[] extra = { VK_NUMLOCK, 0x91, 0x13, 0x14, 0x08, 0x09, 0x0D, VK_ESCAPE, 0x20,
                            0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C };
            foreach (int vk in extra) list.Add(new KeyValuePair<string, int>(Name(vk), vk));
            return list;
        }

        public static string Name(int vk)
        {
            string name;
            if (Names.TryGetValue(vk, out name)) return name;
            return "0x" + vk.ToString("X2");
        }

        public static List<KeyValuePair<string, int>> KeyChoices { get { return Choices; } }

        public static bool IsModifier(int vk)
        {
            return vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B || vk == 0x5C ||
                   vk == 0xA0 || vk == 0xA1 || vk == 0xA2 || vk == 0xA3 || vk == 0xA4 || vk == 0xA5;
        }
    }
}
