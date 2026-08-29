using Xunit;

namespace ClaudeBuddy.Tests;

// The rules that decide where an inbound line goes, and what a relay is called.
//
// Small, and each one covers something that used to be reachable only by
// standing a whole relay up — which meant a live Claude Code session, somebody's
// quota, and a test that could not assert anything deterministic. Lifting each
// rule out to where it can be called directly is the repo's standing trade for
// exactly that situation.
public class MirrorRoutingTests
{
    private const string RealCrossSessionRow =
        "Another Claude session sent a message:\n" +
        "<cross-session-message from=\"bridge:session_01SX9H3aCQbpjVN9hM4njAXd\" " +
        "from-name=\"job-hunter\" from-mode=\"prompting\">\n" +
        "avatar.internal\n" +
        "</cross-session-message>";

    // A message from another machine arrives as a user row — the relay is handed
    // it, the way a person's typing is handed to a session.
    [Fact]
    public void AUserRowCarriesTheMessagesInIt()
    {
        var only = Assert.Single(BridgeProtocol.ParseInboundMessagesFrom("user", RealCrossSessionRow));

        Assert.Equal("job-hunter", only.FromName);
        Assert.Equal("avatar.internal", only.Body);
    }

    // The bug this rule fixes. An assistant row carrying the same tag is the
    // relay's own model quoting a message back while narrating what it just did
    // — its own writing, sometimes abridged. Delivering those put a paraphrase
    // in the panel beside the message it paraphrased.
    [Theory]
    [InlineData("assistant")]
    [InlineData("system")]
    [InlineData("summary")]
    [InlineData("")]
    // "attachment" is the row type the transcript actually writes for an
    // absorbed queued command, and it is deliberately *not* the one accepted
    // below. It is a catch-all — token reminders and file snapshots wear it too
    // — so trusting it would deliver whatever text any of those happened to
    // carry. Route identifies the one shape that qualifies and passes
    // AbsorbedRow; the raw row type never qualifies on its own.
    [InlineData("attachment")]
    public void NoOtherKindOfRowCarriesAMessageHoweverMuchItLooksLikeOne(string rowType) =>
        Assert.Empty(BridgeProtocol.ParseInboundMessagesFrom(rowType, RealCrossSessionRow));

    // CB-41. A message handed to a relay that is already mid-turn never becomes
    // a user row at all: Claude Code queues it, folds it into the running turn,
    // and writes it as a queued_command attachment. The rule above dropped every
    // one of those — on the machine this was found on, *every* cross-session
    // message it had ever received arrived that way, so the live view never
    // opened once.
    //
    // Trustworthy for the same reason a user row is: the prompt is the verbatim
    // text the session was handed, not a model's account of it. The roster
    // frames it was found on carry a SHA-256 of their own payload and verify
    // byte for byte after the trip.
    [Fact]
    public void AMessageAbsorbedIntoARunningTurnStillCarriesIt()
    {
        var only = Assert.Single(
            BridgeProtocol.ParseInboundMessagesFrom(BridgeProtocol.AbsorbedRow, RealCrossSessionRow));

        Assert.Equal("job-hunter", only.FromName);
        Assert.Equal("avatar.internal", only.Body);
    }

    // Case-sensitively "user", because the transcript's own vocabulary is fixed
    // and a near-miss here would start delivering narration again.
    [Fact]
    public void TheRowTypeIsMatchedExactly() =>
        Assert.Empty(BridgeProtocol.ParseInboundMessagesFrom("User", RealCrossSessionRow));

    // Same rule, same reason, for the row type added alongside it.
    [Fact]
    public void TheAbsorbedRowTypeIsMatchedExactlyToo() =>
        Assert.Empty(BridgeProtocol.ParseInboundMessagesFrom("Queued_Command", RealCrossSessionRow));

    [Fact]
    public void AUserRowWithNothingInItCarriesNothing() =>
        Assert.Empty(BridgeProtocol.ParseInboundMessagesFrom("user", "just some text"));

    // --- what a relay is called ---------------------------------------------

    // Two machines on one account used to build the identical relay name, and
    // that name is what SendMessage addresses.
    [Theory]
    [InlineData("Warrens-MacBook-Pro.local", "warrens-macbook-pro")]
    [InlineData("MINI.LOCAL", "mini")]
    [InlineData("avatar.internal", "avatarinternal")]
    [InlineData("mini", "mini")]
    [InlineData("MINI", "mini")]
    public void AMachineTagIsLowercaseAndTmuxSafe(string machine, string expected) =>
        Assert.Equal(expected, RemoteControlBridge.MachineTag(machine));

