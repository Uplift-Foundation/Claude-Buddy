using Xunit;

namespace ClaudeBuddy.Tests;

// Covers BridgeProtocol — the parsing side of Buddy's conversation with its
// hidden Remote Control bridge session.
//
// Every fixture below is real captured output, per this repo's fixture rule
// (CLAUDE.md, Testing section). They came from a bridge running on a MacBook
// talking to a Claude Code session on a Mac mini (`avatar.internal`) on
// 23 Aug 2026, both on the `~/.claude-board` account, Claude Code 2.1.241.
// docs/remote-control-findings.md quotes the same strings and records which
// machine produced each one. Nothing here was written from memory — the first
// version of this feature's parser was designed against an *assumed* fenced-JSON
// protocol that the real output turned out not to use at all, which is the whole
// reason the rule exists.
public class BridgeProtocolTests
{
    // Exactly what the ListAgents tool_result contained, header line included.
    // The header matters: it carries a name and a [ref] in the same shape as a
    // peer row, so it is the thing most likely to be mis-parsed as a peer.
    private const string RealListAgentsOutput =
        "This session is claude-buddy-52 [30e947] — the name other sessions use to message it (it is not listed below; a message to it would be a message to yourself).\n" +
        "\n" +
        "Peer sessions (1):\n" +
        "  job-hunter [94f106]  ·  Remote Control  ·  idle";

    // The reply from the mini, as it appeared in the bridge's transcript.
    private const string RealCrossSessionRow =
        "Another Claude session sent a message:\n" +
        "<cross-session-message from=\"bridge:session_01SX9H3aCQbpjVN9hM4njAXd\" from-name=\"job-hunter\" from-mode=\"prompting\">\n" +
        "avatar.internal\n" +
        "</cross-session-message>\n" +
        "\n" +
        "This came from another Claude session — not typed by your user, but very likely working on their behalf.";

    private const string RealSendMessageResult =
        "{\"success\":true,\"message\":\"“bridge connectivity test, asking for hostname” → job-hunter " +
        "(a Claude session on another machine, over Remote Control)\"," +
        "\"msg_id\":\"e547dcf7-4510-4992-b14e-faa5b95e1872\"}";

    // The session header, verbatim. Two warnings and the RC line, which is the
    // only positive confirmation that Remote Control actually attached.
    private const string RealBanner =
        " ⚠ 2 MCP servers need authentication · run /mcp\n" +
        " ⚠ Your login expires in 3 days · run /login to renew\n" +
        "  /remote-control is active · Continue here, on your phone, or at https://claude.ai/code/session_01XfZfJnPe9EGxapEmtzrhBL";

    [Fact]
    public void ParseAgents_ReadsTheRealPeerRow()
    {
        var agents = BridgeProtocol.ParseAgents(RealListAgentsOutput);

        var agent = Assert.Single(agents);
        Assert.Equal("job-hunter", agent.Name);
        Assert.Equal("94f106", agent.Ref);
        Assert.Equal("Remote Control", agent.Kind);
        Assert.Equal("idle", agent.Status);
        Assert.True(agent.IsRemoteControl);
    }

    // The header names a session and brackets a ref exactly like a peer row
    // does. It is flush left where peer rows are indented, which is the only
    // thing separating them — so this is the regression that matters most.
    [Fact]
    public void ParseAgents_DoesNotMistakeTheHeaderForAPeer()
    {
        var agents = BridgeProtocol.ParseAgents(RealListAgentsOutput);

        Assert.DoesNotContain(agents, a => a.Name.Contains("This session"));
        Assert.DoesNotContain(agents, a => a.Ref == "30e947");
    }

