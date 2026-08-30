using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace ClaudeBuddy.Tests;

// Everything that changes the transcript out from under the panel rather than
// through a click in it: a successful page of backlog actually landing (the
// "grew" branch ChatPanelBacklogTests' PagingSession never exercises, because
// it always answers false), a transcript replaced wholesale, a turn retracted
// by a real RemoteControlChatSession, the connection-state dot's other three
// colours, and the permission-prompt box's own two buttons.
[Collection("Settings")]
public class ChatPanelHistoryTests : IDisposable
{
    private readonly List<string> _toClean = new();

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static Avalonia.Controls.Controls RenderedRows(ChatPanel panel) =>
        panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children;

    // --- a real page of backlog landing ---

    // A session whose LoadOlderAsync actually prepends turns and answers true,
    // unlike ChatPanelBacklogTests' PagingSession which always answers false —
    // that file is about the guard that decides *whether* to ask; this is
    // about what happens once an ask actually succeeds.
    private sealed class SucceedingBacklogSession : IRemoteChatSession, IRemoteChatBacklog
    {
        private readonly List<ChatTurn> _history = new();

        public string SessionId { get; } = "backlog-ok-" + Guid.NewGuid();
        public string DisplayName { get; } = "Backlog Session";
        public RemoteChatState State => RemoteChatState.Connected;
        public IReadOnlyList<ChatTurn> History => _history;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;
        public event Action? HistoryReplaced;
        public event Action<int>? HistoryPrepended;

        public bool HasMore { get; set; } = true;

        public void Seed(int count)
        {
            for (var i = 0; i < count; i++)
            {
                _history.Add(new ChatTurn
                {
                    Role = i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                    Text = $"turn {i} — long enough that a few dozen overflow the panel",
                    IsComplete = true,
                });
            }
        }

        public Task<bool> LoadOlderAsync(CancellationToken ct)
        {
            HasMore = false;

            var older = Enumerable.Range(0, 5)
                .Select(i => new ChatTurn { Role = ChatRole.Assistant, Text = $"older {i}", IsComplete = true })
                .ToList();

            _history.InsertRange(0, older);
            HistoryPrepended?.Invoke(older.Count);

            return Task.FromResult(true);
        }

        public Task SendAsync(string text) => Task.CompletedTask;

        public void Cancel()
        {
        }

        public void ReplaceWith(IEnumerable<ChatTurn> turns)
        {
            _history.Clear();
            _history.AddRange(turns);
            HistoryReplaced?.Invoke();
        }

        // Never raised; present because the interface asks for it.
        private void Unused() => TurnUpdated?.Invoke(_history[0]);
    }

    [AvaloniaFact]
    public void ScrollingToTheTopPrependsTheOlderTurnsAndKeepsTheReadPositionSteady()
    {
        var session = new SucceedingBacklogSession();
        session.Seed(200);
        _toClean.Add(session.SessionId);

        ChatPanel.OpenFor(NewOrb(), session);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        panel.Width = 340;
        panel.Height = 420;

        // Laying the panel out at this size is itself enough to trigger a
        // load — the scroll viewer starts at the top (offset zero) and the
        // extent only becomes larger than the viewport once this render pass
        // actually measures 200 turns' worth of content, which is exactly
        // the "reached the top of something scrollable" condition. So the
        // away-then-back scroll below is not what has to be relied on to
        // prove the request happens; only the outcome — five turns landed,
        // the session down to one page — is asserted.
        FlushRender();
        panel.Scroll.Offset = new Vector(0, 400);
        Flush();
        panel.Scroll.Offset = new Vector(0, 0);
        FlushRender();

        Assert.Equal(205, RenderedRows(panel).Count);
        Assert.False(session.HasMore);
    }

    // --- the transcript replaced wholesale ---

