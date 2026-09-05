namespace StandingsOverlay.Data;

/// <summary>
/// Pure decision logic for whether the overlay widgets belong on screen. Deliberately free of WPF
/// so the interplay of auto-hide, the manual hotkey override, and edit mode is unit-testable
/// (App owns the window handles and just applies the result).
///
/// Auto-detect wants the overlays visible only while iRacing (or one of our own windows) is the
/// foreground app. The hotkey layers a manual override on top: it force-shows or force-hides
/// against what auto-detect wants, then clears itself the moment auto-detect naturally agrees with
/// it — so after you tab back the widget quietly hands control to auto-hide again instead of
/// staying stuck. Edit mode always wins (you can't drag a widget you can't see); demo mode pins
/// everything visible so testing never blanks the screen.
/// </summary>
public sealed class OverlayVisibility
{
    public bool AutoHideEnabled { get; set; } = true;
    public bool IracingFocused { get; set; }
    public bool EditMode { get; set; }
    public bool Demo { get; set; }

    // null = defer to auto-detect; true/false = the user forced show/hide via the hotkey.
    private bool? _override;

    /// <summary>True if a manual hotkey override is currently in force.</summary>
    public bool OverrideActive => _override is not null;

    /// <summary>What auto-detect alone wants, ignoring any manual override.</summary>
    public bool AutoWantsVisible => Demo || !AutoHideEnabled || IracingFocused;

    /// <summary>Desired visibility after applying edit mode and any manual override. Also prunes an
    /// override that auto-detect has caught up to, so control reverts to auto-hide on its own.</summary>
    public bool Resolve()
    {
        if (EditMode) return true;                 // edit mode force-shows, override untouched
        if (_override is bool forced)
        {
            if (forced == AutoWantsVisible) { _override = null; return AutoWantsVisible; }
            return forced;
        }
        return AutoWantsVisible;
    }

    /// <summary>Hotkey: flip to the opposite of what's on screen now, as a manual override.
    /// Returns the new desired visibility.</summary>
    public bool ToggleOverride()
    {
        _override = !Resolve();
        return Resolve();
    }
}
