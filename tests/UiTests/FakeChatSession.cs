namespace ClaudeBuddy.Tests;

// An in-memory IRemoteChatSession, exactly the shape RemoteChat.cs's own
// header comment says the interface was designed to be driven by "before any
// real gateway exists". Honours the four rules stated there:
//
//  1. Every event fires on the UI thread — trivially true in a headless test,
//     since everything here runs on the one dispatcher thread already.
//  2. TurnUpdated would carry the whole mutated turn, not a delta (no test
//     below needs to raise it, so there is nothing to get wrong yet).
//  3. SendAsync raises TurnAdded for the user's own turn itself — the panel
//     never inserts it optimistically.
//  4. History is pre-bounded and already ordered oldest to newest — callers
//     of this fake are expected to build it that way; it does no trimming or
//     sorting of its own.
internal sealed class FakeChatSession : IRemoteChatSession
{
    public string SessionId { get; init; } = "fake-session";
    public string DisplayName { get; init; } = "Fake Session";
    public RemoteChatState State { get; set; } = RemoteChatState.Connected;

    private readonly List<ChatTurn> _history;
    public IReadOnlyList<ChatTurn> History => _history;

    public event Action<ChatTurn>? TurnAdded;
    public event Action<ChatTurn>? TurnUpdated;
    public event Action<RemoteChatState>? StateChanged;

    // What SendAsync was actually called with, in order — so a test can
    // assert on what the panel sent without the fake having to simulate a
    // reply.
    public List<string> SentTexts { get; } = new();

    public FakeChatSession(IEnumerable<ChatTurn>? seedHistory = null)
    {
        _history = seedHistory?.ToList() ?? new List<ChatTurn>();
    }

    public Task SendAsync(string text)
    {
        SentTexts.Add(text);

        var turn = new ChatTurn { Role = ChatRole.User, Text = text };
        _history.Add(turn);
        TurnAdded?.Invoke(turn);

        return Task.CompletedTask;
    }

    public void Cancel()
    {
        // No-op: nothing is ever in flight in this fake.
    }

    // Test helpers, not part of the interface: raise the two events the
    // panel subscribes to but that SendAsync never needs to for these tests.
    public void RaiseTurnAdded(ChatTurn turn)
    {
        _history.Add(turn);
        TurnAdded?.Invoke(turn);
    }

    public void RaiseTurnUpdated(ChatTurn turn) => TurnUpdated?.Invoke(turn);

    public void RaiseStateChanged(RemoteChatState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}
