using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace StandingsOverlay.Interop;

/// <summary>
/// Registers a single system-wide hotkey (default Ctrl+Alt+H) to force-show / force-hide the
/// overlays regardless of which app is focused. Uses a message-only window as the RegisterHotKey
/// host so nothing shows in the taskbar. Combo strings are parsed from config ("Ctrl+Alt+H",
/// "Shift+F8", "Win+O"); create and register on the UI thread.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;   // one message per press, not autorepeat
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private const int HotkeyId = 0x4841;        // 'HA' — arbitrary, unique within our window

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private bool _registered;
    private string _combo = "";

    /// <summary>Raised (on the UI thread) each time the hotkey is pressed.</summary>
    public event Action? Pressed;

    /// <summary>Register the given combo, replacing any previous one. Returns true on success;
    /// idempotent when the combo hasn't changed. A blank/invalid combo just unregisters.</summary>
    public bool Register(string combo)
    {
        combo = combo?.Trim() ?? "";
        if (_registered && combo == _combo) return true;
        Unregister();
        _combo = combo;
        if (!TryParse(combo, out uint mods, out uint vk))
        {
            if (combo.Length > 0) Log.Write($"hotkey: could not parse '{combo}'");
            return false;
        }
        _source ??= CreateMessageWindow();
        _registered = RegisterHotKey(_source.Handle, HotkeyId, mods | MOD_NOREPEAT, vk);
        if (!_registered) Log.Write($"hotkey: RegisterHotKey failed for '{combo}' (already in use?)");
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _source is not null) UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private HwndSource CreateMessageWindow()
    {
        var src = new HwndSource(new HwndSourceParameters("StandingsOverlayHotkey")
        {
            ParentWindow = HWND_MESSAGE,   // message-only: never visible, no taskbar entry
        });
        src.AddHook(WndProc);
        return src;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Parse "Ctrl+Alt+H" style combos into RegisterHotKey modifier flags + virtual key.</summary>
    private static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;
        foreach (var token in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": case "windows": case "meta": mods |= MOD_WIN; break;
                default:
                    if (vk != 0) return false;   // more than one non-modifier key
                    if (!Enum.TryParse<Key>(NormalizeKey(token), ignoreCase: true, out var key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    if (vk == 0) return false;
                    break;
            }
        }
        return vk != 0;
    }

    // WPF's Key enum names digits D0..D9; accept a bare "1" too.
    private static string NormalizeKey(string k) =>
        k.Length == 1 && char.IsDigit(k[0]) ? "D" + k : k;

    public void Dispose()
    {
        Unregister();
        _source?.Dispose();
        _source = null;
    }
}
