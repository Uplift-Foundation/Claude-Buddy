namespace ClaudeBuddy.Tests;

// Same fake as tests/UiTests/FakeChatSession.cs, copied rather than shared
// across projects — this project deliberately doesn't reference that one
// (see TestAppBuilder's own comment on why the two stay isolated), and the
// fake itself needs nothing internal, so a plain copy costs nothing. See
// the original for the four IRemoteChatSession rules this honours.
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
}
