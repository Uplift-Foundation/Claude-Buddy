using Xunit;

namespace ClaudeBuddy.UnitTests;

// The chat panel's third header line: which session, which folder, which
// machine.
//
// Every rule in ChatHeaderMeta is a judgement about what a person should read,
// and the reason it is a pure function at all is that a judgement which can
// only be checked by opening the app is a judgement nobody checks. So the table
// below is the specification: one case per arm, including the two arms that
// cannot be arranged on a real machine — a far session reporting this machine's
// own name, and a header that knows nothing whatsoever.
public class ChatHeaderMetaTests
{
    // The shapes this actually meets. Named rather than inlined because every
    // case below is about one fact changing, and a wall of literals makes the
    // one that moved hard to see.
    private const string Home = "/Users/warren";
    private const string Project = "/Users/warren/Source/HauntedMansionTerminalTheme";
    private const string Mine = "warrens-macbook-pro";
    private const string Mini = "the-host-mac-mini";

    // Text is the two rows read as one string, so a case that asserts Text and
    // not the rows could pass while the panel drew something else — and the
    // other way round. Checked on every composed result rather than once,
    // which is what makes it an invariant instead of an example.
    private static ChatHeaderMeta.Meta Composed(
        string? title, string? sessionName, string? cwd, string? home,
        string? machine, bool isLocalMachine)
    {
        var meta = ChatHeaderMeta.Compose(title, sessionName, cwd, home, machine, isLocalMachine);

        Assert.Equal(
            string.Join(" · ", new[] { meta.Lead, meta.Machine }.Where(part => part.Length > 0)),
            meta.Text);

        return meta;
    }

    // --- the ordinary case ------------------------------------------------

    [Fact]
    public void APersonaNamedSessionShowsItsName_ItsFolderAndItsMachine()
    {
        // CB-133 put "Leota" on the title line, which is exactly what makes the
        // session's own name worth showing: the header no longer says it
        // anywhere else.
        var meta = Composed("Leota", "haunted-mansion", Project, Home, Mine, true);

        Assert.True(meta.IsVisible);
        Assert.Equal(
            "haunted-mansion · ~/Source/HauntedMansionTerminalTheme · warrens-macbook-pro",
            meta.Text);

        // The machine is its own row — it is drawn in its own colour when the
        // session is somewhere else, and at the width a panel opens at the two
        // do not fit on one line. So it carries no separator: the dot in the
        // line above belongs to the two facts it actually sits between.
        Assert.Equal("haunted-mansion · ~/Source/HauntedMansionTerminalTheme", meta.Lead);
        Assert.Equal(Mine, meta.Machine);
        Assert.False(meta.RemoteMachine);
    }

    [Fact]
    public void TheTooltipCarriesTheWholePathRatherThanTheHomeRelativeOne()
    {
        // CharacterEllipsis is what happens to this line in a narrow panel, so
        // the tooltip is the only place the whole path exists — and a tooltip
        // repeating the abbreviation would be a hover that answers nothing.
        var meta = Composed("Leota", "haunted-mansion", Project, Home, Mine, true);

        Assert.Equal("haunted-mansion · " + Project + " · " + Mine, meta.Tooltip);
        Assert.DoesNotContain("~", meta.Tooltip);
    }

    [Fact]
    public void AVeryLongPathIsLeftWholeInTheTooltipAndNotShortenedHere()
    {
        // Deliberately not truncated in code: TextTrimming decides where a line
        // runs out at the width the panel actually has, and a helper that
        // guessed a character count would be trimming a second time at a width
        // nobody measured.
        var deep = Home + "/Source/" + string.Join("/", Enumerable.Repeat("a-long-directory-name", 6));

        var meta = Composed("Leota", "haunted-mansion", deep, Home, Mine, true);

        Assert.Contains("~/Source/a-long-directory-name/a-long-directory-name", meta.Text);
        Assert.Contains(deep, meta.Tooltip);
        Assert.DoesNotContain("…", meta.Text);
    }