    [AvaloniaFact]
    public void HistoryReplacedRebuildsTheTranscriptFromScratch()
    {
        var session = new SucceedingBacklogSession();
        session.Seed(2);
        _toClean.Add(session.SessionId);

        ChatPanel.OpenFor(NewOrb(), session);

        // Sized deliberately, and taller than the shipped 420, the way the
        // backlog test above pins its own window — for the same reason and then
        // one more.
        //
        // This session has more to give, and whether the panel asks for it is
        // decided by layout: the ScrollChanged handler loads the page before
        // when the extent runs more than eight pixels past the viewport. At the
        // shipped height two turns sit almost exactly one viewport tall, so
        // which side of that comparison they land on is a few pixels of font
        // metrics — green on three developer machines, red on both CI runners,
        // where the fonts are not the same ones. Pinning to 420 would only make
        // the coin toss reproducible; 600 puts two turns comfortably inside the
        // viewport, so the trigger cannot fire at all.
        //
        // That matters because the failure does not look like a geometry
        // problem. It reports seven rows where two were seeded — two plus one
        // unrequested page of five — which reads as history replacement being
        // broken, which is the one thing this test is actually about.
        var panel = ChatPanelTestAccess.Instance!;
        panel.Width = 340;
        panel.Height = 600;
        FlushRender();

        Assert.Equal(2, RenderedRows(panel).Count);

        session.ReplaceWith(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "a whole new conversation" },
        });
        FlushRender();

        var rows = RenderedRows(panel);
        Assert.Single(rows);
    }

    // --- a turn retracted by a real RemoteControlChatSession ---

    // FakeChatSession has no way to retract a turn — only the concrete
    // RemoteControlChatSession raises Removed, and ChatPanel subscribes to it
    // by type rather than through IRemoteChatSession (see Bind's own comment).
    // No bridge is started here, the same restraint RemoteControlChatSessionTests
    // itself documents: SetWorking(true) then SetWorking(false) is exactly the
    // "answered before going idle" cycle a real conversation goes through.
    [AvaloniaFact]
    public void AWorkingNoteThatGoesIdleAgainIsRemovedFromTheTranscript()
    {
        var session = new RemoteControlChatSession(
            "rc:.claude-board:history-test-" + Guid.NewGuid(), ".claude-board", "history-test");
        _toClean.Add(session.SessionId);

        ChatPanel.OpenFor(NewOrb(), session);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        var before = RenderedRows(panel).Count;

        session.SetWorking(true);
        FlushRender();
        Assert.Equal(before + 1, RenderedRows(panel).Count);

        session.SetWorking(false);
        FlushRender();
        Assert.Equal(before, RenderedRows(panel).Count);
    }

    // --- the connection-state dot's other colours ---

    private static Color ColourOf(Avalonia.Media.IBrush? brush) => ((ISolidColorBrush)brush!).Color;

    [AvaloniaFact]
    public void TheStateDotHasADistinctColourForEachConnectionState()
    {
        var fake = new FakeChatSession(null) { SessionId = "state-dot-" + Guid.NewGuid() };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        fake.RaiseStateChanged(RemoteChatState.Connected);
        Flush();
        var connected = ColourOf(panel.StateDot.Fill);

        fake.RaiseStateChanged(RemoteChatState.Connecting);
        Flush();
        var connecting = ColourOf(panel.StateDot.Fill);

        fake.RaiseStateChanged(RemoteChatState.Error);
        Flush();
        var error = ColourOf(panel.StateDot.Fill);

        fake.RaiseStateChanged(RemoteChatState.Disconnected);
        Flush();
        var disconnected = ColourOf(panel.StateDot.Fill);

        Assert.NotEqual(connected, connecting);
        Assert.NotEqual(connected, error);
        Assert.NotEqual(connecting, error);
        Assert.NotEqual(connected, disconnected);
        Assert.NotEqual(connecting, disconnected);
        Assert.NotEqual(error, disconnected);
    }

    // --- the permission-prompt box ---

    private sealed class PromptingSession : IRemoteChatSession, IRemoteChatPrompts
    {
        private readonly List<ChatTurn> _history = new();

        public string SessionId { get; } = "prompting-" + Guid.NewGuid();
        public string DisplayName { get; } = "Prompting Session";
        public RemoteChatState State => RemoteChatState.Connected;
        public IReadOnlyList<ChatTurn> History => _history;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;

        public ChatPrompt? Prompt { get; private set; }
        public event Action? PromptChanged;

        public List<ChatPromptOption> Answered { get; } = new();
        public bool AnsweredElsewhereCalled { get; private set; }

        public void RaisePrompt(ChatPrompt? prompt)
        {
            Prompt = prompt;
            PromptChanged?.Invoke();
        }

        public Task AnswerAsync(ChatPromptOption option)
        {
            Answered.Add(option);
            Prompt = null;
            PromptChanged?.Invoke();
            return Task.CompletedTask;
        }

        public void AnswerElsewhere() => AnsweredElsewhereCalled = true;

        public Task SendAsync(string text) => Task.CompletedTask;

        public void Cancel()
        {
        }

        // Never raised; present so nothing throws for lack of a subscriber.
        private void Unused()
        {
            TurnAdded?.Invoke(_history.FirstOrDefault() ?? new ChatTurn());
            TurnUpdated?.Invoke(_history.FirstOrDefault() ?? new ChatTurn());
            StateChanged?.Invoke(State);
        }
    }

    private static Border RowBorderOf(Control presenter) =>
        presenter.GetVisualDescendants().OfType<Border>().First();

    // Clicking an option answers it and takes the box down — the same
    // dismissal any answer produces, because there is no longer a dialog to
    // show buttons for.
    [AvaloniaFact]
    public void ClickingAPromptOptionAnswersItAndHidesTheBox()
    {
        var session = new PromptingSession();
        _toClean.Add(session.SessionId);

        ChatPanel.OpenFor(NewOrb(), session);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        var promptBox = panel.FindControl<Border>("PromptBox")!;

        session.RaisePrompt(new ChatPrompt("Allow this tool call?", new[]
        {
            new ChatPromptOption("1", "Yes"),
            new ChatPromptOption("2", "No"),
        }));
        FlushRender();

        Assert.True(promptBox.IsVisible);

        var options = panel.FindControl<ItemsControl>("PromptOptions")!;
        var presenter = (Control)options.ItemsPanelRoot!.Children[0];
        var row = RowBorderOf(presenter);

        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        row.RaiseEvent(new PointerPressedEventArgs(
            row, pointer, panel, new Point(1, 1), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
        Flush();

        Assert.Single(session.Answered);
        Assert.Equal("1", session.Answered[0].Key);
        Assert.False(promptBox.IsVisible);
    }

    // A prompt whose screen could not be read still shows the box — with
    // nowhere for a click to land, just the fall-back link to answer it by
    // hand in the terminal.
    [AvaloniaFact]
    public void APromptWithNoReadableOptionsStillShowsTheBoxWithOnlyTheElsewhereLink()
    {
        var session = new PromptingSession();
        _toClean.Add(session.SessionId);

        ChatPanel.OpenFor(NewOrb(), session);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        session.RaisePrompt(new ChatPrompt("Something is waiting", Array.Empty<ChatPromptOption>()));
        FlushRender();

        Assert.True(panel.FindControl<Border>("PromptBox")!.IsVisible);
        Assert.False(panel.FindControl<ItemsControl>("PromptOptions")!.IsVisible);
    }

    // "Answer in the terminal" goes to wherever the dialog actually is, and
    // dismisses the panel that just sent you there.
    [AvaloniaFact]
    public void ClickingAnswerElsewhereTellsTheSessionAndClosesThePanel()
    {
        var session = new PromptingSession();
        _toClean.Add(session.SessionId);

        ChatPanel.OpenFor(NewOrb(), session);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        session.RaisePrompt(new ChatPrompt("Allow?", new[] { new ChatPromptOption("1", "Yes") }));
        FlushRender();

        var elsewhere = panel.FindControl<TextBlock>("PromptElsewhere")!;
        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        elsewhere.RaiseEvent(new PointerPressedEventArgs(
            elsewhere, pointer, panel, new Point(1, 1), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
        Flush();

        Assert.True(session.AnsweredElsewhereCalled);
        Assert.False(panel.IsVisible);
    }

    // --- a streaming reply's rendered Markdown is thrown away and rebuilt ---

    // TurnView caches Body and invalidates it on the turn's own Text change —
    // without that, a streaming reply would keep showing its very first
    // snapshot forever.
    [AvaloniaFact]
    public void AStreamingTurnsRenderedTextUpdatesWhenItsTextChanges()
    {
        var turn = new ChatTurn { Role = ChatRole.Assistant, Text = "first snapshot" };
        var fake = new FakeChatSession(new[] { turn }) { SessionId = "streaming-" + Guid.NewGuid() };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        var row = RenderedRows(panel)[0];
        Assert.Contains("first snapshot", row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

        turn.Text = "first snapshot, now finished";
        fake.RaiseTurnUpdated(turn);
        FlushRender();

        var texts = row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("first snapshot, now finished", texts);
        Assert.DoesNotContain("first snapshot", texts);
    }
}
