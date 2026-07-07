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
    private TrafficWindow? _trafficWindow;
    private RelativeWindow? _relativeWindow;
    private FuelWindow? _fuelWindow;
    private TrafficAudio? _trafficAudio;
    private TrayIcon? _tray;
    private SettingsWindow? _settings;
    private bool _editMode;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // An overlay must never die mid-race because one frame failed to render:
        // log UI-thread exceptions and keep going. Everything else gets logged on the way down.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("dispatcher", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("appdomain", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("task", args.Exception);
            args.SetObserved();
        };
        Log.Write($"started (v{typeof(App).Assembly.GetName().Version}) args: {string.Join(' ', e.Args)}");

        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        _configService = new ConfigService(configPath);

        // --demo [race|qual|practice|timed]
        int demoIdx = Array.FindIndex(e.Args, a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        bool demo = demoIdx >= 0;
        string demoArg = demoIdx >= 0 && demoIdx + 1 < e.Args.Length
            ? e.Args[demoIdx + 1].ToLowerInvariant() : "";
        string demoSession = demoArg switch
        {
            "qual" or "quali" or "qualify" => "Lone Qualify",
            "practice" => "Practice",
            _ => "Race",
        };
        bool demoTimed = demoArg is "timed" or "time";

        _source = demo
            ? new DemoSource(() => _configService.Current, demoSession, demoTimed)
            : new IRacingSource(() => _configService.Current);

        _window = new OverlayWindow(_configService);
        _trafficWindow = new TrafficWindow(_configService);
        _relativeWindow = new RelativeWindow(_configService);
        _fuelWindow = new FuelWindow(_configService);
        _trafficAudio = new TrafficAudio();
        _tray = new TrayIcon(demo);

        _source.SnapshotReady += snapshot =>
        {
            _window.OnSnapshot(snapshot);
            var status = snapshot.Connected ? "connected" : "waiting for iRacing";
            Dispatcher.BeginInvoke(() => _tray?.SetStatus(demo ? "demo" : status));
        };
        _source.TrafficReady += traffic =>
        {
            _trafficWindow.OnTraffic(traffic);
            _trafficAudio.Handle(traffic.Cues, _configService.Current.Traffic.Audio);
        };
        _source.RelativeReady += relative => _relativeWindow.OnRelative(relative);
        _source.FuelReady += fuel => _fuelWindow.OnFuel(fuel);
        _window.Show();
        _trafficWindow.Show();
        _relativeWindow.Show();
        _fuelWindow.Show();
        _source.Start();

        _tray.EditModeToggled += on => Dispatcher.BeginInvoke(() => SetEditMode(on));
        _tray.SettingsRequested += () => Dispatcher.BeginInvoke(ShowSettings);
        _tray.ExitRequested += () => Dispatcher.BeginInvoke(() => Shutdown());

        // --settings opens the settings window on launch (useful as a desktop shortcut target).
        if (e.Args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
            Dispatcher.BeginInvoke(ShowSettings);
    }

    /// <summary>Single source of truth for "move overlays" mode: both the tray checkbox and the
    /// settings toggle route here, and it mirrors the resulting state back to whichever UI is open.</summary>
    private void SetEditMode(bool on)
    {
        if (_editMode == on) return;   // idempotent: the mirrors below re-enter this harmlessly
        _editMode = on;
        _window!.EditMode = on;
        _trafficWindow!.EditMode = on;
        _relativeWindow!.EditMode = on;
        _fuelWindow!.EditMode = on;
        _tray?.ReflectEditMode(on);
        _settings?.ReflectEditMode(on);
    }

    private void ShowSettings()
    {
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsWindow(_configService!, _editMode);
        _settings.EditModeChanged += on => SetEditMode(on);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _source?.Dispose();
        _tray?.Dispose();
        _trafficAudio?.Dispose();
        _configService?.Dispose();
        base.OnExit(e);
    }
}
