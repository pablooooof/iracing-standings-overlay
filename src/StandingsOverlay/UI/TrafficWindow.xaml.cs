using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using StandingsOverlay.Config;
using StandingsOverlay.Data;
using StandingsOverlay.Interop;

namespace StandingsOverlay.UI;

/// <summary>Display-ready row for the Row style's ItemsControl template.</summary>
public sealed record TrafficRowViewModel(
    Brush StripeBrush, Brush NumBrush, Brush TtaBrush, Brush BlueTagBrush,
    Brush PaceBrush, Brush PulseBrush, Brush DirBrush, Brush BarRestBrush, Brush AlongBrush,
    string DirGlyph,
    string CarNumber, string Name, string IRatingText, string SubText,
    string TtaText, string PaceText, string Chevrons, string TrainText,
    Visibility BlueTagVisibility, Visibility TrainVisibility, Visibility DirVisibility, Visibility ContentVisibility,
    Visibility AlongBorderVisibility, Visibility AlongLeftVisibility, Visibility AlongRightVisibility,
    bool IsImminent, GridLength BarStar, GridLength BarRestStar)
{
    /// <summary>An empty fixed-height spacer, so the AHEAD/BEHIND slot count (and the YOU line) stay
    /// put even when fewer cars are present.</summary>
    public static readonly TrafficRowViewModel Blank = new(
        Brushes.Transparent, Brushes.Transparent, Brushes.Transparent, Brushes.Transparent,
        Brushes.Transparent, Brushes.Transparent, Brushes.Transparent, Brushes.Transparent,
        Brushes.Transparent, "", "", "", "", "", "", "", "", "",
        Visibility.Collapsed, Visibility.Collapsed, Visibility.Collapsed, Visibility.Collapsed,
        Visibility.Collapsed, Visibility.Collapsed, Visibility.Collapsed,
        false, new GridLength(0, GridUnitType.Star), new GridLength(1, GridUnitType.Star));

    /// <summary>Build a row VM. <paramref name="showCaret"/> shows the ▲/▼ direction caret — only in
    /// the flat (ungrouped) list; the split "me in the middle" layout conveys direction by position.
    /// <paramref name="along"/> marks this as the car overlapping you (spotter left/right).</summary>
    public static TrafficRowViewModel From(TrafficRow r, bool showCaret, AlongsideDir along = AlongsideDir.None)
    {
        var classBrush = Frozen(r.ClassColor);
        var ttaBrush = r.Phase == TrafficPhase.Imminent ? DangerBrush
                     : r.IsBlue ? BlueBrush : r.IsLapping ? GainBrush : WarnBrush;
        // Direction color: what kind of threat and which way. Blue = being lapped (yield),
        // amber = faster/charger closing from behind, green = a slower car ahead you're catching.
        var dirBrush = r.IsBlue ? BlueBrush : r.FromBehind ? WarnBrush : GainBrush;
        bool left = along is AlongsideDir.Left or AlongsideDir.TwoLeft or AlongsideDir.Both;
        bool right = along is AlongsideDir.Right or AlongsideDir.TwoRight or AlongsideDir.Both;
        return new TrafficRowViewModel(
            StripeBrush: r.IsBlue ? BlueBrush : r.IsLapping ? GainBrush : classBrush,
            NumBrush: classBrush,
            TtaBrush: ttaBrush,
            BlueTagBrush: BlueTagStripes,
            PaceBrush: PaceBrushFor(r.PaceSign),
            // Blue rows pulse BLUE at imminence — an all-red pulse used to hide what kind of
            // alert was underneath.
            PulseBrush: r.IsBlue ? BlueBrush : DangerBrush,
            DirBrush: dirBrush,
            BarRestBrush: BarTrackBrush,
            AlongBrush: AlongsideMarkBrush,
            // ▲ = coming up behind you (mirrors) · ▼ = ahead, you're reeling it in.
            DirGlyph: r.FromBehind ? "▲" : "▼",
            CarNumber: r.CarNumber,
            Name: r.Name,
            IRatingText: r.IRatingText,
            SubText: r.SubText,
            TtaText: r.TtaText,
            PaceText: r.PaceText,
            Chevrons: r.Chevrons,
            TrainText: $"×{r.TrainCount}",
            BlueTagVisibility: r.IsBlue ? Visibility.Visible : Visibility.Collapsed,
            TrainVisibility: r.TrainCount > 1 ? Visibility.Visible : Visibility.Collapsed,
            DirVisibility: showCaret ? Visibility.Visible : Visibility.Collapsed,
            ContentVisibility: Visibility.Visible,
            AlongBorderVisibility: along != AlongsideDir.None ? Visibility.Visible : Visibility.Collapsed,
            AlongLeftVisibility: left ? Visibility.Visible : Visibility.Collapsed,
            AlongRightVisibility: right ? Visibility.Visible : Visibility.Collapsed,
            IsImminent: r.Phase == TrafficPhase.Imminent,
            BarStar: new GridLength(r.BarPct, GridUnitType.Star),
            BarRestStar: new GridLength(Math.Max(0.001, 1 - r.BarPct), GridUnitType.Star));
    }

    /// <summary>Same colors as the relative's pace arrows so the widgets agree.</summary>
    internal static Brush PaceBrushFor(int sign) =>
        sign > 0 ? LossRed : sign < 0 ? GainBrush : SamePaceYellow;

    internal static readonly Brush WarnBrush = Frozen("#FFB84D");
    internal static readonly Brush DangerBrush = Frozen("#FF4040");
    internal static readonly Brush BlueBrush = Frozen("#2F7BFF");
    internal static readonly Brush GainBrush = Frozen("#4CFF6A");
    internal static readonly Brush DimBrush = Frozen("#9DA0AA");
    internal static readonly Brush LossRed = Frozen("#FF5C5C");
    internal static readonly Brush SamePaceYellow = Frozen("#FFD34D");
    internal static readonly Brush BarTrackBrush = Frozen("#22FFFFFF");   // unfilled part of the proximity bar
    internal static readonly Brush AlongsideMarkBrush = Frozen("#FFFFFF"); // "car beside you" edge + arrows

    /// <summary>The actual blue flag: blue with diagonal yellow stripes.</summary>
    internal static readonly Brush BlueTagStripes = MakeStripes();

    private static Brush MakeStripes()
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(7, 7),
            MappingMode = BrushMappingMode.Absolute,
            SpreadMethod = GradientSpreadMethod.Repeat,
        };
        var blue = (Color)ColorConverter.ConvertFromString("#2F7BFF");
        var gold = (Color)ColorConverter.ConvertFromString("#FFD34D");
        b.GradientStops.Add(new GradientStop(blue, 0.0));
        b.GradientStops.Add(new GradientStop(blue, 0.68));
        b.GradientStops.Add(new GradientStop(gold, 0.68));
        b.GradientStops.Add(new GradientStop(gold, 1.0));
        b.Freeze();
        return b;
    }

    internal static Brush Frozen(string hex)
    {
        var brush = RowViewModel.TryBrush(hex) ?? DimBrush;
        return brush;
    }
}

