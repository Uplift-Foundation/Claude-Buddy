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
[Collection("Settings")]
public class RemoteControlChatSessionTests
{
    private const string Account = ".claude-board";

    private static RemoteControlChatSession NewSession(string name = "job-hunter") =>
        new($"rc:{Account}:{name}", Account, name);

    // Everything after the opening explainer, which is always first and is
    // asserted on its own in OpensWithALineExplainingWhatThePanelIs. Named so
    // each test below still reads as a statement about the conversation rather
    // than about an off-by-one.
    private static IReadOnlyList<ChatTurn> Said(RemoteControlChatSession session) =>
        session.History.Skip(1).ToList();

    private static BridgeProtocol.InboundMessage From(
        string name, string body, string account = Account) =>
        new(name, "bridge:session_1", "prompting", body, account);

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

        // Said outright rather than left to the default.
        //
        // Off *is* the default and this suite does point settings at a temp
        // dir, but neither fact makes it off by the time this line runs: the
        // temp dir is one directory for the whole assembly, and any earlier
        // test that turns the feature on turns it on for this one too. That is
        // not hypothetical — TrayRemoteItemTests has to enable it to build the
        // menu it checks, and when the runner reached that class first this
        // test sent for real, got a bridge failure instead of the guard, and
        // failed on the wording of a message it was never testing.
        //
        // The state this test needs is part of the test, so it is set here.
        ClaudeBuddySettings.RemoteControlEnabled = false;

        await session.SendAsync("run the tests");

        var said = Said(session);
        Assert.Equal(2, said.Count);

        Assert.Equal(ChatRole.User, said[0].Role);
        Assert.Equal("run the tests", said[0].Text);