    // tmux parses a dot or a colon as a window/pane separator, so neither can
    // survive into a session name.
    [Fact]
    public void NothingTmuxParsesSurvivesIntoTheTag()
    {
        var tag = RemoteControlBridge.MachineTag("a.b:c d/e_f");

        Assert.DoesNotContain('.', tag);
        Assert.DoesNotContain(':', tag);
        Assert.DoesNotContain(' ', tag);
        Assert.DoesNotContain('/', tag);
        Assert.DoesNotContain('_', tag);
    }

    [Fact]
    public void AVeryLongMachineNameIsTruncatedRatherThanPastedWhole() =>
        Assert.Equal(20, RemoteControlBridge.MachineTag(new string('a', 200)).Length);

    // Never empty, and specifically never empty for *two* machines at once —
    // which would put them straight back into the collision this exists to end.
    [Theory]
    [InlineData("")]
    [InlineData("...")]
    [InlineData("---")]
    [InlineData(null)]
    public void AMachineWithNoUsableNameStillGetsATag(string? machine) =>
        Assert.Equal("machine", RemoteControlBridge.MachineTag(machine));

    // The prefix is what BridgeProtocol.IsOwnRelay keys on to keep relays off
    // the board, and what the mirror keys on to find a far Buddy. Adding the
    // machine tag must not have disturbed it.
    [Fact]
    public void ARelayStillWearsThePrefixEverythingElseLooksFor()
    {
        var bridge = new RemoteControlBridge(".claude-board");

        Assert.StartsWith("claude-buddy-rc-", bridge.ScratchName, StringComparison.Ordinal);
        Assert.True(RemoteMirrorServer.IsRelayName(bridge.ScratchName));

        Assert.True(new BridgeProtocol.RemoteAgent(
            bridge.ScratchName, "aa11bb", "Remote Control", "idle").IsOwnRelay);
    }

    [Fact]
    public void TwoAccountsStillGetDifferentRelayNames() =>
        Assert.NotEqual(
            new RemoteControlBridge(".claude").ScratchName,
            new RemoteControlBridge(".claude-board").ScratchName);

    // --- what other machines will call this relay ------------------------------

    // The single fact the whole mirror rests on, and it took a live probe to
    // settle because this repo's own notes and its code disagreed about it.
    //
    // A relay's Remote Control name is **not** the one passed to
    // `--remote-control`; that flag is ignored, and so is
    // `--remote-control-session-name-prefix`. The name comes from the working
    // directory's basename. So the relay is run from a directory named after
    // itself, and this is the assertion that keeps it that way — if the cwd ever
    // stops carrying the prefix, IsOwnRelay silently stops matching (stale
    // relays become phantom orbs) and mirror discovery silently stops finding
    // anything.
    [Fact]
    public void TheRelayRunsFromADirectoryNamedAfterItself()
    {
        var bridge = new RemoteControlBridge(".claude-board");
        var basename = Path.GetFileName(bridge.RelayCwd);

        Assert.Equal(bridge.ScratchName, basename);
        Assert.StartsWith("claude-buddy-rc-", basename, StringComparison.Ordinal);
        Assert.True(RemoteMirrorServer.IsRelayName(basename));
    }

    // Which means the name Claude Code derives from it is one every recogniser
    // already matches. The "-43" is Claude Code's own suffix, reproduced here
    // from the real probe: cwd `.../claude-buddy-rc` gave `claude-buddy-rc-43`.
    [Fact]
    public void TheNameClaudeCodeDerivesFromThatDirectoryIsStillRecognisable()
    {
        var bridge = new RemoteControlBridge(".claude-board");
        var derived = Path.GetFileName(bridge.RelayCwd).ToLowerInvariant() + "-43";

        Assert.True(new BridgeProtocol.RemoteAgent(
            derived, "b57bc7", "Remote Control", "idle").IsOwnRelay);

        Assert.True(RemoteMirrorServer.IsRelayName(derived));
    }

    // Two accounts on one machine, and the same account on two machines, all
    // have to stay distinguishable — the name is what SendMessage addresses.
    [Fact]
    public void EachRelayGetsItsOwnDirectory() =>
        Assert.NotEqual(
            new RemoteControlBridge(".claude").RelayCwd,
            new RemoteControlBridge(".claude-board").RelayCwd);

