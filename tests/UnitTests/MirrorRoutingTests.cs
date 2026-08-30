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

    // --- CB-45: addressing a peer so SendMessage cannot stop to ask -------------

    private static BridgeProtocol.RemoteAgent Peer(
        string name, string peerRef, string status = "idle") =>
        new(name, peerRef, "Remote Control", status);

    // The ordinary case, and the one that must not change. A ref is only
    // resolvable while it is listed, so spending one where the bare name already
    // works would trade a rare failure for a new one.
    [Fact]
    public void AUniqueNameIsAddressedBare()
    {
        var peers = new[] { Peer("job-hunter", "94f106"), Peer("someone-else", "aa11bb") };

        Assert.Equal("job-hunter", BridgeProtocol.AddressFor("job-hunter", peers));
    }

    // The bug. Two live sessions wearing one name make SendMessage render a
    // "which one?" picker in the relay's pane and wait — and because Buddy sends
    // every frame by typing into that pane, everything behind it stops too.
    [Fact]
    public void ADuplicatedNameIsAddressedByItsRef()
    {
        var peers = new[] { Peer("job-hunter", "2548f2"), Peer("job-hunter", "462b2e") };

        Assert.Equal("job-hunter [2548f2]", BridgeProtocol.AddressFor("job-hunter", peers));
    }

    // The case it was actually found on: one live session wearing several stale
    // registrations of its own. A registration outlives its process, so counting
    // the dead ones would make a name look ambiguous when only one thing can
    // answer to it.
    [Fact]
    public void OfflineRegistrationsAreNotCompetitionForTheName()
    {
        var peers = new[]
        {
            Peer("job-hunter", "2548f2"),
            Peer("job-hunter", "462b2e", "offline"),
            Peer("job-hunter", "889aa1", "offline")
        };

        Assert.Equal("job-hunter", BridgeProtocol.AddressFor("job-hunter", peers));
    }

    // Deterministic rather than arbitrary: the first live row wins, so the same
    // peer list always produces the same address and a retry goes where the
    // first attempt did.
    [Fact]
    public void ThreeLiveNamesakesPickTheFirstEveryTime()
    {
        var peers = new[]
        {
            Peer("job-hunter", "111111"),
            Peer("job-hunter", "222222"),
            Peer("job-hunter", "333333")
        };

        Assert.Equal("job-hunter [111111]", BridgeProtocol.AddressFor("job-hunter", peers));
        Assert.Equal("job-hunter [111111]", BridgeProtocol.AddressFor("job-hunter", peers));
    }

    // Names are matched the way every other comparison in this file matches
    // them, because ListAgents is not the only thing that writes one.
    [Fact]
    public void TheNameIsMatchedWithoutRegardToCase()
    {
        var peers = new[] { Peer("Job-Hunter", "2548f2"), Peer("job-hunter", "462b2e") };

        Assert.Equal("job-hunter [2548f2]", BridgeProtocol.AddressFor("job-hunter", peers));
    }

    // Nothing to disambiguate with. Sending the bare name may still stop on the
    // picker, but inventing an address that resolves to nothing would fail every
    // time instead of sometimes — and a row with no ref is a shape this code has
    // never seen rather than one it can reason about.
    [Fact]
    public void ANamesakeWithNoRefIsStillAddressedBare()
    {
        var peers = new[] { Peer("job-hunter", ""), Peer("job-hunter", "462b2e") };

        Assert.Equal("job-hunter", BridgeProtocol.AddressFor("job-hunter", peers));
    }

    // Before the first poll there is no evidence of ambiguity, and a name that
    // has always worked is a better guess than a ref nobody has listed.
    [Theory]
    [InlineData("job-hunter")]
    [InlineData("")]
    public void WithNoPeerListTheNameIsSentAsItIs(string name) =>
        Assert.Equal(name, BridgeProtocol.AddressFor(name, null));

    // A name nothing in the list answers to is left alone rather than guessed at.
    [Fact]
    public void ANameNobodyClaimsIsSentAsItIs()
    {
        var peers = new[] { Peer("someone-else", "aa11bb") };

        Assert.Equal("job-hunter", BridgeProtocol.AddressFor("job-hunter", peers));
    }

    [Fact]
    public void AnEmptyNameIsNotDecorated() =>
        Assert.Equal("", BridgeProtocol.AddressFor("", new[] { Peer("", "aa11bb"), Peer("", "bb22cc") }));

    // --- CB-52: a relay that is waiting rather than working --------------------

    // The picker that actually stalled the mini's relay, copied from the pane.
    // Every frame Buddy sends is typed into that pane, so this did not fail one
    // send — it stopped the machine serving until somebody pressed Escape.
    private const string PickerPane = """
          2. [2548f2]
             Remote Control session on another machine, active 22s ago.
          3. [462b2e]
             Remote Control session on another machine, active 29s ago.
          4. All three
             Send the identical line to each of the three sessions.
          5. Type something.
          6. Chat about this
        Enter to select · ↑/↓ to navigate · Esc to cancel
        """;

    [Fact]
    public void APickerLeftOnScreenIsReportedAsAStall()
    {
        var stall = Assert.NotNull(BridgeProtocol.ReadStall(PickerPane));

        Assert.Contains("Escape", stall.Advice);
        Assert.Contains("waiting", stall.Describe());
    }

    // Not a select list at all, which is why the generic footer test cannot be
    // the only one: this stalls a relay while drawing nothing that looks like a
    // prompt, and it was the one nothing in the app would ever have noticed —
    // it was fixed by hand on two accounts.
    [Fact]
    public void AHeldMessageIsReportedWithTheSettingThatClearsIt()
    {
        var stall = Assert.NotNull(BridgeProtocol.ReadStall(
            "  ⏵ message from job-hunter not delivered to Claude (1 held)"));

        Assert.Contains("crossSessionInbound", stall.Advice);
        Assert.Contains("accept", stall.Advice);
    }

    // First-run setup, and the advice has to differ: Escape does not finish a
    // wizard, and telling someone to press it would send them round the loop
    // that produced the problem.
    [Theory]
    [InlineData("Not logged in · Please run /login")]
    [InlineData("  7. Light mode (ANSI colors only)")]
    [InlineData("Choose the text style that looks best with your terminal")]
    public void FirstRunSetupSaysToFinishSetupRatherThanToPressEscape(string pane)
    {
        var stall = Assert.NotNull(BridgeProtocol.ReadStall(pane));

        Assert.DoesNotContain("Escape", stall.Advice);
        Assert.Contains("finish setup", stall.Advice);
    }

    [Fact]
    public void AToolPermissionPromptIsNamedAsOne()
    {
        var stall = Assert.NotNull(BridgeProtocol.ReadStall(
            "Bash command\n  gunzip\n\nDo you want to proceed?\n  1. Yes\n  2. No"));

        Assert.Contains("tool-permission", stall.Kind);
        Assert.Contains("Escape", stall.Advice);
    }

    // The whole point of the guard, and the reason it is not a fifth special
    // case. An unrecognised prompt is still reported as a prompt, with the one
    // instruction that clears all of them — because there will be a fifth shape
    // and the alternative is the silence this replaces.
    [Fact]
    public void AnUnrecognisedPromptIsStillReportedAsOne()
    {
        var stall = Assert.NotNull(BridgeProtocol.ReadStall(
            "Some future question nobody has written a rule for\n"
            + "  1. One\n  2. Two\nEnter to select · Esc to cancel"));

        Assert.Contains("does not recognise", stall.Kind);
        Assert.Contains("Escape", stall.Advice);
    }

    // A relay that is merely busy must stay distinguishable from one that is
    // stuck: reporting a working relay as stalled would send someone to press
    // Escape in a pane that is mid-answer, which is how you break a transfer
    // that was about to land.
    [Theory]
    [InlineData("")]
    [InlineData("Envisioning… 1m 59s · ↓ 1.1k tokens")]
    [InlineData("  /remote-control is active · Continue here, on your phone")]
    [InlineData("> Use SendMessage to send job-hunter exactly this text")]
    public void ARelayThatIsWorkingIsNotReportedAsStalled(string pane) =>
        Assert.Null(BridgeProtocol.ReadStall(pane));

    // --- what the Settings row says -------------------------------------------

    // The stall joins the count rather than replacing it. Both are true, and a
    // reader needs both to know what they have lost — the same lesson the
    // warning already taught this function.
    [Fact]
    public void TheStatusLineKeepsTheCountAlongsideTheStall()
    {
        var said = RemoteControlSessions.Compose("3 remote sessions", null, "waiting for an answer");

        Assert.Contains("3 remote sessions", said);
        Assert.Contains("waiting for an answer", said);
    }

    // A relay that has not started has no count worth keeping, so the stall is
    // the whole answer rather than an appendix to "starting".
    [Fact]
    public void AStallReplacesAStateThatSaysNothing() =>
        Assert.Equal("waiting", RemoteControlSessions.Compose("starting", null, "waiting"));

    // All three facts survive together.
    [Fact]
    public void AStallAndAWarningBothSurvive()
    {
        var said = RemoteControlSessions.Compose("1 remote session", "login expires", "waiting");

        Assert.Contains("1 remote session", said);
        Assert.Contains("waiting", said);
        Assert.Contains("login expires", said);
    }

    // Nothing wrong is still the plain state, unchanged from before this existed.
    [Fact]
    public void NoStallLeavesTheStateExactlyAsItWas() =>
        Assert.Equal("2 remote sessions", RemoteControlSessions.Compose("2 remote sessions", null));
}
