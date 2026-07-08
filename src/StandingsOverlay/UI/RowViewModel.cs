using System.Windows;
using System.Windows.Media;
using StandingsOverlay.Data;

namespace StandingsOverlay.UI;

public sealed record DeltaCellViewModel(string Text, Brush Brush);

/// <summary>Display-ready row: strings plus frozen brushes for the template to bind to.</summary>
public sealed class RowViewModel
{
    public string PosText { get; init; } = "";
    public string Laps { get; init; } = "";
    public string PosGained { get; init; } = "";
    public string CarNumber { get; init; } = "";
    public string Name { get; init; } = "";
    public string IRating { get; init; } = "";
    public string License { get; init; } = "";
    public string Gap { get; init; } = "";
    public string Interval { get; init; } = "";
    public string BestLap { get; init; } = "";
    public string LastLap { get; init; } = "";
    public IReadOnlyList<DeltaCellViewModel> DeltaCells { get; init; } = [];
    public string Status { get; init; } = "";
    public string CarBrand { get; init; } = "";
    public string Rank { get; init; } = "";
    public string Strat { get; init; } = "";
    public string Pace { get; init; } = "";

    public Brush PosGainedBrush { get; init; } = Brushes.White;
    public Brush CarNumberBrush { get; init; } = Brushes.White;
    public Brush LicenseBrush { get; init; } = Brushes.Gray;      // chip background
    public Brush LicenseTextBrush { get; init; } = Brushes.White;
    public Brush BestLapBrush { get; init; } = Brushes.White;
    public Brush StatusBrush { get; init; } = Brushes.Orange;
    public Brush PaceBrush { get; init; } = Brushes.White;
    public Brush RankBrush { get; init; } = Brushes.White;
    public Brush ClassBarBrush { get; init; } = Brushes.Transparent;
    public Brush RowBackground { get; init; } = Brushes.Transparent;
    public Brush NameBrush { get; init; } = Brushes.White;
    public System.Windows.FontWeight NameWeight { get; init; } = System.Windows.FontWeights.Normal;

    public Visibility IrChipVisibility { get; init; } = Visibility.Collapsed;
    public Visibility LicChipVisibility { get; init; } = Visibility.Collapsed;
    public Visibility TyreVisibility { get; init; } = Visibility.Collapsed;
    public Brush TyreBrush { get; init; } = Brushes.Gray;

    public Visibility FlagVisibility { get; init; } = Visibility.Collapsed;
    public Brush FlagBodyBrush { get; init; } = Brushes.Black;
    public Brush FlagStrokeBrush { get; init; } = Brushes.Gray;
    public Brush FlagDotBrush { get; init; } = Brushes.Transparent;
    public Visibility FlagDotVisibility { get; init; } = Visibility.Collapsed;

    private static readonly Brush PurpleBrush = Frozen("#C77DFF");
    private static readonly Brush GainBrush = Frozen("#4CFF6A");
    private static readonly Brush LossBrush = Frozen("#FF5C5C");
    private static readonly Brush DimBrush = Frozen("#9DA0AA");
    private static readonly Brush PitBrush = Frozen("#FFB84D");
    private static readonly Brush WarnBrush = Frozen("#FF8A3D");
    private static readonly Brush DangerBrush = Frozen("#FF4040");
    private static readonly Brush DryTyreBrush = Frozen("#C9C9CF");
    private static readonly Brush WetTyreBrush = Frozen("#1E6FFF");
    private static readonly Brush LicFallbackBrush = Frozen("#3A3A46");
    private static readonly Brush FlagBlackBody = Frozen("#0A0A0A");
    private static readonly Brush FlagWarnBody = Frozen("#26262C");
    private static readonly Brush FlagStrokeGray = Frozen("#B4B4BC");
    private static readonly Brush FlagYellowStroke = Frozen("#FFD34D");
    private static readonly Brush MeatballOrange = Frozen("#FF8A00");
    private static readonly Brush SamePaceYellow = Frozen("#FFD34D");
    // Single-class / spec fields have no class color to key off — a warm gold reads far more
    // like a racing timing screen than a flat white or the teal UI accent.
    private static readonly Brush NoClassBrush = Frozen("#FFC24D");

