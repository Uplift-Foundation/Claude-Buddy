using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// Where the transcript is sitting, which is a separate question from what is in
// it.
//
// The complaint these came from was "sometimes the chats don't scroll to the
// bottom and when I open them they are somewhere in the middle of the chat",
// and "sometimes" was two different faults wearing the same symptom:
//
//  1. There is one ChatPanel for every orb, so binding a session to it inherits
//     whatever offset the *previous* session left behind. Bind asked whether
//     that offset was at the bottom — a question about a transcript that is no
//     longer on screen — and left it alone when the answer was no.
//  2. Every scroll-to-bottom was a single post at Loaded priority, which is one
//     yield too early: the rows are in the tree but have not been measured, so
//     ScrollToEnd() reads the extent from before they existed. LoadOlderAsync
//     had already found this and yields twice; nothing else did.
//
// Both land you above the newest message, which is why they read as one bug.
// Every test here asserts on Offset.Y against the extent rather than on a
// pixel count, since the height of a rendered turn is not this suite's business.
[Collection("Settings")]
public class ChatPanelScrollTests : IDisposable
{
    private readonly List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    // Layout, then the jobs that layout unblocks, then layout again — three
    // rounds because the scroll correction is deliberately spread across two
    // dispatcher priorities and each one needs a measure between it and the
    // next. This is settling the framework, not waiting out a race: nothing
    // here is timed, and running it more times changes no answer below.
    private static void Flush()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // The offset a scroll viewer has when it is showing the end of its content.
    private static double BottomOf(ChatPanel panel) =>
        Math.Max(0, panel.Scroll.Extent.Height - panel.Scroll.Viewport.Height);

    private static void AssertAtBottom(ChatPanel panel, string because)
    {
        var bottom = BottomOf(panel);

        // A transcript that does not overflow the panel is at its bottom by
        // sitting at zero; asserting that would prove nothing, so every case
        // below seeds enough turns to scroll and this guards that it worked.
        Assert.True(bottom > 0, "the transcript should be long enough to scroll");

        Assert.True(
            panel.Scroll.Offset.Y >= bottom - 1,
            $"{because}: offset {panel.Scroll.Offset.Y:F0} of {bottom:F0}");
    }

    private static List<ChatTurn> Transcript(int turns, string tag) =>
        Enumerable.Range(0, turns).Select(i => new ChatTurn
        {
            Role = i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
            Text = $"{tag} turn {i} — long enough that a few dozen of them overflow the panel",
            IsComplete = true,
        }).ToList();

    private ChatPanel Open(FakeChatSession session)
    {
        _toClean.Add(session.SessionId);

        ChatPanel.OpenFor(NewOrb(), session);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        panel.Width = 340;
        panel.Height = 420;
        Flush();

        return panel;
    }

    private static FakeChatSession NewSession(int turns, string tag) =>
        new(Transcript(turns, tag)) { SessionId = $"{tag}-{Guid.NewGuid()}" };

    // --- opening ---

    // The plainest statement of what a user expects, and it fails without the
    // fix: run against the old code this lands at offset 300 of an extent of
    // 10342, which is the report's "somewhere in the middle of the chat"
    // measured. Which of the two faults put it there depends on what the
    // singleton panel was last bound to, and deliberately isn't asserted —
    // the test is that neither can.
    [AvaloniaFact]
    public void OpeningALongTranscriptShowsItsNewestTurn()
    {
        var panel = Open(NewSession(120, "first"));

        AssertAtBottom(panel, "a panel just opened should be showing the newest message");
    }

    // Fault 1, which is the one the report describes. Read one chat part way up,
    // click a different orb, and the second chat opened at the first one's
    // offset — the middle of a conversation you had not been reading.
    [AvaloniaFact]
    public void OpeningASecondSessionDoesNotInheritTheFirstsScrollPosition()
    {
        var first = NewSession(120, "first");
        var panel = Open(first);

        // Part way up the first transcript, the way reading back through it
        // leaves you.
        panel.Scroll.Offset = new Vector(0, 300);
        Flush();
        Assert.Equal(300, panel.Scroll.Offset.Y, 0);

        var second = NewSession(120, "second");
        _toClean.Add(second.SessionId);
        ChatPanel.OpenFor(NewOrb(), second);
        Flush();

        Assert.True(ChatPanel.IsOpenFor(second.SessionId));
        AssertAtBottom(panel, "a newly bound session should open at its own newest message");
    }

    // The same inheritance, in the direction that does not look wrong on screen
    // but is the same bug: the panel it is reused from was at the bottom of a
    // *short* transcript, so the offset carried over is small rather than
    // mid-conversation. Asserted separately because a fix that only clamped
    // large offsets would pass the test above and fail here.
    [AvaloniaFact]
    public void OpeningALongSessionAfterAShortOneStillShowsTheNewestTurn()
    {
        Open(NewSession(1, "short"));

        var second = NewSession(120, "long");
        _toClean.Add(second.SessionId);
        ChatPanel.OpenFor(NewOrb(), second);
        Flush();

        AssertAtBottom(ChatPanelTestAccess.Instance!, "a long transcript opened after a short one");
    }