    // --- the session name, and when it is noise ---------------------------

    [Theory]
    [InlineData("haunted-mansion")]
    [InlineData("Haunted-Mansion")]
    [InlineData("  haunted-mansion  ")]
    public void TheSessionNameIsDroppedWhenTheTitleAlreadySaysIt(string title)
    {
        // The ordinary local session: LocalCliChatSession builds DisplayName
        // straight out of status.Title, so without this rule the panel would
        // say "haunted-mansion" twice, one line under the other. Case and
        // whitespace do not make a repeat into a new fact.
        var meta = Composed(title, "haunted-mansion", Project, Home, Mine, true);

        Assert.Equal("~/Source/HauntedMansionTerminalTheme · " + Mine, meta.Text);
        Assert.DoesNotContain("haunted-mansion ·", meta.Text);
    }

    [Fact]
    public void ANullTitleIsNotSomethingTheNameCanRepeat()
    {
        // TitleText.Text is a nullable string and the panel hands it straight
        // over, so this is the panel's own first frame rather than a defensive
        // case: nothing has been titled yet, and a name that suppressed itself
        // against a null would leave the row with nothing but a folder on it.
        var meta = Composed(null, "haunted-mansion", Project, Home, Mine, true);

        Assert.Equal(
            "haunted-mansion · ~/Source/HauntedMansionTerminalTheme · " + Mine, meta.Text);
    }

    [Fact]
    public void AnEmptySessionNameIsSimplyAbsent()
    {
        // The early state of every local session, and the reason ApplyTitle
        // re-reads rather than caching at Bind: Claude Code names a chat some
        // way into it, and the status file says "title":"" until it does.
        var meta = Composed("haunted-mansion", "", Project, Home, Mine, true);

        Assert.Equal("~/Source/HauntedMansionTerminalTheme · " + Mine, meta.Text);
    }

    [Fact]
    public void TheNameArrivesOnALaterWriteWithoutAnythingElseMoving()
    {
        // The pair above and below, side by side, because the transition is the
        // behaviour: the same panel, the same folder, one more fact.
        var before = Composed("Leota", "", Project, Home, Mine, true);
        var after = Composed("Leota", "haunted-mansion", Project, Home, Mine, true);

        Assert.Equal("~/Source/HauntedMansionTerminalTheme · " + Mine, before.Text);
        Assert.Equal("haunted-mansion · " + before.Text, after.Text);
    }

    // --- the directory ----------------------------------------------------

    [Fact]
    public void AGatewayRoomHasNoDirectoryAndSaysOnlyTheMachine()
    {
        // A conversation in a channel is not running anywhere on this disk. The
        // room is already the title line, so the name is suppressed by the same
        // rule as any other repeat and the machine is all that is left.
        var meta = Composed("#openclaw-management", "", "", Home, Mine, true);

        Assert.True(meta.IsVisible);
        Assert.Equal(Mine, meta.Text);
        Assert.Equal("", meta.Lead);
        Assert.Equal(Mine, meta.Tooltip);
    }

