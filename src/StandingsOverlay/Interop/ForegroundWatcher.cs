using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StandingsOverlay.Interop;

/// <summary>
/// Watches which app owns the foreground window via a WinEvent hook (EVENT_SYSTEM_FOREGROUND) —
/// event-driven, no polling, in keeping with the overlay's "never touch the sim's frame time"
/// rule. Raises <see cref="ForegroundChanged"/> with true when the new foreground window belongs
/// to the iRacing sim or to us (so adjusting settings never blanks the overlay), false otherwise.
///
/// Install on the UI thread: WINEVENT_OUTOFCONTEXT delivers the callback on the installing
/// thread's message loop, so handlers may touch WPF directly.
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private readonly WinEventDelegate _callback;   // held so the GC never collects the thunk
    private IntPtr _hook;

    /// <summary>Raised (on the UI thread) with true when the iRacing sim or one of our own windows
    /// becomes the foreground app, false for anything else.</summary>
    public event Action<bool>? ForegroundChanged;

    public ForegroundWatcher() => _callback = OnForeground;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void OnForeground(IntPtr h, uint ev, IntPtr hwnd, int idObj, int idChild, uint thread, uint time)
        => ForegroundChanged?.Invoke(IsTarget(hwnd));

    /// <summary>The current state, for seeding at startup before the first foreground change.</summary>
    public static bool IsForegroundTarget() => IsTarget(GetForegroundWindow());

    private static bool IsTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;
        if (pid == (uint)Environment.ProcessId) return true;   // our own settings / overlay window
        try
        {
            using var p = Process.GetProcessById((int)pid);
            // iRacingSim64DX11 today; older builds were iRacingSim64DX9 / iRacingSim64.
            return p.ProcessName.StartsWith("iRacingSim", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Process gone between the hook firing and the lookup, or access denied: not iRacing.
            return false;
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
