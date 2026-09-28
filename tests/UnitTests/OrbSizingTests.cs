using System;
using Xunit;

namespace ClaudeBuddy.Tests;

// The rules behind CB-198's orb size: the per-platform floor, clamping, the
// per-orb override winning over the slider, which presets the Size menu offers,
// and the anchors every former (28,28) now asks for.
//
// These fail quietly when wrong — a size the floor does not honour draws a dead
// click strip, and an anchor off by the orb's half draws a team arrow or a chat
// panel somewhere near the orb instead of on it — so each rule gets a case per
// outcome rather than one happy path.
public class OrbSizingTests
{
    [Fact]
    public void WindowsFloorIsTheMeasuredSeventyPercentAndMacIsSixty()
    {
        Assert.Equal(0.7, OrbSizing.MinFor(windows: true));
        Assert.Equal(0.6, OrbSizing.MinFor(windows: false));

        // 0.7's window must actually clear the 39-DIP floor it was chosen for,
        // and the step below it must not — otherwise the constant is a number
        // rather than the measurement the comment says it is.
        Assert.True(OrbSizing.WindowDip(OrbSizing.MinWindows) >= 39);
        Assert.True(OrbSizing.WindowDip(OrbSizing.MinWindows - OrbSizing.Step) < 39);
    }

    [Fact]
    public void MinIsTheRunningPlatformsFloor()
    {
        Assert.Equal(OrbSizing.MinFor(OperatingSystem.IsWindows()), OrbSizing.Min);
    }

    [Theory]
    [InlineData(1.0, 0.6, 1.0)]
    [InlineData(0.1, 0.6, 0.6)]
    [InlineData(0.1, 0.7, 0.7)]
    [InlineData(0.65, 0.7, 0.7)]
    [InlineData(9.0, 0.6, 2.0)]
    [InlineData(-3.0, 0.6, 0.6)]
    [InlineData(1.23, 0.6, 1.25)]   // snapped to the 0.05 step
    [InlineData(1.0000000001, 0.6, 1.0)]
    public void ClampBoundsAndSnaps(double given, double min, double expected)
    {
        Assert.Equal(expected, OrbSizing.Clamp(given, min));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ANonNumberIsTheDefaultNotAnEnd(double given)
    {
        Assert.Equal(OrbSizing.Default, OrbSizing.Clamp(given, 0.6));
        Assert.Equal(OrbSizing.Default, OrbSizing.Clamp(given));
    }

    [Fact]
    public void ClampWithoutAFloorUsesThePlatformsFloor()
    {
        Assert.Equal(OrbSizing.Min, OrbSizing.Clamp(0));
    }

    [Fact]
    public void AnOverrideWinsAndNoOverrideFollowsTheSlider()
    {
        Assert.Equal(1.5, OrbSizing.Effective(1.5, 0.75));
        Assert.Equal(0.75, OrbSizing.Effective(null, 0.75));

        // An override is clamped the same as the slider: a hand-edited 50
        // must not draw a 2800-DIP orb.
        Assert.Equal(OrbSizing.Max, OrbSizing.Effective(50, 1.0));
    }

    [Fact]
    public void PresetsAreFilteredByTheFloorNotClampedToIt()
    {
        Assert.Equal(new[] { 0.6, 0.75, 1.0, 1.25, 1.5, 2.0 }, OrbSizing.PresetsFor(0.6));
        Assert.Equal(new[] { 0.75, 1.0, 1.25, 1.5, 2.0 }, OrbSizing.PresetsFor(0.7));
        Assert.Equal(OrbSizing.PresetsFor(OrbSizing.Min), OrbSizing.Presets);

        // Every preset is a value the slider can also land on.
        foreach (var p in OrbSizing.Presets) Assert.Equal(p, OrbSizing.Clamp(p));
        Assert.Contains(OrbSizing.Default, OrbSizing.Presets);
    }

    [Theory]
    [InlineData(1.0, 56, 28, 34)]
    [InlineData(2.0, 112, 56, 62)]
    [InlineData(0.6, 33.6, 16.8, 22.8)]
    public void AnchorsScaleWithTheOrb(double size, double window, double centre, double gap)
    {
        Assert.Equal(window, OrbSizing.WindowDip(size), 6);
        Assert.Equal(centre, OrbSizing.CentreDip(size), 6);
        Assert.Equal(gap, OrbSizing.ChatPanelGap(size), 6);
    }

    [Fact]
    public void TheFlyoutsButtonsStillClearTheLargestOrb()
    {
        // OrbFlyout's arc is a fixed 56 DIP from the orb's centre, and it is not
        // scaled with the orb: the buttons are the same size at every orb size.
        // Their inner edge has to stay outside the biggest orb's circle, or a
        // 2x orb would draw under its own buttons.
        Assert.True(OrbFlyout.ArcRadius - OrbFlyout.ButtonHalf > 18 * OrbSizing.Max,
            $"{OrbFlyout.ArcRadius - OrbFlyout.ButtonHalf} does not clear {18 * OrbSizing.Max}");
    }
}
