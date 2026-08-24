using System.Text;
using Xunit;

namespace ClaudeBuddy.Tests;

// Drives a whole mirror — client, protocol and server — over real transcript
// files on disk, with the relay replaced by a delegate that hands one side's
// frames straight to the other.
//
// The seam being cut is deliberately narrow. Everything below is the real
// RemoteMirrorClient talking to the real RemoteMirrorServer through the real
// MirrorProtocol, reading real FileStreams; the only thing faked is the pair of
// `SendFrame` delegates, which in production paste a line into a tmux pane and
// wait for a model to relay it. That is the one part a test cannot have —
// it needs two machines, a live Claude Code session on each, and somebody's
// quota — and it is also the part that carries no logic. Everything it *would*
// have carried is asserted here.
//
// What these prove that the unit tests cannot: that the bytes coming out the far
// end are the same bytes that were on the far disk. A hash agreeing with itself
// is easy; a transcript arriving intact through chunking, gzip, base64, framing,
// reassembly and window alignment is the actual claim this feature makes.
public class MirrorRoundTripTests : IDisposable
{
    private readonly string _dir;

    public MirrorRoundTripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-mirror-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // --- the claim ------------------------------------------------------------

    // The whole feature in one test: what the panel is handed is exactly the
    // rows on the other machine's disk, byte for byte.
    [Fact]
    public async Task ATailArrivesAsTheSameBytesThatAreOnTheFarDisk()
    {
        var rows = Conversation(60);
        var path = WriteTranscript("session.jsonl", rows);

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        Assert.True(await harness.HandshakeAsync("job-hunter"));
        Assert.True(await harness.Client.OpenAsync("job-hunter"));

        var delivered = Assert.Single(harness.Windows);

        // Not "looks right" — identical to what this machine's own parser
        // produces from those same bytes. The far Buddy runs the same
        // ChatTranscript, so equality here is the whole verbatim claim: the
        // panel is handed exactly the turns a local panel would have built.
        Assert.Equal(
            MirrorProtocol.TurnsFrom(rows, MirrorProtocol.CliClaudeCode),
            delivered.Turns);
    }

    // A conversation big enough to need many frames, which is where chunking,
    // ordering and the whole-payload hash all have to hold at once.
    [Fact]
    public async Task ALongTranscriptSurvivesBeingCutIntoManyFrames()
    {
        // Large, because turns are a fraction of the rows they came from and
        // it now takes a great deal of conversation to need a second frame —
        // which is the entire point of shipping turns, and is measured in
        // MirrorProtocol's note.
        var rows = Conversation(6000);
        var path = WriteTranscript("long.jsonl", rows);

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        Assert.True(await harness.Client.OpenAsync("job-hunter"));

        var delivered = Assert.Single(harness.Windows);
        var all = MirrorProtocol.TurnsFrom(rows, MirrorProtocol.CliClaudeCode);

        Assert.True(harness.ChunkFrames > 1, "this transcript should have needed more than one frame");

        // A tail, so the end of the file rather than all of it — the same
        // 512KB window a local panel opens on. What matters is that it is an
        // exact, unbroken suffix: no row dropped at a frame boundary, none
        // duplicated, none reworded.
        Assert.NotEmpty(delivered.Turns);
        Assert.True(delivered.Turns.Count < all.Count, "this file should be bigger than one window");
        Assert.Equal(all.Skip(all.Count - delivered.Turns.Count).ToList(), delivered.Turns);
    }

    // The bulk of a transcript is tool results and file-history snapshots that
    // no panel ever shows. Sending them would be paying a model to paste
    // megabytes into the void, so they are dropped on the far side — and the
    // rows that *are* shown must be untouched by that.
    [Fact]
    public async Task TheRowsNobodySeesNeverCrossTheWire()
    {
        var rows = new List<string>
        {
            Row("user", "u1", "a question"),
            "{\"type\":\"file-history-snapshot\",\"uuid\":\"h1\",\"blob\":\"" + new string('x', 200_000) + "\"}",
            Row("assistant", "a1", "an answer")
        };

        var path = WriteTranscript("noisy.jsonl", rows);

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        await harness.Client.OpenAsync("job-hunter");

        var delivered = Assert.Single(harness.Windows);

        Assert.Equal(2, delivered.Turns.Count);
        Assert.DoesNotContain(delivered.Turns, t => t.Text.Contains("file-history-snapshot"));
        Assert.Contains("a question", delivered.Turns[0].Text);
        Assert.Contains("an answer", delivered.Turns[1].Text);

        // And the snapshot's 200KB did not become frames.
        Assert.True(harness.ChunkFrames <= 2, $"the big row was relayed anyway ({harness.ChunkFrames} frames)");
    }

