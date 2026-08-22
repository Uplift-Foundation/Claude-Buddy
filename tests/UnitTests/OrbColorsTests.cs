using Avalonia.Media;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers OrbColors.cs. State is a bare string off a hook script — there's no
// enum for it, "idle"/"generating"/"waiting" plus "anything unrecognised
// reads as idle" (OrbColors.For's own comment) — so these tests stay string-
// based rather than inventing an enum that doesn't exist in the source.
public class OrbColorsTests
{
    [Theory]
    [InlineData("idle")]
    [InlineData("generating")]
    [InlineData("waiting")]
    public void DefaultFor_ReturnsAStableNonNullColorForEachKnownState(string state)
    {
        var first = OrbColors.DefaultFor(state);
        var second = OrbColors.DefaultFor(state);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DefaultFor_TheThreeKnownStatesAreDistinctColors()
    {
        Assert.NotEqual(OrbColors.DefaultFor("idle"), OrbColors.DefaultFor("generating"));
        Assert.NotEqual(OrbColors.DefaultFor("idle"), OrbColors.DefaultFor("waiting"));
        Assert.NotEqual(OrbColors.DefaultFor("generating"), OrbColors.DefaultFor("waiting"));
    }

    [Fact]
    public void DefaultFor_UnrecognisedStateReadsAsIdle()
    {
        Assert.Equal(OrbColors.DefaultFor("idle"), OrbColors.DefaultFor("ended"));
        Assert.Equal(OrbColors.DefaultFor("idle"), OrbColors.DefaultFor("something-a-future-hook-invents"));
    }

    [Theory]
    [InlineData(0x5B, 0x7A, 0x94, "#5B7A94")]
    [InlineData(0x00, 0xAF, 0x00, "#00AF00")]
    [InlineData(0xFF, 0xFF, 0xFF, "#FFFFFF")]
    public void ToHex_RoundTripsToUppercaseSixDigitHexWithNoAlpha(byte r, byte g, byte b, string expected)
    {
        var color = Color.FromRgb(r, g, b);
        Assert.Equal(expected, OrbColors.ToHex(color));
    }
}
