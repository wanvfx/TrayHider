using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TrayHider
{
    /// <summary>
    /// Passive global hotkey engine. It never blocks keys, so other applications keep
    /// receiving them exactly as before; the tool only reacts to the configured trigger.
    ///   Mode 0: modifiers + key
    ///   Mode 1: an ordered key sequence (any keys, any counts)
    /// </summary>
    internal sealed class HotkeyEngine : IDisposable
    {
        public event Action Triggered;

        public bool Enabled;
        public int Mode;
        public uint Modifiers;
        public int Key;
        public List<int> Sequence = new List<int>();
        public int WindowMs = 800;

        private KeyboardHook hook;
        private int ctrlCount, altCount, shiftCount, winCount;
        private readonly List<int> buffer = new List<int>();
        private readonly List<long> stamps = new List<long>();
        private bool disposed;

        public void Start()
        {
            if (hook == null)
            {
                hook = new KeyboardHook();
                hook.KeyDown += OnKeyDown;
                hook.KeyUp += OnKeyUp;
            }
            if (!hook.Installed) hook.Install();
        }

        public void Stop()
        {
            if (hook != null) hook.Uninstall();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (hook != null) { hook.Dispose(); hook = null; }
        }

        private void OnKeyUp(int vk) { UpdateModifiers(vk, true); }

        private void OnKeyDown(int vk)
        {
            UpdateModifiers(vk, false);
            if (!Enabled) return;

            if (Mode == 0)
            {
                if (vk != Key || !ModifiersMatch()) return;
                Fire();
                return;
            }

            if (Sequence == null || Sequence.Count == 0) return;
            long now = Stopwatch.GetTimestamp();
            if (stamps.Count > 0 &&
                (now - stamps[0]) * 1000L / Stopwatch.Frequency > WindowMs)
            {
                buffer.Clear();
                stamps.Clear();
            }
            buffer.Add(vk);
            stamps.Add(now);
            while (buffer.Count > Sequence.Count) { buffer.RemoveAt(0); stamps.RemoveAt(0); }
            if (buffer.Count != Sequence.Count) return;
            for (int i = 0; i < Sequence.Count; i++)
                if (buffer[i] != Sequence[i]) return;
            buffer.Clear();
            stamps.Clear();
            Fire();
        }

        private bool ModifiersMatch()
        {
            if ((Modifiers & VirtualKeys.MOD_CONTROL) != 0 && ctrlCount == 0) return false;
            if ((Modifiers & VirtualKeys.MOD_ALT) != 0 && altCount == 0) return false;
            if ((Modifiers & VirtualKeys.MOD_SHIFT) != 0 && shiftCount == 0) return false;
            if ((Modifiers & VirtualKeys.MOD_WIN) != 0 && winCount == 0) return false;
            return true;
        }

        private void UpdateModifiers(int vk, bool up)
        {
            int delta = up ? -1 : 1;
            switch (vk)
            {
                case 0x11: case 0xA2: case 0xA3: ctrlCount += delta; break;
                case 0x12: case 0xA4: case 0xA5: altCount += delta; break;
                case 0x10: case 0xA0: case 0xA1: shiftCount += delta; break;
                case 0x5B: case 0x5C: winCount += delta; break;
            }
            if (ctrlCount < 0) ctrlCount = 0;
            if (altCount < 0) altCount = 0;
            if (shiftCount < 0) shiftCount = 0;
            if (winCount < 0) winCount = 0;
        }

        private void Fire() { if (Triggered != null) Triggered(); }

        /// <summary>Test hook: feeds a key through exactly the same path as the keyboard hook.</summary>
        internal void SimulateKey(int vk) { OnKeyDown(vk); }
    }
}
