using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// RemoteControlChatSession drives the panel for a session on another machine.
//
// Tested here rather than in UnitTests because it raises its events through the
// Avalonia dispatcher — that is the contract IRemoteChatSession states and the
// panel relies on — so it needs a dispatcher to exist. These use the session
// directly rather than through ChatPanel: what is worth pinning is the
// transcript it builds, and ChatPanel's own rendering of a turn is already
// covered by ChatPanelTests via FakeChatSession.
//
// Note what is deliberately *not* here: nothing that would start a bridge. Every
// path below either fails before reaching one or is fed a message as though one
// had. A test that started a real Claude Code session would cost the person
// running it money — see LiveBridgeFactAttribute for where that is allowed.
public class RemoteControlChatSessionTests
{
    private static RemoteControlChatSession NewSession(string name = "job-hunter") =>
        new("rc:" + name, name);

    // The input box has to say where the message is going. A panel that looks
    // exactly like a local one but delivers to a different computer is a
    // surprise worth spending a line of text on.
    [AvaloniaFact]
    public void ComposerHint_NamesTheMachineTheMessageLeavesFor()
    {
        var session = NewSession();

        Assert.Contains("job-hunter", session.ComposerHint);
        Assert.Contains("other machine", session.ComposerHint);
    }

    // There is no transcript on this machine to page back into, so claiming
    // IRemoteChatBacklog would put a "loading older messages" spinner on a
    // conversation with no history to load. The panel type-tests for the
    // interface, so not implementing it is how that stays true.
    [AvaloniaFact]
    public void DoesNotClaimABacklogItCannotProvide()
    {
        var session = NewSession();

        Assert.IsNotAssignableFrom<IRemoteChatBacklog>(session);
        Assert.IsAssignableFrom<IRemoteChatComposer>(session);
    }

    // The user's own turn is added by the session, not the panel, so a send that
    // fails leaves the message on screen with the reason under it rather than
    // vanishing. With the feature off, that is both turns and no bridge started.
    [AvaloniaFact]
    public async Task AFailedSendKeepsTheMessageOnScreenAndExplainsItself()
    {
        var session = NewSession();

        // Off is the default and this suite points settings at a temp dir, so
        // this reaches the guard rather than any bridge.
        await session.SendAsync("run the tests");

        Assert.Equal(2, session.History.Count);

        Assert.Equal(ChatRole.User, session.History[0].Role);
        Assert.Equal("run the tests", session.History[0].Text);

        Assert.Equal(ChatRole.System, session.History[1].Role);
        Assert.Contains("switched off", session.History[1].Text);
    }

    // A reply from the other machine arrives as an assistant turn, the same as
    // any other answer in any other panel.
    [AvaloniaFact]
    public void AnInboundMessageBecomesAnAssistantTurn()
    {
        var session = NewSession();

        session.OnInbound(new BridgeProtocol.InboundMessage(
            "job-hunter", "bridge:session_1", "prompting", "avatar.internal"));

        var turn = Assert.Single(session.History);
        Assert.Equal(ChatRole.Assistant, turn.Role);
        Assert.Equal("avatar.internal", turn.Text);
    }

    // The correlation that keeps two remote conversations apart. One bridge
    // feeds every remote panel, and a message names only who it came from — so
    // a session that accepted messages addressed to another would put one
    // machine's reply in another machine's window.
    [AvaloniaFact]
    public void AMessageForADifferentMachineIsIgnored()
    {
        var session = NewSession("job-hunter");

        session.OnInbound(new BridgeProtocol.InboundMessage(
            "resumes-2b", "bridge:session_2", "prompting", "not for you"));

        Assert.Empty(session.History);
    }

    // The peer list's casing is upstream's to change, and losing a reply over a
    // capital letter would be a poor trade.
    [AvaloniaFact]
    public void MatchingTheSenderIgnoresCase()
    {
        var session = NewSession("job-hunter");

        session.OnInbound(new BridgeProtocol.InboundMessage(
            "Job-Hunter", "bridge:session_1", "prompting", "still me"));

        Assert.Single(session.History);
    }

    [AvaloniaFact]
    public void AnEmptyInboundMessageIsNotDrawnAsABlankBubble()
    {
        var session = NewSession();

        session.OnInbound(new BridgeProtocol.InboundMessage("job-hunter", "b", "prompting", "   "));

        Assert.Empty(session.History);
    }

    // An idle shutdown is invisible from the panel — nothing on screen changes —
    // so it says so rather than letting the next message be the first hint.
    [AvaloniaFact]
    public void ABridgeStoppingIsSaidOutLoud()
    {
        var session = NewSession();

        session.OnBridgeStopped("idle");

        var turn = Assert.Single(session.History);
        Assert.Equal(ChatRole.System, turn.Role);
        Assert.Contains("idle", turn.Text);

        // And says it is recoverable, because it is: the next send restarts it.
        Assert.Contains("start it back up", turn.Text);
    }

    // Bounded like the local sessions' history: a panel left open should not
    // grow without limit. Generous, because every turn here is something a
    // person typed or a machine answered.
    [AvaloniaFact]
    public void HistoryIsBounded()
    {
        var session = NewSession();

        for (var i = 0; i < 260; i++)
            session.OnInbound(new BridgeProtocol.InboundMessage("job-hunter", "b", "prompting", $"reply {i}"));

        Assert.Equal(200, session.History.Count);

        // The newest survive, not the oldest.
        Assert.Equal("reply 259", session.History[^1].Text);
    }
}
