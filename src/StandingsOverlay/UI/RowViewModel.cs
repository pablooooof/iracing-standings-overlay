using System.Windows.Media;
using StandingsOverlay.Data;

namespace StandingsOverlay.UI;

public sealed record DeltaCellViewModel(string Text, Brush Brush);

/// <summary>Display-ready row: strings plus frozen brushes for the template to bind to.</summary>
public sealed class RowViewModel
{
    public string PosText { get; init; } = "";
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
    public string Strat { get; init; } = "";
    public string Pace { get; init; } = "";

    public Brush PosGainedBrush { get; init; } = Brushes.White;
    public Brush CarNumberBrush { get; init; } = Brushes.White;
    public Brush LicenseBrush { get; init; } = Brushes.Gray;
    public Brush StatusBrush { get; init; } = Brushes.Orange;
    public Brush PaceBrush { get; init; } = Brushes.White;
    public Brush RowBackground { get; init; } = Brushes.Transparent;
    public Brush NameBrush { get; init; } = Brushes.White;
    public System.Windows.FontWeight NameWeight { get; init; } = System.Windows.FontWeights.Normal;

    private static readonly Brush GainBrush = Frozen("#4CFF6A");
    private static readonly Brush LossBrush = Frozen("#FF5C5C");
    private static readonly Brush DimBrush = Frozen("#9DA0AA");
    private static readonly Brush PitBrush = Frozen("#FFB84D");
    private static readonly Brush WarnBrush = Frozen("#FF8A3D");
    private static readonly Brush DangerBrush = Frozen("#FF4040");

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

        return new RowViewModel
        {
            PosText = r.PosText,
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
                c.Text,
                c.Sign < 0 ? GainBrush : c.Sign > 0 ? LossBrush : DimBrush)).ToList(),
            Status = r.StatusText,
            Strat = r.StratText,
            Pace = r.PaceText,
            PosGainedBrush = r.PosGainedSign < 0 ? GainBrush : r.PosGainedSign > 0 ? LossBrush : DimBrush,
            CarNumberBrush = TryBrush(r.ClassColor) ?? accent,
            LicenseBrush = TryBrush(r.LicColor) ?? DimBrush,
            StatusBrush = r.StatusText switch
            {
                "DQ" or "BLK" => DangerBrush,
                "DMG" or "WRN" => WarnBrush,
                _ => PitBrush,
            },
            PaceBrush = r.PaceSign < 0 ? GainBrush : r.PaceSign > 0 ? LossBrush : DimBrush,
            RowBackground = r.IsPlayer ? highlight : Brushes.Transparent,
            NameBrush = r.StatusText == "PIT" ? DimBrush : Brushes.White,
        };
    }

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
}
