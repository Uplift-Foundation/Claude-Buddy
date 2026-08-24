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
    public void NoOtherKindOfRowCarriesAMessageHoweverMuchItLooksLikeOne(string rowType) =>
        Assert.Empty(BridgeProtocol.ParseInboundMessagesFrom(rowType, RealCrossSessionRow));

    // Case-sensitively "user", because the transcript's own vocabulary is fixed
    // and a near-miss here would start delivering narration again.
    [Fact]
    public void TheRowTypeIsMatchedExactly() =>
        Assert.Empty(BridgeProtocol.ParseInboundMessagesFrom("User", RealCrossSessionRow));

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

    [Fact]
    public void ARelayNameCarriesNothingTmuxWouldSplitOn()
    {
        var name = new RemoteControlBridge(".claude-board").ScratchName;

        Assert.DoesNotContain('.', name);
        Assert.DoesNotContain(':', name);
    }
}
