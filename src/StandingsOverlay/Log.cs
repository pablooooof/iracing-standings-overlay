using System.IO;

namespace StandingsOverlay;

/// <summary>Minimal append-only log next to the exe. Errors and rare lifecycle events only —
/// never per-tick data, this is an overlay.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Path =
        System.IO.Path.Combine(AppContext.BaseDirectory, "overlay.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                // Keep the log from growing unbounded across long sessions.
                if (File.Exists(Path) && new FileInfo(Path).Length > 1_000_000)
                    File.Delete(Path);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Error(string context, Exception ex) =>
        Write($"ERROR [{context}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
}
