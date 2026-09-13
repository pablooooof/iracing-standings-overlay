using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StandingsOverlay.Config;
using StandingsOverlay.Data;
using StandingsOverlay.Interop;

namespace StandingsOverlay.UI;

/// <summary>
/// The fuel consumption table (Last / Last-5 / Last-10 / Stint / Target × usage + laps-to-empty),
/// with a live E—F gauge, a laps-remaining range, and a target-vs-actual stint tracker that shows
/// whether you're banking or overspending and the average burn that still makes the target stint.
/// The target is two interlocked numbers (laps ↔ L/lap, tied by the tank); it is set in the
/// settings window and — when "Interactive" is on (or in edit mode) — with ▲/▼ on the widget.
/// Same plumbing as the other widgets: click-through topmost, repaints only on change. Works in
/// both practice and race. Spec: docs/FUEL-STRATEGY.md.
/// </summary>
public partial class FuelTableWindow : Window
{
    private static readonly Brush Text = Frozen("#E8E9EE");
    private static readonly Brush Dim = Frozen("#9DA0AA");
    private static readonly Brush Green = Frozen("#35A653");
    private static readonly Brush Red = Frozen("#FF4040");
    private static readonly Brush AheadBg = Frozen("#3335A653");
    private static readonly Brush AheadFg = Frozen("#8FE3AE");
    private static readonly Brush BehindBg = Frozen("#33FF4040");
    private static readonly Brush BehindFg = Frozen("#FF9C9C");

    private readonly ConfigService _configService;
    private FuelTableSnapshot? _last;
    private bool _editMode;
    private Brush _accent = Brushes.Orange;

