using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StandingsOverlay.Config;
using StandingsOverlay.Data;
using StandingsOverlay.Interop;

namespace StandingsOverlay.UI;

/// <summary>Display-ready relative row: strings + frozen brushes for the template. Mutable +
/// <see cref="INotifyPropertyChanged"/> so <see cref="RelativeWindow"/> updates rows in place each
/// tick instead of rebuilding the ItemsSource (same reason as <see cref="RowViewModel"/>).</summary>
public sealed class RelativeRowViewModel : INotifyPropertyChanged
{
    private string _pos = "", _carNumber = "", _brand = "", _name = "", _status = "";
    private string _license = "", _iRating = "", _stint = "", _lastLap = "", _pace = "", _gap = "";
    public string Pos { get => _pos; set => Set(ref _pos, value); }
    public string CarNumber { get => _carNumber; set => Set(ref _carNumber, value); }
    public string Brand { get => _brand; set => Set(ref _brand, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string License { get => _license; set => Set(ref _license, value); }
    public string IRating { get => _iRating; set => Set(ref _iRating, value); }
    public string Stint { get => _stint; set => Set(ref _stint, value); }
    public string LastLap { get => _lastLap; set => Set(ref _lastLap, value); }
    public string Pace { get => _pace; set => Set(ref _pace, value); }
    public string Gap { get => _gap; set => Set(ref _gap, value); }

    private Brush _posBrush = Brushes.Gray, _numBrush = Brushes.White, _classBarBrush = Brushes.Transparent;
    private Brush _tyreBrush = Brushes.Gray, _tyreOldBrush = Brushes.Gray, _nameBrush = Brushes.White;
    private Brush _statusBrush = Brushes.Orange, _statusBg = Brushes.Transparent, _licBrush = Brushes.Gray;
    private Brush _licTextBrush = Brushes.White, _stintBrush = Brushes.Gray, _paceBrush = Brushes.White;
    private Brush _gapBrush = Brushes.White, _battleBrush = Brushes.Cyan, _rowBackground = Brushes.Transparent;
    private Brush _flagBodyBrush = Brushes.Black, _flagStrokeBrush = Brushes.Gray, _flagDotBrush = Brushes.Transparent;
    public Brush PosBrush { get => _posBrush; set => Set(ref _posBrush, value); }
    public Brush NumBrush { get => _numBrush; set => Set(ref _numBrush, value); }
    public Brush ClassBarBrush { get => _classBarBrush; set => Set(ref _classBarBrush, value); }
    public Brush TyreBrush { get => _tyreBrush; set => Set(ref _tyreBrush, value); }
    public Brush TyreOldBrush { get => _tyreOldBrush; set => Set(ref _tyreOldBrush, value); }
    public Brush NameBrush { get => _nameBrush; set => Set(ref _nameBrush, value); }
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
    public Brush StatusBg { get => _statusBg; set => Set(ref _statusBg, value); }
    public Brush LicBrush { get => _licBrush; set => Set(ref _licBrush, value); }
    public Brush LicTextBrush { get => _licTextBrush; set => Set(ref _licTextBrush, value); }
    public Brush StintBrush { get => _stintBrush; set => Set(ref _stintBrush, value); }
    public Brush PaceBrush { get => _paceBrush; set => Set(ref _paceBrush, value); }
    public Brush GapBrush { get => _gapBrush; set => Set(ref _gapBrush, value); }
    public Brush BattleBrush { get => _battleBrush; set => Set(ref _battleBrush, value); }
    public Brush RowBackground { get => _rowBackground; set => Set(ref _rowBackground, value); }
    public Brush FlagBodyBrush { get => _flagBodyBrush; set => Set(ref _flagBodyBrush, value); }
    public Brush FlagStrokeBrush { get => _flagStrokeBrush; set => Set(ref _flagStrokeBrush, value); }
    public Brush FlagDotBrush { get => _flagDotBrush; set => Set(ref _flagDotBrush, value); }

    private FontWeight _nameWeight = FontWeights.Normal;
    public FontWeight NameWeight { get => _nameWeight; set => Set(ref _nameWeight, value); }

    private Visibility _numVis = Visibility.Collapsed, _tyreVis = Visibility.Collapsed, _tyreSwitchVis = Visibility.Collapsed;
    private Visibility _irVis = Visibility.Collapsed, _licVis = Visibility.Collapsed, _battleVis = Visibility.Collapsed;
    private Visibility _flagVis = Visibility.Collapsed, _flagDotVis = Visibility.Collapsed;
    public Visibility NumVisibility { get => _numVis; set => Set(ref _numVis, value); }
    public Visibility TyreVisibility { get => _tyreVis; set => Set(ref _tyreVis, value); }
    public Visibility TyreSwitchVisibility { get => _tyreSwitchVis; set => Set(ref _tyreSwitchVis, value); }
    public Visibility IrVisibility { get => _irVis; set => Set(ref _irVis, value); }
    public Visibility LicVisibility { get => _licVis; set => Set(ref _licVis, value); }
    public Visibility BattleVisibility { get => _battleVis; set => Set(ref _battleVis, value); }
    public Visibility FlagVisibility { get => _flagVis; set => Set(ref _flagVis, value); }
    public Visibility FlagDotVisibility { get => _flagDotVis; set => Set(ref _flagDotVis, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

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
    private static readonly Brush ExitChip = RowViewModel.Frozen("#FF8A00");      // fresh pit exit — bright chip
    private static readonly Brush StintAmber = RowViewModel.Frozen("#FFCA5C");    // stint laps, always amber
    private static readonly Brush Ink = RowViewModel.Frozen("#17171D");           // dark text on a bright chip
    private static readonly Brush SwapPurple = RowViewModel.Frozen("#C77DFF");    // team driver change
    private static readonly Brush RejoinGreen = RowViewModel.Frozen("#4CFF6A");
    private static readonly Brush WarnYellow = RowViewModel.Frozen("#FFD34D");
    private static readonly Brush Meatball = RowViewModel.Frozen("#FF8A00");
    private static readonly Brush Danger = RowViewModel.Frozen("#FF4040");
    private static readonly Brush NoClass = RowViewModel.Frozen("#FFC24D");
    private static readonly Brush LicFallback = RowViewModel.Frozen("#3A3A46");

    public static RelativeRowViewModel From(RelativeRow r, Brush highlight, Brush accent)
    {
        var vm = new RelativeRowViewModel();
        vm.Update(r, highlight, accent);
        return vm;
    }

    /// <summary>Refresh this row in place from a snapshot row (every bound property assigned;
    /// setters no-op when unchanged).</summary>
    public void Update(RelativeRow r, Brush highlight, Brush accent)
    {
        // Only PIT dims the name/gap (a stationary car you can ignore); SPUN/TOW/REJOIN/OUT/EXIT/
        // SWAP are all still cars on track, so they keep their normal lap-parity colour.
        bool dimForPit = r.StatusText == "PIT";
        NameBrush = r.IsPlayer ? White
                  : dimForPit ? Dim
                  : r.LapParity > 0 ? LapsYouRed
                  : r.LapParity < 0 ? LappedBlue
                  : White;
        GapBrush = r.Battle ? accent
                 : dimForPit || r.IsPlayer ? Dim
                 : r.LapParity > 0 ? LapsYouRed
                 : r.LapParity < 0 ? LappedBlue
                 : White;
        var licChip = RowViewModel.TryBrush(r.LicColor) ?? LicFallback;
        // Penalty chip (TextAndFlags style): same drawn flag as the standings.
        var (flagVis, flagBody, flagStroke, flagDot, dotVis) = RowViewModel.PenaltyFlagVisuals(r.PenaltyText);

        Pos = r.PosText;
        CarNumber = r.CarNumber;
        Brand = r.CarBrand;
        Name = r.Name;
        Status = r.StatusText;
        License = r.LicText;
        IRating = r.IRatingText;
        Stint = r.StintText;
        LastLap = r.LastLapText;
        Pace = r.PaceText;
        Gap = r.GapText;
        PosBrush = RowViewModel.TryBrush(r.ClassColor) ?? Dim;
        NumBrush = RowViewModel.TryBrush(r.ClassColor) ?? NoClass;
        ClassBarBrush = RowViewModel.TryBrush(r.ClassColor) ?? Brushes.Transparent;
        TyreBrush = r.Tyre >= 1 ? WetTyre : DryTyre;
        TyreOldBrush = r.TyreSwitch > 0 ? DryTyre : WetTyre;
        NameWeight = r.IsPlayer ? FontWeights.SemiBold : FontWeights.Normal;
        StatusBrush = r.StatusText switch
        {
            "SPUN" or "DQ" => Danger,
            "TOW" => Meatball,
            "SWAP" => SwapPurple,
            "EXIT" => Ink,          // dark text on the bright chip
            "OUT" => PitExitCyan,
            "REJOIN" => RejoinGreen,
            "SLOW" => WarnYellow,
            "WRN" => WarnYellow,
            "DMG" => Meatball,
            "BLK" => White,
            _ => PitAmber,   // PIT
        };
        // A fresh pit exit gets a filled chip so it pops (Spa-24h "who just left the pits").
        StatusBg = r.StatusText == "EXIT" ? ExitChip : Brushes.Transparent;
        LicBrush = licChip;
        LicTextBrush = Brushes.White;
        StintBrush = StintAmber;
        PaceBrush = r.PaceSign > 0 ? LossRed : r.PaceSign < 0 ? FreshGreen
                    : r.PaceText.Length > 0 ? SamePaceYellow : Dim;
        BattleBrush = accent;
        RowBackground = r.IsPlayer ? highlight : Brushes.Transparent;
        NumVisibility = r.CarNumber.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        TyreVisibility = r.Tyre >= 0 && r.TyreSwitch == 0 ? Visibility.Visible : Visibility.Collapsed;
        TyreSwitchVisibility = r.Tyre >= 0 && r.TyreSwitch != 0 ? Visibility.Visible : Visibility.Collapsed;
        IrVisibility = r.IRatingText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        LicVisibility = r.LicText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BattleVisibility = r.Battle ? Visibility.Visible : Visibility.Collapsed;
        FlagVisibility = flagVis;
        FlagBodyBrush = flagBody;
        FlagStrokeBrush = flagStroke;
        FlagDotBrush = flagDot;
        FlagDotVisibility = dotVis;
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
    // Rows created once, updated in place each tick (see Render) — no per-frame ItemsSource rebuild.
    private readonly System.Collections.ObjectModel.ObservableCollection<RelativeRowViewModel> _rows = new();

    public RelativeWindow(ConfigService configService)
    {
        InitializeComponent();
        _configService = configService;
        RowsControl.ItemsSource = _rows;

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

        // Same base size as the standings — one type ramp across the widgets (use the
        // Relative.Scale slider to make the whole box bigger/smaller, not a hidden offset).
        FontSize = cfg.FontSize;
        Resources["FontSm"] = Math.Max(9.0, cfg.FontSize - 2);      // secondary cells (brand, status…)
        Resources["FontXs"] = Math.Max(8.5, cfg.FontSize - 3);      // chips (iR, license)
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
        Dispatcher.BeginInvoke(() => Render(snapshot), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Render(RelativeSnapshot s)
    {
        bool show = s.Rows.Count > 0;
        RootBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        // Update rows in place; grow/shrink only when the count changes (see RowViewModel).
        var rows = s.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            if (i < _rows.Count) _rows[i].Update(rows[i], _highlight, _accent);
            else
            {
                var vm = new RelativeRowViewModel();
                vm.Update(rows[i], _highlight, _accent);
                _rows.Add(vm);
            }
        }
        for (int i = _rows.Count - 1; i >= rows.Count; i--) _rows.RemoveAt(i);
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