    // --- turns arriving ---

    // Sitting at the bottom and a reply lands: the view follows it. Fault 2
    // again, and the intermittent one — the pinned question used to be asked a
    // yield late, by which time the new row had height and the extent had grown
    // past where you were sitting, so the answer was "no" and autoscroll
    // switched itself off partway through a conversation.
    [AvaloniaFact]
    public void AReplyArrivingWhileAtTheBottomKeepsTheViewThere()
    {
        var session = NewSession(120, "reply");
        var panel = Open(session);

        for (var i = 0; i < 3; i++)
        {
            session.RaiseTurnAdded(new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = $"a reply, number {i}, long enough to change the extent it lands in",
                IsComplete = true,
            });
            Flush();
        }

        AssertAtBottom(panel, "replies arriving while pinned should carry the view with them");
    }

    // The other side of the same rule, and the reason it is not simply "always
    // scroll": reading back through a long reply must not be yanked forward
    // every time it grows.
    [AvaloniaFact]
    public void AReplyArrivingWhileScrolledUpLeavesTheViewWhereItIs()
    {
        var session = NewSession(120, "reading");
        var panel = Open(session);

        panel.Scroll.Offset = new Vector(0, 200);
        Flush();

        session.RaiseTurnAdded(new ChatTurn
        {
            Role = ChatRole.Assistant,
            Text = "a reply arriving while the reader is somewhere else entirely",
            IsComplete = true,
        });
        Flush();

        Assert.Equal(200, panel.Scroll.Offset.Y, 0);
    }

    // A streaming reply grows its existing turn rather than adding one, so it
    // arrives as TurnUpdated. Same rule, different event — and this is the path
    // a long answer actually takes, which is why it is asserted rather than
    // assumed to follow from the one above.
    [AvaloniaFact]
    public void AStreamingReplyGrowingKeepsThePinnedViewAtTheBottom()
    {
        var session = NewSession(120, "streaming");
        var panel = Open(session);

        var turn = new ChatTurn { Role = ChatRole.Assistant, Text = "thinking" };
        session.RaiseTurnAdded(turn);
        Flush();

        for (var i = 0; i < 4; i++)
        {
            turn.Text += $"\n\nand then another paragraph, number {i}, of a long answer";
            session.RaiseTurnUpdated(turn);
            Flush();
        }

        AssertAtBottom(panel, "a streaming reply should keep the view at its end");
    }

    // Your own message is the exception to the pinned rule: it always brings the
    // view with it, because a message you just sent landing off screen reads as
    // it not having sent at all.
    [AvaloniaFact]
    public void YourOwnTurnAlwaysBringsTheViewToTheBottom()
    {
        var session = NewSession(120, "own");
        var panel = Open(session);

        panel.Scroll.Offset = new Vector(0, 200);
        Flush();

        session.RaiseTurnAdded(new ChatTurn { Role = ChatRole.User, Text = "a message of my own" });
        Flush();

        AssertAtBottom(panel, "your own turn should always be visible");
    }

    // --- a transcript replaced under the panel ---

    // A remote panel can change what it *is* while open — a messaging channel
    // upgrading to a live view of the far session replaces the whole transcript
    // — and what arrives is the newest state of a conversation, not a longer
    // version of the one being read. Same reasoning as bind: no position worth
    // preserving.
    private sealed class ReplaceableSession : IRemoteChatSession, IRemoteChatBacklog
    {
        private List<ChatTurn> _history;

        public string SessionId { get; } = "replaceable-" + Guid.NewGuid();
        public string DisplayName => "Replaceable Session";
        public RemoteChatState State => RemoteChatState.Connected;
        public IReadOnlyList<ChatTurn> History => _history;

        public bool HasMore => false;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;
        public event Action? HistoryReplaced;
        public event Action<int>? HistoryPrepended;

        public ReplaceableSession(List<ChatTurn> seed) => _history = seed;

        public void Replace(List<ChatTurn> next)
        {
            _history = next;
            HistoryReplaced?.Invoke();
        }

        public Task<bool> LoadOlderAsync(CancellationToken ct) => Task.FromResult(false);

        public Task SendAsync(string text) => Task.CompletedTask;

        public void Cancel()
        {
        }

        // Never raised; present because the interface asks for them.
        private void Unused()
        {
            TurnAdded?.Invoke(_history[0]);
            TurnUpdated?.Invoke(_history[0]);
            StateChanged?.Invoke(State);
            HistoryPrepended?.Invoke(0);
        }
    }

    [AvaloniaFact]
    public void ATranscriptReplacedWholesaleShowsItsNewestTurn()
    {
        var session = new ReplaceableSession(Transcript(120, "before"));
        _toClean.Add(session.SessionId);

        ChatPanel.OpenFor(NewOrb(), session);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        panel.Width = 340;
        panel.Height = 420;
        Flush();

        panel.Scroll.Offset = new Vector(0, 300);
        Flush();

        session.Replace(Transcript(160, "after"));
        Flush();

        AssertAtBottom(panel, "a replaced transcript should show its newest turn");
    }
}
