using System.IO;
using Xunit;

namespace ClaudeBuddy.Tests;

// The words the tray puts on screen: the tooltip's one-line summary, the name a
// session is listed under, and the row that name ends up in.
//
// Tested against the three static helpers directly rather than by building a
// controller, because a TrayController's constructor starts
// ClaudeDesktopOverlay's timer and exports a real TrayIcon — neither of which
// has anything to do with what these three functions decide. The menu's *shape*
// is covered where a controller genuinely is needed; see
// tests/UiTests/TrayMenuTests.cs.
public class TrayMenuTextTests
{
    private static TrayController.SessionEntry Entry(
        string id = "6a6fcb43-fa28-4894-9940-c1c6c9970e54",
        string agent = "", string title = "", string cwd = "", string state = "idle") =>
        new(id, new SessionStatus { Agent = agent, Title = title, Cwd = cwd, State = state });

    // --- Summary -------------------------------------------------------------

    [Fact]
    public void TheTooltipSaysNoSessionsRatherThanZero()
    {
        Assert.Equal("Claude Buddy — no sessions", TrayController.Summary(0, 0, 0));
    }

    [Theory]
    [InlineData(1, 0, 0, "Claude Buddy — 1 session")]
    [InlineData(4, 0, 0, "Claude Buddy — 4 sessions")]
    [InlineData(4, 1, 0, "Claude Buddy — 4 sessions, 1 needs you")]
    [InlineData(4, 0, 2, "Claude Buddy — 4 sessions, 2 working")]
    [InlineData(4, 1, 2, "Claude Buddy — 4 sessions, 1 needs you, 2 working")]
    public void TheTooltipCountsAndOnlyMentionsWhatIsActuallyHappening(
        int total, int waiting, int generating, string expected)
    {
        // "needs you" comes before "working" because it is the half of this the
        // user is being interrupted for — the same ordering the icon uses when
        // it decides which state to show.
        Assert.Equal(expected, TrayController.Summary(total, waiting, generating));
    }

    // --- DisplayName ---------------------------------------------------------

    [Fact]
    public void AnAgentsNameBeatsTheTitleItInheritedFromItsTeam()
    {
        // Every member of an agent team inherits the team session's title, so
        // without this a team of four produced four identical rows differing
        // only by the id the menu appends when it cannot tell them apart.
        Assert.Equal("MenuUX", TrayController.DisplayName(
            Entry(agent: "MenuUX", title: "Windows and Mac launch parity", cwd: "/Users/warren/buddy")));
    }

    [Fact]
    public void TheChatNameBeatsTheFolderOnceClaudeCodeHasWrittenOne()
    {
        Assert.Equal("Windows and Mac launch parity", TrayController.DisplayName(
            Entry(title: "Windows and Mac launch parity", cwd: "/Users/warren/buddy")));
    }

    [Fact]
    public void AnUnnamedSessionIsListedUnderItsFolder()
    {
        // Built with the running platform's own separator rather than asserted
        // for both. DisplayName trims '\' and '/' either way, but the split is
        // Path.GetFileName's, which only knows the native separator — so a
        // Windows cwd read on macOS comes back whole. That costs nothing in
        // practice (a Windows path only ever reaches this from a Windows hook)
        // and asserting otherwise would be testing BCL trivia on one runner and
        // failing on the other.
        var separator = Path.DirectorySeparatorChar;
        var cwd = $"{separator}Users{separator}warren{separator}Source{separator}Claude-Buddy";

        Assert.Equal("Claude-Buddy", TrayController.DisplayName(Entry(cwd: cwd)));
        Assert.Equal("Claude-Buddy", TrayController.DisplayName(Entry(cwd: cwd + separator)));
    }

    [Fact]
    public void ASessionOpenAtAFilesystemRootFallsBackToThePathItself()
    {
        // Trimming the separator off "/" leaves nothing for Path.GetFileName to
        // return, and a blank menu row says less than "/" does.
        Assert.Equal("/", TrayController.DisplayName(Entry(cwd: "/")));
    }

    [Fact]
    public void WithNeitherNameNorFolderTheIdIsAllThereIs()
    {
        Assert.Equal("session-1", TrayController.DisplayName(Entry(id: "session-1")));
    }

    // --- SessionLabel --------------------------------------------------------

    [Theory]
    [InlineData("idle", "buddy — idle")]
    [InlineData("generating", "buddy — working")]
    [InlineData("waiting", "buddy — needs you")]
    // A state nobody here has heard of reads as idle rather than as itself: the
    // menu is not the place to surface a hook writing something new.
    [InlineData("compacting", "buddy — idle")]
    public void EachRowEndsWithWhatTheSessionIsDoingInPlainWords(string state, string expected)
    {
        Assert.Equal(expected, TrayController.SessionLabel(
            Entry(title: "buddy", state: state), disambiguate: false));
    }

    [Fact]
    public void ALongChatNameIsCutAtTheNearestWordBoundary()
    {
        // Chat names are sentence-ish and can run long; a menu that wide covers
        // half the screen. Cutting mid-word reads as a rendering fault, so the
        // last space before the limit wins when there is one close enough.
        var label = TrayController.SessionLabel(
            Entry(title: "Windows and Mac launch parity for the installer scripts"),
            disambiguate: false);

        Assert.Equal("Windows and Mac launch parity for the… — idle", label);
    }

    [Fact]
    public void AnUnbrokenNameIsCutMidWordRatherThanBackToItsFirstSpace()
    {
        // The `space >= MaxLabelLength / 2` guard. Without it, a name whose only
        // space is near the front — a two-character prefix and then a long
        // unbroken string — would be cut back to almost nothing, which says less
        // than a mid-word cut does.
        var label = TrayController.SessionLabel(
            Entry(title: "ab " + new string('c', 50)), disambiguate: false);

        Assert.Equal("ab " + new string('c', 40) + "… — idle", label);
    }

    [Fact]
    public void ANameExactlyAtTheLimitIsLeftWhole()
    {
        var exactly44 = "exactly forty four characters long here.....";
        Assert.Equal(44, exactly44.Length);

        Assert.Equal(
            exactly44 + " — idle",
            TrayController.SessionLabel(Entry(title: exactly44), disambiguate: false));
    }

    [Fact]
    public void TwoSessionsResolvingToOneNameAreSeparatedByTheirIds()
    {
        // "Two sessions that resolve to the same name would otherwise produce
        // identical menu entries, which is worse than useless — you can't tell
        // which terminal a click will take you to."
        Assert.Equal("evidence (6a6f) — idle", TrayController.SessionLabel(
            Entry(id: "6a6fcb43-fa28-4894-9940-c1c6c9970e54", title: "evidence"),
            disambiguate: true));
    }

    [Fact]
    public void AnIdTooShortToShortenIsLeftOffRatherThanIndexedInto()
    {
        // A gateway or invented id is not a UUID, and there is no promise it has
        // four characters to take.
        Assert.Equal("evidence — idle", TrayController.SessionLabel(
            Entry(id: "abc", title: "evidence"), disambiguate: true));
    }
}