    // Local peers are labelled differently, and that label is the only way to
    // tell a session on another machine from one on this one. Mixed output was
    // not captured in a single call, so this composes rows whose individual
    // shapes were each observed — the "interactive"/"bg" labels came from the
    // other account's own ListAgents during the same spike.
    [Fact]
    public void IsRemoteControl_SeparatesRemoteSessionsFromLocalOnes()
    {
        const string mixed =
            "Peer sessions (3):\n" +
            "  job-hunter [94f106]  ·  Remote Control  ·  idle\n" +
            "  evidence [038b3d]  ·  interactive  ·  idle\n" +
            "  claude-buddy [8241aa]  ·  bg  ·  busy";

        var agents = BridgeProtocol.ParseAgents(mixed);

        Assert.Equal(3, agents.Count);
        var remote = Assert.Single(agents, a => a.IsRemoteControl);
        Assert.Equal("job-hunter", remote.Name);
    }

    // Buddy's own relay, seen from a later relay. Real output: an earlier
    // relay's Remote Control registration outlives its process, so it comes back
    // as a peer with status "offline" — and without filtering it would put a
    // phantom orb on screen named after Buddy's own plumbing.
    [Fact]
    public void ARelayOfOurOwnIsNotWorthAnOrb()
    {
        const string withOwnRelay =
            "Peer sessions (2):\n" +
            "  claude-buddy-rc--claude-board [6dfd49]  ·  Remote Control  ·  offline\n" +
            "  job-hunter [94f106]  ·  Remote Control  ·  running";

        var agents = BridgeProtocol.ParseAgents(withOwnRelay);
        Assert.Equal(2, agents.Count);

        var worth = Assert.Single(agents, a => a.IsWorthAnOrb);
        Assert.Equal("job-hunter", worth.Name);

        var own = agents.Single(a => a.Name.StartsWith("claude-buddy-rc-"));
        Assert.True(own.IsOwnRelay);
        Assert.True(own.IsOffline);
    }

    // An offline peer that is not ours is still not worth an orb: there is
    // nothing there to send to.
    [Fact]
    public void AnOfflinePeerIsNotWorthAnOrb()
    {
        var agents = BridgeProtocol.ParseAgents("  gone [111111]  ·  Remote Control  ·  offline");

        var agent = Assert.Single(agents);
        Assert.True(agent.IsRemoteControl);
        Assert.False(agent.IsWorthAnOrb);
    }

    [Fact]
    public void ARunningPeerIsWorthAnOrb()
    {
        var agents = BridgeProtocol.ParseAgents("  job-hunter [94f106]  ·  Remote Control  ·  running");

        Assert.True(Assert.Single(agents).IsWorthAnOrb);
    }

    [Fact]
    public void ParseAgents_ReturnsEmptyRatherThanThrowing_OnJunk()
    {
        Assert.Empty(BridgeProtocol.ParseAgents(""));
        Assert.Empty(BridgeProtocol.ParseAgents("   "));
        Assert.Empty(BridgeProtocol.ParseAgents("No peer sessions."));
    }

    [Fact]
    public void ParseInboundMessage_ReadsTheRealReply()
    {
        var inbound = BridgeProtocol.ParseInboundMessage(RealCrossSessionRow);

        Assert.NotNull(inbound);
        Assert.Equal("job-hunter", inbound!.Value.FromName);
        Assert.Equal("bridge:session_01SX9H3aCQbpjVN9hM4njAXd", inbound.Value.From);
        Assert.Equal("prompting", inbound.Value.Mode);

        // Body only — none of the surrounding narration the row wraps it in.
        Assert.Equal("avatar.internal", inbound.Value.Body);
    }

    // Attributes are read by name, so a reordering or an added one still parses.
    [Fact]
    public void ParseInboundMessage_ToleratesReorderedAndExtraAttributes()
    {
        const string reordered =
            "<cross-session-message from-mode=\"prompting\" from-name=\"job-hunter\" " +
            "something-new=\"x\" from=\"bridge:session_1\">hello</cross-session-message>";

        var inbound = BridgeProtocol.ParseInboundMessage(reordered);

        Assert.NotNull(inbound);
        Assert.Equal("job-hunter", inbound!.Value.FromName);
        Assert.Equal("hello", inbound.Value.Body);
    }

