using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using StandingsOverlay.Data;

namespace StandingsOverlay.UI;

/// <summary>One cell in the per-lap delta strip. Mutable + observable so a row can be updated in
/// place each tick instead of rebuilt.</summary>
public sealed class DeltaCellViewModel : INotifyPropertyChanged
{
    private string _text = "";
    private Brush _brush = Brushes.White;
    public string Text { get => _text; set => Set(ref _text, value); }
    public Brush Brush { get => _brush; set => Set(ref _brush, value); }

    public DeltaCellViewModel() { }
    public DeltaCellViewModel(string text, Brush brush) { _text = text; _brush = brush; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Display-ready standings row: strings plus frozen brushes the template binds to. It is
/// <see cref="INotifyPropertyChanged"/> and mutable so <see cref="OverlayWindow"/> can update the
/// existing rows in place each tick (<see cref="Update"/>) instead of rebuilding the whole
/// ItemsSource — a full rebuild of 50+ rows through the 23-column SharedSize grid pegs the UI
/// thread. Setters fire change notifications only when the value actually changes, so an unchanged
/// cell costs nothing and WPF re-renders only the handful of cells that ticked.
/// </summary>
public sealed class RowViewModel : INotifyPropertyChanged
{
    // ---- text ----
    private string _posText = "", _laps = "", _posGained = "", _carNumber = "", _name = "";
    private string _iRating = "", _license = "", _gap = "", _interval = "", _bestLap = "", _lastLap = "";
    private string _status = "", _carBrand = "", _rank = "", _strat = "", _pace = "";
    private string _pitLap = "", _pitTotal = "", _pitDrive = "", _pitStall = "", _tyreAge = "";

    public string PosText { get => _posText; set => Set(ref _posText, value); }
    public string Laps { get => _laps; set => Set(ref _laps, value); }
    public string PosGained { get => _posGained; set => Set(ref _posGained, value); }
    public string CarNumber { get => _carNumber; set => Set(ref _carNumber, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string IRating { get => _iRating; set => Set(ref _iRating, value); }
    public string License { get => _license; set => Set(ref _license, value); }
    public string Gap { get => _gap; set => Set(ref _gap, value); }
    public string Interval { get => _interval; set => Set(ref _interval, value); }
    public string BestLap { get => _bestLap; set => Set(ref _bestLap, value); }
    public string LastLap { get => _lastLap; set => Set(ref _lastLap, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string CarBrand { get => _carBrand; set => Set(ref _carBrand, value); }
    public string Rank { get => _rank; set => Set(ref _rank, value); }
    public string Strat { get => _strat; set => Set(ref _strat, value); }
    public string Pace { get => _pace; set => Set(ref _pace, value); }
    public string PitLap { get => _pitLap; set => Set(ref _pitLap, value); }
    public string PitTotal { get => _pitTotal; set => Set(ref _pitTotal, value); }
    public string PitDrive { get => _pitDrive; set => Set(ref _pitDrive, value); }
    public string PitStall { get => _pitStall; set => Set(ref _pitStall, value); }
    public string TyreAge { get => _tyreAge; set => Set(ref _tyreAge, value); }

    /// <summary>Stable collection: cells are updated in place, only added/removed when the number
    /// of delta laps changes (a config change, not per tick).</summary>
    public ObservableCollection<DeltaCellViewModel> DeltaCells { get; } = new();

    // ---- brushes ----
    private Brush _posGainedBrush = Brushes.White, _carNumberBrush = Brushes.White;
    private Brush _licenseBrush = Brushes.Gray, _licenseTextBrush = Brushes.White, _bestLapBrush = Brushes.White;
    private Brush _statusBrush = Brushes.Orange, _paceBrush = Brushes.White, _rankBrush = Brushes.White;
    private Brush _classBarBrush = Brushes.Transparent, _rowBackground = Brushes.Transparent, _nameBrush = Brushes.White;
    private Brush _tyreBrush = Brushes.Gray, _tyreOldBrush = Brushes.Gray, _tyreAgeBrush = Brushes.Gray;
    private Brush _flagBodyBrush = Brushes.Black, _flagStrokeBrush = Brushes.Gray, _flagDotBrush = Brushes.Transparent;

    public Brush PosGainedBrush { get => _posGainedBrush; set => Set(ref _posGainedBrush, value); }
    public Brush CarNumberBrush { get => _carNumberBrush; set => Set(ref _carNumberBrush, value); }
    public Brush LicenseBrush { get => _licenseBrush; set => Set(ref _licenseBrush, value); }
    public Brush LicenseTextBrush { get => _licenseTextBrush; set => Set(ref _licenseTextBrush, value); }
    public Brush BestLapBrush { get => _bestLapBrush; set => Set(ref _bestLapBrush, value); }
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
    public Brush PaceBrush { get => _paceBrush; set => Set(ref _paceBrush, value); }
    public Brush RankBrush { get => _rankBrush; set => Set(ref _rankBrush, value); }
    public Brush ClassBarBrush { get => _classBarBrush; set => Set(ref _classBarBrush, value); }
    public Brush RowBackground { get => _rowBackground; set => Set(ref _rowBackground, value); }
    public Brush NameBrush { get => _nameBrush; set => Set(ref _nameBrush, value); }
    public Brush TyreBrush { get => _tyreBrush; set => Set(ref _tyreBrush, value); }
    public Brush TyreOldBrush { get => _tyreOldBrush; set => Set(ref _tyreOldBrush, value); }
    public Brush TyreAgeBrush { get => _tyreAgeBrush; set => Set(ref _tyreAgeBrush, value); }
    public Brush FlagBodyBrush { get => _flagBodyBrush; set => Set(ref _flagBodyBrush, value); }
    public Brush FlagStrokeBrush { get => _flagStrokeBrush; set => Set(ref _flagStrokeBrush, value); }
    public Brush FlagDotBrush { get => _flagDotBrush; set => Set(ref _flagDotBrush, value); }

    private FontWeight _nameWeight = FontWeights.Normal;
    public FontWeight NameWeight { get => _nameWeight; set => Set(ref _nameWeight, value); }

    // ---- visibilities ----
    private Visibility _irChip = Visibility.Collapsed, _licChip = Visibility.Collapsed;
    private Visibility _tyreVis = Visibility.Collapsed, _tyreSwitchVis = Visibility.Collapsed;
    private Visibility _flagVis = Visibility.Collapsed, _flagDotVis = Visibility.Collapsed;

    public Visibility IrChipVisibility { get => _irChip; set => Set(ref _irChip, value); }
    public Visibility LicChipVisibility { get => _licChip; set => Set(ref _licChip, value); }
    public Visibility TyreVisibility { get => _tyreVis; set => Set(ref _tyreVis, value); }
    public Visibility TyreSwitchVisibility { get => _tyreSwitchVis; set => Set(ref _tyreSwitchVis, value); }
    public Visibility FlagVisibility { get => _flagVis; set => Set(ref _flagVis, value); }
    public Visibility FlagDotVisibility { get => _flagDotVis; set => Set(ref _flagDotVis, value); }

    // Cache frozen brushes by hex so the same colour always returns the SAME instance. Rows update
    // in place and compare brushes by reference, so a fresh brush per tick (e.g. a car's fixed
    // class colour) would defeat the "unchanged ⇒ don't re-render" check and churn the GC.
    // MUST be declared before the brush constants below — their initializers call Frozen, and
    // static fields initialize in textual order (a later BrushCache would be null here).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Brush> BrushCache = new();

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

    /// <summary>Update this row in place from a snapshot row. Every bound property is assigned on
    /// every path (setters no-op when unchanged), so a slot reused across row kinds never keeps a
    /// stale cell.</summary>
    public void Update(StandingsRow r, Brush highlight, Brush accent)
    {
        if (r.Kind == RowKind.Separator) { ApplyPlain("···", DimBrush, FontWeights.Normal); return; }
        if (r.Kind == RowKind.ClassHeader)
        {
            ApplyPlain(r.Name, TryBrush(r.ClassColor) ?? accent, FontWeights.Bold);
            return;
        }

        // Penalty channel renders as a drawn flag icon; the state channel stays a text badge.
        // In "Text" status style PenaltyText is empty and any penalty arrives in StatusText.
        var (flagVis, flagBody, flagStroke, flagDot, dotVis) = PenaltyFlagVisuals(r.PenaltyText);
        var licChip = TryBrush(r.LicColor) ?? LicFallbackBrush;

        PosText = r.PosText;
        Laps = r.LapsText;
        PosGained = r.PosGainedText;
        CarNumber = r.CarNumber;
        Name = r.Name;
        IRating = r.IRatingText;
        License = r.LicText;
        Gap = r.GapText;
        Interval = r.IntervalText;
        BestLap = r.BestLapText;
        LastLap = r.LastLapText;
        UpdateDeltaCells(r.DeltaCells);
        Status = r.StatusText;
        CarBrand = r.CarBrand;
        Rank = r.RankText;
        Strat = r.StratText;
        Pace = r.PaceText;
        PitLap = r.PitLapText;
        PitTotal = r.PitTotalText;
        PitDrive = r.PitDriveText;
        PitStall = r.PitStallText;
        PosGainedBrush = r.PosGainedSign < 0 ? GainBrush : r.PosGainedSign > 0 ? LossBrush : DimBrush;
        CarNumberBrush = TryBrush(r.ClassColor) ?? NoClassBrush;
        ClassBarBrush = TryBrush(r.ClassColor) ?? NoClassBrush;
        LicenseBrush = licChip;
        LicenseTextBrush = ContrastText(licChip);
        BestLapBrush = r.BestLapSign == 2 ? PurpleBrush : Brushes.White;
        StatusBrush = StatusTextBrush(r.StatusText);
        PaceBrush = r.PaceSign < 0 ? GainBrush : r.PaceSign > 0 ? LossBrush
                    : r.PaceText.Length > 0 ? SamePaceYellow : DimBrush;
        RankBrush = r.RankSign == 2 ? PurpleBrush : r.RankSign < 0 ? GainBrush : DimBrush;
        RowBackground = r.IsPlayer ? highlight : Brushes.Transparent;
        NameBrush = r.Offline || r.StatusText == "PIT" ? DimBrush : Brushes.White;
        NameWeight = FontWeights.Normal;
        IrChipVisibility = string.IsNullOrEmpty(r.IRatingText) ? Visibility.Collapsed : Visibility.Visible;
        LicChipVisibility = string.IsNullOrEmpty(r.LicText) ? Visibility.Collapsed : Visibility.Visible;
        TyreVisibility = r.Tyre >= 0 && r.TyreSwitch == 0 ? Visibility.Visible : Visibility.Collapsed;
        TyreSwitchVisibility = r.Tyre >= 0 && r.TyreSwitch != 0 ? Visibility.Visible : Visibility.Collapsed;
        TyreBrush = r.Tyre >= 1 ? WetTyreBrush : DryTyreBrush;
        TyreOldBrush = r.TyreSwitch > 0 ? DryTyreBrush : WetTyreBrush;   // switched TO wet ⇒ was dry
        TyreAge = r.TyreAgeText;
        TyreAgeBrush = r.TyreAgeSign == 1 ? GainBrush : r.TyreAgeSign == 2 ? PitBrush : DimBrush;
        FlagVisibility = flagVis;
        FlagBodyBrush = flagBody;
        FlagStrokeBrush = flagStroke;
        FlagDotBrush = flagDot;
        FlagDotVisibility = dotVis;
    }

    /// <summary>Convenience for callers that still want a fresh instance.</summary>
    public static RowViewModel From(StandingsRow r, Brush highlight, Brush accent)
    {
        var vm = new RowViewModel();
        vm.Update(r, highlight, accent);
        return vm;
    }

    /// <summary>Separator / class-header rows: only the name is shown; reset every other cell so a
    /// slot reused from a normal row doesn't leak its old content.</summary>
    private void ApplyPlain(string name, Brush nameBrush, FontWeight weight)
    {
        Name = name; NameBrush = nameBrush; NameWeight = weight;
        PosText = Laps = PosGained = CarNumber = IRating = License = Gap = Interval = "";
        BestLap = LastLap = Status = CarBrand = Rank = Strat = Pace = "";
        PitLap = PitTotal = PitDrive = PitStall = TyreAge = "";
        if (DeltaCells.Count > 0) DeltaCells.Clear();
        PosGainedBrush = CarNumberBrush = BestLapBrush = PaceBrush = RankBrush = Brushes.White;
        LicenseBrush = TyreBrush = TyreOldBrush = TyreAgeBrush = Brushes.Gray;
        LicenseTextBrush = Brushes.White;
        StatusBrush = Brushes.Orange;
        ClassBarBrush = RowBackground = FlagDotBrush = Brushes.Transparent;
        FlagBodyBrush = Brushes.Black; FlagStrokeBrush = Brushes.Gray;
        IrChipVisibility = LicChipVisibility = TyreVisibility = TyreSwitchVisibility =
            FlagVisibility = FlagDotVisibility = Visibility.Collapsed;
    }

    private void UpdateDeltaCells(IReadOnlyList<DeltaCell> cells)
    {
        for (int k = 0; k < cells.Count; k++)
        {
            var text = cells[k].Text ?? "";
            var brush = cells[k].Sign switch
            {
                2 => PurpleBrush,        // class-best quali lap
                < 0 => GainBrush,
                > 0 => LossBrush,
                _ => (cells[k].Text?.Length ?? 0) > 4 ? Brushes.White : DimBrush, // quali laps white, neutral deltas dim
            };
            if (k < DeltaCells.Count) { DeltaCells[k].Text = text; DeltaCells[k].Brush = brush; }
            else DeltaCells.Add(new DeltaCellViewModel(text, brush));
        }
        for (int k = DeltaCells.Count - 1; k >= cells.Count; k--) DeltaCells.RemoveAt(k);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Drawn penalty flag chip visuals, shared by standings and relative.</summary>
    internal static (Visibility Vis, Brush Body, Brush Stroke, Brush Dot, Visibility DotVis)
        PenaltyFlagVisuals(string penalty) => penalty switch
    {
        "DQ"  => (Visibility.Visible, FlagBlackBody, DangerBrush, DangerBrush, Visibility.Visible),
        "BLK" => (Visibility.Visible, FlagBlackBody, FlagStrokeGray, Brushes.Transparent, Visibility.Collapsed),
        "DMG" => (Visibility.Visible, FlagBlackBody, FlagStrokeGray, MeatballOrange, Visibility.Visible),
        "WRN" => (Visibility.Visible, FlagWarnBody, FlagYellowStroke, Brushes.Transparent, Visibility.Collapsed),
        _     => (Visibility.Collapsed, FlagBlackBody, FlagStrokeGray, (Brush)Brushes.Transparent, Visibility.Collapsed),
    };

    /// <summary>Status badge text color — includes the penalty names, which appear as text
    /// in the "Text" status style.</summary>
    private static Brush StatusTextBrush(string status) => status switch
    {
        "SPUN" or "DQ" => DangerBrush,
        "TOW" or "SLOW" or "WRN" or "DMG" => WarnBrush,
        "SWAP" => PurpleBrush,
        "REJOIN" => GainBrush,
        "BLK" => Brushes.White,
        _ => PitBrush,
    };

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

    public static Brush Frozen(string hex) => BrushCache.GetOrAdd(hex, static h =>
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
        b.Freeze();
        return b;
    });

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
