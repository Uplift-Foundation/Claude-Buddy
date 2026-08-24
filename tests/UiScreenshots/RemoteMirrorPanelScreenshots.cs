using Avalonia.Headless.XUnit;

namespace ClaudeBuddy.Tests;

// What a remote session's panel actually looks like in each of its two modes.
//
// Worth capturing rather than asserting on strings alone, because the thing
// being fixed here is a *reading* problem: the old panel showed a model's
// summary of a conversation in a window that looked exactly like the one
// showing a real conversation, and nobody could tell which they were looking
// at. Whether the two now read differently at a glance is a question about
// pixels, so it belongs in the suite that renders them.
//
// The far Buddy is the real RemoteMirrorServer reading a real file in a temp
// directory, reached through the real client and protocol — only the relay is
// faked, same seam as MirrorRoundTripTests.
public class RemoteMirrorPanelScreenshots : IDisposable
{
    private const string Account = ".claude-board";
    private const string Name = "job-hunter";
    private const string FarRelay = "claude-buddy-rc--claude-mini";
    private const string NearRelay = "claude-buddy-rc--claude-laptop";

    private readonly string _dir;
    private readonly List<string> _sessionIdsToClean = new();
    private readonly bool _remoteWasEnabled;

    private readonly List<(string SessionId, SessionStatus Status)> _sessions = new();
    private readonly List<AgentRoster.Entry> _agents = new();

    private RemoteMirrorServer _server = null!;
    private RemoteMirrorClient _client = null!;

    public RemoteMirrorPanelScreenshots()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-shot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _remoteWasEnabled = ClaudeBuddySettings.RemoteControlEnabled;
        ClaudeBuddySettings.RemoteControlEnabled = true;
    }

    public void Dispose()
    {
        foreach (var id in _sessionIdsToClean) ChatPanel.HideFor(id);

        ClaudeBuddySettings.RemoteControlEnabled = _remoteWasEnabled;
        RemoteControlSessions.ResetForTests();

        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Deliberately never closed — same reason as ChatPanelScreenshots: closing a
    // headless Window corrupts a process-wide FontManager cache for every window
    // built afterward in this run.
    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    // The fix, on screen: the far session's own conversation, with a line at the
    // top saying that is what it is and that typing goes into its terminal.
    [AvaloniaFact]
    public async Task ALiveViewShowsTheFarSessionsOwnConversation()
    {
        Wire(
            ("user", "did the release build come out clean on both runners?"),
            ("assistant", "Yes — macos-latest and windows-latest both green. "
                        + "The dmg and the installer are on the run's artifacts."),
            ("user", "/color green"),
            ("assistant", "Set. This session's orb is green now."));

        var session = Open();
        await _client.DiscoverAsync(Peers, new[] { Name });

        ChatPanel.OpenFor(NewOrb(), session);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-remote-live-view.png");
    }

    // The fallback, and the reason it has to look different: there is no Buddy on
    // the other machine, so what arrives here is written *for* the reader by a
    // model rather than read off a disk. The panel says so instead of letting it
    // pass for a transcript.
    [AvaloniaFact]
    public async Task WithNoBuddyOverThereThePanelSaysItIsOnlyAMessagingChannel()
    {
        WireEmpty();

        var session = Open();

        await _client.DiscoverAsync(
            new[] { new BridgeProtocol.RemoteAgent(Name, "94f106", "Remote Control", "idle") },
            new[] { Name });

        ChatPanel.OpenFor(NewOrb(), session);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-remote-no-live-view.png");
    }

    // A transfer that failed its integrity check. Nothing of it is shown — which
    // is the point, and is also why this is worth a picture: an empty panel with
    // an explanation is what "we refused to show you something we could not
    // verify" has to look like.
    [AvaloniaFact]
    public async Task AMirrorThatFailedItsIntegrityCheckShowsTheRefusal()
    {
        Wire(
            ("user", "what did it say?"),
            ("assistant", "something that will not survive the trip"));

        _mangle = true;

        var session = Open();
        await _client.DiscoverAsync(Peers, new[] { Name });

        ChatPanel.OpenFor(NewOrb(), session);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-remote-integrity-refusal.png");
    }

    // --- wiring -------------------------------------------------------------------

    private bool _mangle;
    private string _path = "";

    private RemoteControlChatSession Open()
    {
        var id = $"rc:{Account}:{Name}";
        _sessionIdsToClean.Add(id);

        var session = new RemoteControlChatSession(id, Account, Name);
        session.PanelOpened();
        return session;
    }

    private static IReadOnlyList<BridgeProtocol.RemoteAgent> Peers =>
        new[]
        {
            new BridgeProtocol.RemoteAgent(FarRelay, "aa11bb", "Remote Control", "idle"),
            new BridgeProtocol.RemoteAgent(Name, "94f106", "Remote Control", "idle")
        };

    private void Wire(params (string Role, string Text)[] turns)
    {
        _path = Path.Combine(_dir, "session.jsonl");

        var rows = turns.Select((t, i) => t.Role == "user"
            ? $"{{\"type\":\"user\",\"uuid\":\"u{i}\",\"message\":{{\"role\":\"user\",\"content\":\"{t.Text}\"}}}}"
            : $"{{\"type\":\"assistant\",\"uuid\":\"a{i}\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"{t.Text}\"}}]}}}}");

        File.WriteAllText(_path, string.Join("\n", rows) + "\n");

        var sessionId = Guid.NewGuid().ToString();
        _agents.Add(new AgentRoster.Entry(Name, sessionId, 4242));
        _sessions.Add((sessionId, new SessionStatus
        {
            Title = Name,
            Cwd = _dir,
            Source = SessionSource.ClaudeCode,
            TranscriptPath = _path,
            TmuxPane = "%1",
            SessionPid = 4242
        }));

        Build();
    }

    private void WireEmpty() => Build();

    private void Build()
    {
        _server = new RemoteMirrorServer(Account, new RemoteMirrorServer.Seams(
            SendToClientAsync,
            () => _sessions,
            () => _agents,
            _ => true,
            _ => true,
            (_, _) => Task.FromResult(true)));

        _client = new RemoteMirrorClient(Account, new RemoteMirrorClient.Seams(SendToServerAsync));
        RemoteControlSessions.UseMirrorClientForTests(Account, _client);
    }

    private async Task<bool> SendToServerAsync(string peer, string line)
    {
        var frame = MirrorProtocol.TryParseFrame(line);
        if (frame is null) return false;

        await _server.HandleAsync(NearRelay, frame);
        return true;
    }

    private async Task<bool> SendToClientAsync(string peer, string line)
    {
        var frame = MirrorProtocol.TryParseFrame(line);
        if (frame is null) return false;

        // Windows only, so the handshake still succeeds and the failure lands
        // where this is about.
        if (_mangle && frame.Type == MirrorProtocol.Chunk && frame.Get("wfrom") is not null)
        {
            var start = line.IndexOf(";p=", StringComparison.Ordinal);
            var end = line.IndexOf(";h=", StringComparison.Ordinal);

            if (start >= 0 && end >= 0)
            {
                var swapped = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes("a tidier version of that"));

                frame = MirrorProtocol.TryParseFrame(line[..(start + 3)] + swapped + line[end..]) ?? frame;
            }
        }

        await _client.OnFrameAsync(FarRelay, frame);
        return true;
    }
}
