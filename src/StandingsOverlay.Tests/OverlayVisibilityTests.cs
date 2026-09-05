using StandingsOverlay.Data;
using Xunit;

namespace StandingsOverlay.Tests;

public class OverlayVisibilityTests
{
    [Fact]
    public void AutoHideOff_AlwaysVisible()
    {
        var v = new OverlayVisibility { AutoHideEnabled = false, IracingFocused = false };
        Assert.True(v.Resolve());
    }

    [Fact]
    public void AutoHideOn_FollowsForeground()
    {
        var v = new OverlayVisibility { AutoHideEnabled = true };
        v.IracingFocused = true;
        Assert.True(v.Resolve());
        v.IracingFocused = false;
        Assert.False(v.Resolve());
    }

    [Fact]
    public void Demo_PinsVisibleRegardlessOfFocus()
    {
        var v = new OverlayVisibility { AutoHideEnabled = true, Demo = true, IracingFocused = false };
        Assert.True(v.Resolve());
    }

    [Fact]
    public void EditMode_ForceShowsEvenWhenAutoWouldHide()
    {
        var v = new OverlayVisibility { AutoHideEnabled = true, IracingFocused = false, EditMode = true };
        Assert.True(v.Resolve());
    }

    [Fact]
    public void Hotkey_ForceHidesWhileFocused_ThenClearsWhenAutoCatchesUp()
    {
        var v = new OverlayVisibility { AutoHideEnabled = true, IracingFocused = true };
        Assert.True(v.Resolve());               // visible: iRacing focused

        Assert.False(v.ToggleOverride());       // force-hide even though iRacing is focused
        Assert.True(v.OverrideActive);
        Assert.False(v.Resolve());

        // Tab away: auto now wants hidden too, which matches the override — it should retire.
        v.IracingFocused = false;
        Assert.False(v.Resolve());
        Assert.False(v.OverrideActive);

        // Tab back to iRacing: auto-hide is back in charge, so it shows again on its own.
        v.IracingFocused = true;
        Assert.True(v.Resolve());
    }

    [Fact]
    public void Hotkey_ForceShowsWhileInAnotherApp_ThenClearsOnReturn()
    {
        var v = new OverlayVisibility { AutoHideEnabled = true, IracingFocused = false };
        Assert.False(v.Resolve());              // hidden: another app focused

        Assert.True(v.ToggleOverride());        // force-show while away from iRacing
        Assert.True(v.OverrideActive);
        Assert.True(v.Resolve());

        // Return to iRacing: auto wants visible too, so the override retires quietly.
        v.IracingFocused = true;
        Assert.True(v.Resolve());
        Assert.False(v.OverrideActive);
    }

    [Fact]
    public void Hotkey_TwoPresses_ReturnToAutoBehavior()
    {
        var v = new OverlayVisibility { AutoHideEnabled = true, IracingFocused = true };
        Assert.False(v.ToggleOverride());       // hide
        Assert.True(v.ToggleOverride());        // show again
        Assert.True(v.Resolve());
    }
}
