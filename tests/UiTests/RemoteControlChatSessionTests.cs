using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

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

    // The backlog is claimed now and answered honestly, which is a change from
    // not claiming it at all.
    //
    // A live view really can page back — it is a real transcript on the other
    // machine and the far Buddy will read any byte range of it — so the
    // interface has to be there. What kept the spinner off a conversation with
    // no history was never the missing interface itself but the promise behind
    // it, and HasMore keeps that promise directly: the panel asks before every
    // fetch, and in messaging mode the answer is false forever.
    [AvaloniaFact]
    public void ClaimsABacklogButOffersNoneWithoutALiveView()
    {
        var session = NewSession();

        var backlog = Assert.IsAssignableFrom<IRemoteChatBacklog>(session);
        Assert.False(backlog.HasMore);
        Assert.IsAssignableFrom<IRemoteChatComposer>(session);
    }

    // Nothing to page means nothing prepended, and specifically not a spinner
    // that never resolves. The panel measures a scroll correction off the return
    // value, so false has to mean "that was the end" rather than "not yet".
    [AvaloniaFact]
    public async Task LoadingOlderWithoutALiveViewIsANoOp()
    {
        var session = NewSession();
        var backlog = (IRemoteChatBacklog)session;

        var prepended = 0;
        backlog.HistoryPrepended += n => prepended += n;

        Assert.False(await backlog.LoadOlderAsync(CancellationToken.None));
        Assert.Equal(0, prepended);
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
        OrbweaverSettings.RemoteControlEnabled = false;
        // Both transports, because "off" is now two switches. A test that
        // turns one off and leaves the other to whatever the last test set
        // is asserting about a state it did not arrange — and settings here
        // persist through ReloadForTests, since the setter writes the file.
        OrbweaverSettings.PeerLinkEnabled = false;

        var outcome = await session.SendAsync("run the tests");

        var said = Said(session);
        Assert.Equal(2, said.Count);

        Assert.Equal(ChatRole.User, said[0].Role);
        Assert.Equal("run the tests", said[0].Text);

        Assert.Equal(ChatRole.System, said[1].Role);
        Assert.Contains("switched off", said[1].Text);

        Assert.Equal(ChatSendOutcome.Failed, outcome);
    }

    // CB-35: replying is allowed, but this session never became a live view
    // (no roster wiring in this test, so TryUpgrade has nothing to upgrade
    // to) — the one refusal that is neither "the setting is off" nor
    // anything a live view's own typing can fail with. NoWayToSendNote is
    // what a direct link says once the relay fallback that used to answer
    // this is gone: there is no terminal to type into and no messaging
    // channel behind a conversation that was never confirmed live.
    [AvaloniaFact]
    public async Task WithNoLiveViewSendingSaysThereIsNothingToTypeInto()
    {
        var before = OrbweaverSettings.PeerLinkEnabled;
        try
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.PeerLinkEnabled = true;

            var session = NewSession();
            Assert.False(session.IsMirroring);

            var outcome = await session.SendAsync("are you there?");

            Assert.Contains(Said(session),
                t => t.Role == ChatRole.System && t.Text.Contains("nothing to type into"));
            Assert.Equal(ChatSendOutcome.Failed, outcome);
        }
        finally
        {
            OrbweaverSettings.PeerLinkEnabled = before;
        }
    }

    // The panel opens with one line explaining what it is, so an empty remote
    // conversation reads as "nothing said yet" rather than "failed to load" —
    // every other panel in this app fills itself from a transcript on this disk.
    //
    // It no longer says the far conversation *stays* on the other machine,
    // because that is now only true when there is no Buddy over there to read
    // it. Until the handshake answers, the honest line is that the question is
    // still open — promising a live view and falling back would be worse than
    // saying "checking" and then succeeding.
    [AvaloniaFact]
    public void OpensWithALineExplainingWhatThePanelIs()
    {
        var session = NewSession();

        var opening = Assert.Single(session.History);
        Assert.Equal(ChatRole.System, opening.Role);
        Assert.Contains("job-hunter", opening.Text);
        Assert.Contains("live view", opening.Text, StringComparison.OrdinalIgnoreCase);
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
    public async Task HistoryIsBounded()
    {
        // Fed through sends that cannot go anywhere — no live view, so each adds
        // the user's turn and a note saying why. That is the messaging-mode
        // traffic still reachable now the relay's inbound messages are gone
        // (CB-238), and it is bounded by the same rule.
        var before = OrbweaverSettings.PeerLinkEnabled;
        try
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.PeerLinkEnabled = true;

            var session = NewSession();

            for (var i = 0; i < 130; i++) await session.SendAsync($"send {i}");

            Assert.Equal(200, session.History.Count);

            // The newest survive, not the oldest.
            Assert.Equal("send 129", session.History[^2].Text);
        }
        finally
        {
            OrbweaverSettings.PeerLinkEnabled = before;
        }
    }
}