    public static RowViewModel From(StandingsRow r, Brush highlight, Brush accent)
    {
        if (r.Kind == RowKind.Separator)
            return new RowViewModel { Name = "···", NameBrush = DimBrush };

        if (r.Kind == RowKind.ClassHeader)
        {
            var classBrush = TryBrush(r.ClassColor) ?? accent;
            return new RowViewModel
            {
                Name = r.Name,
                NameBrush = classBrush,
                NameWeight = System.Windows.FontWeights.Bold,
            };
        }

        // Penalty flags render as drawn flag icons; PIT stays a text badge.
        var (flagVis, flagBody, flagStroke, flagDot, dotVis) = r.StatusText switch
        {
            "DQ"  => (Visibility.Visible, FlagBlackBody, DangerBrush, DangerBrush, Visibility.Visible),
            "BLK" => (Visibility.Visible, FlagBlackBody, FlagStrokeGray, Brushes.Transparent, Visibility.Collapsed),
            "DMG" => (Visibility.Visible, FlagBlackBody, FlagStrokeGray, MeatballOrange, Visibility.Visible),
            "WRN" => (Visibility.Visible, FlagWarnBody, FlagYellowStroke, Brushes.Transparent, Visibility.Collapsed),
            _     => (Visibility.Collapsed, FlagBlackBody, FlagStrokeGray, (Brush)Brushes.Transparent, Visibility.Collapsed),
        };
        var licChip = TryBrush(r.LicColor) ?? LicFallbackBrush;

        return new RowViewModel
        {
            PosText = r.PosText,
            Laps = r.LapsText,
            PosGained = r.PosGainedText,
            CarNumber = r.CarNumber,
            Name = r.Name,
            IRating = r.IRatingText,
            License = r.LicText,
            Gap = r.GapText,
            Interval = r.IntervalText,
            BestLap = r.BestLapText,
            LastLap = r.LastLapText,
            DeltaCells = r.DeltaCells.Select(c => new DeltaCellViewModel(
                c.Text ?? "",
                c.Sign switch
                {
                    2 => PurpleBrush,        // class-best quali lap
                    < 0 => GainBrush,
                    > 0 => LossBrush,
                    _ => (c.Text?.Length ?? 0) > 4 ? Brushes.White : DimBrush, // quali laps white, neutral deltas dim
                })).ToList(),
            Status = r.StatusText is "PIT" or "SPUN" or "REJOIN" or "TOW" ? r.StatusText : "",
            CarBrand = r.CarBrand,
            Rank = r.RankText,
            Strat = r.StratText,
            Pace = r.PaceText,
            PosGainedBrush = r.PosGainedSign < 0 ? GainBrush : r.PosGainedSign > 0 ? LossBrush : DimBrush,
            CarNumberBrush = TryBrush(r.ClassColor) ?? NoClassBrush,
            ClassBarBrush = TryBrush(r.ClassColor) ?? NoClassBrush,
            LicenseBrush = licChip,
            LicenseTextBrush = ContrastText(licChip),
            BestLapBrush = r.BestLapSign == 2 ? PurpleBrush : Brushes.White,
            StatusBrush = r.StatusText == "SPUN" ? DangerBrush
                        : r.StatusText == "TOW" ? WarnBrush
                        : r.StatusText == "REJOIN" ? GainBrush : PitBrush,
            PaceBrush = r.PaceSign < 0 ? GainBrush : r.PaceSign > 0 ? LossBrush
                        : r.PaceText.Length > 0 ? SamePaceYellow : DimBrush,
            RankBrush = r.RankSign == 2 ? PurpleBrush : r.RankSign < 0 ? GainBrush : DimBrush,
            RowBackground = r.IsPlayer ? highlight : Brushes.Transparent,
            NameBrush = r.Offline || r.StatusText == "PIT" ? DimBrush : Brushes.White,
            IrChipVisibility = string.IsNullOrEmpty(r.IRatingText) ? Visibility.Collapsed : Visibility.Visible,
            LicChipVisibility = string.IsNullOrEmpty(r.LicText) ? Visibility.Collapsed : Visibility.Visible,
            TyreVisibility = r.Tyre >= 0 ? Visibility.Visible : Visibility.Collapsed,
            TyreBrush = r.Tyre >= 1 ? WetTyreBrush : DryTyreBrush,
            FlagVisibility = flagVis,
            FlagBodyBrush = flagBody,
            FlagStrokeBrush = flagStroke,
            FlagDotBrush = flagDot,
            FlagDotVisibility = dotVis,
        };
    }

    /// <summary>Black text on bright chip colors (yellow C license), white otherwise.</summary>
    private static Brush ContrastText(Brush chip) =>
        chip is SolidColorBrush s && 0.299 * s.Color.R + 0.587 * s.Color.G + 0.114 * s.Color.B > 160
            ? Brushes.Black : Brushes.White;

    public static Brush? TryBrush(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        try { return Frozen(hex); }
        catch { return null; }
    }

    public static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>Per-widget size multiplier as a frozen LayoutTransform (identity at 1.0).
    /// Every overlay window applies this to its root element so one slider scales the whole box.</summary>
    public static Transform ScaleTransformFor(double scale)
    {
        double s = Math.Clamp(scale, 0.5, 2.0);
        if (Math.Abs(s - 1.0) < 0.001) return Transform.Identity;
        var t = new ScaleTransform(s, s);
        t.Freeze();
        return t;
    }
}