    // --- keeping up ------------------------------------------------------------

    [Fact]
    public async Task WhatIsAppendedAfterwardsArrivesAsAnUpdate()
    {
        var rows = Conversation(10);
        var path = WriteTranscript("live.jsonl", rows);

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        await harness.Client.OpenAsync("job-hunter");

        Assert.Single(harness.Windows);

        File.AppendAllText(path, Row("assistant", "later", "said after the panel opened") + "\n");
        await harness.Server.TickAsync();

        var delta = Assert.Single(harness.Deltas);
        Assert.Contains("said after the panel opened", Assert.Single(delta.Turns).Text);
    }

    // An update that is nothing but tool results moves the offset without
    // sending anything, which is silence rather than a gap — and specifically
    // must not deliver an empty update that the panel would render as a blank
    // turn.
    [Fact]
    public async Task AnUpdateWithNothingWorthShowingSaysNothing()
    {
        var path = WriteTranscript("quiet.jsonl", Conversation(4));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        await harness.Client.OpenAsync("job-hunter");

        File.AppendAllText(path, "{\"type\":\"file-history-snapshot\",\"uuid\":\"h9\"}\n");
        await harness.Server.TickAsync();

        Assert.Empty(harness.Deltas);
    }

    // /clear starts a new transcript. A client holding a byte offset into the
    // old one would be asking for a position that now means something else, so
    // the generation counter tells it to throw away what it has.
    [Fact]
    public async Task ATranscriptReplacedUnderneathBumpsItsGenerationAndReAnchors()
    {
        var path = WriteTranscript("cleared.jsonl", Conversation(40));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        await harness.Client.OpenAsync("job-hunter");

        var first = Assert.Single(harness.Windows);

        // Shorter than where the watch was reading: the /clear case.
        File.WriteAllText(path, Row("user", "fresh", "starting over") + "\n");
        await harness.Server.TickAsync();

        // The client re-anchors by itself: a delta whose generation does not
        // match what the feed is holding means the file underneath changed, so
        // it re-reads the tail rather than appending onto a file that no longer
        // exists. Nothing here asks it to.
        Assert.Equal(2, harness.Windows.Count);

        var second = harness.Windows[^1];
        Assert.Contains("starting over", Assert.Single(second.Turns).Text);
        Assert.NotEqual(first.Gen, second.Gen);
    }

    // --- paging back ------------------------------------------------------------