    [Fact]
    public void ParseInboundMessage_KeepsAMultiLineBodyIntact()
    {
        const string multi =
            "<cross-session-message from-name=\"job-hunter\">\nline one\n\nline two\n</cross-session-message>";

        var inbound = BridgeProtocol.ParseInboundMessage(multi);

        Assert.NotNull(inbound);
        Assert.Equal("line one\n\nline two", inbound!.Value.Body);
    }

    // A message with no sender cannot be attributed, and a bubble on the wrong
    // machine's panel is worse than a missing one.
    [Fact]
    public void ParseInboundMessage_IsNullWithoutASender()
    {
        Assert.Null(BridgeProtocol.ParseInboundMessage(
            "<cross-session-message from=\"bridge:session_1\">orphan</cross-session-message>"));
    }

    [Fact]
    public void ParseInboundMessage_IsNullOnAnOrdinaryRow()
    {
        Assert.Null(BridgeProtocol.ParseInboundMessage("I'll go ahead and check that for you."));
        Assert.Null(BridgeProtocol.ParseInboundMessage(""));
    }

    [Fact]
    public void ParseSentMessageId_ReadsTheServerIssuedId()
    {
        Assert.Equal(
            "e547dcf7-4510-4992-b14e-faa5b95e1872",
            BridgeProtocol.ParseSentMessageId(RealSendMessageResult));
    }

    [Fact]
    public void ParseSentMessageId_IsNullWhenAbsent()
    {
        Assert.Null(BridgeProtocol.ParseSentMessageId("{\"success\":false}"));
        Assert.Null(BridgeProtocol.ParseSentMessageId(""));
    }

    // The status file proves the session started; only this proves Remote
    // Control attached, which is the part that makes the bridge useful.
    [Fact]
    public void ReadHealth_ConfirmsRemoteControlFromTheRealBanner()
    {
        var health = BridgeProtocol.ReadHealth(RealBanner);

        Assert.True(health.RemoteControlActive);
        Assert.True(health.IsUsable);
    }

    [Fact]
    public void ReadHealth_SurfacesTheLoginExpiryWarning()
    {
        var health = BridgeProtocol.ReadHealth(RealBanner);

        // Reported without the " · run /login to renew" tail, which is an
        // instruction to whoever is looking at a terminal, not to Buddy.
        Assert.Equal("Your login expires in 3 days", health.Warning);
    }

    // A session that came up but whose RC never attached is running and useless,
    // and those two states must not look alike.
    [Fact]
    public void ReadHealth_IsNotUsableWithoutTheRemoteControlLine()
    {
        var health = BridgeProtocol.ReadHealth(
            " ⚠ 2 MCP servers need authentication · run /mcp");

        Assert.False(health.RemoteControlActive);
        Assert.False(health.IsUsable);
        Assert.Null(health.Warning);
    }

    // Captured verbatim from a relay that would not start: an account whose
    // .claude.json had no lastOnboardingVersion got the first-run theme picker
    // on every new session, and in a detached tmux pane nobody was there to
    // answer it. Real output, trimmed to the distinctive lines.
    private const string RealThemePrompt =
        " ❯ 1. Auto (match terminal) ✔\n" +
        "   2. Dark mode\n" +
        "   3. Light mode\n" +
        "   4. Dark mode (colorblind-friendly)\n" +
        "  Syntax theme: Monokai Extended (ctrl+t to disable)";

    // The whole point of this check: without it the relay waits out its full
    // 45-second timeout and then says "failed to start", which is true and tells
    // the person nothing they can act on.
    [Fact]
    public void ReadSetupBlock_RecognisesTheFirstRunThemePicker()
    {
        var reason = BridgeProtocol.ReadSetupBlock(RealThemePrompt);

        Assert.NotNull(reason);
        Assert.Contains("first-run setup", reason);

        // And says what to do about it, because that is the only reason to
        // report it rather than just failing.
        Assert.Contains("Run `claude` in a terminal", reason);
    }

    [Fact]
    public void ReadSetupBlock_RecognisesTheTrustPrompt()
    {
        var reason = BridgeProtocol.ReadSetupBlock("Do you trust the files in this folder?");

        Assert.NotNull(reason);
        Assert.Contains("trust", reason);
    }

