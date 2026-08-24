using Xunit;

namespace ClaudeBuddy.Tests;

// The paths a mirror takes when something is missing, stale, refused or asked
// for twice — the ones a happy round trip never reaches.
//
// Split from MirrorRoundTripTests so that file stays a readable statement of
// what the feature does, and this one carries the accumulated "and what if"
// list without burying it.
public class MirrorEdgeCaseTests : IDisposable
{
    private const string FarRelay = "claude-buddy-rc--claude-mini";
    private const string NearRelay = "claude-buddy-rc--claude-laptop";
    private const string Account = ".claude-board";
    private const string Name = "job-hunter";

    private readonly string _dir;
    private readonly List<(string SessionId, SessionStatus Status)> _sessions = new();
    private readonly List<AgentRoster.Entry> _agents = new();
    private readonly List<string> _toClient = new();

    private RemoteMirrorServer _server = null!;
    private RemoteMirrorClient _client = null!;

    private readonly List<RemoteMirrorClient.MirrorRows> _windows = new();
    private readonly List<RemoteMirrorClient.MirrorRows> _deltas = new();
    private readonly List<(string Name, string Why)> _failures = new();

    private bool _dropEverything;
    private int _refuseAfter = -1;
    private int _sent;
    private string _path = "";

    public MirrorEdgeCaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-edge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Build();
    }

    public void Dispose()
    {
        RemoteControlSessions.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // --- asking about nothing --------------------------------------------------

    // HELLO carries the names the asker cares about. One with none is malformed,
    // and is answered rather than ignored so the asker is not left waiting out a
    // timeout for a question it asked wrong.
    [Fact]
    public async Task AHandshakeThatNamesNoSessionsIsRefusedRatherThanIgnored()
    {
        await _server.HandleAsync(NearRelay, Frame(MirrorProtocol.Hello, "abcd1234"));

        Assert.Contains(_toClient, line => line.Contains(";t=" + MirrorProtocol.Err));
    }

    [Fact]
    public async Task AFetchWithNoNameIsRefused()
    {
        await _server.HandleAsync(NearRelay, Frame(MirrorProtocol.Fetch, "abcd1234"));

        Assert.Contains(_toClient, line => line.Contains("code=" + MirrorProtocol.ErrNoSession));
    }

    [Fact]
    public async Task AFetchForASessionThatIsNotHereIsRefused()
    {
        await _server.HandleAsync(NearRelay, Frame(
            MirrorProtocol.Fetch, "abcd1234",
            ("n", MirrorProtocol.Encode("someone-else")), ("w", "tail")));

        Assert.Contains(_toClient, line => line.Contains("code=" + MirrorProtocol.ErrNoSession));
    }

    // Registered, running, and Buddy has no transcript path for it — a session
    // whose hook has not written one yet.
    [Fact]
    public async Task AFetchForASessionWithNoTranscriptSaysSoRatherThanFailingSilently()
    {
        AddSession(transcriptPath: "");

        await _server.HandleAsync(NearRelay, Frame(
            MirrorProtocol.Fetch, "abcd1234",
            ("n", MirrorProtocol.Encode(Name)), ("w", "tail")));

        Assert.Contains(_toClient, line => line.Contains("code=" + MirrorProtocol.ErrNoTranscript));
    }

    // The path is there and the file is not — a session whose transcript was
    // deleted under it.
    [Fact]
    public async Task AFetchForATranscriptThatIsNotOnDiskIsRefused()
    {
        AddSession(transcriptPath: Path.Combine(_dir, "gone.jsonl"));

        await _server.HandleAsync(NearRelay, Frame(
            MirrorProtocol.Fetch, "abcd1234",
            ("n", MirrorProtocol.Encode(Name)), ("w", "tail")));

        Assert.Contains(_toClient, line => line.Contains("code=" + MirrorProtocol.ErrNoTranscript));
    }

    [Fact]
    public async Task AWatchOnASessionThatIsNotHereIsRefused()
    {
        await _server.HandleAsync(NearRelay, Frame(
            MirrorProtocol.Watch, "abcd1234", ("n", MirrorProtocol.Encode("someone-else"))));

        Assert.Contains(_toClient, line => line.Contains("code=" + MirrorProtocol.ErrNoSession));
    }

    [Fact]
    public async Task AWatchWithNoNameIsDroppedRatherThanRegistered()
    {
        await _server.HandleAsync(NearRelay, Frame(MirrorProtocol.Watch, "abcd1234"));

        Assert.False(_server.Busy);
    }

    [Fact]
    public async Task AResendForATransferNobodyRemembersIsRefused()
    {
        await _server.HandleAsync(NearRelay, Frame(
            MirrorProtocol.Resend, "abcd1234", ("seq", "0")));

        Assert.Contains(_toClient, line => line.Contains("code=" + MirrorProtocol.ErrBadHash));
    }

    [Fact]
    public async Task AnInputWhoseTextDidNotSurviveIsRefusedBeforeAnythingIsResolved()
    {
        AddSession();

        // A payload whose digest does not match: exactly what a courier that
        // altered the text in flight produces. Typing this into somebody's
        // terminal is the worst thing that could happen in this file.
        var line = "CB-MIRROR:v1;t=" + MirrorProtocol.Input + ";id=abcd1234;n="
                   + MirrorProtocol.Encode(Name)
                   + ";p=" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("rm -rf /"))
                   + ";h=" + new string('0', 64);

        await _server.HandleAsync(NearRelay, MirrorProtocol.TryParseFrame(line)!);

        Assert.Contains(_toClient, l => l.Contains("code=" + MirrorProtocol.ErrBadHash));
        Assert.Empty(_typed);
    }

    [Fact]
    public async Task AnInputWithNoNameIsRefused()
    {
        var frame = MirrorProtocol.TryParseFrame(MirrorProtocol.BuildFrame(
            MirrorProtocol.Input, "abcd1234", null,
            System.Text.Encoding.UTF8.GetBytes("hello")))!;

        await _server.HandleAsync(NearRelay, frame);

        Assert.Contains(_toClient, l => l.Contains("code=" + MirrorProtocol.ErrNoSession));
        Assert.Empty(_typed);
    }

    // The typing itself failing — tmux gone, pane closed between the check and
    // the send. Reported rather than swallowed, since the person is waiting to
    // see their message appear.
    [Fact]
    public async Task ATypeThatFailsAtTheLastMomentIsReportedNotSwallowed()
    {
        AddSession();
        _typeSucceeds = false;

        await Handshake();

        Assert.Equal(MirrorProtocol.ErrNoPane, await _client.SendInputAsync(Name, "hello"));
    }

    [Fact]
    public async Task ATypeThatThrowsIsReportedRatherThanCrashingTheRelay()
    {
        AddSession();
        _typeThrows = true;

        await Handshake();

        Assert.Equal(MirrorProtocol.ErrNoPane, await _client.SendInputAsync(Name, "hello"));
    }

    [Fact]
    public async Task SendingToASessionNoBuddyHasClaimedIsRefusedWithoutAskingAnyone()
    {
        Assert.Equal(MirrorProtocol.ErrNoSession, await _client.SendInputAsync("never-heard-of-it", "hi"));
        Assert.Empty(_toClient);
    }

    // --- the client's side -------------------------------------------------------

    [Fact]
    public async Task ADiscoveryThatAsksAboutNothingSendsNothing()
    {
        await _client.DiscoverAsync(Peers, Array.Empty<string>());

        Assert.Empty(_toClient);
    }

    // Silence is not "no". A relay that was busy, or whose model did not call
    // the tool, is asked again next poll — only a real answer settles a name as
    // having no live view.
    [Fact]
    public async Task ARelayThatDoesNotAnswerLeavesTheQuestionOpen()
    {
        AddSession();
        _dropEverything = true;

        // The one test here that waits out a timeout on purpose, so it is the
        // one that shortens it.
        _client.TimeoutOverrideForTests = TimeSpan.FromMilliseconds(250);

        await _client.DiscoverAsync(Peers, new[] { Name });

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unknown,
            _client.StateFor(Name).Availability);
    }

    // Asked once and answered, the roster is not asked for again — every
    // handshake is a model turn on both relays.
    [Fact]
    public async Task ASessionAlreadyAnsweredForIsNotAskedAboutAgain()
    {
        AddSession();

        await Handshake();
        var after = _toClient.Count;

        await _client.DiscoverAsync(Peers, new[] { Name });

        Assert.Equal(after, _toClient.Count);
    }

    [Fact]
    public async Task OpeningASessionNoBuddyHasClaimedDoesNothing()
    {
        Assert.False(await _client.OpenAsync("never-heard-of-it"));
        Assert.Empty(_toClient);
    }

    [Fact]
    public async Task ThereIsNoBacklogForASessionThatWasNeverOpened() =>
        await Task.Run(() =>
        {
            Assert.False(_client.HasMore("never-heard-of-it"));
            Assert.Null(_client.LoadOlderAsync("never-heard-of-it").Result);
        });

    // Rebinding a panel to the session it is already showing should cost a
    // renewal, not another full read of the tail — the panel is a singleton and
    // clicking between two orbs rebinds constantly.
    [Fact]
    public async Task ReopeningAPanelAlreadyOpenDoesNotReReadTheWholeTail()
    {
        AddSession(rows: 20);
        await Handshake();

        await _client.OpenAsync(Name);
        Assert.Single(_windows);

        await _client.ReopenAsync(Name);

        Assert.Single(_windows);
    }

    [Fact]
    public async Task ReopeningAPanelThatWasClosedReadsItAgain()
    {
        AddSession(rows: 20);
        await Handshake();

        await _client.OpenAsync(Name);
        await _client.CloseAsync(Name);
        await _client.ReopenAsync(Name);

        Assert.Equal(2, _windows.Count);
    }

    // Closing tells the far side to stop pushing rather than letting the watch
    // lapse: a relay kept awake by a panel nobody is looking at is somebody's
    // quota.
    [Fact]
    public async Task ClosingAPanelStopsTheFarSideServingIt()
    {
        AddSession(rows: 20);
        await Handshake();
        await _client.OpenAsync(Name);

        Assert.True(_server.Busy);

        await _client.CloseAsync(Name);

        Assert.False(_server.Busy);
        Assert.False(_client.Busy);
    }

    [Fact]
    public async Task ClosingSomethingThatWasNeverOpenIsHarmless()
    {
        await _client.CloseAsync("never-heard-of-it");

        Assert.Empty(_toClient);
    }

    // A watch that is never renewed lapses, so a Buddy that quit without saying
    // goodbye stops being served.
    [Fact]
    public async Task AWatchNobodyRenewsLapsesOnItsOwn()
    {
        AddSession(rows: 20);
        await Handshake();

        await _server.HandleAsync(NearRelay, Frame(
            MirrorProtocol.Watch, "abcd1234",
            ("n", MirrorProtocol.Encode(Name)), ("ttl", "0")));

        Assert.True(_server.Busy);

        // TTL 0 is out of range and is clamped to the maximum rather than taken
        // literally, so this is still live...
        await _server.TickAsync();
        Assert.True(_server.Busy);

        // ...whereas an explicit unwatch is not.
        await _server.HandleAsync(NearRelay, Frame(MirrorProtocol.Unwatch, "abcd1234"));
        Assert.False(_server.Busy);
    }

    [Fact]
    public async Task RenewingAWatchUpdatesTheOneAlreadyThereRatherThanStackingAnother()
    {
        AddSession(rows: 20);
        await Handshake();
        await _client.OpenAsync(Name);

        // A panel left open for an afternoon renews every ninety seconds;
        // thirty stacked subscriptions would send every update thirty times.
        for (var i = 0; i < 5; i++) await _client.TickAsync();

        File.AppendAllText(_path, UserRow("late", "one more") + "\n");
        await _server.TickAsync();

        Assert.Single(_deltas);
    }

    [Fact]
    public async Task TickingWithNothingOpenDoesNothing()
    {
        await _client.TickAsync();
        await _server.TickAsync();

        Assert.Empty(_toClient);
    }

    // A session that goes away while it is being watched stops producing
    // updates instead of throwing on every tick.
    [Fact]
    public async Task AWatchedSessionThatDisappearsJustStopsUpdating()
    {
        AddSession(rows: 20);
        await Handshake();
        await _client.OpenAsync(Name);

        _agents.Clear();
        _sessions.Clear();

        File.AppendAllText(_path, UserRow("late", "nobody will see this") + "\n");
        await _server.TickAsync();

        Assert.Empty(_deltas);
    }

    // --- asking a session what it is ------------------------------------------------

    // Asked once *ever* meant never for anyone whose first ask went unanswered:
    // that session's autocomplete stayed empty for as long as Buddy ran, with
    // nothing on screen to say a question had been asked at all.
    [Fact]
    public void AnUnansweredCapabilityQuestionIsAskedAgainLaterButNotForever()
    {
        var now = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        RemoteControlSessions.Now = () => now;

        const string key = Account + ":" + Name;

        Assert.True(RemoteControlSessions.ShouldAsk(key));

        // Not again straight away — a poll every twenty seconds must not become
        // a message every twenty seconds.
        Assert.False(RemoteControlSessions.ShouldAsk(key));

        now = now.AddMinutes(9);
        Assert.False(RemoteControlSessions.ShouldAsk(key));

        now = now.AddMinutes(2);
        Assert.True(RemoteControlSessions.ShouldAsk(key));

        now = now.AddMinutes(11);
        Assert.True(RemoteControlSessions.ShouldAsk(key));

        // Three is the cap. A session that has ignored three is telling you
        // something, and each one is a real message into a real session.
        now = now.AddMinutes(11);
        Assert.False(RemoteControlSessions.ShouldAsk(key));
    }

    // A far Buddy's roster carries the command list, read off its own disk, so
    // it wins over anything a model recited — and it includes built-ins, which
    // now genuinely run.
    [Fact]
    public async Task ARosterCommandListBeatsOneAModelRecited()
    {
        AddSession(rows: 4);
        await Handshake();

        RemoteControlSessions.UseMirrorClientForTests(Account, _client);

        var commands = RemoteControlSessions.CommandsFor(Account, Name);

        Assert.Contains(commands, c => c.Name == "/color");
    }

    [Fact]
    public void WithNoMirrorAtAllTheStateIsUnknownRatherThanAThrow()
    {
        var state = RemoteControlSessions.MirrorStateFor("no-such-account", "nobody");

        Assert.Equal(RemoteMirrorClient.MirrorAvailability.Unknown, state.Availability);
        Assert.Null(state.Entry);
        Assert.Null(RemoteControlSessions.MirrorClientFor("no-such-account"));
        Assert.Empty(RemoteControlSessions.CommandsFor("no-such-account", "nobody"));
    }

    // --- the last few branches ---------------------------------------------------

    // A session the registry knows and Buddy's own status files do not yet —
    // its hook has fired for the registry but not for the scan. Matching on pid
    // as well costs nothing and covers the gap.
    [Fact]
    public async Task ASessionWhoseIdBuddyDoesNotKnowIsStillFoundByItsPid()
    {
        _path = Path.Combine(_dir, "session.jsonl");
        File.WriteAllText(_path, UserRow("u0", "hello") + "\n");

        // The registry's session id and Buddy's disagree; only the pid lines up.
        _agents.Add(new AgentRoster.Entry(Name, "an-id-buddy-has-never-seen", 4242));
        _sessions.Add(("a-different-id", new SessionStatus
        {
            Title = Name,
            Cwd = _dir,
            Source = SessionSource.ClaudeCode,
            TranscriptPath = _path,
            TmuxPane = "%1",
            SessionPid = 4242
        }));

        await Handshake();

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Available,
            _client.StateFor(Name).Availability);
    }

    // A watch nobody renews lapses, so a Buddy that quit without saying goodbye
    // stops being served rather than being pushed to forever.
    [Fact]
    public async Task AWatchThatIsNeverRenewedLapses()
    {
        AddSession(rows: 20);

        var now = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        _server.Now = () => now;

        await Handshake();
        await _client.OpenAsync(Name);

        Assert.True(_server.Busy);

        now = now.AddSeconds(MirrorProtocol.WatchTtlSeconds + 1);
        await _server.TickAsync();

        Assert.False(_server.Busy);
    }

    // The transcript going away under a live watch. The next tick tries again
    // rather than the whole relay falling over.
    [Fact]
    public async Task ATranscriptDeletedUnderALiveWatchDoesNotTakeTheRelayWithIt()
    {
        AddSession(rows: 20);
        await Handshake();
        await _client.OpenAsync(Name);

        File.Delete(_path);

        await _server.TickAsync();

        Assert.Empty(_deltas);
        Assert.True(_server.Busy);
    }

    // A relay that refuses mid-transfer. Stopping is right: the client times the
    // transfer out and can ask again, and pushing the rest would spend turns
    // filling in something nobody can complete.
    [Fact]
    public async Task ARelayThatStopsAcceptingMidTransferDoesNotKeepPushing()
    {
        // Big enough to need several frames, so there is a middle to stop in.
        AddSession(rows: 4000);

        await Handshake();

        // Failure here arrives as a timeout — the far side simply stops
        // sending — so this is the other test that shortens it.
        _client.TimeoutOverrideForTests = TimeSpan.FromMilliseconds(250);

        _refuseAfter = 2;
        _windows.Clear();

        Assert.False(await _client.OpenAsync(Name));
        Assert.Empty(_windows);
    }

    // The real wiring, constructed. Nothing here runs a process — the point is
    // that every delegate is present and the two that read settings read the
    // right ones, since a null in this record would be a crash the first time
    // another machine asked anything.
    [Fact]
    public void TheRealSeamsAreWiredToTheRealThings()
    {
        var seams = RemoteMirrorServer.RealSeams(
            ".claude-board",
            (_, _) => Task.FromResult(true),
            () => Array.Empty<(string, SessionStatus)>());

        Assert.NotNull(seams.SendFrame);
        Assert.NotNull(seams.LocalSessions);
        Assert.NotNull(seams.Agents);
        Assert.NotNull(seams.TypeInto);

        // The setting a local panel obeys is the one a remote request obeys.
        var was = ClaudeBuddySettings.ClaudeCodeReplyEnabled;
        try
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = false;
            Assert.False(seams.ReplyEnabled(SessionSource.ClaudeCode));

            ClaudeBuddySettings.ClaudeCodeReplyEnabled = true;
            Assert.True(seams.ReplyEnabled(SessionSource.ClaudeCode));
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = was;
        }

        // A session with no pane cannot be typed into, which is answered without
        // running tmux at all.
        Assert.False(seams.CanType(new SessionStatus { Source = SessionSource.ClaudeCode }));
    }

    // --- wiring ----------------------------------------------------------------------

    private readonly List<(string Name, string Text)> _typed = new();
    private bool _typeSucceeds = true;
    private bool _typeThrows;

    private static IReadOnlyList<BridgeProtocol.RemoteAgent> Peers =>
        new[]
        {
            new BridgeProtocol.RemoteAgent(FarRelay, "aa11bb", "Remote Control", "idle"),
            new BridgeProtocol.RemoteAgent(Name, "94f106", "Remote Control", "idle")
        };

    private Task Handshake() => _client.DiscoverAsync(Peers, new[] { Name });

    private void AddSession(string? transcriptPath = null, int rows = 2)
    {
        if (transcriptPath is null)
        {
            _path = Path.Combine(_dir, "session.jsonl");
            File.WriteAllText(_path, string.Join("\n",
                Enumerable.Range(0, rows).Select(i => UserRow($"u{i}", $"line {i}"))) + "\n");
            transcriptPath = _path;
        }

        var sessionId = Guid.NewGuid().ToString();
        _agents.Add(new AgentRoster.Entry(Name, sessionId, 4242));
        _sessions.Add((sessionId, new SessionStatus
        {
            Title = Name,
            Cwd = _dir,
            Source = SessionSource.ClaudeCode,
            TranscriptPath = transcriptPath,
            TmuxPane = "%1",
            SessionPid = 4242
        }));
    }

    private void Build()
    {
        _server = new RemoteMirrorServer(Account, new RemoteMirrorServer.Seams(
            SendToClientAsync,
            () => _sessions,
            () => _agents,
            _ => true,
            _ => true,
            (status, text) =>
            {
                if (_typeThrows) throw new InvalidOperationException("tmux went away");

                if (_typeSucceeds) _typed.Add((status.Title, text));
                return Task.FromResult(_typeSucceeds);
            }));

        _client = new RemoteMirrorClient(Account, new RemoteMirrorClient.Seams(SendToServerAsync))
        {
            // The real waits are minutes long, which is right for a relay whose
            // model may be mid-turn and far too long for a test.
            //
            // Generous rather than tight, and that is a correction: this was
            // 250ms, which is plenty for a loopback reply until the machine is
            // busy — and then a test that expected an *answer* saw a timeout
            // instead and failed once in a run. A timeout is the slowest thing
            // in this file, so the only test that should ever hit one is the one
            // deliberately dropping replies, which shortens this for itself.
            TimeoutOverrideForTests = TimeSpan.FromSeconds(10)
        };

        _client.Delivered += rows =>
        {
            if (rows.Mode == RemoteMirrorClient.MirrorDelivery.Window) _windows.Add(rows);
            else _deltas.Add(rows);
        };

        _client.Failed += (name, why) => _failures.Add((name, why));
    }

    private static MirrorProtocol.MirrorFrame Frame(
        string type, string id, params (string Key, string Value)[] fields) =>
        MirrorProtocol.TryParseFrame(MirrorProtocol.BuildFrame(
            type, id, fields.ToDictionary(f => f.Key, f => f.Value)))!;

    private async Task<bool> SendToServerAsync(string peer, string line)
    {
        var frame = MirrorProtocol.TryParseFrame(line);
        if (frame is null) return false;

        await _server.HandleAsync(NearRelay, frame);
        return true;
    }

    private async Task<bool> SendToClientAsync(string peer, string line)
    {
        _toClient.Add(line);
        if (_dropEverything) return true;

        // A relay that stops accepting part-way through a transfer.
        if (_refuseAfter >= 0 && ++_sent > _refuseAfter) return false;

        var frame = MirrorProtocol.TryParseFrame(line);
        if (frame is null) return false;

        await _client.OnFrameAsync(FarRelay, frame);
        return true;
    }

    private static string UserRow(string uuid, string text) =>
        $"{{\"type\":\"user\",\"uuid\":\"{uuid}\",\"message\":{{\"role\":\"user\",\"content\":\"{text}\"}}}}";
}
