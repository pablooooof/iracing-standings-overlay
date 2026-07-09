using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StandingsOverlay.Config;
using StandingsOverlay.Data;
using StandingsOverlay.Interop;

namespace StandingsOverlay.UI;

/// <summary>Display-ready relative row: strings + frozen brushes for the template.</summary>
public sealed record RelativeRowViewModel(
    string Pos, string CarNumber, string Brand, string Name, string Status,
    string License, string IRating, string Stint, string LastLap, string Pace, string Gap,
    Brush PosBrush, Brush NumBrush, Brush ClassBarBrush, Brush TyreBrush, Brush TyreOldBrush,
    Brush NameBrush, FontWeight NameWeight,
    Brush StatusBrush, Brush LicBrush, Brush LicTextBrush, Brush StintBrush, Brush PaceBrush,
    Brush GapBrush, Brush BattleBrush, Brush RowBackground,
    Visibility NumVisibility, Visibility TyreVisibility, Visibility TyreSwitchVisibility,
    Visibility LicVisibility, Visibility BattleVisibility)
{
    private static readonly Brush White = RowViewModel.Frozen("#E8E9EE");
    private static readonly Brush DryTyre = RowViewModel.Frozen("#C9C9CF");
    private static readonly Brush WetTyre = RowViewModel.Frozen("#1E6FFF");
    private static readonly Brush Dim = RowViewModel.Frozen("#9DA0AA");
    private static readonly Brush LapsYouRed = RowViewModel.Frozen("#FF6A6A");
    private static readonly Brush LappedBlue = RowViewModel.Frozen("#63A8FF");
    private static readonly Brush FreshGreen = RowViewModel.Frozen("#4CFF6A");
    private static readonly Brush LossRed = RowViewModel.Frozen("#FF5C5C");
    private static readonly Brush SamePaceYellow = RowViewModel.Frozen("#FFD34D");
    private static readonly Brush PitAmber = RowViewModel.Frozen("#FFB84D");
    private static readonly Brush PitExitCyan = RowViewModel.Frozen("#40D8FF");   // cold out-lap car
    private static readonly Brush ExitBright = RowViewModel.Frozen("#8FF0FF");    // fresh pit exit (~15s)
    private static readonly Brush SwapPurple = RowViewModel.Frozen("#C77DFF");    // team driver change
    private static readonly Brush RejoinGreen = RowViewModel.Frozen("#4CFF6A");
    private static readonly Brush WarnYellow = RowViewModel.Frozen("#FFD34D");
    private static readonly Brush Meatball = RowViewModel.Frozen("#FF8A00");
    private static readonly Brush Danger = RowViewModel.Frozen("#FF4040");
    private static readonly Brush NoClass = RowViewModel.Frozen("#FFC24D");
    private static readonly Brush LicFallback = RowViewModel.Frozen("#3A3A46");

    public static RelativeRowViewModel From(RelativeRow r, Brush highlight, Brush accent)
    {
        bool parked = r.StatusText.Length > 0;
        var nameBrush = r.IsPlayer ? White
                      : parked ? Dim
                      : r.LapParity > 0 ? LapsYouRed
                      : r.LapParity < 0 ? LappedBlue
                      : White;
        // The gap column carries the same lap-parity language as the sim colors names with;
        // battle-relevant cars get the accent instead so the actual fight pops.
        var gapBrush = r.Battle ? accent
                     : parked || r.IsPlayer ? Dim
                     : r.LapParity > 0 ? LapsYouRed
                     : r.LapParity < 0 ? LappedBlue
                     : White;
        var licChip = RowViewModel.TryBrush(r.LicColor) ?? LicFallback;

        return new RelativeRowViewModel(
            Pos: r.PosText,
            CarNumber: r.CarNumber,
            Brand: r.CarBrand,
            Name: r.Name,
            Status: r.StatusText,
            License: r.LicText,
            IRating: r.IRatingText,
            Stint: r.StintText,
            LastLap: r.LastLapText,
            Pace: r.PaceText,
            Gap: r.GapText,
            PosBrush: RowViewModel.TryBrush(r.ClassColor) ?? Dim,
            NumBrush: RowViewModel.TryBrush(r.ClassColor) ?? NoClass,
            ClassBarBrush: RowViewModel.TryBrush(r.ClassColor) ?? Brushes.Transparent,
            TyreBrush: r.Tyre >= 1 ? WetTyre : DryTyre,
            TyreOldBrush: r.TyreSwitch > 0 ? DryTyre : WetTyre,
            NameBrush: nameBrush,
            NameWeight: r.IsPlayer ? FontWeights.SemiBold : FontWeights.Normal,
            StatusBrush: r.StatusText switch
            {
                "SPUN" or "DQ" => Danger,
                "TOW" => Meatball,
                "SWAP" => SwapPurple,
                "EXIT" => ExitBright,
                "OUT" => PitExitCyan,
                "REJOIN" => RejoinGreen,
                "WRN" => WarnYellow,
                "DMG" => Meatball,
                "BLK" => White,
                _ => PitAmber,   // PIT
            },
            LicBrush: licChip,
            LicTextBrush: Brushes.White,
            StintBrush: r.StintFresh ? FreshGreen : Dim,
            PaceBrush: r.PaceSign > 0 ? LossRed : r.PaceSign < 0 ? FreshGreen
                       : r.PaceText.Length > 0 ? SamePaceYellow : Dim,
            GapBrush: gapBrush,
            BattleBrush: accent,
            RowBackground: r.IsPlayer ? highlight : Brushes.Transparent,
            NumVisibility: r.CarNumber.Length > 1 ? Visibility.Visible : Visibility.Collapsed,
            TyreVisibility: r.Tyre >= 0 && r.TyreSwitch == 0 ? Visibility.Visible : Visibility.Collapsed,
            TyreSwitchVisibility: r.Tyre >= 0 && r.TyreSwitch != 0 ? Visibility.Visible : Visibility.Collapsed,
            LicVisibility: r.LicText.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            BattleVisibility: r.Battle ? Visibility.Visible : Visibility.Collapsed);
    }
}

