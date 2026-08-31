using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.UiTests;

// RemoteControlChatSession's transcript: what goes into it, how much of it is
// kept, and which thread the panel finds out on.
//
// Here rather than in tests/UnitTests because every one of these paths asks
// Dispatcher.UIThread.CheckAccess() and takes a different branch depending on the
// answer. Both branches matter — a turn arriving from the relay's poll thread
// reaches the panel by Post, and one raised inline reaches it directly — and only
// a suite with a real dispatcher can tell the difference.
//
// What is NOT here is the send path. SendAsync with remote control switched on
// calls EnsureStarted, and RemoteControlProfileDirs always returns at least the
// default account, so it would try to start a real bridge: a live Claude Code
// session in a tmux pane. tests/IntegrationTests/RemoteControlBridgeLiveTests is
// where that belongs, behind its platform gate. The switched-off arm is safe and
// is covered below.
[Collection("Settings")]
public class RemoteControlChatSessionTurnTests
{
    private static RemoteControlChatSession Session() =>
        new("remote:mac-mini:zara", ".claude", "zara");

    // The panel opens with a line saying what it is, because an empty panel reads
    // as a broken one.
    [AvaloniaFact]
    public void ASessionOpensWithSomethingToRead()
    {
        var session = Session();

        Assert.NotEmpty(session.History);
        Assert.All(session.History, t => Assert.True(t.IsComplete));
    }

    // Switched off is a System turn naming the setting, not an exception and not
    // silence: the person has just typed a sentence.
    //
    // Note the user's own turn is kept as well, so the refusal reads as "this did
    // not go" under the message rather than the message vanishing.
    //
    // That differs from OpenClawChatSession, which returns BEFORE adding the
    // user's turn when replying is off — so there the typed text disappears. The
    // comment above this method claims "same reasoning as OpenClawChatSession's",
    // and in this one case the two do the opposite thing. Asserted as it is
    // rather than as the comment reads: which of the two is right is a product
    // decision, not something a coverage ticket should quietly change, but the
    // pair of tests now makes the difference visible instead of leaving it in a
    // comment that is wrong.
    [AvaloniaFact]
    public async Task WithRemoteControlOffTheMessageIsRefusedButKept()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.RemoteControlEnabled = false;
        // Both transports, because "off" is now two switches. A test that
        // turns one off and leaves the other to whatever the last test set
        // is asserting about a state it did not arrange — and settings here
        // persist through ReloadForTests, since the setter writes the file.
        ClaudeBuddySettings.PeerLinkEnabled = false;

        var session = Session();
        var before = session.History.Count;

        await session.SendAsync("are you there?");

        var added = session.History.Skip(before).ToList();