    // A healthy session must not be mistaken for a blocked one, or the relay
    // would refuse to start for everybody.
    [Fact]
    public void ReadSetupBlock_IsNullForAWorkingSession()
    {
        Assert.Null(BridgeProtocol.ReadSetupBlock(RealBanner));
        Assert.Null(BridgeProtocol.ReadSetupBlock(""));
        Assert.Null(BridgeProtocol.ReadSetupBlock("Peer sessions (1):"));
    }

    // A colour answer must be recognisable, because it comes back as an
    // ordinary cross-session message. Without the marker the word "green" would
    // land in the panel as though the remote session had said it to the person
    // reading — who never asked the question.
    [Theory]
    [InlineData("CB-COLOR:green", "green")]
    [InlineData("CB-COLOR: blue", "blue")]
    [InlineData("CB-COLOR:#D75F5F", "#D75F5F")]
    [InlineData("cb-color:Purple", "purple")]
    public void ParseColorReply_ReadsTheAnswer(string body, string expected)
    {
        Assert.Equal(expected, BridgeProtocol.ParseColorReply(body), ignoreCase: true);
    }

    // A model that answers in a sentence instead of one word still yields the
    // colour, because asking politely does not guarantee obedience.
    [Fact]
    public void ParseColorReply_SurvivesAChattyAnswer()
    {
        Assert.Equal("teal", BridgeProtocol.ParseColorReply("CB-COLOR: I'm set to teal right now"));
    }

    // "none" must not match "orange" by substring — the whole reason the match
    // is word-bounded rather than a Contains.
    [Fact]
    public void ParseColorReply_TreatsNoneAsNoColour()
    {
        Assert.Null(BridgeProtocol.ParseColorReply("CB-COLOR:none"));
        Assert.Null(BridgeProtocol.ParseColorReply("CB-COLOR: I have not set one"));
    }

    // Swallowed whether or not it parses: showing someone a fumbled answer to a
    // question they did not ask is worse than showing nothing.
    [Fact]
    public void IsColorReply_RecognisesTheAnswerEvenWhenItCannotBeParsed()
    {
        Assert.True(BridgeProtocol.IsColorReply("CB-COLOR:none"));
        Assert.True(BridgeProtocol.IsColorReply("CB-COLOR: no idea sorry"));
        Assert.Null(BridgeProtocol.ParseColorReply("CB-COLOR: no idea sorry"));
    }

    // And an ordinary reply is never mistaken for one, or a real answer would
    // vanish from the panel.
    [Fact]
    public void IsColorReply_IsFalseForAnOrdinaryReply()
    {
        Assert.False(BridgeProtocol.IsColorReply("avatar.internal"));
        Assert.False(BridgeProtocol.IsColorReply("I painted the fence green"));
    }

    [Fact]
    public void ColorQueryPrompt_AsksForTheMarkerAndNamesThePeer()
    {
        var prompt = BridgeProtocol.ColorQueryPrompt("job-hunter");

        Assert.Contains("SendMessage", prompt);
        Assert.Contains("job-hunter", prompt);
        Assert.Contains(BridgeProtocol.ColorMarker, prompt);
        Assert.Contains("/color", prompt);
    }

    // The prompts are instructions to a model, so the only thing worth
    // asserting is that they name the tool and carry the payload through
    // unmangled — a summarised or paraphrased message is the failure here.
    [Fact]
    public void SendMessagePrompt_NamesThePeerAndCarriesTheTextVerbatim()
    {
        var prompt = BridgeProtocol.SendMessagePrompt("job-hunter", "run the tests");

        Assert.Contains("SendMessage", prompt);
        Assert.Contains("job-hunter", prompt);
        Assert.Contains("run the tests", prompt);
    }

    [Fact]
    public void ListAgentsPrompt_AsksForRawOutput()
    {
        Assert.Contains("ListAgents", BridgeProtocol.ListAgentsPrompt);
        Assert.Contains("verbatim", BridgeProtocol.ListAgentsPrompt);
    }
}
