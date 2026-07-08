using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using StandingsOverlay.Config;
using StandingsOverlay.Data;
using StandingsOverlay.Interop;

namespace StandingsOverlay.UI;

/// <summary>
/// The fuel calculator's overlay window: a live-numbers row (fuel, per-lap, laps in tank,
/// target), a one-line plan summary, and Pirelli-style horizontal strategy bars — one row per
/// candidate strategy, past stints dimmed, future stints colored push/save/splash, white ticks
/// at the stops, a ▼ marker at now. Same plumbing as TrafficWindow: click-through topmost,
/// repaints only when the snapshot visually changes. Spec: docs/FUEL-STRATEGY.md.
/// </summary>
public partial class FuelWindow : Window
{
    private static readonly Brush PastBrush = Frozen("#3A3E48");
    private static readonly Brush PushBrush = Frozen("#35A653");
    private static readonly Brush SaveBrush = Frozen("#FFB84D");
    private static readonly Brush SplashBrush = Frozen("#FF4040");
    private static readonly Brush DimBrush = Frozen("#9DA0AA");
    private static readonly Brush TextBrush = Frozen("#E8E9EE");

    private readonly ConfigService _configService;
    private FuelSnapshot? _last;
    private bool _editMode;
    private string _lastLoggedPlan = "";
    private string _lastLoggedRace = "";

