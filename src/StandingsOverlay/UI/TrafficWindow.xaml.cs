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
    string CarNumber, string Name, string IRatingText, string SubText,
    string TtaText, string RateText, string Chevrons, string TrainText,
    Visibility BlueTagVisibility, Visibility TrainVisibility,
    bool IsImminent, GridLength BarStar, GridLength BarRestStar)
{
    public static TrafficRowViewModel From(TrafficRow r)
    {
        var classBrush = Frozen(r.ClassColor);
        var ttaBrush = r.Phase == TrafficPhase.Imminent ? DangerBrush
                     : r.IsBlue ? BlueBrush : WarnBrush;
        return new TrafficRowViewModel(
            StripeBrush: r.IsBlue ? BlueBrush : classBrush,
            NumBrush: classBrush,
            TtaBrush: ttaBrush,
            BlueTagBrush: BlueTagStripes,
            CarNumber: r.CarNumber,
            Name: r.Name,
            IRatingText: r.IRatingText,
            SubText: r.SubText,
            TtaText: r.TtaText,
            RateText: r.RateText,
            Chevrons: r.Chevrons,
            TrainText: $"×{r.TrainCount}",
            BlueTagVisibility: r.IsBlue ? Visibility.Visible : Visibility.Collapsed,
            TrainVisibility: r.TrainCount > 1 ? Visibility.Visible : Visibility.Collapsed,
            IsImminent: r.Phase == TrafficPhase.Imminent,
            BarStar: new GridLength(r.BarPct, GridUnitType.Star),
            BarRestStar: new GridLength(Math.Max(0.001, 1 - r.BarPct), GridUnitType.Star));
    }

    internal static readonly Brush WarnBrush = Frozen("#FFB84D");
    internal static readonly Brush DangerBrush = Frozen("#FF4040");
    internal static readonly Brush BlueBrush = Frozen("#2F7BFF");
    internal static readonly Brush GainBrush = Frozen("#4CFF6A");
    internal static readonly Brush DimBrush = Frozen("#9DA0AA");

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
        Left = cfg.Traffic.X;
        Top = cfg.Traffic.Y;
        Root.LayoutTransform = RowViewModel.ScaleTransformFor(cfg.Traffic.Scale);
        var accent = RowViewModel.TryBrush(cfg.AccentColor) ?? Brushes.Cyan;
        YouBar.Background = accent;
        EditHint.Foreground = accent;
    }

    /// <summary>Called from the telemetry thread; skips the dispatch when nothing visual changed.</summary>
    public void OnTraffic(TrafficSnapshot snapshot)
    {
        bool same = snapshot.VisuallyEquals(_last);
        _last = snapshot;
        if (same || _editMode) return;
        Dispatcher.BeginInvoke(() => Render(snapshot));
    }

    private void Render(TrafficSnapshot s)
    {
        var tc = _configService.Current.Traffic;

        bool alongside = s.Alongside != AlongsideDir.None;
        AlongsideBanner.Visibility = alongside ? Visibility.Visible : Visibility.Collapsed;
        if (alongside)
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

        ClearBanner.Visibility = s.ClearFlash && !alongside ? Visibility.Visible : Visibility.Collapsed;

        bool beacon = tc.BeaconStyle;
        bool showRows = !alongside && s.Rows.Count > 0;

        RowsControl.Visibility = showRows && !beacon ? Visibility.Visible : Visibility.Collapsed;
        OverflowChip.Visibility = showRows && !beacon && s.Overflow > 0 ? Visibility.Visible : Visibility.Collapsed;
        BeaconPanel.Visibility = showRows && beacon ? Visibility.Visible : Visibility.Collapsed;

        if (showRows && !beacon)
        {
            RowsControl.ItemsSource = s.Rows.Select(TrafficRowViewModel.From).ToList();
            if (s.Overflow > 0) OverflowText.Text = $"+{s.Overflow} more in window";
            StopChevrons();
        }
        else if (showRows)
        {
            RenderBeacon(s);
        }
        else
        {
            StopChevrons();
        }
    }

    private void RenderBeacon(TrafficSnapshot s)
    {
        var head = s.Rows[0];
        bool imminent = head.Phase == TrafficPhase.Imminent;
        var classBrush = TrafficRowViewModel.Frozen(head.ClassColor);
        var ttaBrush = imminent ? TrafficRowViewModel.DangerBrush
                     : head.IsBlue ? TrafficRowViewModel.BlueBrush : TrafficRowViewModel.WarnBrush;

        // Class name comes from SubText's first segment ("GTP · P2 in class" → "GTP").
        string className = head.SubText.Split('·')[0].Trim();
        if (head.IsBlue)
        {
            BeaconClassBorder.Background = TrafficRowViewModel.BlueBrush;
            BeaconClassText.Foreground = Brushes.White;
            BeaconClassText.Text = $"{className} · BLUE · #{head.CarNumber}";
        }
        else
        {
            BeaconClassBorder.Background = classBrush;
            BeaconClassText.Foreground = TrafficRowViewModel.Frozen("#17171D");
            BeaconClassText.Text = head.TrainCount > 1
                ? $"{className} ×{head.TrainCount} · #{head.CarNumber}"
                : $"{className} · #{head.CarNumber}";
        }

        BeaconTta.Text = head.TtaText;
        BeaconTta.Foreground = ttaBrush;
        BeaconRate.Text = string.IsNullOrEmpty(head.RateText) ? head.Chevrons : $"{head.Chevrons} {head.RateText}";

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
        StartChevrons(head.Chevrons, ttaBrush);
    }

    /// <summary>Chevron rain: three glyphs falling toward the YOU bar, faster when the catch
    /// rate is higher. The storyboard only restarts when speed bucket or color changes.</summary>
    private void StartChevrons(string bucket, Brush brush)
    {
        string state = bucket + brush.GetHashCode();
        if (state == _chevronState) return;
        _chevronState = state;

        double dur = bucket.Length >= 3 ? 0.55 : bucket.Length == 2 ? 0.8 : 1.15;
        for (int i = 0; i < _chevrons.Length; i++)
        {
            _chevrons[i].Foreground = brush;
            _chevrons[i].Opacity = 0.9;
            var anim = new DoubleAnimation(-14, 40, TimeSpan.FromSeconds(dur))
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
                new TrafficRow(0, TrafficPhase.Imminent, false, "#E33241", "07", "R. Vergne", "4.2k",
                               "GTP · P2 in class", "3.2", "+7.1s/lap", "▾▾▾", 2, 0.73),
                new TrafficRow(1, TrafficPhase.Watch, false, "#E33241", "22", "S. Okafor", "3.1k",
                               "GTP · P3 in class", "4.5", "+6.8s/lap", "▾▾▾", 1, 0.62),
                new TrafficRow(2, TrafficPhase.Watch, true, "#FFDA59", "11", "M. Rossi", "5.6k",
                               "GT3 · leader · +1 lap", "11.0", "+2.1s/lap", "▾", 1, 0.30),
            ],
            Overflow: 0, Alongside: AlongsideDir.None, ClearFlash: false, Cues: TrafficCues.None));
    }
}