    // Kept out of the tree SweepStaleScratch walks. That sweeper deletes any
    // directory under ScratchRoot no configured account owns, and
    // PreparePrivateTmp deletes its own on every start — either would take the
    // relay's working directory out from under a running relay.
    [Fact]
    public void TheRelaysWorkingDirectoryIsNotInsideTheSweptTree()
    {
        var bridge = new RemoteControlBridge(".claude-board");

        Assert.DoesNotContain(RemoteControlBridge.ScratchRoot, bridge.RelayCwd, StringComparison.Ordinal);
        Assert.StartsWith(RemoteControlBridge.CwdRoot, bridge.RelayCwd, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelayNameCarriesNothingTmuxWouldSplitOn()
    {
        var name = new RemoteControlBridge(".claude-board").ScratchName;

        Assert.DoesNotContain('.', name);
        Assert.DoesNotContain(':', name);
    }

    // --- IsOwnRelayName / IsOwnRelayCwd --------------------------------------

    // The prefix, and only the prefix. A relay's full name carries the account
    // directory and the machine name, and both change — a user switches profile,
    // renames their Mac, or a build runs with the test tag set — so the relay
    // still running from before stops matching the name this app would launch
    // with today. Matching the live tag would stop recognising it, and an
    // unrecognised relay is the phantom orb TmuxNames' comment records having
    // measured, arriving from the other side.
    [Fact]
    public void EveryRelayThisAppHasEverLaunchedIsRecognised()
    {
        // What it launches today.
        Assert.True(RemoteControlBridge.IsOwnRelayName(
            "claude-buddy-rc--claude-warrens-mbp"));

        // A different account directory, a different machine, and a build with
        // the test tag set — all names this app has launched and none of them the
        // current one.
        Assert.True(RemoteControlBridge.IsOwnRelayName(
            "claude-buddy-rc--claude-board-some-other-mac"));
        Assert.True(RemoteControlBridge.IsOwnRelayName(
            "claude-buddy-rc--claude-t1-warrens-mbp"));

        // Case is not part of the identity: tmux and the bridge's own listing
        // have disagreed about it before, which is why the existing comparison
        // was already ordinal-ignore-case.
        Assert.True(RemoteControlBridge.IsOwnRelayName("CLAUDE-BUDDY-RC-anything"));
    }

    // And the distinction that has to survive: a person's own remote-control
    // session is not this app's plumbing and keeps its orb. `claude-buddy-rc-` is
    // this app's namespace — RelayCwd generates it and nobody types it — which is
    // what makes the prefix able to draw this line at all.
    [Theory]
    [InlineData("job-hunter")]
    [InlineData("warrenthompson-9b")]          // the measured non-prefixed shape
    [InlineData("claude-buddy")]               // close, and not it
    [InlineData("my-claude-buddy-rc-copy")]    // contains it, does not start with it
    [InlineData("")]
    [InlineData(null)]
    public void SomebodyElsesSessionIsNotOurRelay(string? name)
    {
        Assert.False(RemoteControlBridge.IsOwnRelayName(name));
    }

    // Asked of a status file, which is where the local scan meets it. The cwd's
    // last segment *is* the relay name by construction — RelayCwd runs every relay
    // from a directory named after itself, precisely so the name is recoverable —
    // so this is the same key, already on disk, costing no `ps`.
    [Fact]
    public void ARelaysStatusFileIsRecognisedByItsCwd()
    {
        Assert.True(RemoteControlBridge.IsOwnRelayCwd(
            "/Users/w/Library/Application Support/ClaudeBuddy/rc-cwd/claude-buddy-rc--claude-warrens-mbp"));

        // A trailing separator must not turn the leaf into an empty string, which
        // would then match nothing and quietly give the relay its orb back.
        Assert.True(RemoteControlBridge.IsOwnRelayCwd(
            "/Users/w/rc-cwd/claude-buddy-rc--claude-warrens-mbp/"));
    }

    [Theory]
    [InlineData("/Users/warren/Source/Claude-Buddy")]
    [InlineData("/Users/warren")]
    [InlineData("")]
    [InlineData(null)]
    public void AnOrdinarySessionsCwdIsNotARelays(string? cwd)
    {
        Assert.False(RemoteControlBridge.IsOwnRelayCwd(cwd));
    }
}
