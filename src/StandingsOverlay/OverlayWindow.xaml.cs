using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StandingsOverlay.Config;
using StandingsOverlay.Data;
using StandingsOverlay.Interop;
using StandingsOverlay.UI;

namespace StandingsOverlay;

/// <summary>Per-column Visibility flags; the Window's DataContext, rebuilt on config change.</summary>
public sealed record ColumnVisibility(
    Visibility PosGained, Visibility IRating, Visibility License,
    Visibility Gap, Visibility Interval, Visibility BestLap, Visibility LastLap,
    Visibility Delta, Visibility Strategy, Visibility Pace, Visibility Status)
{
    public static ColumnVisibility From(OverlayConfig c)
    {
        static Visibility V(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
        return new(V(c.ShowPositionsGained), V(c.ShowIRating), V(c.ShowLicense),
                   V(c.ShowGap), V(c.ShowInterval), V(c.ShowBestLap), V(c.ShowLastLap),
                   V(c.ShowDelta), V(c.ShowStrategy), V(c.ShowPace), V(c.ShowStatus));
    }
}

public partial class OverlayWindow : Window
{
    private readonly ConfigService _configService;
    private StandingsSnapshot? _lastSnapshot;
    private bool _editMode;

    public OverlayWindow(ConfigService configService)
    {
        InitializeComponent();
        _configService = configService;

        SourceInitialized += (_, _) => Win32.ApplyOverlayStyle(this, clickThrough: true);
        MouseLeftButtonDown += (_, e) =>
        {
            if (_editMode && e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        ApplyConfig(configService.Current);
        configService.Changed += cfg => Dispatcher.BeginInvoke(() =>
        {
            ApplyConfig(cfg);
            if (_lastSnapshot is not null) Render(_lastSnapshot);
        });
    }

    public void ApplyConfig(OverlayConfig cfg)
    {
        Left = cfg.X;
        Top = cfg.Y;
        FontSize = cfg.FontSize;
        FontFamily = new FontFamily("Segoe UI");
        Foreground = Brushes.White;
        DataContext = ColumnVisibility.From(cfg);

        var bg = RowViewModel.TryBrush(cfg.BackgroundColor) is SolidColorBrush b ? b.Color : Color.FromRgb(0x21, 0x21, 0x29);
        var brush = new SolidColorBrush(bg) { Opacity = Math.Clamp(cfg.Opacity, 0.05, 1.0) };
        brush.Freeze();
        RootBorder.Background = brush;

        var accent = RowViewModel.TryBrush(cfg.AccentColor) ?? Brushes.Cyan;
        HeaderLeft.Foreground = accent;
        EditHint.Foreground = accent;

        ColumnHeader.Visibility = cfg.ShowColumnHeader ? Visibility.Visible : Visibility.Collapsed;

        // One Δ header cell per lap, oldest on the left: Δ-5 … Δ-1.
        DeltaHeaderCells.ItemsSource =
            Enumerable.Range(0, cfg.DeltaLaps).Select(k => $"Δ-{cfg.DeltaLaps - k}").ToList();
    }

    /// <summary>Called from the telemetry thread; skips the dispatch entirely when nothing changed.</summary>
    public void OnSnapshot(StandingsSnapshot snapshot)
    {
        if (SnapshotsEqual(snapshot, _lastSnapshot)) return;
        _lastSnapshot = snapshot;
        Dispatcher.BeginInvoke(() => Render(snapshot));
    }

    /// <summary>Value comparison including rows (record Equals alone compares the list by reference).</summary>
    private static bool SnapshotsEqual(StandingsSnapshot a, StandingsSnapshot? b)
    {
        if (b is null) return false;
        if (a.Connected != b.Connected || a.HeaderLeft != b.HeaderLeft ||
            a.HeaderMid != b.HeaderMid || a.HeaderRight != b.HeaderRight ||
            a.Rows.Count != b.Rows.Count) return false;
        for (int i = 0; i < a.Rows.Count; i++)
        {
            var (ra, rb) = (a.Rows[i], b.Rows[i]);
            // Substitute the list so the record compares scalars only, then compare cells by value.
            if (!(ra with { DeltaCells = rb.DeltaCells }).Equals(rb)) return false;
            if (ra.DeltaCells.Count != rb.DeltaCells.Count) return false;
            for (int k = 0; k < ra.DeltaCells.Count; k++)
                if (ra.DeltaCells[k] != rb.DeltaCells[k]) return false;
        }
        return true;
    }

    private void Render(StandingsSnapshot s)
    {
        var cfg = _configService.Current;
        HeaderLeft.Text = s.HeaderLeft;
        HeaderMid.Text = s.HeaderMid;
        HeaderRight.Text = s.HeaderRight;

        var highlightBase = RowViewModel.TryBrush(cfg.HighlightColor) is SolidColorBrush hb ? hb.Color : Colors.Orange;
        var highlight = new SolidColorBrush(highlightBase) { Opacity = 0.30 };
        highlight.Freeze();
        var accent = RowViewModel.TryBrush(cfg.AccentColor) ?? Brushes.Cyan;

        RowsControl.ItemsSource = s.Rows.Select(r => RowViewModel.From(r, highlight, accent)).ToList();
    }

    public bool EditMode
    {
        get => _editMode;
        set
        {
            _editMode = value;
            Win32.ApplyOverlayStyle(this, clickThrough: !value);
            EditHint.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!value)
            {
                // Persist the dragged position.
                _configService.Current.X = Left;
                _configService.Current.Y = Top;
                _configService.Save();
            }
        }
    }
}
