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
/// edits <see cref="OverlayConfig"/> live. Laid out like iOverlay's settings:
/// <list type="bullet">
/// <item>The left rail holds the global controls — which profile is edited (In car /
/// Spectating, held in <c>_edit</c>), Move overlays, and which session type's widget layout is on
/// screen (positions are per session type) — then the overlay list.</item>
/// <item>Each overlay page has a <b>Visible</b> switch with race / quali / practice boxes, and up to
/// three tabs: <b>General</b> (settings that don't vary by session type), <b>Content</b> (columns /
/// rows) and <b>Header</b>. Content and Header rows are <see cref="Item"/>s: a master switch, the
/// R/Q/P boxes, and an optional "settings" link that expands that item's own options.</item>
/// </list>
/// Every widget re-applies on <see cref="ConfigService.Changed"/>, so this just mutates <c>_edit</c>
/// and calls <see cref="ConfigService.SaveProfileAndNotify"/> (debounced). Rows are built from small
/// descriptor helpers — adding a setting is one line, and every control looks the same.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly Brush Text = RowViewModel.Frozen("#E8E9EE");
    private static readonly Brush Dim = RowViewModel.Frozen("#9DA0AA");
    private static readonly Brush Line = RowViewModel.Frozen("#2C2C36");
    private static readonly Brush Accent = RowViewModel.Frozen("#00E6C3");
    private static readonly Brush Amber = RowViewModel.Frozen("#E8B14C");
    private static readonly Brush SubPanel = RowViewModel.Frozen("#1C1C24");

    private readonly ConfigService _cfg;
    private readonly DispatcherTimer _saveTimer;
    private bool _dirty;

    // Edit ("move overlays") mode is owned by App; the rail switch mirrors and drives it.
    public event Action<bool>? EditModeChanged;
    private bool _editMode;
    private bool _suppressEdit;

    private string _section = "General";
    private string _tab = "";
    private readonly Dictionary<string, string> _tabBySection = new();   // remembered per section
    private readonly HashSet<string> _openSettings = new();              // expanded item "settings"

    // Which state the settings window is currently EDITING (independent of which is live-active).
    // The rail switch drives it; every Build* method reads/writes _edit.
    private bool _editingSpectate;
    private OverlayConfig _edit;

    public SettingsWindow(ConfigService cfg, bool editMode)
    {
        InitializeComponent();
        _cfg = cfg;
        _editMode = editMode;
        // Open on whichever state is live, so what you see matches the car/spectate context you're in.
        _editingSpectate = cfg.Spectating;
        _edit = EditTarget();

        // Normal priority (a plain DispatcherTimer defaults to Background): the overlay widgets
        // dispatch their repaints at Background, so a Background save timer would be starved by a
        // busy race and your setting changes would never flush. Keep this above them.
        _saveTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(140) };
        _saveTimer.Tick += (_, _) => Flush();

        // Section pages capture the edit-target object in closures, so a live state swap (or an
        // external edit replacing the base) can leave the open page editing a stale instance —
        // rebuild the visible section against the new one. Ordinary in-app saves keep the instance,
        // so they don't rebuild.
        cfg.Changed += OnProfileMaybeSwapped;
        SourceInitialized += (_, _) => Win32.UseDarkTitleBar(this);
        // Shown at startup alongside the topmost overlay widgets and (usually) a running,
        // borderless iRacing — a plain Show() leaves this control panel buried and unreachable.
        // Pop it to the front once it's up (the tray double-click used to do the equivalent).
        Loaded += (_, _) => BringToFront();
        Closed += (_, _) => { _cfg.Changed -= OnProfileMaybeSwapped; Flush(); };   // never drop a pending edit

        // The global "Move overlays" switch lives in the rail (reachable from every page).
        MoveToggle.IsChecked = _editMode;
        MoveToggle.Checked += (_, _) => { _editMode = true; if (!_suppressEdit) EditModeChanged?.Invoke(true); ReflectLayout(); };
        MoveToggle.Unchecked += (_, _) => { _editMode = false; if (!_suppressEdit) EditModeChanged?.Invoke(false); ReflectLayout(); };

        BuildStateSwitch();
        BuildLayoutSwitch();
        UpdateInheritBanner();

        foreach (var name in new[] { "General", "Standings", "Relative", "Traffic", "Fuel", "Fuel Table", "Lap Lab", "About" })
            Nav.Items.Add(name);
        Nav.SelectedIndex = 0;
    }

    // ---- page "Visible" switch + race/quali/practice ---------------------

    /// <summary>For a widget section, how to read/write which session types the box shows in, and
    /// the session types it can show in at all. Non-widget sections (General, About) return null.</summary>
    private static (Func<OverlayConfig, Sessions> get, Action<OverlayConfig, Sessions> set, Sessions allowed)? BoxFor(string section) => section switch
    {
        "Standings"  => (c => c.ShowStandings,     (c, v) => c.ShowStandings = v,     Sessions.All),
        "Relative"   => (c => c.Relative.Enabled,  (c, v) => c.Relative.Enabled = v,  Sessions.All),
        "Traffic"    => (c => c.Traffic.Enabled,   (c, v) => c.Traffic.Enabled = v,   Sessions.All),
        "Fuel"       => (c => c.Fuel.Enabled,      (c, v) => c.Fuel.Enabled = v,      Sessions.All),
        "Fuel Table" => (c => c.FuelTable.Enabled, (c, v) => c.FuelTable.Enabled = v, Sessions.All),
        // Lap Lab never shows in races (a practice tool by design).
        "Lap Lab"    => (c => c.LapLab.Enabled,    (c, v) => c.LapLab.Enabled = v,    Sessions.Qualify | Sessions.Practice),
        _ => null,
    };

    // Re-reads the header Visible cluster in place (after a save or external edit) without
    // rebuilding it, so its remembered R/Q/P set survives a master off → on.
    private Action? _refreshBox;

    /// <summary>Build the header Visible cluster for the current section (or clear it), and grey the
    /// page body when the box is off in every session type. Called on every page (re)build.</summary>
    private void ConfigureBoxToggle(string section)
    {
        VisibleHost.Children.Clear();
        _refreshBox = null;
        if (BoxFor(section) is not { } b)
        {
            PageBody.IsEnabled = true;
            return;
        }
        var edit = _edit;
        var (master, boxes, refresh) = SessionControls(() => b.get(edit), v =>
        {
            b.set(edit, v);
            PageBody.IsEnabled = (v & b.allowed) != 0;
            UpdateInheritBanner();
        }, b.allowed);
        _refreshBox = () =>
        {
            refresh();
            PageBody.IsEnabled = (b.get(edit) & b.allowed) != 0;
        };
        VisibleHost.Children.Add(new TextBlock
        {
            Text = "Visible", Foreground = Text, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        master.Margin = new Thickness(0, 0, 4, 0);
        VisibleHost.Children.Add(master);
        VisibleHost.Children.Add(VDivider());
        VisibleHost.Children.Add(boxes);
        PageBody.IsEnabled = (b.get(edit) & b.allowed) != 0;
    }

    // ---- state (in-car / spectating) editing target ---------------------

    /// <summary>The config instance the current state edits: the base while editing In car, the
    /// effective spectate config (base + overrides) while editing Spectating.</summary>
    private OverlayConfig EditTarget() => _editingSpectate ? _cfg.SpectateEffective : _cfg.Base;

    private void BuildStateSwitch()
    {
        void Add(string text, bool spectate)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("RailSegment"), Content = text, GroupName = "statesel",
                IsChecked = _editingSpectate == spectate,
            };
            rb.Checked += (_, _) => SetEditTarget(spectate);
            StateSwitchHost.Children.Add(rb);
        }
        Add("In car", false);
        Add("Spectating", true);
    }

    /// <summary>Retarget the whole editor to a state. Flushes pending edits to the OLD target first,
    /// then rebuilds the visible section against the new one.</summary>
    private void SetEditTarget(bool spectate)
    {
        if (_editingSpectate == spectate) return;
        Flush();                       // commit anything pending to the state we're leaving
        _editingSpectate = spectate;
        _edit = EditTarget();
        UpdateInheritBanner();
        RebuildPage();                 // rebuild (and retarget the Visible switch) for the new state
    }

    private void UpdateInheritBanner()
    {
        // Contextual footer reset: defaults while editing In car, inherit-from-In-car while spectating.
        ResetButton.Content = _editingSpectate ? "Reset section to In car" : "Reset section";
        if (!_editingSpectate)
        {
            InheritBanner.Visibility = Visibility.Collapsed;
            return;
        }
        int n = _cfg.SpectateOverrideCount;
        InheritBannerText.Text = n == 0
            ? "Spectating — every setting currently follows In car. Change anything here to override it for spectating only."
            : $"Spectating — {n} setting{(n == 1 ? "" : "s")} overridden. Everything else follows In car; change In car and these follow along.";
        InheritBanner.Visibility = Visibility.Visible;
    }

    // ---- per-session layout picker ---------------------------------------

    private readonly Dictionary<SessionKind, RadioButton> _layoutButtons = new();
    private bool _suppressLayout;

    private static string SessionName(SessionKind k) => k switch
    {
        SessionKind.Race => "race",
        SessionKind.Qualify => "qualifying",
        _ => "practice",
    };

    /// <summary>Race / Quali / Practice under Move overlays: which session type's widget layout is
    /// on screen. Live it follows the session; while moving overlays you can pick another to
    /// arrange it without being in that session (App snaps back to live when you finish).</summary>
    private void BuildLayoutSwitch()
    {
        foreach (var (text, kind) in new[] { ("Race", SessionKind.Race), ("Quali", SessionKind.Qualify), ("Practice", SessionKind.Practice) })
        {
            var rb = new RadioButton { Style = (Style)FindResource("RailSegment"), Content = text, GroupName = "layoutsel" };
            rb.Checked += (_, _) => { if (!_suppressLayout && _editMode) _cfg.SetSession(kind); };
            _layoutButtons[kind] = rb;
            LayoutHost.Children.Add(rb);
        }
        ReflectLayout();
    }

    private void ReflectLayout()
    {
        _suppressLayout = true;
        _layoutButtons[_cfg.Session].IsChecked = true;
        _suppressLayout = false;
        LayoutHost.IsEnabled = _editMode;
        LayoutHint.Text = _editMode
            ? $"Dragging saves the {SessionName(_cfg.Session)} layout. Pick another session type to arrange it too."
            : $"Each session type keeps its own widget positions. On screen: {SessionName(_cfg.Session)}.";
    }

    /// <summary>Restore (if minimized) and take focus/activation. Called on Loaded so this normal,
    /// non-topmost control panel isn't left buried at startup when the foreground-lock withholds
    /// activation while iRacing is the active app. It does not force itself above other windows.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    /// <summary>Reflect edit mode toggled elsewhere into the rail switch, no re-fire.</summary>
    public void ReflectEditMode(bool on)
    {
        _editMode = on;
        _suppressEdit = true;
        MoveToggle.IsChecked = on;
        _suppressEdit = false;
        ReflectLayout();
    }

    /// <summary>Connection status shown under the brand (this window replaces the old tray tooltip).</summary>
    public void SetStatus(string text, bool connected)
    {
        StatusText.Text = text;
        StatusText.Foreground = connected ? Text : Dim;
        StatusDot.Fill = connected ? Accent : Amber;
    }

    private string? _updateUrl;

    /// <summary>Reveal the "update available" banner in the nav rail (launch update-check found a
    /// newer release). Replaces the old tray "Update available" menu item.</summary>
    public void ShowUpdateAvailable(string tag, string url)
    {
        _updateUrl = url;
        UpdateBannerText.Text = $"Update available — {tag}\nClick to open the release page.";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void OnUpdateBannerClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_updateUrl is { } url) UpdateCheck.OpenReleasePage(url);
    }

    /// <summary>This is the app's main window, so closing it quits everything
    /// (ShutdownMode=OnMainWindowClose) — confirm first, since the overlays go with it.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;   // respect a cancel from a base handler
        var choice = MessageBox.Show(this,
            "Quit Standings Overlay? This closes the overlays too.",
            "Quit Standings Overlay",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes) e.Cancel = true;
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
        _cfg.SaveProfileAndNotify(_editingSpectate);   // persist to the state we're editing
        UpdateInheritBanner();                         // override count may have changed
    }

    /// <summary>A live state change (in car ↔ spectating) or an external file edit can replace the
    /// instance our edit target points at. Our own saves keep the instance, so those don't rebuild;
    /// a genuine swap does. The edit target itself is user-chosen and does NOT jump with live state.
    /// A session-type change (layout) arrives here too.</summary>
    private void OnProfileMaybeSwapped(OverlayConfig cfg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ReflectLayout();
            var target = EditTarget();
            UpdateInheritBanner();
            if (ReferenceEquals(target, _edit))
            {
                _refreshBox?.Invoke();   // the Visible flags may have changed without a rebuild
                return;
            }
            _edit = target;
            RebuildPage();   // rebuild the visible section against the new instance
        });
    }

    // ---- navigation ------------------------------------------------------

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not string name) return;
        _section = name;
        RebuildPage();
    }

    /// <summary>Tabs per section. Sections with no tabs are a single page.</summary>
    private static string[] TabsFor(string section) => section switch
    {
        "Standings" => ["General", "Content", "Header"],
        "Relative" or "Traffic" or "Fuel Table" => ["General", "Content"],
        _ => [],
    };

    private void RebuildPage()
    {
        var tabs = TabsFor(_section);
        _tab = tabs.Length == 0 ? ""
            : _tabBySection.TryGetValue(_section, out var t) && tabs.Contains(t) ? t : tabs[0];
        BuildTabs(tabs);

        PageBody.Children.Clear();
        switch (_section)
        {
            case "General":
                SectionHeader("General", "Startup, refresh, visibility and how every widget looks. Applies to all session types.");
                BuildGeneral();
                break;
            case "Standings":
                SectionHeader("Standings", "The leaderboard. Content and Header items switch on per session type — race, qualifying, practice.");
                if (_tab == "Content") BuildStandingsContent();
                else if (_tab == "Header") BuildStandingsHeader();
                else BuildStandingsGeneral();
                break;
            case "Relative":
                SectionHeader("Relative", "Cars physically around you, in track order.");
                if (_tab == "Content") BuildRelativeContent(); else BuildRelativeGeneral();
                break;
            case "Traffic":
                SectionHeader("Traffic alerter", "Warnings when a faster class is closing, or you're being lapped. Blue flags are race-only by definition.");
                if (_tab == "Content") BuildTrafficContent(); else BuildTrafficGeneral();
                break;
            case "Fuel":
                SectionHeader("Fuel & strategy", "Live burn, laps in the tank, and endurance stint bars (race-only).");
                BuildFuel();
                break;
            case "Fuel Table":
                SectionHeader("Fuel table", "Per-lap consumption — Last / Last-5 / Last-10 / Stint — with a clickable target.");
                if (_tab == "Content") BuildFuelTableContent(); else BuildFuelTableGeneral();
                break;
            case "Lap Lab":
                SectionHeader("Lap Lab", "Practice lap table: your sectors against a reference lap. Never shown in races.");
                BuildLapLab();
                break;
            case "About":
                SectionHeader("About", "Standings Overlay.");
                BuildAbout();
                break;
        }

        ConfigureBoxToggle(_section);   // point the header Visible switch at this section
    }

    private void BuildTabs(string[] tabs)
    {
        TabHost.Children.Clear();
        TabStrip.Visibility = tabs.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var name in tabs)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Tab"), Content = name, GroupName = "tabs",
                IsChecked = name == _tab,
            };
            rb.Checked += (_, _) =>
            {
                if (_tab == name) return;
                _tabBySection[_section] = name;
                RebuildPage();
            };
            TabHost.Children.Add(rb);
        }
    }

    private void SectionHeader(string title, string blurb)
    {
        SectionTitle.Text = title;
        SectionBlurb.Text = blurb;
    }

    // ---- General (app-wide) ----------------------------------------------

    private void BuildGeneral()
    {
        PageBody.Children.Add(Toggle("Start with Windows", "Launch the overlay automatically when you log in.",
            AutoStart.IsEnabled, AutoStart.Set));

        var c = _edit;
        PageBody.Children.Add(Toggle("Check for updates at launch", "One request to GitHub for the latest release; a banner appears here if newer. Never downloads anything.",
            () => c.CheckForUpdates, v => c.CheckForUpdates = v));
        PageBody.Children.Add(Slider("Refresh rate", "Snapshots per second. Rendering still only happens on change.",
            1, 10, 1, () => c.UpdateHz, v => c.UpdateHz = (int)v, v => $"{v:0} Hz"));

        // Spectating: a one-click way to line the widgets up with the in-car layout instead of
        // dragging each one again. Positions are just settings, so this writes overrides like any edit.
        if (_editingSpectate)
        {
            PageBody.Children.Add(Subhead("Spectating layout"));
            PageBody.Children.Add(ButtonRow("Copy widget positions from In car",
                "Move every widget to where it sits in your in-car profile (all session types).", "Copy layout",
                () => Apply(() => CopyPositions(_cfg.Base, _edit))));
        }

        PageBody.Children.Add(Subhead("Visibility"));
        PageBody.Children.Add(Toggle("Hide when iRacing isn't focused",
            "Show the overlays only while iRacing is the active window; hide them the moment you tab to another app. Data keeps being collected the whole time.",
            () => c.AutoHide.Enabled, v => c.AutoHide.Enabled = v));
        PageBody.Children.Add(Toggle($"Show/hide hotkey  ({c.AutoHide.Hotkey})",
            "A system-wide shortcut to force the overlays on or off — even inside iRacing, or while in another app. Edit the combo in config.json.",
            () => c.AutoHide.HotkeyEnabled, v => c.AutoHide.HotkeyEnabled = v));

        PageBody.Children.Add(Subhead("Appearance"));
        PageBody.Children.Add(Slider("Opacity", null, 0.1, 1.0, 0.05, () => c.Opacity, v => c.Opacity = v, v => $"{v * 100:0}%"));
        PageBody.Children.Add(Slider("Font size", null, 10, 24, 1, () => c.FontSize, v => c.FontSize = v, v => $"{v:0} px"));
        PageBody.Children.Add(ColorRow("Background", () => c.BackgroundColor, v => c.BackgroundColor = v));
        PageBody.Children.Add(ColorRow("Accent", () => c.AccentColor, v => c.AccentColor = v));
        PageBody.Children.Add(ColorRow("Player highlight", () => c.HighlightColor, v => c.HighlightColor = v));
    }

    // ---- Standings -------------------------------------------------------

    private void BuildStandingsGeneral()
    {
        var c = _edit;
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
        PageBody.Children.Add(Toggle("List full class in qualifying", "Instead of the top-N + window layout.",
            () => c.QualifyShowFullClass, v => c.QualifyShowFullClass = v));
        PageBody.Children.Add(Toggle("Pin towed cars", "A towed class rival shows at its position until it leaves the pits.",
            () => c.PinTowedCars, v => c.PinTowedCars = v));
        PageBody.Children.Add(Slider("Name column width", "Fixed width so long names don't resize the table.", 60, 400, 10,
            () => c.NameColumnWidth, v => c.NameColumnWidth = v, v => $"{v:0}px"));
        PageBody.Children.Add(Slider("Header text size", "Standings header pill text.", 10, 20, 0.5,
            () => c.HeaderFontSize, v => c.HeaderFontSize = v, v => $"{v:0.#}"));
    }

    /// <summary>Columns in on-screen order. Columns that can only ever render in some session types
    /// (race-only strategy/pit data, no per-lap cells in practice) grey out the others.</summary>
    private void BuildStandingsContent()
    {
        var c = _edit;
        const Sessions RaceOnly = Sessions.Race;
        void Add(FrameworkElement row) => PageBody.Children.Add(row);

        Add(Column("Tyre compound", "Dry/wet ring next to the position.", s => s.ShowTyre, (s, v) => s.ShowTyre = v,
            settings: p =>
            {
                p.Children.Add(Segmented("Tyre-switch display", "Header flash, an inline o→o in the row, or both.",
                    [("Flash", "Flash"), ("Inline", "Inline"), ("Both", "Both")],
                    () => c.TyreSwitchDisplay, v => Apply(() => c.TyreSwitchDisplay = v)));
                p.Children.Add(Slider("Tyre-switch alert", "How long a dry↔wet tyre switch is shown.", 5, 60, 5,
                    () => c.TyreSwitchAlertSec, v => c.TyreSwitchAlertSec = (int)v, v => $"{v:0}s"));
            }));
        Add(Column("Tyre age", "Laps on their tires: 42² = double-stint, ³ = triple (inferred from stop lengths).",
            s => s.ShowTyreAge, (s, v) => s.ShowTyreAge = v, RaceOnly, TireInference));
        Add(Column("Positions gained", null, s => s.ShowPositionsGained, (s, v) => s.ShowPositionsGained = v, RaceOnly));
        Add(Column("iRating", null, s => s.ShowIRating, (s, v) => s.ShowIRating = v));
        Add(Column("Car brand", null, s => s.ShowCarBrand, (s, v) => s.ShowCarBrand = v));
        Add(Column("License", null, s => s.ShowLicense, (s, v) => s.ShowLicense = v));
        Add(Column("Laps completed", null, s => s.ShowLapsCount, (s, v) => s.ShowLapsCount = v));
        Add(Column("Gap", "Gap to the class leader.", s => s.ShowGap, (s, v) => s.ShowGap = v, settings: p =>
        {
            p.Children.Add(Toggle("Smooth gaps", "Continuous gap & interval (like the relative) instead of stepping at timing lines.",
                () => c.SmoothGaps, v => c.SmoothGaps = v));
            p.Children.Add(Slider("Gap decimals", null, 0, 3, 1, () => c.GapPrecision, v => c.GapPrecision = (int)v, v => $"{v:0}"));
            p.Children.Add(Slider("Qualifying gap decimals", "Gap and interval in qualifying, where hundredths matter.", 0, 3, 1,
                () => c.QualifyGapPrecision, v => c.QualifyGapPrecision = (int)v, v => $"{v:0}"));
        }));
        Add(Column("Interval", "Gap to the car ahead.", s => s.ShowInterval, (s, v) => s.ShowInterval = v, settings: p =>
            p.Children.Add(Slider("Interval decimals", null, 0, 3, 1, () => c.IntervalPrecision, v => c.IntervalPrecision = (int)v, v => $"{v:0}"))));
        Add(Column("Best lap", null, s => s.ShowBestLap, (s, v) => s.ShowBestLap = v, settings: LapDecimals));
        Add(Column("Last lap", null, s => s.ShowLastLap, (s, v) => s.ShowLastLap = v, settings: LapDecimals));
        Add(Column("Per-lap cells", "Race: gap change per lap over the last N laps (the headline feature) · Qualifying: each lap time.",
            s => s.ShowCells, (s, v) => s.ShowCells = v, Sessions.Race | Sessions.Qualify, p =>
            {
                p.Children.Add(Slider("Delta laps", "How many per-lap gap-change columns in a race.", 1, 10, 1,
                    () => c.DeltaLaps, v => c.DeltaLaps = (int)v, v => $"{v:0}"));
                p.Children.Add(Slider("Delta decimals", null, 0, 3, 1, () => c.DeltaPrecision, v => c.DeltaPrecision = (int)v, v => $"{v:0}"));
            }));
        Add(Column("Pace rank", "Fastest-in-class over the last 5 clean laps.", s => s.ShowPaceRank, (s, v) => s.ShowPaceRank = v));
        Add(Column("Strategy", "Next pit lap: ~34 · 34! overdue · 0stp none needed.", s => s.ShowStrategy, (s, v) => s.ShowStrategy = v, RaceOnly));
        Add(Column("Pace arrow", "Their recent pace vs yours.", s => s.ShowPace, (s, v) => s.ShowPace = v, RaceOnly));
        Add(Column("Status", "PIT / off-track / penalties.", s => s.ShowStatus, (s, v) => s.ShowStatus = v, settings: p =>
        {
            p.Children.Add(Segmented("Status style", "Penalties as flag chips beside the state text, or everything as one text badge.",
                [("Text + flags", "TextAndFlags"), ("All text", "Text")],
                () => c.StatusStyle, v => Apply(() => c.StatusStyle = v)));
            p.Children.Add(Toggle("Rejoin badge", "Show REJOIN when a stopped car starts moving again (experimental).",
                () => c.ShowRejoinState, v => c.ShowRejoinState = v));
        }));
        Add(Column("Pit lap", "Lap of their last stop.", s => s.ShowPitLap, (s, v) => s.ShowPitLap = v, RaceOnly));
        Add(Column("Pit time — total", "Time on pit road.", s => s.ShowPitTotal, (s, v) => s.ShowPitTotal = v, RaceOnly));
        Add(Column("Pit time — drive-through", "Pit-lane transit.", s => s.ShowPitDrive, (s, v) => s.ShowPitDrive = v, RaceOnly));
        Add(Column("Pit time — in box", "Time sat stationary.", s => s.ShowPitStall, (s, v) => s.ShowPitStall = v, RaceOnly));
    }

    private void BuildStandingsHeader()
    {
        var c = _edit;
        void Add(FrameworkElement row) => PageBody.Children.Add(row);

        Add(Item("Column header row", "The P / DRIVER / GAP … labels above the rows.", () => c.ShowColumnHeader, v => c.ShowColumnHeader = v));
        Add(Item("Real-life clock", "Your wall-clock time next to the in-sim clock.", () => c.ShowRealClock, v => c.ShowRealClock = v));
        Add(Item("Local time of day", "In-sim track clock.", () => c.ShowTimeOfDay, v => c.ShowTimeOfDay = v));
        Add(Item("Strength of Field", null, () => c.ShowSof, v => c.ShowSof = v));
        Add(Item("Track temperature", null, () => c.ShowTrackTemp, v => c.ShowTrackTemp = v, settings: p =>
            p.Children.Add(Slider("Decimals", null, 0, 2, 1, () => c.ShowTrackTempDecimals, v => c.ShowTrackTempDecimals = (int)v, v => $"{v:0}"))));
        Add(Item("Incident count", null, () => c.ShowIncidents, v => c.ShowIncidents = v));
        Add(Item("Weather / track state", "Track wetness + rain %, and the dry↔wet banner.", () => c.ShowWeather, v => c.ShowWeather = v, settings: p =>
        {
            p.Children.Add(Toggle("Abbreviate track state", "“M.Dry” / “V.Wet” instead of the full words.",
                () => c.AbbreviateWetness, v => c.AbbreviateWetness = v));
            p.Children.Add(Slider("Wet↔dry alert", "How long the track-state change banner stays.", 10, 300, 10,
                () => c.WeatherAlertSec, v => c.WeatherAlertSec = (int)v, v => $"{v:0}s"));
        }));
        Add(Item("Wind direction & speed", null, () => c.ShowWind, v => c.ShowWind = v));
    }

    // Shared item sub-settings.
    private void LapDecimals(StackPanel p)
    {
        var c = _edit;
        p.Children.Add(Slider("Lap time decimals", "Shared by every lap-time readout.", 0, 3, 1,
            () => c.LapTimePrecision, v => c.LapTimePrecision = (int)v, v => $"{v:0}"));
    }

    private void TireInference(StackPanel p)
    {
        var c = _edit;
        p.Children.Add(Toggle("Tire-change inference", "No SDK channel exists for opponents' tires: infer them from stop lengths (needs fuel-and-tires-separate rules). Shared by standings and relative.",
            () => c.InferTireChanges, v => c.InferTireChanges = v));
    }

    // ---- Relative --------------------------------------------------------

    private void BuildRelativeGeneral()
    {
        var r = _edit.Relative;
        var body = PageBody;
        body.Children.Add(Slider("Size", "Scales the whole relative box.", 0.6, 2.0, 0.05,
            () => r.Scale, v => r.Scale = v, v => $"{v * 100:0}%"));
        body.Children.Add(Slider("Cars ahead", null, 0, 8, 1, () => r.CarsAhead, v => r.CarsAhead = (int)v, v => $"{v:0}"));
        body.Children.Add(Slider("Cars behind", null, 0, 8, 1, () => r.CarsBehind, v => r.CarsBehind = (int)v, v => $"{v:0}"));
        body.Children.Add(Segmented("Status style", "Independent of the standings: flag chips + text, or one text badge.",
            [("Text + flags", "TextAndFlags"), ("All text", "Text")],
            () => r.StatusStyle, v => Apply(() => r.StatusStyle = v)));
        body.Children.Add(Toggle("Hide parked cars", "Drop cars sat in the pits (DNF / no driver) — cuts endurance noise.",
            () => r.HideParkedCars, v => r.HideParkedCars = v));
        body.Children.Add(Slider("Battle threshold", "Same-lap cars within this gap get the battle marker.", 0.5, 5, 0.5,
            () => r.BattleGapSec, v => r.BattleGapSec = v, v => $"{v:0.0}s"));
        body.Children.Add(Slider("Gap precision", null, 0, 2, 1, () => r.GapPrecision, v => r.GapPrecision = (int)v, v => $"{v:0}"));
    }

    private void BuildRelativeContent()
    {
        var r = _edit.Relative;
        void Add(FrameworkElement row) => PageBody.Children.Add(row);

        Add(Item("Class position", "Off = overall position.", () => r.ShowClassPos, v => r.ShowClassPos = v));
        Add(Item("Tyre compound", "Dry/wet ring, like the standings.", () => r.ShowTyre, v => r.ShowTyre = v));
        Add(Item("Car brand", null, () => r.ShowBrand, v => r.ShowBrand = v));
        Add(Item("iRating", null, () => r.ShowIRating, v => r.ShowIRating = v));
        Add(Item("License", null, () => r.ShowLicense, v => r.ShowLicense = v));
        Add(Item("Stint age", "STn = laps since their last stop; green while fresh. ST8+ = last stop took no tires.",
            () => r.ShowStintAge, v => r.ShowStintAge = v, settings: TireInference));
        Add(Item("Last lap", null, () => r.ShowLastLap, v => r.ShowLastLap = v, settings: LapDecimals));
        Add(Item("Pace arrow", "Their recent pace vs yours.", () => r.ShowPace, v => r.ShowPace = v));
        Add(Item("Closing rate", "s/lap the gap to you is closing — amber = a car behind is catching you, green = you're catching one ahead. Only shows real movers.",
            () => r.ShowClosing, v => r.ShowClosing = v));
    }

    // ---- Traffic ---------------------------------------------------------

    private void BuildTrafficGeneral()
    {
        var t = _edit.Traffic;
        var body = PageBody;

        body.Children.Add(Slider("Size", "Scales the whole traffic widget.", 0.6, 2.0, 0.05,
            () => t.Scale, v => t.Scale = v, v => $"{v * 100:0}%"));
        body.Children.Add(Toggle("Alert while spectating", "Keep warnings while you're out of the car (teammate stint, garage).",
            () => t.WhileSpectating, v => t.WhileSpectating = v));
        body.Children.Add(Segmented("Style", "How the alert is drawn.",
            [("Row", "Row"), ("Beacon", "Beacon")], () => t.Style, v => Apply(() => t.Style = v)));
        body.Children.Add(Segmented("Trigger", "Which cars raise an alert.",
            [("Faster class", "FasterClassOnly"), ("+ Lapping", "FasterClassAndLapping"), ("All closing", "AllClosing")],
            () => t.Mode, v => Apply(() => t.Mode = v)));
        body.Children.Add(Toggle("Split by direction", "\"Me in the middle\": a fixed YOU line with cars you're catching AHEAD above it and cars closing from BEHIND below it. Off = one flat list with ▲/▼ arrows.",
            () => t.GroupByDirection, v => Apply(() => t.GroupByDirection = v)));
        body.Children.Add(Slider("Slots ahead", "Fixed rows above the YOU line (split layout).", 1, 6, 1,
            () => t.SlotsAhead, v => t.SlotsAhead = (int)v, v => $"{v:0}"));
        body.Children.Add(Slider("Slots behind", "Fixed rows below the YOU line (split layout).", 1, 6, 1,
            () => t.SlotsBehind, v => t.SlotsBehind = (int)v, v => $"{v:0}"));
        body.Children.Add(Slider("Max rows", "Total cap in the flat (unsplit) list.", 1, 6, 1, () => t.MaxRows, v => t.MaxRows = (int)v, v => $"{v:0}"));
        body.Children.Add(Toggle("Panel background", "Off (default) = transparent widget with a text shadow. On = one solid rounded box behind it.",
            () => t.ShowPanel, v => Apply(() => t.ShowPanel = v)));

        body.Children.Add(Subhead("Timing"));
        body.Children.Add(Segmented("Alert basis", "Gap: cars appear, escalate, sort and print by on-track gap — the same seconds as the relative box (the lead/imminent sliders read as gap). Countdown: everything by time-to-arrival instead.",
            [("Gap", "Gap"), ("Countdown", "ETA")],
            () => t.ShowTimeToArrival ? "ETA" : "Gap",
            v => Apply(() => t.ShowTimeToArrival = v == "ETA")));
        body.Children.Add(Slider("Lead time", "Alert when a car comes within this many seconds — of on-track gap (Gap basis) or arrival time (Countdown).", 4, 30, 1,
            () => t.AlertLeadTimeSec, v => t.AlertLeadTimeSec = v, v => $"{v:0}s"));
        body.Children.Add(Slider("Blue-flag lead", "More warning when you're the one being lapped.", 5, 40, 1,
            () => t.BlueLeadTimeSec, v => t.BlueLeadTimeSec = v, v => $"{v:0}s"));
        body.Children.Add(Slider("Imminent", "Escalate to the urgent cue inside this many seconds (same basis as above).", 1, 10, 1,
            () => t.ImminentSec, v => t.ImminentSec = v, v => $"{v:0}s"));
        body.Children.Add(Toggle("Warn when lapping traffic", "Alert on slower/lapped cars ahead you're about to lap.",
            () => t.WarnLapping, v => t.WarnLapping = v));
        body.Children.Add(Slider("Lapping gap", "Gap at which the lapping alert fires.", 2, 10, 1,
            () => t.LapTrafficGapSec, v => t.LapTrafficGapSec = v, v => $"{v:0}s"));
        body.Children.Add(Toggle("Warn same-class charger", "Heads-up when a same-class car behind is genuinely catching you — the only same-class threat cue in a single-class pack.",
            () => t.WarnSameClassClosing, v => t.WarnSameClassClosing = v));
        body.Children.Add(Slider("Charger rate", "…show it when catching faster than this (OR the pin below is met).", 0.1, 1.5, 0.1,
            () => t.SameClassClosingRate, v => t.SameClassClosingRate = v, v => $"{v:0.0}/L"));
        body.Children.Add(Toggle("Pin nearby same-class", "Keep a same-class car on the widget while it's within the gap below — catching or not — so a wheel-to-wheel car doesn't vanish when you match its pace.",
            () => t.PinNearbySameClass, v => t.PinNearbySameClass = v));
        body.Children.Add(Slider("Pin within", "That \"nearby\" gap.", 0.5, 5, 0.5,
            () => t.SameClassPinSec, v => t.SameClassPinSec = v, v => $"{v:0.0}s"));

        body.Children.Add(Subhead("Audio"));
        var audio = t.Audio;
        body.Children.Add(Toggle("Audio cues", null, () => audio.Enabled, v => audio.Enabled = v));
        body.Children.Add(Slider("Volume", null, 0, 100, 5, () => audio.Volume, v => audio.Volume = (int)v, v => $"{v:0}%"));
        body.Children.Add(Toggle("Watch chirp", "Rising tone as traffic enters the window.", () => audio.WatchCue, v => audio.WatchCue = v));
        body.Children.Add(Toggle("Imminent beep", "Urgent triple beep.", () => audio.ImminentCue, v => audio.ImminentCue = v));
        body.Children.Add(Toggle("Blue-flag tone", "Calm two-tone when being lapped.", () => audio.BlueCue, v => audio.BlueCue = v));
    }

    private void BuildTrafficContent()
    {
        var t = _edit.Traffic;
        PageBody.Children.Add(Item("iRating", null, () => t.ShowIRating, v => t.ShowIRating = v));
        PageBody.Children.Add(Item("Alongside banner", "Left/right marker on the car that's beside you.",
            () => t.AlongsideBanner, v => t.AlongsideBanner = v, settings: p =>
                p.Children.Add(Toggle("Any car", "Show it for ANY car the spotter reports beside you, not just alerted traffic — for constant same-class side-by-side in a pack.",
                    () => t.AlongsideAnyCar, v => t.AlongsideAnyCar = v))));
    }

    // ---- Fuel ------------------------------------------------------------

    private void BuildFuel()
    {
        var f = _edit.Fuel;
        var body = PageBody;

        body.Children.Add(Slider("Size", "Scales the whole fuel widget.", 0.6, 2.0, 0.05,
            () => f.Scale, v => f.Scale = v, v => $"{v * 100:0}%"));
        body.Children.Add(Slider("Strategy bars", "How many candidate plans to show (races).", 1, 3, 1,
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

    // ---- Fuel table ------------------------------------------------------

    private void BuildFuelTableGeneral()
    {
        var ft = _edit.FuelTable;
        var body = PageBody;

        body.Children.Add(Slider("Size", "Scales the whole fuel table.", 0.6, 2.0, 0.05,
            () => ft.Scale, v => ft.Scale = v, v => $"{v * 100:0}%"));
        body.Children.Add(Toggle("Clickable on widget", "Drop click-through so the target ▲/▼ (and mouse wheel) work during a session. Off = set the target here only; still clickable while overlays are unlocked.",
            () => ft.Interactive, v => Apply(() => { ft.Interactive = v; })));

        body.Children.Add(Subhead("Target — the last value you set wins"));
        body.Children.Add(Segmented("Driven by", "Which number is fixed; the other follows from the usable tank. (Changing a value below also sets this.)",
            [("Laps / stint", "Laps"), ("L per lap", "PerLap")],
            () => ft.TargetMode, v => Apply(() => ft.TargetMode = v)));
        body.Children.Add(Slider("Target laps", "Laps you want out of a full tank (one stint). Sets the target L/lap from the tank.", 1, 80, 1,
            () => ft.TargetLaps, v => { ft.TargetLaps = v; ft.TargetMode = "Laps"; }, v => v < 1 ? "—" : $"{v:0}"));
        body.Children.Add(Slider("Target L/lap", "Litres per lap to aim for. Sets the target laps from the tank.", 0, 8, 0.01,
            () => ft.TargetPerLap, v => { ft.TargetPerLap = v; ft.TargetMode = "PerLap"; }, v => v < 0.01 ? "—" : $"{v:0.00}"));
    }

    private void BuildFuelTableContent()
    {
        var ft = _edit.FuelTable;
        PageBody.Children.Add(Item("Last-5 row", "Average burn over your last 5 laps.", () => ft.ShowLast5, v => ft.ShowLast5 = v));
        PageBody.Children.Add(Item("Last-10 row", null, () => ft.ShowLast10, v => ft.ShowLast10 = v));
        PageBody.Children.Add(Item("Stint row", "Average burn since your last pit stop.", () => ft.ShowStint, v => ft.ShowStint = v));
        PageBody.Children.Add(Item("Target tracker", "The ahead/behind line while a stint is running: how much fuel you've banked or overspent, and the burn you now need to make the target stint.",
            () => ft.ShowStatus, v => ft.ShowStatus = v));
    }

    // ---- Lap Lab ---------------------------------------------------------

    private void BuildLapLab()
    {
        var l = _edit.LapLab;
        var body = PageBody;

        body.Children.Add(Slider("Size", "Scales the whole lap table.", 0.6, 2.0, 0.05,
            () => l.Scale, v => l.Scale = v, v => $"{v * 100:0}%"));
        body.Children.Add(Slider("Laps shown", null, 3, 15, 1,
            () => l.MaxRows, v => l.MaxRows = (int)v, v => $"{v:0}"));
        body.Children.Add(Slider("Gap decimals", null, 1, 3, 1,
            () => l.Decimals, v => l.Decimals = (int)v, v => $"{v:0}"));
        body.Children.Add(Segmented("Columns", "Official sectors, or turn zones detected from the reference lap's speed trace (a chicane or esses complex counts as one T). Switch freely — laps always record both.",
            [("Sectors", "Sectors"), ("Turns", "Turns")],
            () => l.View, v => Apply(() => l.View = v)));
        body.Children.Add(Subhead("Heatmap (% off the reference split)"));
        body.Children.Add(Slider("On pace under", "Within this % of the reference: quiet — improving it is telemetry-at-the-desk territory.", 0.2, 2.0, 0.1,
            () => l.HeatGoodPct, v => l.HeatGoodPct = v, v => $"{v:0.0}%"));
        body.Children.Add(Slider("Full heat at", "The focus band: cells ramp to full red between the two — time you can find on track.", 1.0, 4.0, 0.1,
            () => l.HeatFullPct, v => l.HeatFullPct = v, v => $"{v:0.0}%"));
        body.Children.Add(Slider("Ignore above", "Bigger than this is a mistake or traffic, not pace — shown dim instead of red.", 2, 10, 0.5,
            () => l.HeatIgnorePct, v => l.HeatIgnorePct = v, v => $"{v:0.0}%"));
        body.Children.Add(Toggle("Hide slow laps", "Whole laps slower than the cutoff collapse to one quiet line — traffic, spins, offs.",
            () => l.HideSlowLaps, v => l.HideSlowLaps = v));
        body.Children.Add(Slider("Slow-lap cutoff", null, 102, 112, 1,
            () => l.SlowLapPct, v => l.SlowLapPct = (int)v, v => $"{v:0}%"));

        body.Children.Add(Subhead("Reference lap"));
        body.Children.Add(Segmented("Reference", "Fastest full lap · best sectors combined · your saved best from an earlier session · an imported telemetry lap.",
            [("Best", "SessionBest"), ("Optimal", "SessionOptimal"), ("Prev", "PreviousBest"), ("File", "File")],
            () => l.Reference, v => Apply(() => l.Reference = v)));
        body.Children.Add(FileRow("Reference .ibt", "Telemetry file used when Reference is File — yours, a teammate's, or a Garage 61 / VRS download. Its conditions are checked against the live session.",
            () => l.ReferenceFile, v => l.ReferenceFile = v));
        body.Children.Add(Toggle("Save session best", "Keep your best clean lap per car+track as the next session's Prev reference.",
            () => l.SaveSessionBest, v => l.SaveSessionBest = v));
        body.Children.Add(Slider("Track-temp warning", "Amber chip when the live track temp differs from the reference by more than this.", 1, 15, 1,
            () => l.WarnTrackTempDelta, v => l.WarnTrackTempDelta = v, v => $"{v:0}°C"));
    }

    // ---- About -----------------------------------------------------------

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
        P("Tip: switch on Move overlays (left rail) and drag each widget where you want it. Race, "
          + "qualifying and practice each keep their own layout — pick one under Move overlays to arrange it.", Dim, 12.5, 14);

        PageBody.Children.Add(Divider());
        PageBody.Children.Add(new TextBlock { Text = "config.json", Foreground = Dim, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0) });
        PageBody.Children.Add(new TextBox
        {
            Text = System.IO.Path.Combine(AppContext.BaseDirectory, "config.json"),
            IsReadOnly = true, Style = (Style)FindResource("HexBox"),
            Margin = new Thickness(0, 3, 0, 0), FontSize = 11.5,
        });
    }

    // ---- per-session item rows -------------------------------------------

    /// <summary>The R/Q/P cluster: a master switch (on = shown in at least one session type) plus a
    /// race / quali / practice checkbox each. Master off clears the boxes (remembering them for this
    /// page), master on restores them (or every allowed one). Session types the item can never show
    /// in (<paramref name="allowed"/>) are greyed out and never written as on.</summary>
    private (CheckBox master, FrameworkElement boxes, Action refresh) SessionControls(Func<Sessions> get, Action<Sessions> set, Sessions allowed)
    {
        var master = new CheckBox { Style = (Style)FindResource("Switch") };
        var boxes = new StackPanel { Orientation = Orientation.Horizontal };
        var checks = new List<(CheckBox cb, Sessions bit)>();
        bool sync = false;
        var remembered = Sessions.None;

        void Refresh()
        {
            sync = true;
            var v = get() & allowed;
            master.IsChecked = v != Sessions.None;
            foreach (var (cb, bit) in checks) cb.IsChecked = (v & bit) != 0;
            sync = false;
        }
        void Write(Sessions v)
        {
            Apply(() => set(v & allowed));
            Refresh();
        }

        foreach (var (label, bit, width) in new[] { ("race", Sessions.Race, 66.0), ("quali", Sessions.Qualify, 70.0), ("practice", Sessions.Practice, 84.0) })
        {
            var cb = new CheckBox
            {
                Style = (Style)FindResource("Check"), Content = label, Width = width,
                IsEnabled = (allowed & bit) != 0,
            };
            if (!cb.IsEnabled)
            {
                cb.ToolTip = "Never shown in this session type.";
                ToolTipService.SetShowOnDisabled(cb, true);
            }
            cb.Checked += (_, _) => { if (!sync) Write((get() & allowed) | bit); };
            cb.Unchecked += (_, _) => { if (!sync) Write(get() & allowed & ~bit); };
            checks.Add((cb, bit));
            boxes.Children.Add(cb);
        }
        master.Checked += (_, _) => { if (!sync) Write(remembered != Sessions.None ? remembered : allowed); };
        master.Unchecked += (_, _) =>
        {
            if (sync) return;
            remembered = get() & allowed;
            Write(Sessions.None);
        };

        Refresh();
        return (master, boxes, Refresh);
    }

    /// <summary>A Content/Header row, iOverlay-style: master switch · label · optional "settings"
    /// link · race / quali / practice. The hint shows as a tooltip; <paramref name="settings"/> fills
    /// the item's own options panel (built on first expand, stays open across page rebuilds).</summary>
    private FrameworkElement Item(string label, string? hint, Func<Sessions> get, Action<Sessions> set,
                                  Sessions allowed = Sessions.All, Action<StackPanel>? settings = null)
    {
        var (master, boxes, _) = SessionControls(get, set, allowed);

        var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        master.Margin = new Thickness(0, 0, 14, 0);
        Grid.SetColumn(master, 0);
        grid.Children.Add(master);

        var text = new TextBlock
        {
            Text = label, Foreground = Text, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (!string.IsNullOrEmpty(hint))
        {
            text.ToolTip = hint;
            text.Cursor = System.Windows.Input.Cursors.Help;
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var divider = VDivider();
        Grid.SetColumn(divider, 3);
        grid.Children.Add(divider);
        Grid.SetColumn(boxes, 4);
        grid.Children.Add(boxes);

        var row = new StackPanel();
        row.Children.Add(grid);

        if (settings is not null)
        {
            var link = new Button { Style = (Style)FindResource("SettingsLink"), Content = "settings" };
            Grid.SetColumn(link, 2);
            grid.Children.Add(link);

            var panel = new StackPanel();
            var sub = new Border
            {
                Background = SubPanel, CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 2, 14, 2),
                Margin = new Thickness(54, 0, 0, 10), Child = panel, Visibility = Visibility.Collapsed,
            };
            string key = _section + "/" + label;
            bool built = false;
            void Show(bool open)
            {
                if (open && !built) { settings(panel); built = true; }
                sub.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                link.Content = open ? "hide" : "settings";
            }
            link.Click += (_, _) =>
            {
                bool open = sub.Visibility != Visibility.Visible;
                if (open) _openSettings.Add(key); else _openSettings.Remove(key);
                Show(open);
            };
            if (_openSettings.Contains(key)) Show(true);
            row.Children.Add(sub);
        }

        return new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
    }

    /// <summary>An <see cref="Item"/> for a standings column. Columns are stored per session type
    /// (<see cref="OverlayConfig.Race"/> / Qualify / Practice column sets), so the R/Q/P boxes map
    /// straight onto those three flags.</summary>
    private FrameworkElement Column(string label, string? hint, Func<SessionColumns, bool> get,
                                    Action<SessionColumns, bool> set, Sessions allowed = Sessions.All,
                                    Action<StackPanel>? settings = null)
    {
        var c = _edit;
        return Item(label, hint,
            () => (get(c.Race) ? Sessions.Race : 0) | (get(c.Qualify) ? Sessions.Qualify : 0)
                  | (get(c.Practice) ? Sessions.Practice : 0),
            v =>
            {
                set(c.Race, v.In(SessionKind.Race));
                set(c.Qualify, v.In(SessionKind.Qualify));
                set(c.Practice, v.In(SessionKind.Practice));
            },
            allowed, settings);
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

    private FrameworkElement Toggle(string label, string? hint, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = get() };
        box.Checked += (_, _) => Apply(() => set(true));
        box.Unchecked += (_, _) => Apply(() => set(false));
        return Row(label, hint, box);
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
            rb.Checked += (_, _) => onPick(value);   // caller applies
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

    /// <summary>A row whose control is a single action button.</summary>
    private FrameworkElement ButtonRow(string label, string? hint, string buttonText, Action onClick)
    {
        var btn = new Button { Content = buttonText, Style = (Style)FindResource("LinkButton") };
        btn.Click += (_, _) => onClick();
        return Row(label, hint, btn);
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

    private static FrameworkElement VDivider() => new Border
    {
        Width = 1, Height = 20, Background = Line, Margin = new Thickness(14, 0, 14, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ---- footer actions --------------------------------------------------

    private void OnResetSection(object sender, RoutedEventArgs e)
    {
        var c = _edit;
        // While editing Spectating, "reset" means inherit from In car (copy the base values, which
        // the diff then drops as overrides). While editing In car, it resets to app defaults.
        var src = _editingSpectate ? _cfg.Base : new OverlayConfig();
        switch (_section)
        {
            case "General":
                c.UpdateHz = src.UpdateHz; c.Opacity = src.Opacity; c.FontSize = src.FontSize;
                c.BackgroundColor = src.BackgroundColor; c.AccentColor = src.AccentColor; c.HighlightColor = src.HighlightColor;
                break;
            case "Standings":
                c.ShowStandings = src.ShowStandings;
                c.Scale = src.Scale; c.NameColumnWidth = src.NameColumnWidth; c.HeaderFontSize = src.HeaderFontSize;
                c.SmoothGaps = src.SmoothGaps; c.ShowRejoinState = src.ShowRejoinState; c.StatusStyle = src.StatusStyle;
                c.DriversAtTop = src.DriversAtTop; c.DriversAhead = src.DriversAhead; c.DriversBehind = src.DriversBehind;
                c.MinLeadingCars = src.MinLeadingCars; c.TyreSwitchDisplay = src.TyreSwitchDisplay;
                c.OtherClassesDriversAtTop = src.OtherClassesDriversAtTop; c.DeltaLaps = src.DeltaLaps;
                c.ShowColumnHeader = src.ShowColumnHeader; c.QualifyShowFullClass = src.QualifyShowFullClass;
                c.ShowSof = src.ShowSof; c.ShowRealClock = src.ShowRealClock; c.ShowTimeOfDay = src.ShowTimeOfDay;
                c.ShowTrackTemp = src.ShowTrackTemp; c.ShowTrackTempDecimals = src.ShowTrackTempDecimals;
                c.ShowIncidents = src.ShowIncidents; c.ShowWeather = src.ShowWeather; c.AbbreviateWetness = src.AbbreviateWetness;
                c.WeatherAlertSec = src.WeatherAlertSec; c.TyreSwitchAlertSec = src.TyreSwitchAlertSec; c.ShowWind = src.ShowWind;
                c.GapPrecision = src.GapPrecision; c.IntervalPrecision = src.IntervalPrecision; c.LapTimePrecision = src.LapTimePrecision;
                c.DeltaPrecision = src.DeltaPrecision; c.QualifyGapPrecision = src.QualifyGapPrecision;
                c.Race = JsonClone(src.Race); c.Qualify = JsonClone(src.Qualify); c.Practice = JsonClone(src.Practice);
                break;
            // Widget sections: resetting In car keeps the on-screen positions (defaults only reset
            // behavior); resetting Spectating fully inherits In car, positions included.
            case "Relative": c.Relative = _editingSpectate ? JsonClone(src.Relative) : KeepPos(new RelativeConfig(), c.Relative); break;
            case "Traffic": c.Traffic = _editingSpectate ? JsonClone(src.Traffic) : KeepPos(new TrafficConfig(), c.Traffic); break;
            case "Fuel": c.Fuel = _editingSpectate ? JsonClone(src.Fuel) : KeepPos(new FuelConfig(), c.Fuel); break;
            case "Fuel Table": c.FuelTable = _editingSpectate ? JsonClone(src.FuelTable) : KeepPos(new FuelTableConfig(), c.FuelTable); break;
            case "Lap Lab": c.LapLab = _editingSpectate ? JsonClone(src.LapLab) : KeepPos(new LapLabConfig(), c.LapLab); break;
            case "About": return;
        }
        Apply(() => { });   // mark dirty + schedule save
        RebuildPage();      // rebuild the section from the reset values
    }

    private static T KeepPos<T>(T fresh, T current) where T : WidgetConfig
    {
        fresh.Position = JsonClone(current.Position);
        return fresh;
    }

    /// <summary>Deep copy via a JSON round-trip (independent instance, safe to assign into _edit).</summary>
    private static T JsonClone<T>(T value) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(value))!;

    /// <summary>Copy every widget's on-screen positions (all session types) from one profile to
    /// another (the spectating "Copy layout from In car" button). Positions are ordinary settings.</summary>
    private static void CopyPositions(OverlayConfig from, OverlayConfig to)
    {
        to.Position = JsonClone(from.Position);
        to.Relative.Position = JsonClone(from.Relative.Position);
        to.Traffic.Position = JsonClone(from.Traffic.Position);
        to.Fuel.Position = JsonClone(from.Fuel.Position);
        to.FuelTable.Position = JsonClone(from.FuelTable.Position);
        to.LapLab.Position = JsonClone(from.LapLab.Position);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
