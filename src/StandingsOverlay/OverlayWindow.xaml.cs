using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StandingsOverlay.Config;
using StandingsOverlay.Data;
using StandingsOverlay.Interop;
using StandingsOverlay.UI;

namespace StandingsOverlay;

/// <summary>Per-column Visibility flags for the active session type; the Window's DataContext,
/// rebuilt on config change or session-type change.</summary>
public sealed record ColumnVisibility(
    Visibility Tyre, Visibility PosGained, Visibility IRating, Visibility License, Visibility CarBrand,
    Visibility LapsCount,
    Visibility Gap, Visibility Interval, Visibility BestLap, Visibility LastLap,
    Visibility Delta, Visibility PaceRank, Visibility Strategy, Visibility Pace, Visibility Status,
    double CellWidth)
{
    public static ColumnVisibility From(SessionColumns c, SessionKind kind)
    {
        static Visibility V(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
        bool race = kind == SessionKind.Race;
        return new(
            V(c.ShowTyre),
            V(c.ShowPositionsGained && race), V(c.ShowIRating), V(c.ShowLicense), V(c.ShowCarBrand),
            V(c.ShowLapsCount),
            V(c.ShowGap), V(c.ShowInterval), V(c.ShowBestLap), V(c.ShowLastLap),
            V(c.ShowCells && kind != SessionKind.Practice),
            V(c.ShowPaceRank),
            V(c.ShowStrategy && race), V(c.ShowPace && race), V(c.ShowStatus),
            // Race cells hold "0.4"; quali cells hold "1:43.210".
            CellWidth: kind == SessionKind.Qualify ? 62 : 34);
    }
}

public partial class OverlayWindow : Window
{
    private readonly ConfigService _configService;
    private StandingsSnapshot? _lastSnapshot;
    private SessionKind? _visibleKind;
    private IReadOnlyList<string>? _visibleCellHeaders;
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
        RootBorder.LayoutTransform = RowViewModel.ScaleTransformFor(cfg.Scale);
        FontFamily = new FontFamily("Segoe UI");
        Foreground = Brushes.White;
        DataContext = ColumnVisibility.From(cfg.ColumnsFor(_visibleKind ?? SessionKind.Race),
                                            _visibleKind ?? SessionKind.Race);

        var bg = RowViewModel.TryBrush(cfg.BackgroundColor) is SolidColorBrush b ? b.Color : Color.FromRgb(0x21, 0x21, 0x29);
        var brush = new SolidColorBrush(bg) { Opacity = Math.Clamp(cfg.Opacity, 0.05, 1.0) };
        brush.Freeze();
        RootBorder.Background = brush;

        var accent = RowViewModel.TryBrush(cfg.AccentColor) ?? Brushes.Cyan;
        HeaderLeft.Foreground = accent;
        EditHint.Foreground = accent;

        ColumnHeader.Visibility = cfg.ShowColumnHeader ? Visibility.Visible : Visibility.Collapsed;
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
        if (a.Connected != b.Connected || a.Kind != b.Kind || a.HeaderLeft != b.HeaderLeft ||
            a.HeaderMid != b.HeaderMid || a.HeaderRight != b.HeaderRight ||
            a.Rows.Count != b.Rows.Count || !a.CellHeaders.SequenceEqual(b.CellHeaders)) return false;
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

        // Session type drives which columns exist and what the cell strip means.
        if (s.Kind != _visibleKind)
        {
            _visibleKind = s.Kind;
            DataContext = ColumnVisibility.From(cfg.ColumnsFor(s.Kind), s.Kind);
        }
        if (_visibleCellHeaders is null || !s.CellHeaders.SequenceEqual(_visibleCellHeaders))
        {
            _visibleCellHeaders = s.CellHeaders;
            DeltaHeaderCells.ItemsSource = s.CellHeaders;
        }

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
