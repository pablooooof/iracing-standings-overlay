using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StandingsOverlay.Config;
using StandingsOverlay.Data;
using StandingsOverlay.Interop;

namespace StandingsOverlay.UI;

/// <summary>
/// The one piece of real chrome the app has: a normal (activatable, dark-title-bar) window that
/// edits <see cref="OverlayConfig"/> live. Every widget already re-applies on
/// <see cref="ConfigService.Changed"/>, so this just mutates <c>Current</c> and calls
/// <see cref="ConfigService.SaveAndNotify"/> (debounced). Rows are built from small descriptor
/// helpers — adding a setting is one line, and every control looks the same.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly Brush Text = RowViewModel.Frozen("#E8E9EE");
    private static readonly Brush Dim = RowViewModel.Frozen("#9DA0AA");
    private static readonly Brush Line = RowViewModel.Frozen("#2C2C36");
    private static readonly Brush Accent = RowViewModel.Frozen("#00E6C3");

    private readonly ConfigService _cfg;
    private readonly DispatcherTimer _saveTimer;
    private bool _dirty;

    // Edit ("move overlays") mode is owned by App; the General toggle mirrors and drives it.
    public event Action<bool>? EditModeChanged;
    private bool _editMode;
    private bool _suppressEdit;
    private CheckBox? _editToggle;

    private string _section = "General";

    public SettingsWindow(ConfigService cfg, bool editMode)
    {
        InitializeComponent();
        _cfg = cfg;
        _editMode = editMode;
        // Edits go to whichever profile is active when the window opens; make that visible.
        if (cfg.Spectating) Title += " — spectate profile";

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _saveTimer.Tick += (_, _) => Flush();

        // Section pages capture the profile objects in closures, so a profile swap (or an
        // external edit replacing Current) would leave the open page editing a stale instance —
        // rebuild the visible section against the new one. SaveAndNotify keeps the instance,
        // so ordinary in-app edits don't rebuild.
        _builtAgainst = cfg.Current;
        cfg.Changed += OnProfileMaybeSwapped;
        SourceInitialized += (_, _) => Win32.UseDarkTitleBar(this);
        Closed += (_, _) => { _cfg.Changed -= OnProfileMaybeSwapped; Flush(); };   // never drop a pending edit

        foreach (var name in new[] { "General", "Standings", "Relative", "Traffic", "Fuel", "Lap Lab", "About" })
            Nav.Items.Add(name);
        Nav.SelectedIndex = 0;
    }

    /// <summary>Reflect edit mode toggled elsewhere (the tray) into the General switch, no re-fire.</summary>
    public void ReflectEditMode(bool on)
    {
        _editMode = on;
        if (_editToggle is null) return;
        _suppressEdit = true;
        _editToggle.IsChecked = on;
        _suppressEdit = false;
    }

    // ---- change plumbing -------------------------------------------------

    private void Apply(Action mutate)
    {
        mutate();
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Flush()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        _dirty = false;
        _cfg.SaveAndNotify();
    }

    private OverlayConfig? _builtAgainst;

    private void OnProfileMaybeSwapped(OverlayConfig cfg)
    {
        if (ReferenceEquals(cfg, _builtAgainst)) return;
        Dispatcher.BeginInvoke(() =>
        {
            _builtAgainst = _cfg.Current;
            Title = "Standings Overlay — Settings" + (_cfg.Spectating ? " — spectate profile" : "");
            OnNavChanged(Nav, null!);   // rebuild the visible section against the new instance
        });
    }

    // ---- navigation ------------------------------------------------------

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not string name) return;
        _section = name;
        _editToggle = null;
        PageBody.Children.Clear();

        switch (name)
        {
            case "General": SectionHeader("General", "Refresh, startup, and how every widget looks."); BuildGeneral(); break;
            case "Standings": SectionHeader("Standings table", "The leaderboard: layout, header extras, and per-session columns."); BuildStandings(); break;
            case "Relative": SectionHeader("Relative box", "Cars physically around you, in track order."); BuildRelative(); break;
            case "Traffic": SectionHeader("Traffic alerter", "Warnings when a faster class is closing, or you're being lapped."); BuildTraffic(); break;
            case "Fuel": SectionHeader("Fuel & strategy", "Live fuel numbers and endurance stint planning."); BuildFuel(); break;
            case "Lap Lab": SectionHeader("Lap Lab", "Practice lap table: your sectors against a reference lap. Testing, practice and qualifying only."); BuildLapLab(); break;
            case "About": SectionHeader("About", "Standings Overlay."); BuildAbout(); break;
        }
    }

    private void SectionHeader(string title, string blurb)
    {
        SectionTitle.Text = title;
        SectionBlurb.Text = blurb;
    }

    // ---- section content -------------------------------------------------

    private void BuildGeneral()
    {
        PageBody.Children.Add(Toggle("Move overlays", "Unlock every widget so you can drag it into place. Turn off to lock.",
            () => _editMode, v =>
            {
                _editMode = v;
                if (!_suppressEdit) EditModeChanged?.Invoke(v);
            }, out var editCb, strong: true));
        _editToggle = editCb;

        PageBody.Children.Add(Toggle("Start with Windows", "Launch the overlay automatically when you log in.",
            AutoStart.IsEnabled, AutoStart.Set));

        var c = _cfg.Current;
        PageBody.Children.Add(Toggle("Check for updates at launch", "One request to GitHub for the latest release; a tray link appears if newer. Never downloads anything.",
            () => c.CheckForUpdates, v => c.CheckForUpdates = v));
        PageBody.Children.Add(Slider("Refresh rate", "Snapshots per second. Rendering still only happens on change.",
            1, 10, 1, () => c.UpdateHz, v => c.UpdateHz = (int)v, v => $"{v:0} Hz"));

        PageBody.Children.Add(Subhead("Appearance"));
        PageBody.Children.Add(Slider("Opacity", null, 0.1, 1.0, 0.05, () => c.Opacity, v => c.Opacity = v, v => $"{v * 100:0}%"));
        PageBody.Children.Add(Slider("Font size", null, 10, 24, 1, () => c.FontSize, v => c.FontSize = v, v => $"{v:0} px"));
        PageBody.Children.Add(ColorRow("Background", () => c.BackgroundColor, v => c.BackgroundColor = v));
        PageBody.Children.Add(ColorRow("Accent", () => c.AccentColor, v => c.AccentColor = v));
        PageBody.Children.Add(ColorRow("Player highlight", () => c.HighlightColor, v => c.HighlightColor = v));
    }

    private void BuildStandings()
    {
        var c = _cfg.Current;

        PageBody.Children.Add(Subhead("Layout"));
        PageBody.Children.Add(Slider("Size", "Scales the whole standings table.", 0.6, 2.0, 0.05,
            () => c.Scale, v => c.Scale = v, v => $"{v * 100:0}%"));
        PageBody.Children.Add(Slider("Leaders at top", "Always-shown positions from the front.", 0, 10, 1,
            () => c.DriversAtTop, v => c.DriversAtTop = (int)v, v => $"{v:0}"));
        PageBody.Children.Add(Slider("Cars ahead of you", null, 0, 15, 1,
            () => c.DriversAhead, v => c.DriversAhead = (int)v, v => $"{v:0}"));
        PageBody.Children.Add(Slider("Cars behind you", null, 0, 15, 1,
            () => c.DriversBehind, v => c.DriversBehind = (int)v, v => $"{v:0}"));
        PageBody.Children.Add(Slider("Show leaders when near front", "When your position is within this, show at least this many from the top. 0 = off.",
            0, 20, 1, () => c.MinLeadingCars, v => c.MinLeadingCars = (int)v, v => v <= 0 ? "off" : $"{v:0}"));
        PageBody.Children.Add(Slider("Other-class leaders", "Top cars of each other class in a multiclass field.", 0, 6, 1,
            () => c.OtherClassesDriversAtTop, v => c.OtherClassesDriversAtTop = (int)v, v => $"{v:0}"));
        PageBody.Children.Add(Toggle("Pin towed cars", "A towed class rival shows at its position until it leaves the pits.",
            () => c.PinTowedCars, v => c.PinTowedCars = v));
        PageBody.Children.Add(Slider("Delta laps", "Per-lap gap-change columns — the reason this overlay exists.", 1, 10, 1,
            () => c.DeltaLaps, v => c.DeltaLaps = (int)v, v => $"{v:0}"));
        PageBody.Children.Add(Slider("Name column width", "Fixed width so long names don't resize the table.", 60, 400, 10,
            () => c.NameColumnWidth, v => c.NameColumnWidth = v, v => $"{v:0}px"));
        PageBody.Children.Add(Slider("Header text size", "Standings header pill text.", 10, 20, 0.5,
            () => c.HeaderFontSize, v => c.HeaderFontSize = v, v => $"{v:0.#}"));
        PageBody.Children.Add(Toggle("Column header row", null, () => c.ShowColumnHeader, v => c.ShowColumnHeader = v));
        PageBody.Children.Add(Toggle("List full class in qualifying", "Instead of the top-N + window layout.",
            () => c.QualifyShowFullClass, v => c.QualifyShowFullClass = v));
        PageBody.Children.Add(Toggle("Smooth gaps", "Continuous gap/interval (like the relative) instead of stepping at timing lines.",
            () => c.SmoothGaps, v => c.SmoothGaps = v));
        PageBody.Children.Add(Toggle("Rejoin badge", "Show REJOIN when a stopped car starts moving again (experimental).",
            () => c.ShowRejoinState, v => c.ShowRejoinState = v));
        PageBody.Children.Add(Segmented("Status style", "Penalties as flag chips beside the state text, or everything as one text badge.",
            new[] { ("Text + flags", "TextAndFlags"), ("All text", "Text") },
            () => c.StatusStyle, v => Apply(() => c.StatusStyle = v)));

        PageBody.Children.Add(Subhead("Header extras"));
        PageBody.Children.Add(Toggle("Real-life clock", "Your wall-clock time next to the in-sim clock.", () => c.ShowRealClock, v => c.ShowRealClock = v));
        PageBody.Children.Add(Toggle("Local time of day", "In-sim track clock.", () => c.ShowTimeOfDay, v => c.ShowTimeOfDay = v));
        PageBody.Children.Add(Toggle("Strength of Field", null, () => c.ShowSof, v => c.ShowSof = v));
        PageBody.Children.Add(Toggle("Track temperature", null, () => c.ShowTrackTemp, v => c.ShowTrackTemp = v));
        PageBody.Children.Add(Slider("Track temp decimals", null, 0, 2, 1, () => c.ShowTrackTempDecimals, v => c.ShowTrackTempDecimals = (int)v, v => $"{v:0}"));
        PageBody.Children.Add(Toggle("Incident count", null, () => c.ShowIncidents, v => c.ShowIncidents = v));
        PageBody.Children.Add(Toggle("Weather / track state", null, () => c.ShowWeather, v => c.ShowWeather = v));
        PageBody.Children.Add(Toggle("Abbreviate track state", "“M.Dry” / “V.Wet” instead of the full words.", () => c.AbbreviateWetness, v => c.AbbreviateWetness = v));
        PageBody.Children.Add(Slider("Wet↔dry alert", "How long the track-state change banner stays.", 10, 300, 10,
            () => c.WeatherAlertSec, v => c.WeatherAlertSec = (int)v, v => $"{v:0}s"));
        PageBody.Children.Add(Slider("Tyre-switch alert", "How long a dry↔wet tyre switch is shown.", 5, 60, 5,
            () => c.TyreSwitchAlertSec, v => c.TyreSwitchAlertSec = (int)v, v => $"{v:0}s"));
        PageBody.Children.Add(Segmented("Tyre-switch display", "Header flash, an inline o→o in the row, or both.",
            new[] { ("Flash", "Flash"), ("Inline", "Inline"), ("Both", "Both") },
            () => c.TyreSwitchDisplay, v => Apply(() => c.TyreSwitchDisplay = v)));
        PageBody.Children.Add(Toggle("Wind direction & speed", null, () => c.ShowWind, v => c.ShowWind = v));

        PageBody.Children.Add(Subhead("Precision (decimals)"));
        PageBody.Children.Add(Slider("Gap", null, 0, 3, 1, () => c.GapPrecision, v => c.GapPrecision = (int)v, v => $"{v:0}"));
        PageBody.Children.Add(Slider("Interval", null, 0, 3, 1, () => c.IntervalPrecision, v => c.IntervalPrecision = (int)v, v => $"{v:0}"));
        PageBody.Children.Add(Slider("Lap time", null, 0, 3, 1, () => c.LapTimePrecision, v => c.LapTimePrecision = (int)v, v => $"{v:0}"));
        PageBody.Children.Add(Slider("Delta", null, 0, 3, 1, () => c.DeltaPrecision, v => c.DeltaPrecision = (int)v, v => $"{v:0}"));
        PageBody.Children.Add(Slider("Qualifying gap", null, 0, 3, 1, () => c.QualifyGapPrecision, v => c.QualifyGapPrecision = (int)v, v => $"{v:0}"));

        // Per-session column toggles: a segment picker swaps which SessionColumns we edit.
        PageBody.Children.Add(Subhead("Columns per session"));
        var host = new StackPanel();
        PageBody.Children.Add(Segmented("Session", "Column visibility is independent for each session type.",
            new[] { ("Race", "Race"), ("Qualify", "Qualify"), ("Practice", "Practice") },
            () => "Race", v => BuildColumns(host, v)));
        PageBody.Children.Add(host);
        BuildColumns(host, "Race");
    }

    private void BuildColumns(StackPanel host, string session)
    {
        host.Children.Clear();
        var s = session switch
        {
            "Qualify" => _cfg.Current.Qualify,
            "Practice" => _cfg.Current.Practice,
            _ => _cfg.Current.Race,
        };
        void T(string label, string? hint, Func<bool> get, Action<bool> set) =>
            host.Children.Add(Toggle(label, hint, get, set));

        T("Positions gained", "Race only.", () => s.ShowPositionsGained, v => s.ShowPositionsGained = v);
        T("Tyre compound", "Dry/wet ring next to the position.", () => s.ShowTyre, v => s.ShowTyre = v);
        T("Tyre age", "Race only. Laps on their tires: 42² = double-stint, ³ = triple (inferred from stop lengths).",
            () => s.ShowTyreAge, v => s.ShowTyreAge = v);
        T("iRating", null, () => s.ShowIRating, v => s.ShowIRating = v);
        T("License", null, () => s.ShowLicense, v => s.ShowLicense = v);
        T("Car brand", null, () => s.ShowCarBrand, v => s.ShowCarBrand = v);
        T("Laps completed", null, () => s.ShowLapsCount, v => s.ShowLapsCount = v);
        T("Gap", null, () => s.ShowGap, v => s.ShowGap = v);
        T("Interval", null, () => s.ShowInterval, v => s.ShowInterval = v);
        T("Best lap", null, () => s.ShowBestLap, v => s.ShowBestLap = v);
        T("Last lap", null, () => s.ShowLastLap, v => s.ShowLastLap = v);
        T("Per-lap cells", "Race: gap deltas · Qualify: each lap time.", () => s.ShowCells, v => s.ShowCells = v);
        T("Pace rank", "Fastest-in-class over the last 5 clean laps.", () => s.ShowPaceRank, v => s.ShowPaceRank = v);
        T("Status", "PIT / off-track / penalties.", () => s.ShowStatus, v => s.ShowStatus = v);
        T("Strategy", "Race only. Next pit lap: ~34 · 34! overdue · 0stp none needed.", () => s.ShowStrategy, v => s.ShowStrategy = v);
        T("Pace arrow", "Race only.", () => s.ShowPace, v => s.ShowPace = v);
        T("Pit lap", "Race only. Lap of their last stop.", () => s.ShowPitLap, v => s.ShowPitLap = v);
        T("Pit time — total", "Race only. Time on pit road.", () => s.ShowPitTotal, v => s.ShowPitTotal = v);
        T("Pit time — drive-through", "Race only. Pit-lane transit.", () => s.ShowPitDrive, v => s.ShowPitDrive = v);
        T("Pit time — in box", "Race only. Time sat stationary.", () => s.ShowPitStall, v => s.ShowPitStall = v);
    }

    private void BuildRelative()
    {
        var r = _cfg.Current.Relative;
        var body = Master("Show the relative box", "Cars just ahead and behind you on track.",
            () => r.Enabled, v => r.Enabled = v);

        body.Children.Add(Slider("Size", "Scales the whole relative box.", 0.6, 2.0, 0.05,
            () => r.Scale, v => r.Scale = v, v => $"{v * 100:0}%"));
        body.Children.Add(Slider("Cars ahead", null, 0, 8, 1, () => r.CarsAhead, v => r.CarsAhead = (int)v, v => $"{v:0}"));
        body.Children.Add(Slider("Cars behind", null, 0, 8, 1, () => r.CarsBehind, v => r.CarsBehind = (int)v, v => $"{v:0}"));

        body.Children.Add(Subhead("Columns"));
        body.Children.Add(Toggle("Class position", null, () => r.ShowClassPos, v => r.ShowClassPos = v));
        body.Children.Add(Toggle("Tyre compound", "Dry/wet ring, like the standings.", () => r.ShowTyre, v => r.ShowTyre = v));
        body.Children.Add(Toggle("Car brand", null, () => r.ShowBrand, v => r.ShowBrand = v));
        body.Children.Add(Toggle("iRating", null, () => r.ShowIRating, v => r.ShowIRating = v));
        body.Children.Add(Toggle("License", null, () => r.ShowLicense, v => r.ShowLicense = v));
        body.Children.Add(Toggle("Stint age", "STn = laps since their last stop; green while fresh.", () => r.ShowStintAge, v => r.ShowStintAge = v));
        body.Children.Add(Toggle("Tire-change inference", "ST8+ = last stop took no tires (from stop lengths; needs fuel-and-tires-separate rules).",
            () => _cfg.Current.InferTireChanges, v => _cfg.Current.InferTireChanges = v));
        body.Children.Add(Toggle("Last lap", null, () => r.ShowLastLap, v => r.ShowLastLap = v));
        body.Children.Add(Toggle("Pace arrow", "Their recent pace vs yours.", () => r.ShowPace, v => r.ShowPace = v));

        body.Children.Add(Subhead("Behavior"));
        body.Children.Add(Segmented("Status style", "Independent of the standings: flag chips + text, or one text badge.",
            new[] { ("Text + flags", "TextAndFlags"), ("All text", "Text") },
            () => r.StatusStyle, v => Apply(() => r.StatusStyle = v)));
        body.Children.Add(Toggle("Hide parked cars", "Drop cars sat in the pits (DNF / no driver) — cuts endurance noise.",
            () => r.HideParkedCars, v => r.HideParkedCars = v));
        body.Children.Add(Slider("Battle threshold", "Same-lap cars within this gap get the battle marker.", 0.5, 5, 0.5,
            () => r.BattleGapSec, v => r.BattleGapSec = v, v => $"{v:0.0}s"));
        body.Children.Add(Slider("Gap precision", null, 0, 2, 1, () => r.GapPrecision, v => r.GapPrecision = (int)v, v => $"{v:0}"));
    }

    private void BuildTraffic()
    {
        var t = _cfg.Current.Traffic;
        var body = Master("Enable traffic alerts", "Audio + visual warning before faster traffic arrives.",
            () => t.Enabled, v => t.Enabled = v);

        body.Children.Add(Slider("Size", "Scales the whole traffic widget.", 0.6, 2.0, 0.05,
            () => t.Scale, v => t.Scale = v, v => $"{v * 100:0}%"));
        body.Children.Add(Toggle("Races only", "Off = also warn in practice/qual (blue flags stay race-only).",
            () => t.RacesOnly, v => t.RacesOnly = v));
        body.Children.Add(Segmented("Style", "How the alert is drawn.",
            new[] { ("Row", "Row"), ("Beacon", "Beacon") }, () => t.Style, v => Apply(() => t.Style = v)));
        body.Children.Add(Segmented("Trigger", "Which cars raise an alert.",
            new[] { ("Faster class", "FasterClassOnly"), ("+ Lapping", "FasterClassAndLapping"), ("All closing", "AllClosing") },
            () => t.Mode, v => Apply(() => t.Mode = v)));

        body.Children.Add(Subhead("Timing"));
        body.Children.Add(Slider("Lead time", "Warn this many seconds before traffic arrives.", 4, 30, 1,
            () => t.AlertLeadTimeSec, v => t.AlertLeadTimeSec = v, v => $"{v:0}s"));
        body.Children.Add(Slider("Blue-flag lead", "More warning when you're the one being lapped.", 5, 40, 1,
            () => t.BlueLeadTimeSec, v => t.BlueLeadTimeSec = v, v => $"{v:0}s"));
        body.Children.Add(Slider("Imminent", "Escalate to the urgent cue inside this window.", 1, 10, 1,
            () => t.ImminentSec, v => t.ImminentSec = v, v => $"{v:0}s"));
        body.Children.Add(Slider("Max rows", null, 1, 6, 1, () => t.MaxRows, v => t.MaxRows = (int)v, v => $"{v:0}"));
        body.Children.Add(Toggle("Warn when lapping traffic", "Alert on slower/lapped cars ahead you're about to lap.",
            () => t.WarnLapping, v => t.WarnLapping = v));
        body.Children.Add(Slider("Lapping gap", "Gap at which the lapping alert fires.", 2, 10, 1,
            () => t.LapTrafficGapSec, v => t.LapTrafficGapSec = v, v => $"{v:0}s"));
        body.Children.Add(Toggle("Show iRating", null, () => t.ShowIRating, v => t.ShowIRating = v));
        body.Children.Add(Toggle("Alongside banner", "Left/right marker when alerted traffic is beside you.",
            () => t.AlongsideBanner, v => t.AlongsideBanner = v));

        body.Children.Add(Subhead("Audio"));
        var audio = t.Audio;
        body.Children.Add(Toggle("Audio cues", null, () => audio.Enabled, v => audio.Enabled = v));
        body.Children.Add(Slider("Volume", null, 0, 100, 5, () => audio.Volume, v => audio.Volume = (int)v, v => $"{v:0}%"));
        body.Children.Add(Toggle("Watch chirp", "Rising tone as traffic enters the window.", () => audio.WatchCue, v => audio.WatchCue = v));
        body.Children.Add(Toggle("Imminent beep", "Urgent triple beep.", () => audio.ImminentCue, v => audio.ImminentCue = v));
        body.Children.Add(Toggle("Blue-flag tone", "Calm two-tone when being lapped.", () => audio.BlueCue, v => audio.BlueCue = v));
    }

    private void BuildFuel()
    {
        var f = _cfg.Current.Fuel;
        var body = Master("Show fuel & strategy", "Live burn, laps in the tank, and endurance stint bars.",
            () => f.Enabled, v => f.Enabled = v);

        body.Children.Add(Slider("Size", "Scales the whole fuel widget.", 0.6, 2.0, 0.05,
            () => f.Scale, v => f.Scale = v, v => $"{v * 100:0}%"));
        body.Children.Add(Slider("Strategy bars", "How many candidate plans to show.", 1, 3, 1,
            () => f.Strategies, v => f.Strategies = (int)v, v => $"{v:0}"));
        body.Children.Add(Slider("Safety margin", "Fuel kept in reserve at the finish.", 0, 3, 0.5,
            () => f.MarginLaps, v => f.MarginLaps = v, v => $"{v:0.0} lap"));
        body.Children.Add(Slider("Bar width", null, 240, 640, 20, () => f.BarWidth, v => f.BarWidth = v, v => $"{v:0}"));

        body.Children.Add(Subhead("Fuel-save model"));
        body.Children.Add(Slider("Max save", "Realistic lift-and-coast fuel saving ceiling.", 0, 0.6, 0.02,
            () => f.MaxSaveLPerLap, v => f.MaxSaveLPerLap = v, v => $"{v:0.00}/lap"));
        body.Children.Add(Slider("Save penalty", "Lap-time cost at that maximum save.", 0, 2, 0.05,
            () => f.MaxSavePenaltySec, v => f.MaxSavePenaltySec = v, v => $"{v:0.00}s"));

        body.Children.Add(Subhead("Pit stop (−1 = learn from your own stops)"));
        body.Children.Add(Slider("Pit lane loss", null, -1, 120, 1,
            () => f.PitLaneLossSec, v => f.PitLaneLossSec = v, v => v < 0 ? "auto" : $"{v:0}s"));
        body.Children.Add(Slider("Fill rate", null, -1, 5, 0.1,
            () => f.FillRateLps, v => f.FillRateLps = v, v => v < 0 ? "auto" : $"{v:0.0} L/s"));
    }

    private void BuildAbout()
    {
        var ver = UpdateCheck.CurrentDisplay;
        void P(string text, Brush brush, double size = 13, double top = 2, FontWeight? weight = null) =>
            PageBody.Children.Add(new TextBlock
            {
                Text = text, Foreground = brush, FontSize = size, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, top, 0, 0), FontWeight = weight ?? FontWeights.Normal,
            });

        P("Standings Overlay", Text, 18, 4, FontWeights.SemiBold);
        P($"Version {ver}", Dim);
        if (UpdateCheck.Latest is { } up)
        {
            var link = new TextBlock
            {
                Text = $"Update available: {up.Tag} — open the release page",
                Foreground = Accent, FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                TextDecorations = TextDecorations.Underline,
            };
            link.MouseLeftButtonUp += (_, _) => UpdateCheck.OpenReleasePage(up.Url);
            PageBody.Children.Add(link);
        }
        P("A lightweight, standings-only overlay for iRacing. Its headline feature is the per-lap "
          + "gap delta over a configurable number of laps. Built to never affect sim frame times: "
          + "it repaints only on change and never busy-polls.", Dim, 12.5, 14);
        P("MIT licensed · uses irsdkSharp (MIT).", Dim, 12.5, 14);
        P("Tip: pick “Move overlays” under General, then drag each widget where you want it.", Dim, 12.5, 14);

        PageBody.Children.Add(Divider());
        PageBody.Children.Add(new TextBlock { Text = "config.json", Foreground = Dim, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0) });
        PageBody.Children.Add(new TextBox
        {
            Text = System.IO.Path.Combine(AppContext.BaseDirectory, "config.json"),
            IsReadOnly = true, Style = (Style)FindResource("HexBox"),
            Margin = new Thickness(0, 3, 0, 0), FontSize = 11.5,
        });
    }

    // ---- reusable row builders ------------------------------------------

    /// <summary>Two-column row: label (+ optional hint) left, control right.</summary>
    private FrameworkElement Row(string label, string? hint, UIElement control, bool strong = false)
    {
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        texts.Children.Add(new TextBlock
        {
            Text = label, Foreground = Text,
            FontSize = strong ? 14.5 : 13.5,
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        });
        if (!string.IsNullOrEmpty(hint))
            texts.Children.Add(new TextBlock
            {
                Text = hint, Foreground = Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0),
            });

        var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(texts, 0);
        grid.Children.Add(texts);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn((UIElement)control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private void BuildLapLab()
    {
        var l = _cfg.Current.LapLab;
        var body = Master("Show lap lab", "Every lap a row, official sectors as columns, gaps vs a reference. Hidden in races.",
            () => l.Enabled, v => l.Enabled = v);

        body.Children.Add(Slider("Size", "Scales the whole lap table.", 0.6, 2.0, 0.05,
            () => l.Scale, v => l.Scale = v, v => $"{v * 100:0}%"));
        body.Children.Add(Slider("Laps shown", null, 3, 15, 1,
            () => l.MaxRows, v => l.MaxRows = (int)v, v => $"{v:0}"));
        body.Children.Add(Slider("Gap decimals", null, 1, 3, 1,
            () => l.Decimals, v => l.Decimals = (int)v, v => $"{v:0}"));

        body.Children.Add(Subhead("Reference lap"));
        body.Children.Add(Segmented("Reference", "Fastest full lap · best sectors combined · your saved best from an earlier session · an imported telemetry lap.",
            new[] { ("Best", "SessionBest"), ("Optimal", "SessionOptimal"), ("Prev", "PreviousBest"), ("File", "File") },
            () => l.Reference, v => Apply(() => l.Reference = v)));
        body.Children.Add(FileRow("Reference .ibt", "Telemetry file used when Reference is File — yours, a teammate's, or a Garage 61 / VRS download. Its conditions are checked against the live session.",
            () => l.ReferenceFile, v => l.ReferenceFile = v));
        body.Children.Add(Toggle("Save session best", "Keep your best clean lap per car+track as the next session's Prev reference.",
            () => l.SaveSessionBest, v => l.SaveSessionBest = v));
        body.Children.Add(Slider("Track-temp warning", "Amber chip when the live track temp differs from the reference by more than this.", 1, 15, 1,
            () => l.WarnTrackTempDelta, v => l.WarnTrackTempDelta = v, v => $"{v:0}°C"));
    }

    private FrameworkElement FileRow(string label, string? hint, Func<string> get, Action<string> set)
    {
        var name = new TextBlock
        {
            Text = get().Length > 0 ? System.IO.Path.GetFileName(get()) : "none",
            Foreground = Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var browse = new Button { Content = "Browse…", Padding = new Thickness(10, 2, 10, 3) };
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "iRacing telemetry (*.ibt)|*.ibt|All files (*.*)|*.*",
            };
            var telemetryDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "telemetry");
            if (get().Length > 0) dlg.FileName = get();
            else if (System.IO.Directory.Exists(telemetryDir)) dlg.InitialDirectory = telemetryDir;
            if (dlg.ShowDialog(this) == true)
            {
                name.Text = System.IO.Path.GetFileName(dlg.FileName);
                Apply(() => set(dlg.FileName));
            }
        };
        var clear = new Button { Content = "✕", Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += (_, _) => { name.Text = "none"; Apply(() => set("")); };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(name);
        panel.Children.Add(browse);
        panel.Children.Add(clear);
        return Row(label, hint, panel);
    }

    private FrameworkElement Toggle(string label, string? hint, Func<bool> get, Action<bool> set) =>
        Toggle(label, hint, get, set, out _);

    private FrameworkElement Toggle(string label, string? hint, Func<bool> get, Action<bool> set,
                                    out CheckBox cb, bool strong = false)
    {
        var box = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = get() };
        box.Checked += (_, _) => Apply(() => set(true));
        box.Unchecked += (_, _) => Apply(() => set(false));
        cb = box;
        return Row(label, hint, box, strong);
    }

    private FrameworkElement Slider(string label, string? hint, double min, double max, double step,
                                    Func<double> get, Action<double> set, Func<double, string> fmt)
    {
        var readout = new TextBlock
        {
            Text = fmt(get()), Foreground = Accent, FontFamily = new FontFamily("Consolas"),
            MinWidth = 60, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        var slider = new Slider
        {
            Style = (Style)FindResource("FlatSlider"),
            Minimum = min, Maximum = max, Value = Math.Clamp(get(), min, max), Width = 172,
            SmallChange = step, LargeChange = step, TickFrequency = step, IsSnapToTickEnabled = true,
        };
        slider.ValueChanged += (_, e) =>
        {
            readout.Text = fmt(e.NewValue);
            Apply(() => set(e.NewValue));
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(slider);
        panel.Children.Add(readout);
        return Row(label, hint, panel);
    }

    private FrameworkElement Segmented(string label, string? hint, (string label, string value)[] options,
                                       Func<string> get, Action<string> onPick)
    {
        var group = "seg_" + Guid.NewGuid().ToString("N");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        string current = get();
        foreach (var (text, value) in options)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Segment"), Content = text, GroupName = group,
                IsChecked = string.Equals(value, current, StringComparison.OrdinalIgnoreCase),
            };
            rb.Checked += (_, _) => onPick(value);   // segment change may just re-render (columns); caller applies
            panel.Children.Add(rb);
        }
        return Row(label, hint, panel);
    }

    private FrameworkElement ColorRow(string label, Func<string> get, Action<string> set)
    {
        var swatch = new Border
        {
            Width = 26, Height = 22, CornerRadius = new CornerRadius(4),
            Background = RowViewModel.TryBrush(get()) ?? Brushes.Gray,
            BorderBrush = Line, BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new TextBox { Style = (Style)FindResource("HexBox"), Text = get(), Width = 92, Margin = new Thickness(8, 0, 0, 0) };
        box.TextChanged += (_, _) =>
        {
            var hex = box.Text.Trim();
            if (RowViewModel.TryBrush(hex) is Brush b)
            {
                swatch.Background = b;
                Apply(() => set(hex));
            }
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(swatch);
        panel.Children.Add(box);
        return Row(label, null, panel);
    }

    /// <summary>Prominent enable toggle at the top of a widget section; returns the body panel it
    /// greys out when off.</summary>
    private StackPanel Master(string label, string hint, Func<bool> get, Action<bool> set)
    {
        var body = new StackPanel();
        PageBody.Children.Add(Toggle(label, hint, get, v => { set(v); body.IsEnabled = v; }, out _, strong: true));
        PageBody.Children.Add(Divider());
        body.IsEnabled = get();
        PageBody.Children.Add(body);
        return body;
    }

    private FrameworkElement Subhead(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(), Foreground = Dim, FontSize = 11, FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 18, 0, 2),
    };

    private FrameworkElement Divider() => new Border
    {
        Height = 1, Background = Line, Margin = new Thickness(0, 8, 0, 4),
    };

    // ---- footer actions --------------------------------------------------

    private void OnResetSection(object sender, RoutedEventArgs e)
    {
        var c = _cfg.Current;
        switch (_section)
        {
            case "General":
                var d = new OverlayConfig();
                c.UpdateHz = d.UpdateHz; c.Opacity = d.Opacity; c.FontSize = d.FontSize;
                c.BackgroundColor = d.BackgroundColor; c.AccentColor = d.AccentColor; c.HighlightColor = d.HighlightColor;
                break;
            case "Standings":
                var s = new OverlayConfig();
                c.Scale = s.Scale; c.NameColumnWidth = s.NameColumnWidth; c.HeaderFontSize = s.HeaderFontSize;
                c.SmoothGaps = s.SmoothGaps; c.ShowRejoinState = s.ShowRejoinState; c.StatusStyle = s.StatusStyle;
                c.DriversAtTop = s.DriversAtTop; c.DriversAhead = s.DriversAhead; c.DriversBehind = s.DriversBehind;
                c.MinLeadingCars = s.MinLeadingCars; c.TyreSwitchDisplay = s.TyreSwitchDisplay;
                c.OtherClassesDriversAtTop = s.OtherClassesDriversAtTop; c.DeltaLaps = s.DeltaLaps;
                c.ShowColumnHeader = s.ShowColumnHeader; c.QualifyShowFullClass = s.QualifyShowFullClass;
                c.ShowSof = s.ShowSof; c.ShowRealClock = s.ShowRealClock; c.ShowTimeOfDay = s.ShowTimeOfDay;
                c.ShowTrackTemp = s.ShowTrackTemp; c.ShowTrackTempDecimals = s.ShowTrackTempDecimals;
                c.ShowIncidents = s.ShowIncidents; c.ShowWeather = s.ShowWeather; c.AbbreviateWetness = s.AbbreviateWetness;
                c.WeatherAlertSec = s.WeatherAlertSec; c.TyreSwitchAlertSec = s.TyreSwitchAlertSec; c.ShowWind = s.ShowWind;
                c.GapPrecision = s.GapPrecision; c.IntervalPrecision = s.IntervalPrecision; c.LapTimePrecision = s.LapTimePrecision;
                c.DeltaPrecision = s.DeltaPrecision; c.QualifyGapPrecision = s.QualifyGapPrecision;
                c.Race = SessionColumns.RaceDefaults(); c.Qualify = SessionColumns.QualifyDefaults(); c.Practice = SessionColumns.PracticeDefaults();
                break;
            // Widget sections keep their on-screen position; only behavior resets.
            case "Relative": c.Relative = KeepPos(new RelativeConfig(), c.Relative.X, c.Relative.Y); break;
            case "Traffic": c.Traffic = KeepPos(new TrafficConfig(), c.Traffic.X, c.Traffic.Y); break;
            case "Fuel": c.Fuel = KeepPos(new FuelConfig(), c.Fuel.X, c.Fuel.Y); break;
            case "Lap Lab": c.LapLab = KeepPos(new LapLabConfig(), c.LapLab.X, c.LapLab.Y); break;
            case "About": return;
        }
        Apply(() => { });          // mark dirty + schedule save
        OnNavChanged(Nav, null!);  // rebuild the section from the reset values
    }

    private static RelativeConfig KeepPos(RelativeConfig r, double x, double y) { r.X = x; r.Y = y; return r; }
    private static TrafficConfig KeepPos(TrafficConfig t, double x, double y) { t.X = x; t.Y = y; return t; }
    private static FuelConfig KeepPos(FuelConfig f, double x, double y) { f.X = x; f.Y = y; return f; }
    private static LapLabConfig KeepPos(LapLabConfig l, double x, double y) { l.X = x; l.Y = y; return l; }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
