using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StandingsOverlay.Config;
using StandingsOverlay.Data;
using StandingsOverlay.Interop;

namespace StandingsOverlay.UI;

/// <summary>
/// Lap Lab's overlay window: the practice lap table — newest lap on top, one column per
/// official sector, every gap against the chosen reference lap. Red = time lost, purple =
/// beat the reference, amber = dirty (off-track/pit), green lap number = session best.
/// Same plumbing as FuelWindow: click-through topmost, repaints only on visual change.
/// Spec: docs/LAP-LAB.md.
/// </summary>
public partial class LapLabWindow : Window
{
    private static readonly Brush TextBrush = RowViewModel.Frozen("#E8E9EE");
    private static readonly Brush DimBrush = RowViewModel.Frozen("#9DA0AA");
    private static readonly Brush FaintBrush = RowViewModel.Frozen("#666B76");
    private static readonly Brush LossBrush = RowViewModel.Frozen("#FF5C5C");
    private static readonly Brush GainBrush = RowViewModel.Frozen("#B8A2FF");
    private static readonly Brush DirtyBrush = RowViewModel.Frozen("#FFB84D");
    private static readonly Brush BestBrush = RowViewModel.Frozen("#35A653");
    private static readonly Brush BlockBg = RowViewModel.Frozen("#33FF4040");
    private static readonly Brush WarnBg = RowViewModel.Frozen("#33FFB84D");
    private static readonly Brush InfoBg = RowViewModel.Frozen("#26FFFFFF");

    private readonly ConfigService _configService;
    private LapLabSnapshot? _last;
    private bool _editMode;