/// <summary>
/// The traffic alerter's own overlay window: click-through/topmost like the standings table,
/// independently positioned (edit mode drags both). Renders TrafficSnapshots in one of two
/// styles — "Row" (stacked info rows) or "Beacon" (giant TTA + chevron rain) — plus the
/// ALONGSIDE and CLEAR banners shared by both. Repaints only when the snapshot visually
/// changes; with no traffic every element is collapsed and nothing runs.
/// </summary>
public partial class TrafficWindow : Window
{
    private readonly ConfigService _configService;
    private TrafficSnapshot? _last;
    private bool _editMode;
    private string _chevronState = "";   // color+bucket of the running rain animation
    private readonly TextBlock[] _chevrons = new TextBlock[3];

    public TrafficWindow(ConfigService configService)
    {
        InitializeComponent();
        _configService = configService;

        FontFamily = new FontFamily("Segoe UI");

        for (int i = 0; i < _chevrons.Length; i++)
        {
            _chevrons[i] = new TextBlock
            {
                Text = "▾",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                RenderTransform = new TranslateTransform(),
            };
            Canvas.SetLeft(_chevrons[i], 61);
            ChevronCanvas.Children.Add(_chevrons[i]);
        }

        SourceInitialized += (_, _) => Win32.ApplyOverlayStyle(this, clickThrough: true);
        MouseLeftButtonDown += (_, e) =>
        {
            if (_editMode && e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        ApplyConfig(configService.Current);
        configService.Changed += cfg => Dispatcher.BeginInvoke(() =>
        {
            ApplyConfig(cfg);
            if (_editMode) RenderSample();
            else if (_last is not null) Render(_last);
        });
    }

    public void ApplyConfig(OverlayConfig cfg)
    {
        var tc = cfg.Traffic;
        Left = tc.X;
        Top = tc.Y;
        Root.LayoutTransform = RowViewModel.ScaleTransformFor(tc.Scale);
        var accent = RowViewModel.TryBrush(cfg.AccentColor) ?? Brushes.Cyan;
        YouBar.Background = accent;
        YouLine.Background = accent;
        YouTag.Background = accent;
        EditHint.Foreground = accent;

        // Frameless (default): the widget is transparent; a soft shadow keeps text legible over a
        // bright track. Panel mode: one solid rounded box behind everything, so no shadow needed.
        bool panel = tc.ShowPanel && !tc.BeaconStyle;
        WidgetPanel.Visibility = panel ? Visibility.Visible : Visibility.Collapsed;
        Root.Effect = panel ? null : _textShadow;
    }

    private static readonly System.Windows.Media.Effects.DropShadowEffect _textShadow = Freeze();
    private static System.Windows.Media.Effects.DropShadowEffect Freeze()
    {
        var e = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black, ShadowDepth = 0, BlurRadius = 5, Opacity = 0.95,
        };
        e.Freeze();
        return e;
    }

    /// <summary>Called from the telemetry thread; skips the dispatch when nothing visual changed.</summary>
    public void OnTraffic(TrafficSnapshot snapshot)
    {
        bool same = snapshot.VisuallyEquals(_last);
        _last = snapshot;
        if (same || _editMode) return;
        Dispatcher.BeginInvoke(() => Render(snapshot), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Render(TrafficSnapshot s)
    {
        var tc = _configService.Current.Traffic;
        bool beacon = tc.BeaconStyle;
        bool showRows = s.Rows.Count > 0;
        bool split = !beacon && tc.GroupByDirection;

        // The car overlapping you is marked on its own row (below), so the full-width banner is only
        // a fallback for when that car isn't one of the listed rows (e.g. an "any car" pack overlap).
        bool alongsideOnRow = s.Alongside != AlongsideDir.None && s.AlongsideCarIdx >= 0 &&
                              s.Rows.Any(r => r.CarIdx == s.AlongsideCarIdx);
        bool alongsideBanner = s.Alongside != AlongsideDir.None && !alongsideOnRow;
        AlongsideBanner.Visibility = alongsideBanner ? Visibility.Visible : Visibility.Collapsed;
        if (alongsideBanner)
        {
            AlongsideText.Text = s.Alongside switch
            {
                AlongsideDir.Left => "◀   CAR LEFT",
                AlongsideDir.Right => "CAR RIGHT   ▶",
                AlongsideDir.Both => "◀  CARS BOTH SIDES  ▶",
                AlongsideDir.TwoLeft => "◀◀   TWO LEFT",
                _ => "TWO RIGHT   ▶▶",
            };
            Pulse(AlongsideBanner, on: true, fast: true);
        }
        else Pulse(AlongsideBanner, on: false);

        // CLEAR coexists with the rows now (a slim chip), never blanking the widget.
        ClearBanner.Visibility = s.ClearFlash ? Visibility.Visible : Visibility.Collapsed;

        OverflowChip.Visibility = showRows && !beacon && s.Overflow > 0 ? Visibility.Visible : Visibility.Collapsed;
        BeaconPanel.Visibility = showRows && beacon ? Visibility.Visible : Visibility.Collapsed;

        AlongsideDir AlongFor(int carIdx) =>
            alongsideOnRow && carIdx == s.AlongsideCarIdx ? s.Alongside : AlongsideDir.None;

        if (split)
        {
            // "Me in the middle": fixed slots so the YOU line never moves. Cars AHEAD fill from the
            // line upward (nearest just above it, blanks pad the top); cars BEHIND fill from the line
            // downward (nearest just below it, blanks pad the bottom). Direction is read by position,
            // so the per-row carets are dropped.
            var aheadCars = s.Rows.Where(r => !r.FromBehind).ToList();   // nearest-first
            var behindCars = s.Rows.Where(r => r.FromBehind).ToList();   // nearest-first
            int na = Math.Max(1, tc.SlotsAhead), nb = Math.Max(1, tc.SlotsBehind);

            var ahead = new List<TrafficRowViewModel>();
            for (int i = 0; i < na - aheadCars.Count; i++) ahead.Add(TrafficRowViewModel.Blank);
            for (int i = Math.Min(aheadCars.Count, na) - 1; i >= 0; i--)   // furthest at top → nearest above line
                ahead.Add(TrafficRowViewModel.From(aheadCars[i], showCaret: false, AlongFor(aheadCars[i].CarIdx)));

            var behind = new List<TrafficRowViewModel>();
            for (int i = 0; i < Math.Min(behindCars.Count, nb); i++)       // nearest below line → furthest
                behind.Add(TrafficRowViewModel.From(behindCars[i], showCaret: false, AlongFor(behindCars[i].CarIdx)));
            for (int i = 0; i < nb - behindCars.Count; i++) behind.Add(TrafficRowViewModel.Blank);

            AheadControl.ItemsSource = ahead;
            RowsControl.ItemsSource = behind;
            AheadControl.Visibility = RowsControl.Visibility = YouDivider.Visibility = Visibility.Visible;
            if (s.Overflow > 0) OverflowText.Text = $"+{s.Overflow} more in window";
            StopChevrons();
        }
        else if (!beacon)
        {
            // Flat list (grouping off): one metric-sorted list, each row keeping its ▲/▼ caret.
            RowsControl.ItemsSource = s.Rows.Select(r =>
                TrafficRowViewModel.From(r, showCaret: true, AlongFor(r.CarIdx))).ToList();
            RowsControl.Visibility = showRows ? Visibility.Visible : Visibility.Collapsed;
            AheadControl.Visibility = YouDivider.Visibility = Visibility.Collapsed;
            if (s.Overflow > 0) OverflowText.Text = $"+{s.Overflow} more in window";
            StopChevrons();
        }
        else if (showRows)
        {
            RowsControl.Visibility = AheadControl.Visibility = YouDivider.Visibility = Visibility.Collapsed;
            RenderBeacon(s);
        }
        else
        {
            RowsControl.Visibility = AheadControl.Visibility = YouDivider.Visibility = Visibility.Collapsed;
            StopChevrons();
        }
    }

    private void RenderBeacon(TrafficSnapshot s)
    {
        var head = s.Rows[0];
        bool imminent = head.Phase == TrafficPhase.Imminent;
        var classBrush = TrafficRowViewModel.Frozen(head.ClassColor);
        var ttaBrush = imminent ? TrafficRowViewModel.DangerBrush
                     : head.IsBlue ? TrafficRowViewModel.BlueBrush
                     : head.IsLapping ? TrafficRowViewModel.GainBrush : TrafficRowViewModel.WarnBrush;

        // Direction glyph so a glance says which way the threat is: ▲ = closing from behind
        // (mirrors), ▼ = ahead and you're catching it.
        string dir = head.FromBehind ? "▲ " : "▼ ";

        // Class name comes from SubText's first segment ("GTP · P2 in class" → "GTP").
        string className = head.SubText.Split('·')[0].Trim();
        if (head.IsBlue)
        {
            BeaconClassBorder.Background = TrafficRowViewModel.BlueBrush;
            BeaconClassText.Foreground = Brushes.White;
            BeaconClassText.Text = $"{dir}{className} · BLUE · {head.CarNumber}";
        }
        else
        {
            BeaconClassBorder.Background = classBrush;
            BeaconClassText.Foreground = TrafficRowViewModel.Frozen("#17171D");
            BeaconClassText.Text = head.TrainCount > 1
                ? $"{dir}{className} ×{head.TrainCount} · {head.CarNumber}"
                : $"{dir}{className} · {head.CarNumber}";
        }

        BeaconTta.Text = head.TtaText;
        BeaconTta.Foreground = ttaBrush;
        BeaconRate.Text = string.IsNullOrEmpty(head.PaceText) ? head.Chevrons : $"{head.Chevrons} {head.PaceText}";
        BeaconRate.Foreground = string.IsNullOrEmpty(head.PaceText)
            ? TrafficRowViewModel.DimBrush : TrafficRowViewModel.PaceBrushFor(head.PaceSign);
        BeaconPulse.BorderBrush = head.IsBlue ? TrafficRowViewModel.BlueBrush : TrafficRowViewModel.DangerBrush;

        // Queue strip: everything behind the headline as class-colored chips with their TTA.
        BeaconQueue.Children.Clear();
        foreach (var r in s.Rows.Skip(1).Take(2))
        {
            BeaconQueue.Children.Add(new Border
            {
                Width = 9, Height = 9, CornerRadius = new CornerRadius(2),
                Background = r.IsBlue ? TrafficRowViewModel.BlueTagStripes : TrafficRowViewModel.Frozen(r.ClassColor),
                Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            BeaconQueue.Children.Add(new TextBlock
            {
                Text = r.TtaText, FontFamily = new FontFamily("Consolas"), FontSize = 10,
                Foreground = TrafficRowViewModel.DimBrush,
                Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        }
        int extra = s.Overflow + Math.Max(0, s.Rows.Count - 3);
        if (extra > 0)
        {
            BeaconQueue.Children.Add(new TextBlock
            {
                Text = $"+{extra}", FontFamily = new FontFamily("Consolas"), FontSize = 10,
                Foreground = TrafficRowViewModel.DimBrush, VerticalAlignment = VerticalAlignment.Center,
            });
        }
        BeaconQueue.Visibility = BeaconQueue.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Pulse(BeaconPulse, on: imminent);
        // Rain falls toward the YOU bar for a car closing from behind; for a car ahead it rises
        // away from YOU (you're the one climbing toward it), so the motion tells the direction too.
        StartChevrons(head.Chevrons, ttaBrush, rise: !head.FromBehind);
    }

    /// <summary>Chevron rain: three glyphs sliding toward (behind) or away from (ahead) the YOU
    /// bar, faster when the catch rate is higher. Restarts only when bucket, color or direction
    /// changes.</summary>
    private void StartChevrons(string bucket, Brush brush, bool rise)
    {
        string state = bucket + brush.GetHashCode() + (rise ? "^" : "v");
        if (state == _chevronState) return;
        _chevronState = state;

        double dur = bucket.Length >= 3 ? 0.55 : bucket.Length == 2 ? 0.8 : 1.15;
        (double from, double to) = rise ? (40.0, -14.0) : (-14.0, 40.0);
        for (int i = 0; i < _chevrons.Length; i++)
        {
            _chevrons[i].Text = rise ? "▴" : "▾";
            _chevrons[i].Foreground = brush;
            _chevrons[i].Opacity = 0.9;
            var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(dur))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(dur * i / _chevrons.Length),
            };
            ((TranslateTransform)_chevrons[i].RenderTransform).BeginAnimation(TranslateTransform.YProperty, anim);
        }
    }

    private void StopChevrons()
    {
        if (_chevronState.Length == 0) return;
        _chevronState = "";
        foreach (var c in _chevrons)
            ((TranslateTransform)c.RenderTransform).BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void Pulse(Border b, bool on, bool fast = false)
    {
        if (on)
        {
            var anim = new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(fast ? 0.22 : 0.35))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                AutoReverse = true,
            };
            b.BeginAnimation(OpacityProperty, anim);
        }
        else
        {
            b.BeginAnimation(OpacityProperty, null);
            b.Opacity = b == BeaconPulse ? 0 : 1;   // the beacon's red edge rests hidden
        }
    }

    public bool EditMode
    {
        get => _editMode;
        set
        {
            _editMode = value;
            Win32.ApplyOverlayStyle(this, clickThrough: !value);
            EditHint.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value)
            {
                RenderSample();
            }
            else
            {
                _configService.Current.Traffic.X = Left;
                _configService.Current.Traffic.Y = Top;
                _configService.Save();
                Render(_last ?? TrafficSnapshot.Empty);
            }
        }
    }

    /// <summary>Edit mode shows representative alerts so there is something to position.</summary>
    private void RenderSample()
    {
        Render(new TrafficSnapshot(
            Rows:
            [
                // Behind block (approaching you): faster class closing, then a leader lapping you.
                new TrafficRow(0, TrafficPhase.Imminent, false, false, true, "#FFD24D", "P2", "R. Vergne", "4.2k",
                               "GTP · #07", "3.2", "▲", 1, "▾▾▾", 1, 0.73),
                new TrafficRow(1, TrafficPhase.Watch, false, false, true, "#FFD24D", "P3", "S. Okafor", "3.1k",
                               "GTP · #22", "4.5", "▲", 1, "▾▾▾", 1, 0.62),
                new TrafficRow(2, TrafficPhase.Watch, true, false, true, "#FF5FA8", "P1", "M. Rossi", "5.6k",
                               "GT3 · #11 · +1 lap", "9.0", "►", 0, "▾", 1, 0.30),
                // Ahead block (you're catching): a slower car you're about to lap.
                new TrafficRow(3, TrafficPhase.Watch, false, true, false, "#57C1FF", "P4", "L. Tanaka", "1.9k",
                               "GT4 · #88 · lapping", "4.1", "▼", -1, "▾▾", 1, 0.35),
            ],
            // Mark car #22 (idx 1) as the one overlapping on your left — shown on its own row.
            Overflow: 0, Alongside: AlongsideDir.Left, AlongsideCarIdx: 1, ClearFlash: false, Cues: TrafficCues.None));
    }
}
