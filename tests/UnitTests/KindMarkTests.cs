using Xunit;

namespace ClaudeBuddy.Tests;

// CB-171. Which session kinds wear a drawn badge rather than a typed one.
//
// Pure, and worth keeping pure: the decision is a switch over SessionKind with
// no window, no settings and no font manager behind it, which is the same rule
// OrbArrangement and OrbGlyph are held to. The drawing half is asserted in
// tests/UiTests, including whether each mark parses and stays inside its box —
// StreamGeometry.Parse needs a platform render interface, so it cannot be
// checked from here. This is the half that says what the app *decides*, and it
// runs without a display.
public class KindMarkTests
{
    // The cloud, and only the cloud. Every other kind is punctuation or a
    // symbol its own surface already uses, and a hand-drawn version of "@"
    // would be worse at every size — see KindMarkFor's own comment for why
    // the clock, gear and arrows are deliberately left as characters despite
    // resolving to colour-emoji faces on Windows.
    [Theory]
    [InlineData(SessionKind.Cron)]
    [InlineData(SessionKind.Direct)]
    [InlineData(SessionKind.Channel)]
    [InlineData(SessionKind.Remote)]
    [InlineData(SessionKind.Background)]
    [InlineData(SessionKind.Unknown)]
    public void OnlyTheCloudIsDrawn(SessionKind kind)
    {
        Assert.Null(OrbWindow.KindMarkFor(kind));
    }

    [Fact]
    public void TheCloudHasAMark()
    {
        Assert.NotNull(OrbWindow.KindMarkFor(SessionKind.Cloud));
    }
}
