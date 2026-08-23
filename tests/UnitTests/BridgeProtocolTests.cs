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
