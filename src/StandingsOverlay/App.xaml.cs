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

        bool demo = e.Args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        _source = demo
            ? new DemoSource(() => _configService.Current)
            : new IRacingSource(() => _configService.Current);

        _window = new OverlayWindow(_configService);
        _source.SnapshotReady += _window.OnSnapshot;
        _window.Show();
        _source.Start();

        _tray = new TrayIcon(demo);
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
