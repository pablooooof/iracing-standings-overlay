using System.IO;
using System.Windows;
using StandingsOverlay.Config;
using StandingsOverlay.Data;
using StandingsOverlay.Interop;
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
    private FuelTableWindow? _fuelTableWindow;
    private LapLabWindow? _lapLabWindow;
    private TrafficAudio? _trafficAudio;
    private SettingsWindow? _settings;
    private bool _editMode;

    // Auto-hide: keep the widgets off screen unless iRacing is focused (data still flows), with a
    // system-wide hotkey to force show/hide. _overlays is every widget window EXCEPT the settings
    // window (which is the app's chrome and stays put).
    private readonly OverlayVisibility _visibility = new();
    private ForegroundWatcher? _foreground;
    private HotkeyService? _hotkey;
    private Window[] _overlays = [];

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
        Log.Write($"started (v{UpdateCheck.CurrentDisplay}) args: {string.Join(' ', e.Args)}");

        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        _configService = new ConfigService(configPath);

        // --demo [race|qual|practice|timed|rain|lab]
        int demoIdx = Array.FindIndex(e.Args, a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        bool demo = demoIdx >= 0;
        string demoArg = demoIdx >= 0 && demoIdx + 1 < e.Args.Length
            ? e.Args[demoIdx + 1].ToLowerInvariant() : "";
        string demoSession = demoArg switch
        {
            "qual" or "quali" or "qualify" => "Lone Qualify",
            "practice" => "Practice",
            "lab" or "test" or "testing" => "Offline Testing",
            _ => "Race",
        };
        bool demoTimed = demoArg is "timed" or "time";
        bool demoRain = demoArg is "rain" or "wet";

        _source = demo
            ? new DemoSource(() => _configService.Current, demoSession, demoTimed, demoRain)
            : new IRacingSource(() => _configService.Current);

        _window = new OverlayWindow(_configService);
        _trafficWindow = new TrafficWindow(_configService);
        _relativeWindow = new RelativeWindow(_configService);
        _fuelWindow = new FuelWindow(_configService);
        _fuelTableWindow = new FuelTableWindow(_configService);
        _lapLabWindow = new LapLabWindow(_configService);
        _trafficAudio = new TrafficAudio();

        // The app's one piece of chrome and its main window: a normal, taskbar-present control
        // panel. There is no tray icon — closing this window (confirmed) quits the whole app
        // (ShutdownMode=OnMainWindowClose). Created before the overlays are shown so WPF doesn't
        // pick a click-through tool-window as MainWindow.
        _settings = new SettingsWindow(_configService, _editMode);
        _settings.EditModeChanged += on => SetEditMode(on);
        MainWindow = _settings;

        _source.SnapshotReady += snapshot =>
        {
            _window.OnSnapshot(snapshot);
            var (text, connected) = demo ? ("Demo mode", true)
                : !snapshot.Connected ? ("Waiting for iRacing…", false)
                : _configService.Spectating ? ("Connected · spectating", true)
                : ("Connected", true);
            Dispatcher.BeginInvoke(() => _settings?.SetStatus(text, connected));
        };
        _source.TrafficReady += traffic =>
        {
            _trafficWindow.OnTraffic(traffic);
            _trafficAudio.Handle(traffic.Cues, _configService.Current.Traffic.Audio);
        };
        _source.RelativeReady += relative => _relativeWindow.OnRelative(relative);
        _source.FuelReady += fuel => _fuelWindow.OnFuel(fuel);
        _source.FuelTableReady += ft => _fuelTableWindow.OnFuelTable(ft);
        _source.LapLabReady += lab => _lapLabWindow.OnLapLab(lab);

        // In the car vs spectating (team stints, garage): swap the whole config profile so
        // positions, row counts and columns can differ. The source debounces IsOnTrack.
        if (_source is IRacingSource live)
            live.DrivingChanged += driving =>
                Dispatcher.BeginInvoke(() => _configService.SetSpectating(!driving));

        _window.Show();
        _trafficWindow.Show();
        _relativeWindow.Show();
        _fuelWindow.Show();
        _fuelTableWindow.Show();
        _lapLabWindow.Show();
        _settings.Show();
        _source.Start();

        SetupAutoHide(demo);

        // Update check: notify + link only, the user does the downloading.
        // One request per launch, silent on any failure.
        if (_configService.Current.CheckForUpdates)
            UpdateCheck.Run((tag, url) =>
                Dispatcher.BeginInvoke(() => _settings?.ShowUpdateAvailable(tag, url)));
    }

    /// <summary>Wire up auto-hide: the foreground watcher, the force show/hide hotkey, and the
    /// initial visibility. Demo mode pins the overlays visible so testing never blanks the screen.</summary>
    private void SetupAutoHide(bool demo)
    {
        _overlays = [_window!, _trafficWindow!, _relativeWindow!, _fuelWindow!, _fuelTableWindow!, _lapLabWindow!];

        var cfg = _configService!.Current;
        _visibility.Demo = demo;
        _visibility.EditMode = _editMode;
        _visibility.AutoHideEnabled = cfg.AutoHide.Enabled;
        _visibility.IracingFocused = ForegroundWatcher.IsForegroundTarget();

        _foreground = new ForegroundWatcher();
        _foreground.ForegroundChanged += focused =>
        {
            _visibility.IracingFocused = focused;
            ApplyOverlayVisibility();
        };
        _foreground.Start();

        _hotkey = new HotkeyService();
        _hotkey.Pressed += () =>
        {
            _visibility.ToggleOverride();
            ApplyOverlayVisibility();
            Log.Write($"auto-hide hotkey: overlays {( _visibility.Resolve() ? "shown" : "hidden")}");
        };
        ApplyHotkeyConfig(cfg);

        // React to config edits (settings window or external file): re-read the toggle + re-bind
        // the hotkey. Changed can arrive on the file-watcher thread, so hop to the UI thread.
        _configService.Changed += c => Dispatcher.BeginInvoke(() =>
        {
            _visibility.AutoHideEnabled = c.AutoHide.Enabled;
            ApplyHotkeyConfig(c);
            ApplyOverlayVisibility();
        });

        ApplyOverlayVisibility();
    }

    private void ApplyHotkeyConfig(OverlayConfig cfg)
    {
        if (cfg.AutoHide.HotkeyEnabled) _hotkey?.Register(cfg.AutoHide.Hotkey);
        else _hotkey?.Unregister();
    }

    /// <summary>Push the resolved show/hide decision to every widget window (never the settings
    /// window). Cheap and idempotent — safe to call on any input change.</summary>
    private void ApplyOverlayVisibility()
    {
        var vis = _visibility.Resolve() ? Visibility.Visible : Visibility.Hidden;
        foreach (var w in _overlays)
            if (w.Visibility != vis) w.Visibility = vis;
    }

    /// <summary>Single source of truth for "move overlays" mode: the settings toggle routes here,
    /// and it mirrors the resulting state back to the settings switch.</summary>
    private void SetEditMode(bool on)
    {
        if (_editMode == on) return;   // idempotent: the mirror below re-enters this harmlessly
        _editMode = on;
        _window!.EditMode = on;
        _trafficWindow!.EditMode = on;
        _relativeWindow!.EditMode = on;
        _fuelWindow!.EditMode = on;
        _fuelTableWindow!.EditMode = on;
        _lapLabWindow!.EditMode = on;
        _settings?.ReflectEditMode(on);
        // Edit mode force-shows every widget — you can't drag one you can't see.
        _visibility.EditMode = on;
        ApplyOverlayVisibility();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _foreground?.Dispose();
        _hotkey?.Dispose();
        _source?.Dispose();
        _trafficAudio?.Dispose();
        _configService?.Dispose();
        base.OnExit(e);
    }
}
