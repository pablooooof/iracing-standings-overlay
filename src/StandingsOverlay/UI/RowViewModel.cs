using System.Windows.Media;
using StandingsOverlay.Data;

namespace StandingsOverlay.UI;

/// <summary>Display-ready row: strings plus frozen brushes for the template to bind to.</summary>
public sealed class RowViewModel
{
    public string PosText { get; init; } = "";
    public string CarNumber { get; init; } = "";
    public string Name { get; init; } = "";
    public string IRating { get; init; } = "";
    public string License { get; init; } = "";
    public string Gap { get; init; } = "";
    public string Interval { get; init; } = "";
    public string LastLap { get; init; } = "";
    public string Delta { get; init; } = "";
    public string PitText { get; init; } = "";
    public bool IsSeparator { get; init; }

    public Brush CarNumberBrush { get; init; } = Brushes.White;
    public Brush LicenseBrush { get; init; } = Brushes.Gray;
    public Brush DeltaBrush { get; init; } = Brushes.White;
    public Brush RowBackground { get; init; } = Brushes.Transparent;
    public Brush NameBrush { get; init; } = Brushes.White;

    private static readonly Brush GainBrush = Frozen("#4CFF6A");
    private static readonly Brush LossBrush = Frozen("#FF5C5C");
    private static readonly Brush DimBrush = Frozen("#9DA0AA");

    public static RowViewModel From(StandingsRow r, Brush highlight, Brush accent)
    {
        if (r.IsSeparator)
            return new RowViewModel { IsSeparator = true, Name = "···", NameBrush = DimBrush };

        return new RowViewModel
        {
            PosText = r.Position.ToString(),
            CarNumber = "#" + r.CarNumber,
            Name = r.Name,
            IRating = r.IRatingText,
            License = r.LicText,
            Gap = r.GapText,
            Interval = r.IntervalText,
            LastLap = r.LastLapText,
            Delta = r.DeltaText,
            PitText = r.InPit ? "PIT" : "",
            CarNumberBrush = TryBrush(r.ClassColor) ?? accent,
            LicenseBrush = TryBrush(r.LicColor) ?? DimBrush,
            DeltaBrush = r.DeltaSign < 0 ? GainBrush : r.DeltaSign > 0 ? LossBrush : DimBrush,
            RowBackground = r.IsPlayer ? highlight : Brushes.Transparent,
            NameBrush = r.InPit ? DimBrush : Brushes.White,
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