    [Fact]
    public async Task ScrollingBackReadsTheOlderBytesAndStopsAtTheStart()
    {
        // Comfortably more than one initial window, so there is genuinely
        // something behind the tail.
        var rows = Rows(MirrorProtocol.InitialBytes * 2);
        var path = WriteTranscript("deep.jsonl", rows);

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        await harness.Client.OpenAsync("job-hunter");

        Assert.True(harness.Client.HasMore("job-hunter"), "a file this size should have a backlog");

        var seen = new List<MirrorProtocol.MirrorTurn>(Assert.Single(harness.Windows).Turns);

        for (var page = 0; page < 10 && harness.Client.HasMore("job-hunter"); page++)
        {
            var older = await harness.Client.LoadOlderAsync("job-hunter");
            if (older is null) break;

            seen.InsertRange(0, older);
        }

        Assert.False(harness.Client.HasMore("job-hunter"));

        // Every row in the file, in order, with none read twice — which is the
        // thing window alignment exists to guarantee.
        Assert.Equal(MirrorProtocol.TurnsFrom(rows, MirrorProtocol.CliClaudeCode), seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    // A window that lands entirely inside one row — which a megabyte-long
    // file-history snapshot manages — reports where it started rather than where
    // it ended, so paging steps over it instead of re-reading the same megabyte
    // for ever. Ported from LocalCliChatSession, where the same rule lives.
    [Fact]
    public void AWindowInsideOneEnormousRowStepsOverItRatherThanStalling()
    {
        var path = Path.Combine(_dir, "giant.jsonl");

        var giant = "{\"type\":\"file-history-snapshot\",\"blob\":\"" + new string('x', 3_000_000) + "\"}";
        File.WriteAllText(path,
            Row("user", "first", "before the giant") + "\n" + giant + "\n" + Row("user", "last", "after") + "\n");

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // A window wholly inside the giant row: no newline anywhere in it.
        var read = RemoteMirrorServer.ReadRange(fs, 1_000_000, 2_000_000, alignStart: true);

        Assert.Empty(read.Lines);
        Assert.Equal(1_000_000, read.From);
    }

    [Fact]
    public void AWindowStartingMidRowDropsThePartialLineAndSaysWhereItActuallyBegan()
    {
        var path = Path.Combine(_dir, "aligned.jsonl");

        var first = Row("user", "u1", "first row");
        var second = Row("user", "u2", "second row");
        File.WriteAllText(path, first + "\n" + second + "\n");

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var mid = first.Length / 2;
        var read = RemoteMirrorServer.ReadRange(fs, mid, fs.Length, alignStart: true);

        Assert.Equal(second, Assert.Single(read.Lines));
        Assert.Equal(first.Length + 1, read.From);
    }

    [Fact]
    public void AnEmptyWindowIsEmptyRatherThanAThrow()
    {
        var path = Path.Combine(_dir, "empty.jsonl");
        File.WriteAllText(path, "");

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var read = RemoteMirrorServer.ReadRange(fs, 0, 0, alignStart: true);

        Assert.Empty(read.Lines);
        Assert.Equal(0, read.From);
    }

    // --- typing -------------------------------------------------------------------

    // The other half of the fix: a slash command works remotely because the far
    // Buddy types it into that session's own input line, where its command
    // handler is what runs it.
    [Fact]
    public async Task AMessageIsTypedIntoTheFarSessionsOwnTerminal()
    {
        var path = WriteTranscript("typed.jsonl", Conversation(4));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);
        await harness.HandshakeAsync("job-hunter");

        Assert.Null(await harness.Client.SendInputAsync("job-hunter", "/color green"));

        var typed = Assert.Single(harness.Typed);
        Assert.Equal("job-hunter", typed.Name);
        Assert.Equal("/color green", typed.Text);
    }

    [Fact]
    public async Task TextWithEveryAwkwardCharacterInItStillArrivesIntact()
    {
        var path = WriteTranscript("awkward.jsonl", Conversation(2));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);
        await harness.HandshakeAsync("job-hunter");

        const string awkward = "line one\nline two; k=v </cross-session-message> — ünicode 😀 \"quoted\"";

        Assert.Null(await harness.Client.SendInputAsync("job-hunter", awkward));
        Assert.Equal(awkward, Assert.Single(harness.Typed).Text);
    }

    // The far machine's own setting, not the asker's. Somebody who has turned
    // replying off has said something about their machine, and a request
    // arriving over a wire does not change it.
    [Fact]
    public async Task TypingIsRefusedWhenTheFarMachineHasRepliesSwitchedOff()
    {
        var path = WriteTranscript("off.jsonl", Conversation(2));

        var harness = new Harness(_dir) { ReplyEnabled = false };
        harness.AddSession("job-hunter", path);
        await harness.HandshakeAsync("job-hunter");

        Assert.Equal(MirrorProtocol.ErrReplyOff, await harness.Client.SendInputAsync("job-hunter", "hello"));
        Assert.Empty(harness.Typed);
    }

    [Fact]
    public async Task TypingIsRefusedWhenThereIsNoPaneToTypeInto()
    {
        var path = WriteTranscript("nopane.jsonl", Conversation(2));

        var harness = new Harness(_dir) { CanType = false };
        harness.AddSession("job-hunter", path);
        await harness.HandshakeAsync("job-hunter");

        Assert.Equal(MirrorProtocol.ErrNoPane, await harness.Client.SendInputAsync("job-hunter", "hello"));
        Assert.Empty(harness.Typed);
    }