    public LapLabWindow(ConfigService configService)
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
        Left = cfg.LapLab.X;
        Top = cfg.LapLab.Y;
        Root.LayoutTransform = RowViewModel.ScaleTransformFor(cfg.LapLab.Scale);
        EditHint.Foreground = RowViewModel.TryBrush(cfg.AccentColor) ?? Brushes.Cyan;
    }

    /// <summary>Called from the telemetry thread; skips the dispatch when nothing changed.</summary>
    public void OnLapLab(LapLabSnapshot snapshot)
    {
        bool same = snapshot.VisuallyEquals(_last);
        _last = snapshot;
        if (same || _editMode) return;
        Dispatcher.BeginInvoke(() => Render(snapshot));
    }

    private void Render(LapLabSnapshot s)
    {
        Panel.Visibility = s.Show ? Visibility.Visible : Visibility.Collapsed;
        if (!s.Show) return;

        RefText.Text = s.RefText;

        WarnChip.Visibility = s.WarnText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (s.WarnText.Length > 0)
        {
            WarnText.Text = s.WarnText;
            (WarnChip.Background, WarnText.Foreground) = s.WarnSeverity switch
            {
                2 => (BlockBg, LossBrush),
                1 => (WarnBg, DirtyBrush),
                _ => (InfoBg, DimBrush),
            };
        }

        Table.Children.Clear();
        Table.ColumnDefinitions.Clear();
        Table.RowDefinitions.Clear();

        int cols = 1 + s.SectorHeaders.Count + 3;   // LAP · S1..Sn · TIME · Δ · status
        for (int c = 0; c < cols; c++)
            Table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r <= s.Rows.Count; r++)
            Table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Header(0, "LAP", left: true);
        for (int i = 0; i < s.SectorHeaders.Count; i++) Header(1 + i, s.SectorHeaders[i]);
        Header(cols - 3, "TIME");
        Header(cols - 2, "Δ");

        for (int r = 0; r < s.Rows.Count; r++)
        {
            var row = s.Rows[r];
            Cell(r + 1, 0, row.LapText, row.IsSessionBest ? BestBrush : DimBrush, left: true, bold: row.IsSessionBest);
            for (int i = 0; i < row.Sectors.Count; i++)
                Cell(r + 1, 1 + i, row.Sectors[i].Text, SignBrush(row.Sectors[i].Sign), heat: row.Sectors[i].Heat);
            Cell(r + 1, cols - 3, row.TimeText,
                 row.IsSessionBest ? BestBrush : row.TimeDim ? FaintBrush : TextBrush, bold: row.IsSessionBest);
            Cell(r + 1, cols - 2, row.Delta.Text, SignBrush(row.Delta.Sign), heat: row.Delta.Heat);
            Cell(r + 1, cols - 1, row.Status.Text, SignBrush(row.Status.Sign));
        }
    }

    private static Brush SignBrush(int sign) => sign switch
    {
        1 => LossBrush,
        2 => GainBrush,
        3 => DirtyBrush,
        4 => FaintBrush,
        _ => TextBrush,
    };

    private void Header(int col, string text, bool left = false)
    {
        var tb = new TextBlock
        {
            Text = text, Foreground = DimBrush, FontFamily = new FontFamily("Consolas"),
            FontSize = 10, Margin = new Thickness(left ? 0 : 10, 0, 0, 1),
            TextAlignment = left ? TextAlignment.Left : TextAlignment.Right,
        };
        Grid.SetRow(tb, 0);
        Grid.SetColumn(tb, col);
        Table.Children.Add(tb);
    }

    private void Cell(int row, int col, string text, Brush brush, bool left = false,
                      bool bold = false, float heat = 0)
    {
        var tb = new TextBlock
        {
            Text = text, Foreground = brush, FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextAlignment = left ? TextAlignment.Left : TextAlignment.Right,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        };

        FrameworkElement cell = tb;
        if (Math.Abs(heat) > 0.02f && text.Length > 0)
        {
            // The heatmap channel: background saturation ∝ time lost (red) / gained (green),
            // readable in peripheral vision at speed where the digits are not.
            byte alpha = (byte)(Math.Min(1f, Math.Abs(heat)) * 0x62);
            var color = heat > 0 ? Color.FromArgb(alpha, 0xD6, 0x45, 0x45)
                                 : Color.FromArgb(alpha, 0x2E, 0x9E, 0x63);
            var bg = new SolidColorBrush(color);
            bg.Freeze();
            cell = new Border
            {
                Background = bg, CornerRadius = new CornerRadius(2),
                Padding = new Thickness(3, 0, 3, 0), Child = tb,
            };
            cell.Margin = new Thickness(7, 0, -3, 0);   // aligns digits with unheated cells
        }
        else
        {
            cell.Margin = new Thickness(left ? 0 : 10, 0, 0, 0);
        }

        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, col);
        Table.Children.Add(cell);
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
                _configService.Current.LapLab.X = Left;
                _configService.Current.LapLab.Y = Top;
                _configService.Save();
                Render(_last ?? LapLabSnapshot.Empty);
            }
        }
    }

    /// <summary>Edit mode shows a plausible practice table so there is something real to position.</summary>
    private void RenderSample()
    {
        Render(new LapLabSnapshot(
            Show: true,
            RefText: "ref file 1:59.48",
            WarnText: "track +5°C vs ref",
            WarnSeverity: 1,
            SectorHeaders: ["S1", "S2", "S3"],
            Rows:
            [
                new LapLabRow("9", false, [new("+0.16", 1, 0.6f), new("+0.53", 1, 1f), new("−0.03", 2, -0.15f)], "2:00.14", false, new("+0.66", 1), new("", 0)),
                new LapLabRow("8", true, [new("+0.09", 1, 0.35f), new("+0.44", 1, 1f), new("+0.11", 1, 0.45f)], "2:00.12", false, new("+0.64", 1), new("", 0)),
                new LapLabRow("7", false, [new("+0.14", 1, 0.55f), new("+1.32", 3), new("+0.10", 1, 0.4f)], "2:01.04", true, new("+1.56", 1), new("off S2", 3)),
                new LapLabRow("6", false, [new("+0.11", 1, 0.45f), new("+0.48", 1, 1f), new("+0.15", 1, 0.6f)], "2:00.22", false, new("+0.74", 1), new("", 0)),
                new LapLabRow("5", false, [new("", 0), new("", 0), new("", 0)], "2:09.50", true, new("", 0), new("slow", 4)),
            ]));
    }
}