    [Theory]
    // Under home, which is the case this exists for.
    [InlineData("/Users/warren/Source/Claude-Buddy", "/Users/warren", "~/Source/Claude-Buddy")]
    // Home itself. "~" rather than "~/" — the trailing slash would be the one
    // character in the line that says nothing.
    [InlineData("/Users/warren", "/Users/warren", "~")]
    // A home the caller handed over with a trailing separator. Environment
    // does not do this, but a settings file or a shell can.
    [InlineData("/Users/warren/Source", "/Users/warren/", "~/Source")]
    // Not under home at all: a checkout in /tmp, which is where every worktree
    // in this project lives.
    [InlineData("/private/tmp/claude-buddy-cb134", "/Users/warren", "/private/tmp/claude-buddy-cb134")]
    // A prefix match is not a path match. `~arren/Source` would be a path that
    // does not exist, which is worse than the whole one.
    [InlineData("/Users/warrenthompson/Source", "/Users/warren", "/Users/warrenthompson/Source")]
    // Windows, tested from a Mac deliberately: the rule is OrdinalIgnoreCase on
    // both platforms so the answer does not depend on the machine the suite
    // runs on, and the native separator is kept rather than normalised because
    // a path is a thing somebody pastes into their own shell.
    [InlineData(@"C:\Users\Warren\Source\Claude-Buddy", @"C:\Users\warren", @"~\Source\Claude-Buddy")]
    [InlineData(@"C:\Users\Warren", @"C:\Users\Warren\", "~")]
    [InlineData(@"C:\Work\Claude-Buddy", @"C:\Users\Warren", @"C:\Work\Claude-Buddy")]
    // No home to be relative to. Handing back the whole path beats handing
    // back nothing, and this is what a locked-down service account looks like.
    [InlineData("/Users/warren/Source", "", "/Users/warren/Source")]
    [InlineData("/Users/warren/Source", null, "/Users/warren/Source")]
    // Nothing in, nothing out.
    [InlineData("", "/Users/warren", "")]
    [InlineData(null, "/Users/warren", "")]
    [InlineData("   ", "/Users/warren", "")]
    public void HomeRelativeCollapsesOnlyARealParent(string? cwd, string? home, string expected) =>
        Assert.Equal(expected, ChatHeaderMeta.HomeRelative(cwd, home));

    [Fact]
    public void ADirectoryWithNoNameAndNoMachineStillDrawsTheName()
    {
        // Everything is optional and every combination has to compose, which is
        // the whole reason the facts are joined rather than formatted.
        var meta = Composed("Leota", "haunted-mansion", "", Home, "", true);

        Assert.True(meta.IsVisible);
        Assert.Equal("haunted-mansion", meta.Text);
        Assert.Equal("haunted-mansion", meta.Lead);
        Assert.Equal("", meta.Machine);
        Assert.False(meta.RemoteMachine);
    }

    [Fact]
    public void ANamelessSessionInAFolderOnThisMachineDrawsBoth()
    {
        var meta = Composed("haunted-mansion", "haunted-mansion", Project, Home, Mine, true);

        Assert.Equal("~/Source/HauntedMansionTerminalTheme · " + Mine, meta.Text);
        Assert.Equal("~/Source/HauntedMansionTerminalTheme", meta.Lead);
        Assert.Equal(Mine, meta.Machine);
    }

    // --- the machine ------------------------------------------------------

    [Fact]
    public void AMirroredSessionsMachineIsFlaggedForTheAccentColour()
    {
        // The case that actually matters, and the reason the machine is shown
        // at all: a panel open on this Mac showing a conversation happening on
        // the mini. It has to be the thing you notice in a line that is
        // otherwise deliberately quiet.
        var meta = Composed("Aurora", "deploy the release", "/Users/warren/Source/x", Home, Mini, false);

        Assert.True(meta.RemoteMachine);
        Assert.Equal(Mini, meta.Machine);
        Assert.EndsWith(Mini, meta.Text);
    }

    [Fact]
    public void ThisMachineIsShownAndIsNotFlagged()
    {
        // Shown always — Warren's explicit choice, and what makes a screenshot
        // taken on the mini say where it came from. Not flagged, because
        // colouring the ordinary case would make the interesting one invisible.
        var meta = Composed("Leota", "haunted-mansion", Project, Home, Mine, true);

        Assert.False(meta.RemoteMachine);
        Assert.Equal(Mine, meta.Machine);
    }

    [Fact]
    public void AnUnknownMachineIsNotDrawnAsARemoteOne()
    {
        // MachineFor never produces this — an empty name means "this machine" —
        // but Compose is a public rule and a blank token drawn in the accent
        // colour would be a coloured separator with nothing after it.
        var meta = Composed("Leota", "haunted-mansion", Project, Home, "", false);

        Assert.False(meta.RemoteMachine);
        Assert.Equal("haunted-mansion · ~/Source/HauntedMansionTerminalTheme", meta.Text);
        Assert.DoesNotContain("· ", meta.Text[^3..]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ASessionThatNamesNoMachineIsOnThisOne(string? reported)
    {
        // Not a guess: IRemoteChatMachine is implemented only by the mirror, so
        // anything silent about a machine is a local CLI session or a gateway
        // conversation, and both are being read where they are being read.
        var (name, isLocal) = ChatHeaderMeta.MachineFor(reported, Mine);

        Assert.Equal(Mine, name);
        Assert.True(isLocal);
    }

    [Fact]
    public void ASessionOnAnotherMachineKeepsThatMachinesName()
    {
        var (name, isLocal) = ChatHeaderMeta.MachineFor(Mini, Mine);

        Assert.Equal(Mini, name);
        Assert.False(isLocal);
    }

    [Theory]
    [InlineData("warrens-macbook-pro")]
    [InlineData("Warrens-MacBook-Pro")]
    [InlineData("  warrens-macbook-pro  ")]
    public void AFarSessionReportingThisMachineIsLocal(string reported)
    {
        // Unarrangeable on a real machine — the mirror only reports a machine
        // that is not this one — and exactly why the comparison is a rule with
        // a test rather than a bool computed at the call site. Case-insensitive
        // because a hostname's case is not a machine.
        var (name, isLocal) = ChatHeaderMeta.MachineFor(reported, Mine);

        Assert.Equal(reported.Trim(), name);
        Assert.True(isLocal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AMachineWithNoNameForItselfNamesNobody(string? mine)
    {
        // MachineNames.Mine() has never returned empty on a real machine, and
        // this is what the line does if it ever does: no token, rather than a
        // separator with a hole after it.
        var (name, isLocal) = ChatHeaderMeta.MachineFor(null, mine);

        Assert.Equal("", name);
        Assert.True(isLocal);
    }

    // --- knowing nothing --------------------------------------------------

    [Fact]
    public void AHeaderThatKnowsNothingCollapsesItsLineEntirely()
    {
        // A panel with no facts stays exactly as tall as it was before this
        // existed, which is the whole reason IsVisible is part of the answer
        // instead of being inferred from an empty string at the call site.
        var meta = Composed("#openclaw-management", "", "", Home, "", true);

        Assert.False(meta.IsVisible);
        Assert.Equal("", meta.Text);
        Assert.Equal("", meta.Tooltip);
        Assert.Equal("", meta.Lead);
        Assert.Equal("", meta.Machine);
        Assert.Equal(ChatHeaderMeta.Nothing, meta);
    }

    [Fact]
    public void NothingIsTheSameAnswerHoweverTheEmptinessArrives()
    {
        // Nulls and whitespace are the same nothing: the status file's fields
        // default to "", the mirror's machine is null until the roster answers,
        // and a hook write leaves whatever whitespace the writer left on it.
        Assert.Equal(ChatHeaderMeta.Nothing, Composed(null, null, null, null, null, true));
        Assert.Equal(ChatHeaderMeta.Nothing, Composed("  ", "  ", "  ", "  ", "  ", false));
    }

    [Fact]
    public void NeitherRowCarriesTheOthersSeparator()
    {
        // Not tidiness: each row is trimmed on its own at whatever width the
        // panel has, so a separator left on the end of one would be the last
        // thing trimmed off it — a dot hanging in the gap the ellipsis just
        // made, pointing at a row underneath it.
        var both = Composed("Leota", "haunted-mansion", Project, Home, Mine, true);

        Assert.DoesNotContain("·", both.Machine);
        Assert.False(both.Lead.StartsWith(" ") || both.Lead.EndsWith(" "),
            "a row must be trimmable without leaving a separator behind");

        // And the separator is still in the one place two facts really are
        // side by side.
        Assert.Contains(" · ", both.Lead);
    }
}
