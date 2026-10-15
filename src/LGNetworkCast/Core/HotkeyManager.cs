using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace LGNetworkCast.Core
{
    /// <summary>System-wide hotkeys via RegisterHotKey on a message-only window.</summary>
    public sealed class HotkeyManager : IDisposable
    {
        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

        readonly HwndSource _source;
        readonly Dictionary<int, Action> _actions = new Dictionary<int, Action>();
        int _nextId = 1;

        public HotkeyManager()
        {
            _source = new HwndSource(new HwndSourceParameters("LGNetworkCastHotkeys") { ParentWindow = new IntPtr(-3) }); // HWND_MESSAGE
            _source.AddHook(WndProc);
        }

        /// <summary>Registers a hotkey like "Ctrl+Alt+K". Returns false if the text is invalid or the combination is taken.</summary>
        public bool Register(string text, Action action)
        {
            if (!TryParse(text, out var mods, out var key)) return false;
            var id = _nextId++;
            if (!RegisterHotKey(_source.Handle, id, mods | MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(key)))
            {
                Log.Info($"Hotkey {text} is already used by Windows or another app");
                return false;
            }
            _actions[id] = action;
            return true;
        }

        public void UnregisterAll()
        {
            foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
            _actions.Clear();
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
            {
                handled = true;
                action();
            }
            return IntPtr.Zero;
        }

        public static bool TryParse(string text, out uint mods, out Key key)
        {
            mods = 0;
            key = Key.None;
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var raw in text.Split('+').Select(p => p.Trim()))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= MOD_CONTROL; break;
                    case "alt": mods |= MOD_ALT; break;
                    case "shift": mods |= MOD_SHIFT; break;
                    case "win": case "windows": mods |= MOD_WIN; break;
                    default:
                        var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                        if (!Enum.TryParse(name, true, out key)) return false;
                        break;
                }
            }
            return key != Key.None;
        }

        /// <summary>Formats a key press for display/storage, e.g. "Ctrl+Shift+F9".</summary>
        public static string Format(ModifierKeys mods, Key key)
        {
            var parts = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            var k = key.ToString();
            if (k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1])) k = k.Substring(1);
            parts.Add(k);
            return string.Join("+", parts);
        }

        public void Dispose()
        {
            UnregisterAll();
            _source.Dispose();
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
