using System.IO;
using System.Windows;
using StandingsOverlay.Config;
using StandingsOverlay.Data;
using StandingsOverlay.UI;

namespace StandingsOverlay;

public partial class App : Application
{
    private ConfigService? _configService;
    private ITelemetrySource? _source;
    private OverlayWindow? _window;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        _configService = new ConfigService(configPath);

        // --demo [race|qual|practice]
        int demoIdx = Array.FindIndex(e.Args, a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        bool demo = demoIdx >= 0;
        string demoSession = demoIdx >= 0 && demoIdx + 1 < e.Args.Length
            ? e.Args[demoIdx + 1].ToLowerInvariant() switch
            {
                "qual" or "quali" or "qualify" => "Lone Qualify",
                "practice" => "Practice",
                _ => "Race",
            }
            : "Race";

        _source = demo
            ? new DemoSource(() => _configService.Current, demoSession)
            : new IRacingSource(() => _configService.Current);

        _window = new OverlayWindow(_configService);
        _tray = new TrayIcon(demo);

        _source.SnapshotReady += snapshot =>
        {
            _window.OnSnapshot(snapshot);
            var status = snapshot.Connected ? "connected" : "waiting for iRacing";
            Dispatcher.BeginInvoke(() => _tray?.SetStatus(demo ? "demo" : status));
        };
        _window.Show();
        _source.Start();

        _tray.EditModeToggled += on => Dispatcher.BeginInvoke(() => _window.EditMode = on);
        _tray.ExitRequested += () => Dispatcher.BeginInvoke(() => Shutdown());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _source?.Dispose();
        _tray?.Dispose();
        _configService?.Dispose();
        base.OnExit(e);
    }
}
