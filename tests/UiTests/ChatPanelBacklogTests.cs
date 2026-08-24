using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// When the panel asks for older messages.
//
// One rule, and it exists because of a specific runaway: ScrollChanged fires on
// extent and viewport changes as well as on scrolling, and a transcript shorter
// than the panel sits at offset zero forever — so an unguarded handler asked for
// the page before, which grew the extent, which fired it again, and walked the
// entire backlog the instant the orb was clicked.
//
// The guard is two conditions, and both are tested from both sides: there has to
// be something to scroll, and you have to be near the top of it.
public class ChatPanelBacklogTests : IDisposable
{
    // A session that can page, which the shared FakeChatSession deliberately
    // cannot — adding backlog to that one would change what every other test in
    // the suite is driving. This one counts the asks, which is the whole
    // assertion.
    private sealed class PagingSession : IRemoteChatSession, IRemoteChatBacklog
    {
        private readonly List<ChatTurn> _history = new();

        public string SessionId { get; init; } = "paging-" + Guid.NewGuid();
        public string DisplayName { get; init; } = "Paging Session";
        public RemoteChatState State => RemoteChatState.Connected;
        public IReadOnlyList<ChatTurn> History => _history;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;

        public int Asks { get; private set; }
        public bool HasMore { get; set; } = true;

        // Never raised here: this fake answers "that was the end" to every ask, so
        // nothing is ever prepended and the transcript is never replaced. Present
        // because the interface asks for them, and the panel subscribes.
        public event Action? HistoryReplaced;
        public event Action<int>? HistoryPrepended;

        public void Seed(int turns)
        {
            for (var i = 0; i < turns; i++)
            {
                _history.Add(new ChatTurn
                {
                    Role = i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                    Text = $"turn {i} — long enough that a few dozen of them overflow the panel",
                    IsComplete = true,
                });
            }
        }

        public Task<bool> LoadOlderAsync(CancellationToken ct)
        {
            Asks++;
            return Task.FromResult(false);
        }

        public Task SendAsync(string text) => Task.CompletedTask;

        public void Cancel()
        {
        }

        // Never raised; present because the interface asks for them.
        private void Unused()
        {
            StateChanged?.Invoke(State);
            TurnAdded?.Invoke(_history[0]);
            TurnUpdated?.Invoke(_history[0]);
            HistoryReplaced?.Invoke();
            HistoryPrepended?.Invoke(0);
        }
    }

    private readonly List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private ChatPanel Open(PagingSession session)
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

    // A transcript that does not fill the panel never asks, however the scroll
    // viewer is nudged. This is the runaway: at offset zero with nothing to
    // scroll, an unguarded handler would ask, grow, and ask again.
    [AvaloniaFact]
    public void ATranscriptShorterThanThePanelNeverAsksForMore()
    {
        var session = new PagingSession();
        session.Seed(1);

        var panel = Open(session);

        // Nudge the scroll viewer the way a resize or an extent change would.
        panel.Scroll.Offset = new Vector(0, 0);
        Flush();
        panel.Scroll.Offset = new Vector(0, 0);
        Flush();

        Assert.Equal(0, session.Asks);
    }

    // Scrolled to the top of something long enough to scroll: that is a real
    // request for more.
    [AvaloniaFact]
    public void ScrollingToTheTopOfALongTranscriptAsksForMore()
    {
        var session = new PagingSession();
        session.Seed(200);

        var panel = Open(session);

        // Away from the top first, so moving back to it is a change.
        panel.Scroll.Offset = new Vector(0, 400);
        Flush();

        panel.Scroll.Offset = new Vector(0, 0);
        Flush();

        Assert.True(session.Asks > 0, "reaching the top of a long transcript should ask for more");
    }

    // Sitting in the middle does not. The threshold is a few pixels from the top,
    // not "anywhere above the fold", or reading upward through a long
    // conversation would fetch continuously.
    [AvaloniaFact]
    public void SittingInTheMiddleDoesNotAskForMore()
    {
        var session = new PagingSession();
        session.Seed(200);

        var panel = Open(session);

        panel.Scroll.Offset = new Vector(0, 400);
        Flush();

        var asksAfterMoving = session.Asks;

        panel.Scroll.Offset = new Vector(0, 380);
        Flush();

        Assert.Equal(asksAfterMoving, session.Asks);
    }

    // A session that cannot page at all is never asked, which is what the shared
    // fake exercises everywhere else in this suite — asserted here so the panel's
    // guard is known to check the capability rather than assuming it.
    [AvaloniaFact]
    public void ASessionThatCannotPageIsNeverAsked()
    {
        var fake = new FakeChatSession(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "only turn" },
        })
        {
            SessionId = "no-backlog-" + Guid.NewGuid(),
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        panel.Scroll.Offset = new Vector(0, 0);
        Flush();

        // Nothing to assert beyond not throwing: the session has no backlog to
        // ask, and the panel must notice rather than casting and failing.
        Assert.True(ChatPanel.IsOpenFor(fake.SessionId));
    }
}
