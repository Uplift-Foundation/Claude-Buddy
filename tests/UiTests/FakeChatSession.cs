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
internal sealed class FakeChatSession :
    IRemoteChatSession, IRemoteChatImages, IRemoteChatSlashCommands,
    IRemoteChatComposer, IRemoteChatElsewhere
{
    public string SessionId { get; init; } = "fake-session";
    public string DisplayName { get; init; } = "Fake Session";
    public RemoteChatState State { get; set; } = RemoteChatState.Connected;

    // Empty by default, the same as a session with nothing to say about
    // IRemoteChatSlashCommands. Settable rather than init-only, and after
    // OpenFor as well as before: a session on another machine has to be asked
    // what it can run, so its list arrives well after the panel has bound to
    // it, and a fake that could only be set up front could not express the
    // case that mattered.
    public IReadOnlyList<SlashCommand> SlashCommands { get; set; } = Array.Empty<SlashCommand>();

    // What the composer's box says, and whether the panel offers to attach this
    // session somewhere it can be typed into. Both default to what an ordinary
    // typeable session answers, so every existing test of this fake is unchanged
    // by their arrival: an ordinary hint, and no button.
    public string ComposerHint { get; set; } = "Message…";

    public bool CanOpenElsewhere { get; set; }

    // Counted rather than performed. The real one opens or focuses a real
    // window, which is the half this suite must never execute — what is being
    // tested is that a click on the button reaches the session at all.
    public int OpenElsewhereCalls { get; private set; }

    public void OpenElsewhere() => OpenElsewhereCalls++;

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

    // What SendWithImagesAsync was actually called with — the panel's paste
    // path takes this route instead of SendAsync whenever it is holding at
    // least one pending picture (see IRemoteChatImages).
    public List<(string Text, List<string> ImagePaths)> SentWithImages { get; } = new();

    public Task SendWithImagesAsync(string text, IReadOnlyList<string> imagePaths)
    {
        SentWithImages.Add((text, imagePaths.ToList()));

        var turn = new ChatTurn { Role = ChatRole.User, Text = text };
        _history.Add(turn);
        TurnAdded?.Invoke(turn);

        return Task.CompletedTask;
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
