using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace StandingsOverlay;

/// <summary>
/// Launch-time update check: ONE GET to GitHub's latest-release endpoint, then done — never
/// polls, never downloads. If a newer tag exists the tray menu and the About page show a link
/// to the release page; installing is the user's click. Toggled by <c>CheckForUpdates</c>.
/// </summary>
public static class UpdateCheck
{
    private const string Owner = "pablooooof";
    private const string Repo = "iracing-standings-overlay";

    /// <summary>Running version for display ("0.2.1-alpha.0.5"), commit metadata stripped.
    /// MinVer stamps the informational version from git tags; AssemblyVersion is major-only.</summary>
    public static string CurrentDisplay { get; } = ComputeDisplay();

    /// <summary>Set once a newer release is found; the About page reads it when (re)built.</summary>
    public static (string Tag, string Url)? Latest { get; private set; }

    private static string ComputeDisplay()
    {
        var info = typeof(UpdateCheck).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return "0.0.0";
        int plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }

    /// <summary>Numeric x.y.z of a version/tag, pre-release suffix and v-prefix dropped.
    /// A dev build "0.2.1-alpha.0.3" compares as 0.2.1, so the 0.2.0 release won't nag it.</summary>
    private static Version? BaseOf(string v)
    {
        v = v.TrimStart('v', 'V');
        int dash = v.IndexOf('-');
        if (dash > 0) v = v[..dash];
        return Version.TryParse(v, out var parsed)
            ? new Version(parsed.Major, Math.Max(parsed.Minor, 0), Math.Max(parsed.Build, 0))
            : null;
    }

    /// <summary>Fire-and-forget. Invokes <paramref name="onUpdate"/> (worker thread) with the
    /// newer release's tag + page URL, or never. Failures are logged, never surfaced — offline
    /// or rate-limited must not cost the user anything.</summary>
    public static void Run(Action<string, string> onUpdate)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.Add(
                    new ProductInfoHeaderValue("StandingsOverlay", CurrentDisplay));
                http.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                var resp = await http.GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
                if (!resp.IsSuccessStatusCode)
                {
                    Log.Write($"update check: HTTP {(int)resp.StatusCode} (no releases yet?)");
                    return;
                }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                string? tag = doc.RootElement.GetProperty("tag_name").GetString();
                string url = doc.RootElement.TryGetProperty("html_url", out var u)
                    ? u.GetString() ?? $"https://github.com/{Owner}/{Repo}/releases"
                    : $"https://github.com/{Owner}/{Repo}/releases";
                var latest = tag is null ? null : BaseOf(tag);
                if (latest is null)
                {
                    Log.Write($"update check: unparseable tag '{tag}'");
                    return;
                }
                var current = BaseOf(CurrentDisplay) ?? new Version(0, 0, 0);
                Log.Write($"update check: running {CurrentDisplay}, latest release {tag}");
                if (latest > current)
                {
                    Latest = (tag!, url);
                    onUpdate(tag!, url);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"update check failed: {ex.Message}");
            }
        });
    }

    public static void OpenReleasePage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open release page", ex); }
    }
}