/// <summary>
/// The relative box's own overlay window: click-through/topmost like the others, independently
/// positioned (default: pinned to the bottom-right of the work area until dragged in edit mode).
/// Repaints only when the snapshot visually changes. Spec: docs/RELATIVE.md.
/// </summary>
public partial class RelativeWindow : Window
{
    private readonly ConfigService _configService;
    private RelativeSnapshot? _last;
    private bool _editMode;
    private Brush _highlight = Brushes.Transparent;
    private Brush _accent = Brushes.Cyan;

    public RelativeWindow(ConfigService configService)
    {
        InitializeComponent();
        _configService = configService;

        FontFamily = new FontFamily("Segoe UI");

        SourceInitialized += (_, _) => Win32.ApplyOverlayStyle(this, clickThrough: true);
        MouseLeftButtonDown += (_, e) =>
        {
            if (_editMode && e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        // Until the user picks a spot, stay glued to the bottom-right corner across resizes.
        SizeChanged += (_, _) =>
        {
            if (_configService.Current.Relative.X < 0) AutoPlace();
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
        if (cfg.Relative.X >= 0)
        {
            Left = cfg.Relative.X;
            Top = cfg.Relative.Y;
        }
        else AutoPlace();

        FontSize = Math.Max(9, cfg.FontSize - 1.5);   // a relative reads at a glance; slightly denser
        Root.LayoutTransform = RowViewModel.ScaleTransformFor(cfg.Relative.Scale);
        var bg = RowViewModel.TryBrush(cfg.BackgroundColor) is SolidColorBrush b
            ? b.Color : Color.FromRgb(0x21, 0x21, 0x29);
        var brush = new SolidColorBrush(bg) { Opacity = Math.Clamp(cfg.Opacity, 0.05, 1.0) };
        brush.Freeze();
        RootBorder.Background = brush;

        var highlightBase = RowViewModel.TryBrush(cfg.HighlightColor) is SolidColorBrush hb
            ? hb.Color : Colors.Orange;
        var highlight = new SolidColorBrush(highlightBase) { Opacity = 0.30 };
        highlight.Freeze();
        _highlight = highlight;
        _accent = RowViewModel.TryBrush(cfg.AccentColor) ?? Brushes.Cyan;
        EditHint.Foreground = _accent;
    }

    private void AutoPlace()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Math.Max(ActualWidth, 410) - 10;
        Top = wa.Bottom - Math.Max(ActualHeight, 170) - 10;
    }

    /// <summary>Called from the telemetry thread; skips the dispatch when nothing visual changed.</summary>
    public void OnRelative(RelativeSnapshot snapshot)
    {
        bool same = snapshot.VisuallyEquals(_last);
        _last = snapshot;
        if (same || _editMode) return;
        Dispatcher.BeginInvoke(() => Render(snapshot));
    }

    private void Render(RelativeSnapshot s)
    {
        bool show = s.Rows.Count > 0;
        RootBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        RowsControl.ItemsSource = s.Rows.Select(r => RelativeRowViewModel.From(r, _highlight, _accent)).ToList();
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
                _configService.Current.Relative.X = Left;
                _configService.Current.Relative.Y = Top;
                _configService.Save();
                Render(_last ?? RelativeSnapshot.Empty);
            }
        }
    }

    /// <summary>Edit mode shows a representative field so there is something to position.</summary>
    private void RenderSample()
    {
        Render(new RelativeSnapshot(
        [
            new RelativeRow(false, "P2", "#E33241", 0, "#07", "POR", "R. Vergne", 1, "", false,
                            "4.2k", "", "", "12", false, "1:41.882", "▲", 1, "+3.1"),
            new RelativeRow(false, "P5", "#FFDA59", 1, "#22", "FER", "S. Okafor", 0, "OUT", false,
                            "3.1k", "", "", "0", true, "1:42.115", "►", 0, "+1.9"),
            new RelativeRow(false, "P3", "#FFDA59", 0, "#11", "BMW", "M. Rossi", 0, "", true,
                            "5.6k", "", "", "9", false, "1:42.301", "►", 0, "+0.6"),
            new RelativeRow(true, "P4", "#FFDA59", 0, "#31", "FER", "You", 0, "", false,
                            "2.8k", "", "", "8", false, "1:42.290", "", 0, "—"),
            new RelativeRow(false, "P6", "#FFDA59", 0, "#44", "AUD", "L. Tanaka", 0, "", true,
                            "2.9k", "", "", "3", true, "1:42.198", "▼", -1, "-0.8"),
            new RelativeRow(false, "P12", "#57C1FF", 1, "#88", "MCL", "A. Novak", -1, "", false,
                            "1.9k", "", "", "15", false, "1:47.554", "▼", -1, "-2.4"),
            new RelativeRow(false, "", "#FFDA59", -1, "#61", "POR", "K. Svensson", 0, "PIT", false,
                            "2.2k", "", "", "", false, "", "", 0, "-4.9"),
        ]));
    }
}
