using Xunit;

namespace Orbweaver.Tests;

// CB-171 and CB-173. Which session kinds wear a drawn badge rather than a
// typed one, and which mark each of them draws.
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
    // The four kinds whose character falls back to a colour-emoji face on
    // Windows, each with its own mark. Asserted against the named constant
    // rather than merely "not null", because swapping two of them — a gear on
    // a cron orb — would pass a null check and be wrong on every screen.
    public static TheoryData<SessionKind, string> DrawnKinds => new()
    {
        { SessionKind.Cron, SymbolMarks.Stopwatch },
        { SessionKind.Background, SymbolMarks.Gear },
        { SessionKind.Remote, SymbolMarks.Arrows },
        { SessionKind.Cloud, SymbolMarks.Cloud },
    };

    [Theory]
    [MemberData(nameof(DrawnKinds))]
    public void EachDrawnKindWearsItsOwnMark(SessionKind kind, string expected)
    {
        Assert.Equal(expected, OrbWindow.KindMarkFor(kind));
    }

    // @ and # stay as text: ASCII, in every font, no emoji face involved, and
    // the characters the surfaces themselves use. Unknown has no badge at all.
    [Theory]
    [InlineData(SessionKind.Direct)]
    [InlineData(SessionKind.Channel)]
    [InlineData(SessionKind.Unknown)]
    public void PunctuationAndNoBadgeAreNotDrawn(SessionKind kind)
    {
        Assert.Null(OrbWindow.KindMarkFor(kind));
    }

    // The rule itself, rather than today's list of kinds: any badge whose
    // character is not ASCII has to be drawn, because a non-ASCII symbol is
    // exactly what reaches font fallback and, on Windows, Segoe UI Emoji. A
    // new kind added to BadgeFor with a symbol and no mark fails here rather
    // than on somebody's Windows screen months later — which is how the
    // clock, gear and arrows went unnoticed until CB-171 made the captures
    // legible.
    [Fact]
    public void EveryNonAsciiBadgeIsDrawn()
    {
        var checkedAny = false;

        foreach (var kind in Enum.GetValues<SessionKind>())
        {
            if (OrbWindow.BadgeFor(kind) is not { } badge) continue;

            checkedAny = true;
            var ascii = badge.Glyph.All(c => c < 0x80);

            Assert.True(ascii || OrbWindow.KindMarkFor(kind) is not null,
                $"{kind}'s badge '{badge.Glyph}' is not ASCII and has no drawn mark");
        }

        Assert.True(checkedAny);
    }

    // And the other direction: a drawn kind is still a badged kind, so the
    // chat panel's chip and the accessibility label still have a character
    // and a word for it. A mark with no badge behind it would draw on an orb
    // that KindLabel says has no kind.
    [Theory]
    [MemberData(nameof(DrawnKinds))]
    public void EveryDrawnKindStillHasABadge(SessionKind kind, string _)
    {
        Assert.NotNull(OrbWindow.BadgeFor(kind));
    }
}

// The speak button's three looks, shared by OrbFlyout and ChatPanel. A
// character or a drawn mark, never both, and the stop square — the one of the
// three that went to a colour-emoji face on Windows only — is the drawn one.
public class SpeakLookTests
{
    [Fact]
    public void SpeakingDrawsTheStopSquareAndTypesNothing()
    {
        var (glyph, mark) = SymbolMarks.SpeakLook(TextToSpeech.SpeakState.Speaking);

        Assert.Null(glyph);
        Assert.Equal(SymbolMarks.Stop, mark);
    }

    [Fact]
    public void PreparingTypesTheHourglass()
    {
        var (glyph, mark) = SymbolMarks.SpeakLook(TextToSpeech.SpeakState.Preparing);

        Assert.Equal("⏳", glyph);
        Assert.Null(mark);
    }

    [Fact]
    public void IdleTypesTheSpeaker()
    {
        var (glyph, mark) = SymbolMarks.SpeakLook(TextToSpeech.SpeakState.Idle);

        Assert.Equal("\U0001F508", glyph);
        Assert.Null(mark);
    }
}

// CB-173. The heartbeat heart, drawn because Segoe UI's U+2665 came out a
// third smaller than macOS's in the same badge. Pure string arithmetic, so it
// runs without a render interface: the outline has to be mirror-symmetric
// about the box's centre line, because a heart one lobe fatter than the other
// reads as a mistake at any size, and a hand-edited control point is exactly
// how that happens without anything failing.
public class HeartMarkTests
{
    [Fact]
    public void TheHeartIsSymmetricAboutTheCentreLine()
    {
        var points = System.Text.RegularExpressions.Regex
            .Matches(SymbolMarks.Heart, @"(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?)")
            .Select(m => (X: double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                          Y: double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();

        Assert.NotEmpty(points);

        foreach (var (x, y) in points)
        {
            Assert.Contains(points, p => Math.Abs(p.X - (16 - x)) < 1e-9 && Math.Abs(p.Y - y) < 1e-9);
        }
    }

    [Fact]
    public void TheHeartComesToAPointOnTheCentreLineAtTheBottom()
    {
        Assert.StartsWith("M8,14.5 ", SymbolMarks.Heart);
    }
}