    public FuelWindow(ConfigService configService)
    {
        InitializeComponent();
        _configService = configService;
        FontFamily = new FontFamily("Segoe UI");

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

    private void ApplyConfig(OverlayConfig cfg)
    {
        Left = cfg.Fuel.X;
        Top = cfg.Fuel.Y;
        Root.LayoutTransform = RowViewModel.ScaleTransformFor(cfg.Fuel.Scale);
        var accent = RowViewModel.TryBrush(cfg.AccentColor) ?? Brushes.Cyan;
        TargetValue.Foreground = accent;
        EditHint.Foreground = accent;
    }

    /// <summary>Called from the telemetry thread; skips the dispatch when nothing changed.</summary>
    public void OnFuel(FuelSnapshot snapshot)
    {
        // Re-plans are rare (once a lap) and small — log them so strategy behavior is
        // verifiable from overlay.log without screenshot loops.
        if (snapshot.Bars.Count > 0)
        {
            string sig = snapshot.PlanText + " | " + string.Join(" | ", snapshot.Bars.Select(b => $"{b.Label} {b.DeltaText}"));
            if (sig != _lastLoggedPlan)
            {
                _lastLoggedPlan = sig;
                Log.Write($"fuel plan: {sig}");
            }
        }

        // The timed-race estimate updates every lap even without strategy bars — log its
        // transitions so the extra-lap logic is verifiable from overlay.log.
        if (snapshot.RaceText.Length > 0 && snapshot.RaceText != _lastLoggedRace)
        {
            _lastLoggedRace = snapshot.RaceText;
            Log.Write($"race est: {snapshot.RaceText}");
        }

        bool same = snapshot.VisuallyEquals(_last);
        _last = snapshot;
        if (same || _editMode) return;
        Dispatcher.BeginInvoke(() => Render(snapshot));
    }

    private void Render(FuelSnapshot s)
    {
        Panel.Visibility = s.Show ? Visibility.Visible : Visibility.Collapsed;
        if (!s.Show) return;

        FuelValue.Text = s.FuelText;
        PerLapValue.Text = s.PerLapText;
        LapsValue.Text = s.LapsText;
        TargetValue.Text = s.TargetText;
        TargetValue.Visibility = s.TargetText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FinishLine.Text = s.FinishText;
        FinishLine.Visibility = s.FinishText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FinishLine.Foreground = s.FinishEmphasis >= 2 ? SplashBrush : s.FinishEmphasis == 1 ? SaveBrush : PushBrush;
        RaceLine.Text = s.RaceText;
        RaceLine.Visibility = s.RaceText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RaceLine.Foreground = s.RaceEmphasis >= 2 ? SplashBrush : s.RaceEmphasis == 1 ? SaveBrush : TextBrush;
        PlanLine.Text = s.PlanText;
        PlanLine.Visibility = s.PlanText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        BarsPanel.Children.Clear();
        BarsPanel.Visibility = s.Bars.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        double width = Math.Max(120, _configService.Current.Fuel.BarWidth);
        foreach (var bar in s.Bars)
            BarsPanel.Children.Add(BuildBarRow(bar, s.NowFrac, width));
    }

    private static FrameworkElement BuildBarRow(FuelStrategyBar bar, double nowFrac, double width)
    {
        const double h = 13;
        var canvas = new Canvas { Width = width, Height = h + 4, VerticalAlignment = VerticalAlignment.Center };

        double x = 0;
        foreach (var seg in bar.Segs)
        {
            double w = seg.Frac * width;
            if (w <= 0.5) { x += w; continue; }
            var rect = new Rectangle
            {
                Width = Math.Max(1, w - 1),   // 1px gap = the stop tick between stints
                Height = h,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = seg.Kind switch
                {
                    FuelSegKind.Past => PastBrush,
                    FuelSegKind.Save => SaveBrush,
                    FuelSegKind.Splash => SplashBrush,
                    _ => PushBrush,
                },
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, 4);
            canvas.Children.Add(rect);
            x += w;
        }

        // Now marker: a small ▼ riding on top of the bar.
        var now = new TextBlock
        {
            Text = "▼", FontSize = 8, Foreground = Brushes.White,
        };
        Canvas.SetLeft(now, Math.Clamp(nowFrac * width - 4, 0, width - 8));
        Canvas.SetTop(now, -3);
        canvas.Children.Add(now);

        var label = new TextBlock
        {
            Text = bar.Label, Foreground = TextBrush,
            FontFamily = new FontFamily("Consolas"), FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        var delta = new TextBlock
        {
            Text = bar.DeltaText,
            Foreground = bar.DeltaText == "fastest" ? PushBrush : DimBrush,
            FontFamily = new FontFamily("Consolas"), FontSize = 11,
            Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(canvas);
        row.Children.Add(label);
        row.Children.Add(delta);
        return row;
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
                _configService.Current.Fuel.X = Left;
                _configService.Current.Fuel.Y = Top;
                _configService.Save();
                Render(_last ?? FuelSnapshot.Empty);
            }
        }
    }

    /// <summary>Edit mode shows the 18h-of-Spa fork so there is something real to position.</summary>
    private void RenderSample()
    {
        Render(new FuelSnapshot(
            Show: true,
            FuelText: "43.2L", PerLapText: "2.31/lap", LapsText: "18.7 laps", TargetText: "tgt 2.21",
            PlanText: "next stop ~L168 · add 74L · 214 laps to go",
            RaceText: "≈24(+1?) laps · you 12 to go · extra lap likely — fuel for 13",
            RaceEmphasis: 2,
            FinishText: "finish on 41.8L · carrying 5.6L extra (2.4 laps)",
            FinishEmphasis: 1,
            NowFrac: 0.75,
            Bars:
            [
                new FuelStrategyBar("A  push · 3 stops", "fastest",
                [
                    new FuelSeg(0.25, FuelSegKind.Past), new FuelSeg(0.25, FuelSegKind.Past),
                    new FuelSeg(0.25, FuelSegKind.Past),
                    new FuelSeg(0.08, FuelSegKind.Push), new FuelSeg(0.08, FuelSegKind.Push),
                    new FuelSeg(0.06, FuelSegKind.Push), new FuelSeg(0.03, FuelSegKind.Splash),
                ]),
                new FuelStrategyBar("B  save 0.11/lap · 2 stops", "+14s",
                [
                    new FuelSeg(0.25, FuelSegKind.Past), new FuelSeg(0.25, FuelSegKind.Past),
                    new FuelSeg(0.25, FuelSegKind.Past),
                    new FuelSeg(0.09, FuelSegKind.Save), new FuelSeg(0.09, FuelSegKind.Save),
                    new FuelSeg(0.07, FuelSegKind.Push),
                ]),
            ]));
    }

    private static Brush Frozen(string hex) => RowViewModel.TryBrush(hex) ?? Brushes.Gray;
}