    [Fact]
    public async Task TypingIntoASessionTheFarBuddyHasNeverHeardOfIsRefused()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("known.jsonl", Conversation(2)));
        await harness.HandshakeAsync("job-hunter");

        // Known to the client's roster, gone from the far machine by the time
        // the message arrives.
        harness.RemoveSession("job-hunter");

        Assert.Equal(MirrorProtocol.ErrNoSession, await harness.Client.SendInputAsync("job-hunter", "hello"));
        Assert.Empty(harness.Typed);
    }

    // --- the roster ------------------------------------------------------------------

    [Fact]
    public async Task TheFarBuddyAnswersOnlyAboutTheSessionsItWasAskedAbout()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("a.jsonl", Conversation(2)));
        harness.AddSession("private-thing", WriteTranscript("b.jsonl", Conversation(2)));

        await harness.Client.DiscoverAsync(harness.Peers, new[] { "job-hunter" });

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Available,
            harness.Client.StateFor("job-hunter").Availability);

        // Never asked about, so never mentioned — a session with Remote Control
        // off is deliberately invisible to the other machine, and a roster is no
        // place to undo that.
        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unknown,
            harness.Client.StateFor("private-thing").Availability);
    }

    [Fact]
    public async Task ASessionTheFarBuddyCannotReadIsSettledAsNoLiveView()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", Path.Combine(_dir, "does-not-exist.jsonl"));

        await harness.Client.DiscoverAsync(harness.Peers, new[] { "job-hunter" });

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unavailable,
            harness.Client.StateFor("job-hunter").Availability);
    }

    // No Buddy over there at all — the ordinary case for a bare peer or a
    // session on a phone. Settled rather than left unknown, so the panel can say
    // "no live view" instead of sitting on "checking…" for ever.
    [Fact]
    public async Task WithNoBuddyOnTheOtherMachineEveryNameIsSettledAsNoLiveView()
    {
        var harness = new Harness(_dir);

        var justAPeer = new[]
        {
            new BridgeProtocol.RemoteAgent("job-hunter", "94f106", "Remote Control", "idle")
        };

        await harness.Client.DiscoverAsync(justAPeer, new[] { "job-hunter" });

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unavailable,
            harness.Client.StateFor("job-hunter").Availability);
    }

    // A relay left registered by a Buddy that has since quit reads "offline".
    // Asking it would be asking nothing.
    [Fact]
    public async Task AnOfflineRelayIsNotMistakenForALiveBuddy()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("c.jsonl", Conversation(2)));

        var offline = new[]
        {
            new BridgeProtocol.RemoteAgent(Harness.FarRelay, "aa11bb", "Remote Control", "offline"),
            new BridgeProtocol.RemoteAgent("job-hunter", "94f106", "Remote Control", "idle")
        };

        await harness.Client.DiscoverAsync(offline, new[] { "job-hunter" });

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unavailable,
            harness.Client.StateFor("job-hunter").Availability);
    }

    // The far Buddy reads the command list off its own disk, which is how a
    // built-in becomes offerable again: it genuinely runs now, because the send
    // is typed into that CLI's input line.
    [Fact]
    public async Task TheRosterCarriesWhatTheFarSessionCanActuallyRun()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("d.jsonl", Conversation(2)));

        await harness.HandshakeAsync("job-hunter");

        var entry = harness.Client.StateFor("job-hunter").Entry;

        Assert.NotNull(entry);
        Assert.NotNull(entry!.Commands);
        Assert.Contains("/color", entry.Commands!);
        Assert.True(entry.HasTranscript);
        Assert.True(entry.HasPane);
    }

    // --- refusing what did not survive --------------------------------------------

    // The guarantee, end to end: a courier that alters a frame in flight
    // produces an error, never altered text on screen.
    [Fact]
    public async Task AFrameMangledInFlightFailsTheMirrorRatherThanShowingSomethingElse()
    {
        var path = WriteTranscript("tampered.jsonl", Conversation(20));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);
        await harness.HandshakeAsync("job-hunter");

        // A courier that "tidies up" every payload it carries. Valid base64,
        // valid frame, different bytes — the most plausible-looking corruption
        // there is, and the one a hash exists to catch.
        harness.MangleChunks = true;

        Assert.False(await harness.Client.OpenAsync("job-hunter"));

        Assert.Empty(harness.Windows);
        Assert.NotEmpty(harness.Failures);
        Assert.Contains("integrity", Assert.Single(harness.Failures).Why);
    }

    // A single bad piece is asked for again rather than costing the whole
    // transfer — on a long transcript that is one round trip instead of thirty.
    [Fact]
    public async Task OneBadPieceIsAskedForAgainAndTheTransferSurvives()
    {
        var path = WriteTranscript("resend.jsonl", Conversation(1200));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);
        await harness.HandshakeAsync("job-hunter");

        // Exactly one piece is broken, once.
        harness.MangleOnce = true;

        Assert.True(await harness.Client.OpenAsync("job-hunter"));

        Assert.True(harness.Resends >= 1, "the broken piece should have been asked for again");

        var all = MirrorProtocol.TurnsFrom(File.ReadAllLines(path), MirrorProtocol.CliClaudeCode);
        var delivered = Assert.Single(harness.Windows).Turns;

        // Recovered whole: the piece that was mangled is in here, correct, and
        // in the right place.
        Assert.NotEmpty(delivered);
        Assert.Equal(all.Skip(all.Count - delivered.Count).ToList(), delivered);
    }

    // Frames are addressed between Buddies, and a request arriving from anything
    // that is not one is not served. A weak check on its own — the account is
    // shared, so anything on it could wear the name — and named as such in the
    // PR rather than presented as a boundary.
    [Fact]
    public async Task ARequestFromSomethingThatIsNotABuddyRelayIsNotServed()
    {
        var path = WriteTranscript("guarded.jsonl", Conversation(4));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        var frame = MirrorProtocol.TryParseFrame(MirrorProtocol.BuildFrame(
            MirrorProtocol.Fetch, "abcd1234",
            new Dictionary<string, string>
            {
                ["n"] = MirrorProtocol.Encode("job-hunter"),
                ["w"] = "tail"
            }))!;

        await harness.Server.HandleAsync("job-hunter", frame);

        Assert.Empty(harness.ToClient);
    }

    [Theory]
    [InlineData("claude-buddy-rc--claude-mini")]
    [InlineData("CLAUDE-BUDDY-RC--claude-mini")]
    public void ARelayIsRecognisedByItsPrefix(string name) =>
        Assert.True(RemoteMirrorServer.IsRelayName(name));

    [Theory]
    [InlineData("job-hunter")]
    [InlineData("claude-buddy")]
    [InlineData("")]
    public void AnythingElseIsNotARelay(string name) =>
        Assert.False(RemoteMirrorServer.IsRelayName(name));

    // Two machines on one account used to build the identical relay name, and
    // that name is what SendMessage addresses.
    [Fact]
    public void AMachineTagIsTmuxSafeAndNeverEmpty()
    {
        var tag = RemoteControlBridge.MachineTag();

        Assert.NotEmpty(tag);
        Assert.DoesNotContain('.', tag);
        Assert.DoesNotContain(':', tag);
        Assert.True(tag.Length <= 20);
        Assert.Equal(tag, RemoteControlBridge.MachineTag());
    }

    // --- fixtures --------------------------------------------------------------------

    private string WriteTranscript(string name, IEnumerable<string> rows)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, string.Join("\n", rows) + "\n");
        return path;
    }

    private static List<string> Conversation(int turns)
    {
        var rows = new List<string>();

        for (var i = 0; i < turns; i++)
        {
            rows.Add(Row("user", $"u{i}", $"question number {i}"));

            // A tool result between every pair, because that is what a real
            // transcript looks like and it is most of the bytes.
            rows.Add("{\"type\":\"file-history-snapshot\",\"uuid\":\"h" + i + "\",\"blob\":\""
                     + new string('x', 400) + "\"}");

            rows.Add(Row("assistant", $"a{i}", $"answer number {i}"));
        }

        return rows;
    }

    // Enough rows to exceed a given number of bytes.
    private static List<string> Rows(int atLeastBytes)
    {
        var rows = new List<string>();
        var bytes = 0;

        for (var i = 0; bytes < atLeastBytes; i++)
        {
            var row = Row(i % 2 == 0 ? "user" : "assistant", $"r{i}", $"row {i} " + new string('y', 200));
            rows.Add(row);
            bytes += row.Length + 1;
        }

        return rows;
    }

    private static string Row(string type, string uuid, string text) =>
        type == "user"
            ? $"{{\"type\":\"user\",\"uuid\":\"{uuid}\",\"message\":{{\"role\":\"user\",\"content\":\"{text}\"}}}}"
            : $"{{\"type\":\"assistant\",\"uuid\":\"{uuid}\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"{text}\"}}]}}}}";

    // --- the loopback ------------------------------------------------------------------

    // Two Buddies wired directly to each other. Everything is real except the
    // pair of delegates that would otherwise paste a line into a tmux pane and
    // wait for a model to carry it.
    private sealed class Harness
    {
        public const string FarRelay = "claude-buddy-rc--claude-mini";
        public const string NearRelay = "claude-buddy-rc--claude-laptop";

        public RemoteMirrorClient Client { get; }
        public RemoteMirrorServer Server { get; }

        public List<RemoteMirrorClient.MirrorRows> Windows { get; } = new();
        public List<RemoteMirrorClient.MirrorRows> Deltas { get; } = new();
        public List<(string Name, string Why)> Failures { get; } = new();
        public List<(string Name, string Text)> Typed { get; } = new();
        public List<string> ToClient { get; } = new();

        public int ChunkFrames { get; private set; }
        public int Resends { get; private set; }

        public bool ReplyEnabled { get; init; } = true;
        public bool CanType { get; init; } = true;

        // A courier that rewrites every payload it carries.
        public bool MangleChunks { get; set; }

        // ...or just the one, once.
        public bool MangleOnce { get; set; }

        private bool _mangledAlready;

        private readonly List<(string SessionId, SessionStatus Status)> _sessions = new();
        private readonly List<AgentRoster.Entry> _agents = new();
        private readonly string _dir;

        public Harness(string dir)
        {
            _dir = dir;

            Server = new RemoteMirrorServer("acct", new RemoteMirrorServer.Seams(
                SendToClientAsync,
                () => _sessions,
                () => _agents,
                _ => ReplyEnabled,
                _ => CanType,
                (status, text) =>
                {
                    Typed.Add((NameOf(status), text));
                    return Task.FromResult(true);
                }));

            Client = new RemoteMirrorClient("acct", new RemoteMirrorClient.Seams(SendToServerAsync));

            Client.Delivered += rows =>
            {
                if (rows.Mode == RemoteMirrorClient.MirrorDelivery.Window) Windows.Add(rows);
                else Deltas.Add(rows);
            };

            Client.Failed += (name, why) => Failures.Add((name, why));
        }

        public IReadOnlyList<BridgeProtocol.RemoteAgent> Peers =>
            new[]
            {
                new BridgeProtocol.RemoteAgent(FarRelay, "aa11bb", "Remote Control", "idle"),
                new BridgeProtocol.RemoteAgent("job-hunter", "94f106", "Remote Control", "idle")
            };

        public void AddSession(string name, string transcriptPath)
        {
            var sessionId = Guid.NewGuid().ToString();

            _agents.Add(new AgentRoster.Entry(name, sessionId, 1000 + _agents.Count));

            _sessions.Add((sessionId, new SessionStatus
            {
                Title = name,
                Cwd = _dir,
                Source = SessionSource.ClaudeCode,
                TranscriptPath = transcriptPath,
                TmuxPane = "%1",
                SessionPid = 1000 + _sessions.Count,
                Color = "green"
            }));
        }

        public void RemoveSession(string name)
        {
            var at = _agents.FindIndex(a => a.Name == name);
            if (at < 0) return;

            var sessionId = _agents[at].SessionId;
            _agents.RemoveAt(at);
            _sessions.RemoveAll(s => s.SessionId == sessionId);
        }

        public Task<bool> HandshakeAsync(params string[] names) =>
            Client.DiscoverAsync(Peers, names)
                .ContinueWith(_ => Client.StateFor(names[0]).Availability
                                   == RemoteMirrorClient.MirrorAvailability.Available);

        private string NameOf(SessionStatus status) => status.Title;

        // The near Buddy's frame reaching the far one.
        private async Task<bool> SendToServerAsync(string peer, string line)
        {
            Assert.Equal(FarRelay, peer);

            var frame = MirrorProtocol.TryParseFrame(line);
            if (frame is null) return false;

            if (frame.Type == MirrorProtocol.Resend) Resends++;

            await Server.HandleAsync(NearRelay, frame);
            return true;
        }

        // ...and back the other way.
        private async Task<bool> SendToClientAsync(string peer, string line)
        {
            Assert.Equal(NearRelay, peer);

            ToClient.Add(line);

            var frame = MirrorProtocol.TryParseFrame(line);
            if (frame is null) return false;

            if (frame.Type == MirrorProtocol.Chunk)
            {
                ChunkFrames++;

                if (MangleChunks || (MangleOnce && !_mangledAlready))
                {
                    _mangledAlready = true;
                    frame = Mangle(line) ?? frame;
                }
            }

            await Client.OnFrameAsync(FarRelay, frame);
            return true;
        }

        // Swaps the payload for different bytes while leaving the digest alone,
        // which is precisely what a model rewording something it was asked to
        // relay would look like on the wire.
        private static MirrorProtocol.MirrorFrame? Mangle(string line)
        {
            var start = line.IndexOf(";p=", StringComparison.Ordinal);
            var end = line.IndexOf(";h=", StringComparison.Ordinal);
            if (start < 0 || end < 0) return null;

            var swapped = Convert.ToBase64String(
                Encoding.UTF8.GetBytes("a tidier version of whatever that said"));

            return MirrorProtocol.TryParseFrame(line[..(start + 3)] + swapped + line[end..]);
        }
    }
}