        Assert.Collection(added,
            mine =>
            {
                Assert.Equal(ChatRole.User, mine.Role);
                Assert.Equal("are you there?", mine.Text);
            },
            note =>
            {
                Assert.Equal(ChatRole.System, note.Role);
                Assert.Contains("switched off", note.Text);
                Assert.Contains("Show sessions from other machines", note.Text);
            });
    }

    // Raised inline when already on the UI thread, which is the case for
    // anything the panel itself triggers — a refused send, most immediately.
    [AvaloniaFact]
    public async Task ATurnAddedOnTheUiThreadIsRaisedWithoutAHop()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.RemoteControlEnabled = false;
        // Both transports, because "off" is now two switches. A test that
        // turns one off and leaves the other to whatever the last test set
        // is asserting about a state it did not arrange — and settings here
        // persist through ReloadForTests, since the setter writes the file.
        ClaudeBuddySettings.PeerLinkEnabled = false;

        var session = Session();
        ChatTurn? seen = null;
        session.TurnAdded += t => seen = t;

        Assert.True(Dispatcher.UIThread.CheckAccess());
        await session.SendAsync("are you there?");

        // No RunJobs: on the UI thread the event fires inline, and needing a
        // drain here would mean the panel learns about its own actions late.
        Assert.NotNull(seen);
        Assert.Equal(ChatRole.System, seen!.Role);
    }

    // A session opens Connected rather than Disconnected — the orb only exists
    // because the relay listed the session, so claiming disconnected until the
    // first poll would flicker. Which means Error is the state to drive here;
    // setting Connected is the no-op case, covered below.
    [AvaloniaFact]
    public void ASessionOpensConnected()
    {
        Assert.Equal(RemoteChatState.Connected, Session().State);
    }

    [AvaloniaFact]
    public void SettingTheStateRaisesStateChangedOnTheUiThread()
    {
        var session = Session();
        RemoteChatState? seen = null;
        session.StateChanged += s => seen = s;

        session.SetState(RemoteChatState.Error);

        Assert.Equal(RemoteChatState.Error, seen);
        Assert.Equal(RemoteChatState.Error, session.State);
    }

    // The same state twice must not raise again. The relay's poll calls this on
    // every tick, so a session that is quietly idle would otherwise raise a
    // StateChanged several times a second for the panel to react to.
    [AvaloniaFact]
    public void SettingTheSameStateAgainRaisesNothing()
    {
        var session = Session();

        // Already Connected from construction, so this is the no-op path.
        var raised = 0;
        session.StateChanged += _ => raised++;
        session.SetState(RemoteChatState.Connected);

        Assert.Equal(0, raised);
    }

    // The off-thread arm. The relay polls on a background thread, so a state
    // change discovered there has to reach the panel by Post rather than being
    // raised where nothing is allowed to touch a control.
    [AvaloniaFact]
    public async Task AStateChangeFromAnotherThreadStillReachesTheSubscriber()
    {
        var session = Session();
        RemoteChatState? seen = null;
        session.StateChanged += s => seen = s;

        await Task.Run(() =>
        {
            Assert.False(Dispatcher.UIThread.CheckAccess());
            session.SetState(RemoteChatState.Error);
        });

        // Posted, so it has not arrived yet — draining the dispatcher is what
        // delivers it, and that is exactly the hop being asserted.
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(RemoteChatState.Error, seen);
    }

    // The off-thread arm of Add, which is the one the relay's own reader uses: an
    // inbound message arrives on a background thread and the panel it reaches is a
    // control, so the turn has to hop before TurnAdded is raised.
    [AvaloniaFact]
    public async Task ATurnAddedFromAnotherThreadStillReachesTheSubscriber()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.RemoteControlEnabled = false;
        // Both transports, because "off" is now two switches. A test that
        // turns one off and leaves the other to whatever the last test set
        // is asserting about a state it did not arrange — and settings here
        // persist through ReloadForTests, since the setter writes the file.
        ClaudeBuddySettings.PeerLinkEnabled = false;

        var session = Session();
        var seen = new List<ChatTurn>();
        session.TurnAdded += seen.Add;

        await Task.Run(async () =>
        {
            Assert.False(Dispatcher.UIThread.CheckAccess());
            await session.SendAsync("from the reader thread");
        });

        // Drained rather than asserted-absent first: awaiting the Task.Run pumps
        // the dispatcher on its way back, so the posts may already have been
        // delivered by the time control returns here. What matters is that they
        // arrive at all — a turn raised straight from the reader thread would
        // reach a subscriber that is not allowed to touch controls from there.
        Dispatcher.UIThread.RunJobs();

        // Both of them: the typed message and the refusal underneath it. Collected
        // rather than kept as "the last one", which is the System note and was my
        // first mistake here.
        Assert.Contains(seen, t => t.Role == ChatRole.User && t.Text == "from the reader thread");
        Assert.Contains(seen, t => t.Role == ChatRole.System);
    }

    // The composer says where the message is going. "Message…" would be a lie by
    // omission on this one: it leaves the machine.
    [AvaloniaFact]
    public void TheComposerHintNamesTheOtherMachine()
    {
        var hint = Session().ComposerHint;

        Assert.Contains("zara", hint);
        Assert.Contains("other machine", hint);
    }

    // Cancel is deliberately empty: SendMessage delivers a message, it does not
    // interrupt a run, so stopping work on another machine is not something this
    // channel can do — and a Cancel that silently did nothing while looking like
    // it worked would be worse than not having one.
    [AvaloniaFact]
    public void CancelDoesNothing()
    {
        var session = Session();
        var before = session.History.Count;

        session.Cancel();

        Assert.Equal(before, session.History.Count);
    }
}