        Assert.Equal(ChatRole.System, said[1].Role);
        Assert.Contains("switched off", said[1].Text);
    }

    // A reply from the other machine arrives as an assistant turn, the same as
    // any other answer in any other panel.
    // The panel opens with one line explaining what it is, so an empty remote
    // conversation reads as "nothing said yet" rather than "failed to load" —
    // every other panel in this app fills itself from a transcript on this disk,
    // and this one cannot.
    [AvaloniaFact]
    public void OpensWithALineExplainingWhatThePanelIs()
    {
        var session = NewSession();

        var opening = Assert.Single(session.History);
        Assert.Equal(ChatRole.System, opening.Role);
        Assert.Contains("job-hunter", opening.Text);
        Assert.Contains("stays on the machine", opening.Text);
    }

    [AvaloniaFact]
    public void AnInboundMessageBecomesAnAssistantTurn()
    {
        var session = NewSession();

        session.OnInbound(From("job-hunter", "avatar.internal"));

        var turn = Assert.Single(Said(session));
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

        session.OnInbound(From("resumes-2b", "not for you"));

        Assert.Empty(Said(session));
    }

    // The peer list's casing is upstream's to change, and losing a reply over a
    // capital letter would be a poor trade.
    [AvaloniaFact]
    public void MatchingTheSenderIgnoresCase()
    {
        var session = NewSession("job-hunter");

        session.OnInbound(From("Job-Hunter", "still me"));

        Assert.Single(Said(session));
    }

    [AvaloniaFact]
    public void AnEmptyInboundMessageIsNotDrawnAsABlankBubble()
    {
        var session = NewSession();

        session.OnInbound(From("job-hunter", "   "));

        Assert.Empty(Said(session));
    }

    // An idle shutdown is invisible from the panel — nothing on screen changes —
    // so it says so rather than letting the next message be the first hint.
    [AvaloniaFact]
    public void ABridgeStoppingIsSaidOutLoud()
    {
        var session = NewSession();

        session.OnBridgeStopped("idle");

        var turn = Assert.Single(Said(session));
        Assert.Equal(ChatRole.System, turn.Role);
        Assert.Contains("idle", turn.Text);

        // And says it is recoverable, because it is: the next send restarts it.
        Assert.Contains("start it back up", turn.Text);
    }

    // The waiting indicator. A reply can be minutes away while the remote
    // session runs a command, and until then the panel is a message you typed
    // and nothing else — indistinguishable from a send that silently failed.
    [AvaloniaFact]
    public void WorkingShowsAWaitingLineAndTheReplyTakesItAway()
    {
        var session = NewSession();

        session.SetWorking(true);

        var note = Assert.Single(Said(session));
        Assert.Equal(ChatRole.System, note.Role);
        Assert.Contains("job-hunter", note.Text);
        Assert.Contains("working", note.Text);

        // Not complete: it is a turn still in progress, which is what the flag
        // means everywhere else.
        Assert.False(note.IsComplete);

        session.OnInbound(From("job-hunter", "done"));

        // The answer replaced it rather than stacking under it — a "working…"
        // line above a finished reply reads as though it were still going.
        var turn = Assert.Single(Said(session));
        Assert.Equal(ChatRole.Assistant, turn.Role);
        Assert.Equal("done", turn.Text);
    }

    // Polls arrive every 20 seconds; re-announcing the same state would fill the
    // panel with identical lines.
    [AvaloniaFact]
    public void RepeatedWorkingSignalsDoNotStack()
    {
        var session = NewSession();

        session.SetWorking(true);
        session.SetWorking(true);
        session.SetWorking(true);

        Assert.Single(Said(session));
    }

    // Went quiet without answering — the line still comes off. A stale
    // "working…" is worse than no indicator, because it is a claim rather than
    // an absence.
    [AvaloniaFact]
    public void GoingIdleWithoutReplyingClearsTheWaitingLine()
    {
        var session = NewSession();

        session.SetWorking(true);
        session.SetWorking(false);

        Assert.Empty(Said(session));
    }

    // The collision multi-account creates, and the reason the account is in the
    // key rather than only in the record. The same person naming a session the
    // same thing on two accounts is the normal case, not a corner one — and
    // without this check one machine's reply lands in the other's window.
    [AvaloniaFact]
    public void AMessageFromTheSameNameOnADifferentAccountIsIgnored()
    {
        var session = NewSession("job-hunter");

        session.OnInbound(From("job-hunter", "from the other account", account: ".claude"));

        Assert.Empty(Said(session));
    }

    [AvaloniaFact]
    public void AMessageFromTheSameNameOnTheSameAccountIsAccepted()
    {
        var session = NewSession("job-hunter");

        session.OnInbound(From("job-hunter", "mine"));

        var turn = Assert.Single(Said(session));
        Assert.Equal("mine", turn.Text);
    }

    // A message with no account on it is accepted rather than dropped. Nothing
    // produces one today, but the field is defaulted, so a caller that forgets
    // to stamp it should degrade to name-only matching — the old behaviour —
    // instead of silently delivering nothing at all.
    [AvaloniaFact]
    public void AnUnstampedMessageStillMatchesOnNameAlone()
    {
        var session = NewSession("job-hunter");

        session.OnInbound(new BridgeProtocol.InboundMessage(
            "job-hunter", "bridge:session_1", "prompting", "unstamped"));

        Assert.Single(Said(session));
    }

    // Offers nothing until the far session says what it can run, and that empty
    // start is the point rather than a gap.
    //
    // The first version offered Claude Code's built-in commands, which cannot
    // work over this channel at all: a peer message never reaches the receiving
    // session's command handler, so only the model reads it — measured, with
    // /color coming back "I can't run /color ... only the harness's own command
    // handler can set" it. A suggestion that does nothing when accepted is worse
    // than no suggestion, so the list is asked for.
    [AvaloniaFact]
    public void OffersNoSlashCommandsUntilTheFarSessionReportsThem()
    {
        var session = NewSession();

        Assert.IsAssignableFrom<IRemoteChatSlashCommands>(session);
        Assert.Empty(session.SlashCommands);
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