    public FuelTableWindow(ConfigService configService)
    {
        InitializeComponent();
        _configService = configService;
        FontFamily = new FontFamily("Segoe UI");

        SourceInitialized += (_, _) => ApplyClickThrough();
        MouseLeftButtonDown += (_, e) =>
        {
            if (_editMode && e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        ApplyConfig(configService.Current);
        configService.Changed += cfg => Dispatcher.BeginInvoke(() =>
        {
            ApplyConfig(cfg);
            if (_editMode) RenderSample();
            else Render(_last ?? FuelTableSnapshot.Empty);
        });
    }

    private void ApplyConfig(OverlayConfig cfg)
    {
        Left = cfg.FuelTable.X;
        Top = cfg.FuelTable.Y;
        Root.LayoutTransform = RowViewModel.ScaleTransformFor(cfg.FuelTable.Scale);
        _accent = RowViewModel.TryBrush(cfg.AccentColor) ?? Brushes.Orange;
        EditHint.Foreground = _accent;
        ApplyClickThrough();
    }

    /// <summary>Click-through unless we're in edit mode or the user turned on Interactive (so the
    /// target ▲/▼ can be clicked mid-session). Off by default like every other overlay.</summary>
    private void ApplyClickThrough()
    {
        bool interactive = _editMode || _configService.Current.FuelTable.Interactive;
        Win32.ApplyOverlayStyle(this, clickThrough: !interactive);
    }

    /// <summary>Called from the telemetry thread; skips the dispatch when nothing changed.</summary>
    public void OnFuelTable(FuelTableSnapshot snapshot)
    {
        bool same = snapshot.VisuallyEquals(_last);
        _last = snapshot;
        if (same || _editMode) return;
        Dispatcher.BeginInvoke(() => Render(snapshot),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Render(FuelTableSnapshot s)
    {
        Panel.Visibility = s.Show ? Visibility.Visible : Visibility.Collapsed;
        if (!s.Show) return;

        TankValue.Text = s.TankText;
        GaugeFill.Width = 44 * s.FuelFrac;   // 46-wide gauge minus the 1px border each side
        GaugeFill.Fill = s.FuelFrac < 0.10 ? Red : s.FuelFrac < 0.25 ? _accent : Green;
        RemValue.Text = s.RemText.Length > 0 ? s.RemText : "—";

        bool editable = _editMode || _configService.Current.FuelTable.Interactive;
        RowsPanel.Children.Clear();
        foreach (var row in s.Rows)
            RowsPanel.Children.Add(BuildRow(row, editable, s.TargetLapsGoal));

        if (s.HasStatus)
        {
            StatusBorder.Visibility = Visibility.Visible;
            StatusBorder.Background = s.StatusState == 2 ? BehindBg : AheadBg;
            StatusValue.Foreground = s.StatusState == 2 ? BehindFg : AheadFg;
            StatusValue.Text = s.StatusText;
        }
        else StatusBorder.Visibility = Visibility.Collapsed;
    }

    private FrameworkElement BuildRow(FuelTableRow row, bool editable, int targetLapsGoal)
    {
        var grid = new Grid { Margin = new Thickness(0, 1.5, 0, 1.5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Brush valueBrush = row.IsTarget ? _accent : Text;

        var label = new TextBlock
        {
            Text = row.Label,
            Foreground = row.IsTarget ? _accent : Dim,
            FontSize = 13, FontWeight = row.IsTarget ? FontWeights.Bold : FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var usage = Cell(Fmt(row.PerLap), valueBrush, row.IsTarget && editable,
                         () => AdjustPerLap(0.01), () => AdjustPerLap(-0.01),
                         d => AdjustPerLap(d * 0.01), targetLapsGoal);
        Grid.SetColumn(usage, 1);
        grid.Children.Add(usage);

        var laps = Cell(Fmt(row.LapsToEmpty), row.IsTarget ? _accent : Text, row.IsTarget && editable,
                        () => AdjustLaps(1, targetLapsGoal), () => AdjustLaps(-1, targetLapsGoal),
                        d => AdjustLaps(d, targetLapsGoal), targetLapsGoal);
        Grid.SetColumn(laps, 2);
        grid.Children.Add(laps);

        return grid;
    }

    /// <summary>A centred value cell. When <paramref name="withSpinner"/>, adds compact ▲/▼ that
    /// (and the mouse wheel) drive the target — the widget's half of the "clickable target".</summary>
    private FrameworkElement Cell(string text, Brush brush, bool withSpinner,
                                  Action up, Action down, Action<int> wheel, int _)
    {
        var value = new TextBlock
        {
            Text = text, Foreground = brush,
            FontFamily = new FontFamily("Consolas"), FontSize = 14, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        panel.Children.Add(value);

        if (withSpinner)
        {
            var spin = new StackPanel { Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            spin.Children.Add(SpinButton("▲", up));
            spin.Children.Add(SpinButton("▼", down));
            panel.Children.Add(spin);

            panel.MouseWheel += (_, e) => wheel(e.Delta > 0 ? 1 : -1);
            panel.Background = Brushes.Transparent;   // so the wheel/hit-test covers the whole cell
        }
        return panel;
    }

    private Button SpinButton(string glyph, Action onClick)
    {
        var b = new Button
        {
            Content = glyph, FontSize = 7, Foreground = Dim, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Width = 14, Height = 8, Cursor = Cursors.Hand,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void AdjustPerLap(double step)
    {
        var ft = _configService.Current.FuelTable;
        double cur = ft.TargetPerLap > 0.01 ? ft.TargetPerLap : TargetRowPerLap();
        if (cur <= 0.01) cur = 3.00;
        ft.TargetPerLap = Math.Round(Math.Clamp(cur + step, 0.50, 30.0), 2);
        ft.TargetMode = "PerLap";
        _configService.SaveAndNotify();
    }

    private void AdjustLaps(double step, int targetLapsGoal)
    {
        var ft = _configService.Current.FuelTable;
        double cur = ft.TargetLaps > 0.5 ? ft.TargetLaps : targetLapsGoal > 0 ? targetLapsGoal : 25;
        ft.TargetLaps = Math.Round(Math.Clamp(cur + step, 1, 200));
        ft.TargetMode = "Laps";
        _configService.SaveAndNotify();
    }

    /// <summary>The target row's current L/lap from the last snapshot (a good seed when the config
    /// value is unset but the tank has already given us a derived figure).</summary>
    private double TargetRowPerLap()
    {
        if (_last is null) return -1;
        foreach (var r in _last.Rows) if (r.IsTarget) return r.PerLap;
        return -1;
    }

    private static string Fmt(double v) => v > 0.005 ? $"{v:0.00}" : "—";

    public bool EditMode
    {
        get => _editMode;
        set
        {
            _editMode = value;
            ApplyClickThrough();
            EditHint.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value) RenderSample();
            else
            {
                _configService.Current.FuelTable.X = Left;
                _configService.Current.FuelTable.Y = Top;
                _configService.Save();
                Render(_last ?? FuelTableSnapshot.Empty);
            }
        }
    }

    /// <summary>Edit mode shows a filled sample so there's something real to position and to see the
    /// clickable target on.</summary>
    private void RenderSample()
    {
        var was = _editMode; _editMode = true;   // force the spinners on for the sample
        Render(new FuelTableSnapshot(
            Show: true,
            TankText: "72.4 / 100.9 L",
            FuelFrac: 0.72,
            RemText: "20.9 – 21.4",
            Rows:
            [
                new FuelTableRow("Last", 3.39, 21.36, false),
                new FuelTableRow("Last 5", 3.42, 21.17, false),
                new FuelTableRow("Last 10", 3.44, 21.05, false),
                new FuelTableRow("Stint", 3.47, 20.86, false),
                new FuelTableRow("Target", 3.46, 20.92, true),
            ],
            TargetLapsGoal: 29,
            HasStatus: true,
            StatusText: "Lap 8 / 29 · 0.6L ahead · need 3.44/lap for 21 to go",
            StatusState: 1));
        _editMode = was;
    }

    private static Brush Frozen(string hex) => RowViewModel.TryBrush(hex) ?? Brushes.Gray;
}
