using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TrayHider
{
    /// <summary>
    /// Low-level keyboard hook used for the "press a key N times" trigger.
    /// NumLock / F10 and friends cannot be registered with RegisterHotKey.
    /// </summary>
    internal sealed class KeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        public event Action<int> KeyDown;
        public event Action<int> KeyUp;

        private readonly LowLevelKeyboardProc proc;
        private readonly Dictionary<int, bool> down = new Dictionary<int, bool>();
        private IntPtr hook = IntPtr.Zero;

        public KeyboardHook()
        {
            proc = Callback;
        }

        public bool Installed { get { return hook != IntPtr.Zero; } }

        public bool Install()
        {
            if (hook != IntPtr.Zero) return true;
            IntPtr module = GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName);
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, module, 0);
            return hook != IntPtr.Zero;
        }

        public void Uninstall()
        {
            if (hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
            lock (down) { down.Clear(); }
        }

        public void Dispose() { Uninstall(); }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                bool pressed = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                if (pressed || up)
                {
                    KBDLLHOOKSTRUCT info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    int vk = (int)info.vkCode;
                    if (up)
                    {
                        lock (down) { down[vk] = false; }
                        if (KeyUp != null) KeyUp(vk);
                    }
                    else
                    {
                        bool wasDown;
                        lock (down) { wasDown = down.ContainsKey(vk) && down[vk]; }
                        if (wasDown) return CallNextHookEx(hook, nCode, wParam, lParam);
                        lock (down) { down[vk] = true; }
                        if (KeyDown != null) KeyDown(vk);
                    }
                }
            }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
