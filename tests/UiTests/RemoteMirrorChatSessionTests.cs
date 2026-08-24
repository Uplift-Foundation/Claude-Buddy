using System.Text;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// RemoteControlChatSession in **live view** — the mode where the panel shows the
// far session's own transcript rather than a reply a model wrote about it.
//
// Separate from RemoteControlChatSessionTests, which covers the messaging
// fallback, because the two modes are genuinely different behaviours sharing a
// class and mixing them would make each file's setup lie about the other's.
//
// The far Buddy here is the real RemoteMirrorServer reading real files in a temp
// directory, reached through the real RemoteMirrorClient and the real protocol.
// Only the relay is faked, and only because the real one is a live Claude Code
// session on somebody's account. Same seam as MirrorRoundTripTests, and for the
// same reason.
public class RemoteMirrorChatSessionTests : IDisposable
{
    private const string Account = ".claude-board";
    private const string Name = "job-hunter";

    private readonly string _dir;
    private readonly List<(string Name, string Text)> _typed = new();

    private RemoteMirrorServer _server = null!;
    private RemoteMirrorClient _client = null!;
    private readonly List<(string SessionId, SessionStatus Status)> _sessions = new();
    private readonly List<AgentRoster.Entry> _agents = new();

    private bool _replyEnabled = true;
    private bool _canType = true;
    private bool _mangle;

    private readonly bool _remoteWasEnabled;

    public RemoteMirrorChatSessionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-mirror-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        // A send checks this before doing anything, so with it off every test
        // below would assert against "remote sessions are switched off" instead
        // of the thing it is about. Put back in Dispose rather than left on: a
        // real setter that writes settings.json and leaks into every test after
        // it is exactly what bugfix/rc-tests-leak-remote-setting had to fix once
        // already.
        _remoteWasEnabled = ClaudeBuddySettings.RemoteControlEnabled;
        ClaudeBuddySettings.RemoteControlEnabled = true;
    }

    public void Dispose()
    {
        ClaudeBuddySettings.RemoteControlEnabled = _remoteWasEnabled;
        RemoteControlSessions.ResetForTests();

        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // --- the point of the whole thing -----------------------------------------

    // What the panel ends up holding is the far session's own conversation,
    // parsed by the same ChatTranscript a local panel uses — not a reply
    // composed about it.
    [AvaloniaFact]
    public async Task ALiveViewShowsTheFarSessionsOwnTranscript()
    {
        Wire("what did the build say?", "it passed on both runners");

        var session = await OpenAsync();

        Assert.True(session.IsMirroring);

        var said = Turns(session);

        Assert.Equal(2, said.Count);
        Assert.Equal(ChatRole.User, said[0].Role);
        Assert.Equal("what did the build say?", said[0].Text);
        Assert.Equal(ChatRole.Assistant, said[1].Role);
        Assert.Equal("it passed on both runners", said[1].Text);
    }

    // The banner replaces the "checking…" line rather than joining it, and says
    // plainly what the panel now is — including that typing goes into somebody
    // else's terminal, which is worth saying out loud.
    [AvaloniaFact]
    public async Task UpgradingReplacesTheOpeningLineWithOneThatSaysItIsLive()
    {
        Wire("hello", "hi");

        var replaced = 0;
        var session = await OpenAsync(s => ((IRemoteChatBacklog)s).HistoryReplaced += () => replaced++);

        Assert.Equal(1, replaced);

        var banner = session.History[0];

        Assert.Equal(ChatRole.System, banner.Role);
        Assert.Contains("Live view", banner.Text);
        Assert.Contains(Name, banner.Text);
        Assert.DoesNotContain(session.History, t => t.Text.Contains("Checking whether"));
    }

    // The box has to stop saying "message this session" once messages are being
    // typed into a terminal instead.
    [AvaloniaFact]
    public async Task TheComposerSaysTypingRatherThanMessaging()
    {
        Wire("a", "b");

        var session = await OpenAsync();

        Assert.Contains("terminal", session.ComposerHint);
        Assert.Contains(Name, session.ComposerHint);
    }

    // A live view is a real transcript, so it can be paged back into — which is
    // exactly what the messaging channel could not do.
    [AvaloniaFact]
    public async Task ALiveViewCanBePagedBackInto()
    {
        // Comfortably more than one opening window.
        var rows = new List<string>();
        var bytes = 0;

        for (var i = 0; bytes < MirrorProtocol.InitialBytes + 50_000; i++)
        {
            var row = UserRow($"r{i}", $"message {i} " + new string('y', 300));
            rows.Add(row);
            bytes += row.Length + 1;
        }

        WireRows(rows);

        var session = await OpenAsync();
        var backlog = (IRemoteChatBacklog)session;

        Assert.True(backlog.HasMore);

        var prepended = 0;
        backlog.HistoryPrepended += n => prepended += n;

        Assert.True(await backlog.LoadOlderAsync(CancellationToken.None));
        Assert.True(prepended > 0);

        // The banner stays at the top: it describes the panel, not a moment in
        // the conversation, so older turns go in under it.
        Assert.Contains("Live view", session.History[0].Text);
    }

    // --- typing -----------------------------------------------------------------

    // The half of this bug that was about slash commands. The message reaches
    // the far session's own input line, which is the only place /color can
    // possibly work.
    [AvaloniaFact]
    public async Task AMessageIsTypedIntoTheFarTerminalRatherThanDescribedToAModel()
    {
        Wire("a", "b");

        var session = await OpenAsync();

        await session.SendAsync("/color green");

        var typed = Assert.Single(_typed);
        Assert.Equal(Name, typed.Name);
        Assert.Equal("/color green", typed.Text);

        // On screen once, after the transcript's own opening turn.
        Assert.Equal(
            new[] { "a", "/color green" },
            Turns(session).Where(t => t.Role == ChatRole.User).Select(t => t.Text));
    }

    // The far transcript will produce the message back, because it went in
    // through the terminal — that is the whole design. So the row that returns
    // adopts the bubble already on screen instead of adding a second.
    [AvaloniaFact]
    public async Task TheEchoOfATypedMessageSettlesTheTurnRatherThanDuplicatingIt()
    {
        Wire("a", "b");

        var session = await OpenAsync();

        var updated = 0;
        session.TurnUpdated += _ => updated++;

        await session.SendAsync("run the tests");

        // The far CLI reads it and writes it into its own transcript.
        Append(UserRow("echo", "run the tests"));
        await _server.TickAsync();

        // Once, not twice: the echo adopted the bubble that was already there.
        Assert.Equal(
            new[] { "a", "run the tests" },
            Turns(session).Where(t => t.Role == ChatRole.User).Select(t => t.Text));

        Assert.Equal(1, updated);
    }

    // An unrelated message that happens to arrive after a send must not be
    // swallowed by the pending turn.
    [AvaloniaFact]
    public async Task ADifferentMessageArrivingAfterASendIsNotMistakenForTheEcho()
    {
        Wire("a", "b");

        var session = await OpenAsync();
        await session.SendAsync("run the tests");

        Append(UserRow("other", "something else entirely"));
        await _server.TickAsync();

        Assert.Equal(
            new[] { "a", "run the tests", "something else entirely" },
            Turns(session).Where(t => t.Role == ChatRole.User).Select(t => t.Text));
    }

    [AvaloniaFact]
    public async Task TheFarMachinesOwnReplySettingIsWhatDecidesWhetherAnythingIsTyped()
    {
        Wire("a", "b");
        _replyEnabled = false;

        var session = await OpenAsync();
        await session.SendAsync("hello");

        Assert.Empty(_typed);

        var last = session.History[^1];
        Assert.Equal(ChatRole.System, last.Role);
        Assert.Contains("switched off", last.Text);
        Assert.Contains("over there", last.Text);
    }

    [AvaloniaFact]
    public async Task ASessionWithNoPaneSaysSoRatherThanFailingSilently()
    {
        Wire("a", "b");
        _canType = false;

        var session = await OpenAsync();
        await session.SendAsync("hello");

        Assert.Empty(_typed);
        Assert.Contains("tmux pane", session.History[^1].Text);
    }

    // --- keeping up ---------------------------------------------------------------

    [AvaloniaFact]
    public async Task WhatTheFarSessionDoesNextArrivesWithoutBeingAskedFor()
    {
        Wire("a", "b");

        var session = await OpenAsync();
        var before = Turns(session).Count;

        Append(AssistantRow("new", "and then it deployed"));
        await _server.TickAsync();

        var said = Turns(session);

        Assert.Equal(before + 1, said.Count);
        Assert.Equal("and then it deployed", said[^1].Text);
    }

    // A live view shows the work itself, so a line claiming the session is
    // working would sit directly under the evidence that it is.
    [AvaloniaFact]
    public async Task TheWorkingNoteIsSuppressedOnceThereIsALiveView()
    {
        Wire("a", "b");

        var session = await OpenAsync();
        var before = session.History.Count;

        session.SetWorking(true);

        Assert.Equal(before, session.History.Count);
    }

    // In live view the transcript is the source of truth. A peer message would
    // be a second, differently-worded account of something already shown —
    // which is precisely the confusion this feature exists to end.
    [AvaloniaFact]
    public async Task APeerMessageIsNotAppendedBesideTheTranscriptItParaphrases()
    {
        Wire("a", "b");

        var session = await OpenAsync();
        var before = Turns(session).Count;

        session.OnInbound(new BridgeProtocol.InboundMessage(
            Name, "bridge:session_1", "prompting",
            "Summary for you: the build passed and I deployed it.", Account));

        Assert.Equal(before, Turns(session).Count);
    }

    // --- refusing what did not survive ------------------------------------------

    // The guarantee, at the panel. A mangled transfer produces an error and an
    // empty conversation — never a partial window, and never a quiet fallback to
    // the model-written version, which would substitute a summary at the exact
    // moment integrity failed.
    [AvaloniaFact]
    public async Task AMirrorThatFailsItsIntegrityCheckShowsNothingRatherThanSomethingElse()
    {
        Wire("what did the build say?", "it passed on both runners");
        _mangle = true;

        var session = await OpenAsync(expectMirror: false);

        var last = session.History[^1];

        Assert.Equal(ChatRole.System, last.Role);
        Assert.Contains("Couldn't verify", last.Text);
        Assert.Contains("rather than something altered", last.Text);

        // Nothing from the far transcript reached the screen.
        Assert.DoesNotContain(session.History, t => t.Text.Contains("it passed on both runners"));
        Assert.DoesNotContain(session.History, t => t.Text.Contains("what did the build say?"));
    }

    // --- no live view --------------------------------------------------------------

    // A bare peer — no Buddy on the other machine — keeps the messaging channel
    // and says so, including the part people need to know: that the replies are
    // written for them and may summarise.
    [AvaloniaFact]
    public async Task WithoutABuddyOverThereThePanelSaysWhyItIsNotALiveView()
    {
        WireClientOnly();

        var session = NewSession();
        session.PanelOpened();

        await _client.DiscoverAsync(
            new[] { new BridgeProtocol.RemoteAgent(Name, "94f106", "Remote Control", "idle") },
            new[] { Name });

        Assert.False(session.IsMirroring);

        var last = session.History[^1];

        Assert.Contains("No live view", last.Text);
        Assert.Contains("may summarise", last.Text);
        Assert.Contains("Message", session.ComposerHint);
    }

    [AvaloniaFact]
    public async Task ThePanelOnlySaysItOnce()
    {
        WireClientOnly();

        var session = NewSession();
        session.PanelOpened();

        var peers = new[] { new BridgeProtocol.RemoteAgent(Name, "94f106", "Remote Control", "idle") };

        await _client.DiscoverAsync(peers, new[] { Name });
        await _client.DiscoverAsync(peers, new[] { Name });

        Assert.Single(session.History.Where(t => t.Text.Contains("No live view")));
    }

    // --- wiring ----------------------------------------------------------------------

    private static RemoteControlChatSession NewSession() =>
        new($"rc:{Account}:{Name}", Account, Name);

    // Everything the far session said, with the panel's own banner left out.
    private static IReadOnlyList<ChatTurn> Turns(RemoteControlChatSession session) =>
        session.History.Where(t => t.Role != ChatRole.System).ToList();

    private async Task<RemoteControlChatSession> OpenAsync(
        Action<RemoteControlChatSession>? before = null, bool expectMirror = true)
    {
        var session = NewSession();
        before?.Invoke(session);

        session.PanelOpened();

        // Discovering is all it takes: the roster landing raises MirrorChanged,
        // the session upgrades itself and reads the tail. Opening the feed by
        // hand here would be a second window and would hide whether the session
        // does that for itself.
        await _client.DiscoverAsync(Peers, new[] { Name });

        if (expectMirror) Assert.True(session.IsMirroring, "the panel should have upgraded to a live view");

        return session;
    }

    private static IReadOnlyList<BridgeProtocol.RemoteAgent> Peers =>
        new[]
        {
            new BridgeProtocol.RemoteAgent(FarRelay, "aa11bb", "Remote Control", "idle"),
            new BridgeProtocol.RemoteAgent(Name, "94f106", "Remote Control", "idle")
        };

    private const string FarRelay = "claude-buddy-rc--claude-mini";
    private const string NearRelay = "claude-buddy-rc--claude-laptop";

    private string _path = "";

    private void Wire(string question, string answer) =>
        WireRows(new List<string> { UserRow("u1", question), AssistantRow("a1", answer) });

    private void WireRows(List<string> rows)
    {
        _path = Path.Combine(_dir, "session.jsonl");
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

        BuildServer();
        BuildClient();
    }

    // No far Buddy at all: a client with nothing on the other end of it.
    private void WireClientOnly()
    {
        BuildServer();
        BuildClient();
    }

    private void Append(string row) => File.AppendAllText(_path, row + "\n");

    private void BuildServer() =>
        _server = new RemoteMirrorServer(Account, new RemoteMirrorServer.Seams(
            SendToClientAsync,
            () => _sessions,
            () => _agents,
            _ => _replyEnabled,
            _ => _canType,
            (status, text) =>
            {
                _typed.Add((status.Title, text));
                return Task.FromResult(true);
            }));

    private void BuildClient()
    {
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

        // Only the transcript windows, not the roster: a handshake that failed
        // would leave the panel in messaging mode and never reach the code this
        // test is about. A window carries wfrom; a roster does not.
        if (_mangle && frame.Type == MirrorProtocol.Chunk && frame.Get("wfrom") is not null)
            frame = Mangle(line) ?? frame;

        await _client.OnFrameAsync(FarRelay, frame);
        return true;
    }

    // A courier that quietly rewords what it was asked to relay.
    private static MirrorProtocol.MirrorFrame? Mangle(string line)
    {
        var start = line.IndexOf(";p=", StringComparison.Ordinal);
        var end = line.IndexOf(";h=", StringComparison.Ordinal);
        if (start < 0 || end < 0) return null;

        var swapped = Convert.ToBase64String(Encoding.UTF8.GetBytes("a tidier version of that"));
        return MirrorProtocol.TryParseFrame(line[..(start + 3)] + swapped + line[end..]);
    }

    private static string UserRow(string uuid, string text) =>
        $"{{\"type\":\"user\",\"uuid\":\"{uuid}\",\"message\":{{\"role\":\"user\",\"content\":\"{text}\"}}}}";

    private static string AssistantRow(string uuid, string text) =>
        $"{{\"type\":\"assistant\",\"uuid\":\"{uuid}\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"{text}\"}}]}}}}";
}
