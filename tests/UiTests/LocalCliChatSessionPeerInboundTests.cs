using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-267: a session running without permission prompts holds every peer
// message — this app's included — until someone approves it in that session,
// and the socket accepts the bytes either way. These drive a real
// LocalCliChatSession over a real transcript on disk, inside a scratch config
// root, with the messenger and the settings.json read/write handed in as fakes:
// what is under test is which session gets an offer, which file it names, what
// the note says after a send, and what "Send it again" does with the bubble.
//
// The config root is a temp directory laid out the way Claude Code lays out
// ~/.claude (projects/<slug>/<id>.jsonl), so ClaudeConfigRoots.RootOfTranscript
// finds it for real. Nothing here reads or writes the real ~/.claude.
//
// In Settings because "Allow replying to sessions" is a process-wide setting
// every one of these flips.
[Collection("Settings")]
public class LocalCliChatSessionPeerInboundTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-peerinbound-" + Guid.NewGuid().ToString("N"));

    private readonly bool _replyBefore = OrbweaverSettings.ClaudeCodeReplyEnabled;

    public LocalCliChatSessionPeerInboundTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "projects", "-work-repo"));
        OrbweaverSettings.ClaudeCodeReplyEnabled = true;
    }

    public void Dispose()
    {
        OrbweaverSettings.ClaudeCodeReplyEnabled = _replyBefore;
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string SettingsPath => Path.Combine(_root, "settings.json");

    // --- fixtures -----------------------------------------------------------

    // The real row shape, measured on three transcripts: `type` first, then the
    // mode, then the session id.
    private static string Mode(string mode) =>
        "{\"type\":\"permission-mode\",\"permissionMode\":\"" + mode + "\",\"sessionId\":\"s1\"}";

    private static string User(string uuid, string text) =>
        "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"timestamp\":\"2026-10-20T12:00:00Z\","
        + "\"message\":{\"role\":\"user\",\"content\":" + System.Text.Json.JsonSerializer.Serialize(text) + "}}";

    private string Transcript(params string[] rows) => TranscriptAt(
        Path.Combine(_root, "projects", "-work-repo", Guid.NewGuid().ToString("N") + ".jsonl"), rows);

    private static string TranscriptAt(string path, params string[] rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', rows) + "\n");
        return path;
    }

    private static SessionRegistry.Entry Entry(string? configRoot) =>
        SessionRegistry.Parse(
            """{"pid":4242,"sessionId":"s1","messagingSocketPath":"/tmp/a.sock","peerProtocol":1,"status":"idle"}""",
            keyPath: null)!.Value with { ConfigRoot = configRoot };

    // A messenger whose socket write can be flipped between attempts, so a
    // resend can be refused once and accepted the next time.
    private sealed class Wire
    {
        public bool Accepts { get; set; } = true;
        public int Writes { get; private set; }

        public SessionMessenger Messenger(SessionRegistry.Entry entry) => new(new SessionMessenger.Seams(
            Registry: () => new[] { entry },
            PidAlive: _ => true,
            ReadKey: _ => null,
            Write: (_, _, _) =>
            {
                Writes++;
                return Task.FromResult(Accepts);
            }));
    }

    private sealed class SettingsFake
    {
        public int Reads { get; set; }
        public List<(string Root, bool Accept)> Applies { get; } = new();
        public PeerInboundWrite Result { get; set; } = new(PeerInboundWriteKind.Written, null);
        public bool WriteAccept { get; set; } = true;

        // Replaces the default reader, for the cases that need ReadText's own
        // contract: null for a file that exists but cannot be read.
        public Func<string, string?>? Reader { get; set; }
    }

    private LocalCliChatSession Session(
        string transcript, SettingsFake settings, Wire? wire = null,
        SessionRegistry.Entry? registry = null, bool noRegistry = false,
        string? entrypoint = null)
    {
        var entry = registry ?? Entry(configRoot: null);
        wire ??= new Wire();

        return new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = transcript,
            Shape = LocalSessionShape.Background,
            Presence = OrbPresence.NeedsInput,
            State = "idle",
            Title = "job-hunter",
            Entrypoint = entrypoint ?? "",
        },
        messenger: wire.Messenger(entry),
        findRegistry: id => !noRegistry && id == entry.SessionId ? entry : null,
        readSettings: root =>
        {
            settings.Reads++;
            if (settings.Reader is { } reader) return reader(root);
            var path = Path.Combine(root, "settings.json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        },
        applySetting: (root, accept) =>
        {
            settings.Applies.Add((root, accept));
            if (settings.WriteAccept)
                File.WriteAllText(Path.Combine(root, "settings.json"), "{\n  \"crossSessionInbound\": \"accept\"\n}\n");
            return settings.Result;
        });
    }

    private static void PumpUntil(Func<bool> done, string what)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), "timed out waiting for " + what);
    }

    private static LocalCliChatSession Loaded(LocalCliChatSession session)
    {
        session.Start();
        PumpUntil(() => session.State == RemoteChatState.Connected, "the opening read");
        return session;
    }

    private static void Invoke(LocalCliChatSession session, string method, params object?[] args) =>
        typeof(LocalCliChatSession)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(session, args);

    private static string Wrapped(string body) =>
        "<cross-session-message from=\"Orbweaver on mini\" from-mode=\"prompting\">\n" + body + "\n</cross-session-message>";

    // --- the offer, per gate -----------------------------------------------

    // The case the ticket is about: the transcript says bypassPermissions and the
    // root's settings.json has no crossSessionInbound. Raised once the opening
    // read lands, since that is when the mode is first known.
    [AvaloniaFact]
    public void ABypassSessionWithoutTheSettingIsOfferedTheFix()
    {
        var settings = new SettingsFake();
        var session = Session(Transcript(Mode("bypassPermissions"), User("u1", "hello")), settings);
        var raised = 0;
        session.InboundChanged += () => raised++;

        Loaded(session);

        var offer = Assert.IsType<PeerInboundOffer>(session.InboundOffer);
        Assert.Equal(PeerInbound.OfferText("job-hunter"), offer.Explanation);
        Assert.Equal(SettingsPath, offer.SettingsPath);
        Assert.True(offer.CanEdit);
        Assert.Equal(1, raised);
        Assert.False(session.HasHeldMessage);
    }

    [AvaloniaFact]
    public void TheSettingAlreadyOnMeansNoOffer()
    {
        File.WriteAllText(SettingsPath, "{\"crossSessionInbound\":\"accept\"}");
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), new SettingsFake()));

        Assert.Null(session.InboundOffer);
    }

    // A mode that does not hold ends it early — and without the registry
    // scan, which is what keeps the scan cheap for most sessions.
    [AvaloniaFact]
    public void ASessionInADifferentModeIsNotOffered()
    {
        var settings = new SettingsFake();
        var session = Loaded(Session(Transcript(Mode("plan")), settings));

        Assert.Null(session.InboundOffer);
        Assert.Equal(0, settings.Reads);
    }

    // The last row wins: a session that started in plan and was switched to
    // bypass is held now.
    [AvaloniaFact]
    public void TheLastModeRowWins()
    {
        var session = Loaded(Session(
            Transcript(Mode("plan"), User("u1", "hello"), Mode("bypassPermissions")), new SettingsFake()));

        Assert.NotNull(session.InboundOffer);
    }

    // No mode row yet: settings.json's permissions.defaultMode stands in.
    [AvaloniaFact]
    public void WithNoModeRowTheDefaultModeDecides()
    {
        File.WriteAllText(SettingsPath, "{\"permissions\":{\"defaultMode\":\"bypassPermissions\"}}");
        var session = Loaded(Session(Transcript(User("u1", "hello")), new SettingsFake()));

        Assert.NotNull(session.InboundOffer);
    }

    [AvaloniaFact]
    public void WithNoModeRowAndNoDefaultModeThereIsNoOffer()
    {
        var session = Loaded(Session(Transcript(User("u1", "hello")), new SettingsFake()));

        Assert.Null(session.InboundOffer);
    }

    [AvaloniaFact]
    public void ReplyingOffMeansNoOffer()
    {
        OrbweaverSettings.ClaudeCodeReplyEnabled = false;
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), new SettingsFake()));

        Assert.Null(session.InboundOffer);
    }

    // CB-266's bridge goes over Remote Control, not the socket; PM excluded it.
    [AvaloniaFact]
    public void ADesktopHostedSessionIsNotOffered()
    {
        var session = Loaded(Session(
            Transcript(Mode("bypassPermissions")), new SettingsFake(), entrypoint: SessionStatus.DesktopEntrypoint));

        Assert.Null(session.InboundOffer);
    }

    // No live registry entry means the panel is not delivering over the socket
    // at all, so nothing it sends could be held. (A session with a tmux pane is
    // the other way the channel is not Messaging; that needs a real tmux and a
    // real pane, which this suite never drives — see TypeIntoTerminalAsync.)
    [AvaloniaFact]
    public void ASessionNotReachedOverItsSocketIsNotOffered()
    {
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), new SettingsFake(), noRegistry: true));

        Assert.Null(session.InboundOffer);
    }

    // A transcript outside any config root's projects/ and a registry entry that
    // names no root: there is no settings.json this panel can honestly point at.
    [AvaloniaFact]
    public void AnUnknownConfigRootMeansNoOffer()
    {
        var elsewhere = TranscriptAt(Path.Combine(_root, "loose", "t.jsonl"), Mode("bypassPermissions"));
        var session = Loaded(Session(elsewhere, new SettingsFake()));

        Assert.Null(session.ConfigRootFor());
        Assert.Null(session.InboundOffer);
    }

    // ...and the registry entry's own root when the transcript cannot name one.
    [AvaloniaFact]
    public void TheRegistryEntryNamesTheRootWhenTheTranscriptCannot()
    {
        var other = Path.Combine(_root, "board");
        Directory.CreateDirectory(other);
        var elsewhere = TranscriptAt(Path.Combine(_root, "loose", "t.jsonl"), Mode("bypassPermissions"));

        var session = Loaded(Session(elsewhere, new SettingsFake(), registry: Entry(configRoot: other)));

        Assert.Equal(other, session.ConfigRootFor());
        Assert.Equal(Path.Combine(other, "settings.json"), session.InboundOffer!.SettingsPath);
    }

    // Neither the transcript nor any registry entry: no root at all.
    [AvaloniaFact]
    public void NoTranscriptRootAndNoRegistryEntryMeansNoRoot()
    {
        var elsewhere = TranscriptAt(Path.Combine(_root, "loose", "t.jsonl"), Mode("bypassPermissions"));
        var session = Loaded(Session(elsewhere, new SettingsFake(), noRegistry: true));

        Assert.Null(session.ConfigRootFor());
    }

    // The transcript moving to another config root (a respawn under a
    // different profile) reads that root's settings.json, not the cached one.
    [AvaloniaFact]
    public void ATranscriptInAnotherRootReadsThatRootsSettings()
    {
        File.WriteAllText(SettingsPath, "{}");
        var settings = new SettingsFake();
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), settings));
        Assert.Equal(SettingsPath, session.InboundOffer!.SettingsPath);

        var board = Path.Combine(_root, "board");
        var moved = TranscriptAt(Path.Combine(board, "projects", "-work-repo", "t.jsonl"), Mode("bypassPermissions"));
        File.WriteAllText(Path.Combine(board, "settings.json"), "{}");

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = moved,
            Shape = LocalSessionShape.Background,
            Presence = OrbPresence.NeedsInput,
            State = "idle",
            Title = "job-hunter",
        });

        Assert.Equal(Path.Combine(board, "settings.json"), session.InboundOffer!.SettingsPath);
    }

    // A file we could not parse is never written to, so it is explained with no
    // button.
    [AvaloniaFact]
    public void AnUnreadableSettingsFileIsExplainedButNotEditable()
    {
        File.WriteAllText(SettingsPath, "{ \"model\": \"opus\", // a comment\n }");
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), new SettingsFake()));

        var offer = Assert.IsType<PeerInboundOffer>(session.InboundOffer);
        Assert.False(offer.CanEdit);
        Assert.Equal(LocalCliChatSession.UnreadableText("job-hunter"), offer.Explanation);
        Assert.Contains(Brand.DisplayName, offer.Explanation, StringComparison.Ordinal);
        Assert.Contains("\"crossSessionInbound\": \"accept\"", offer.Explanation, StringComparison.Ordinal);
    }

    // QA's finding: ReadText answers null both for no file and for a file it
    // could not read. A null with a file on disk is the second, and gets the
    // explanation with no button — the write would fail on the same read.
    [AvaloniaFact]
    public async Task AFileThatExistsButCannotBeReadIsExplainedNotOffered()
    {
        File.WriteAllText(SettingsPath, "{}");
        var settings = new SettingsFake { Reader = _ => null };
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), settings));

        var offer = Assert.IsType<PeerInboundOffer>(session.InboundOffer);
        Assert.False(offer.CanEdit);
        Assert.Equal(LocalCliChatSession.UnreadableText("job-hunter"), offer.Explanation);

        // ...and a send is not claimed held either way.
        await session.SendAsync("check the deploy");
        Assert.False(session.HasHeldMessage);
        Assert.Contains(session.History, t => t.Text.StartsWith("Handed to job-hunter", StringComparison.Ordinal));
    }

    // The control for the case above: the same null with no file on disk is an
    // empty settings object, and the button is offered.
    [AvaloniaFact]
    public void ANullReadWithNoFileIsAnEmptySettingsObject()
    {
        var settings = new SettingsFake { Reader = _ => null };
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), settings));

        Assert.True(session.InboundOffer!.CanEdit);
    }

    // The same through the real reader and a real permission denial. Unix only:
    // mode 000 is a Unix permission, and Windows has no equivalent a test can set
    // without an ACL API this suite does not use.
    [AvaloniaFact]
    public void AnUnreadableFileOnDiskIsExplainedThroughTheRealReader()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "mode 000 is a Unix permission; Windows has no equivalent here");

        File.WriteAllText(SettingsPath, "{}");
        File.SetUnixFileMode(SettingsPath, UnixFileMode.None);
        try
        {
            var settings = new SettingsFake { Reader = PeerInboundSettingsFile.ReadText };
            var session = Loaded(Session(Transcript(Mode("bypassPermissions")), settings));

            Assert.False(session.InboundOffer!.CanEdit);
        }
        finally
        {
            File.SetUnixFileMode(SettingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // ...but only when the transcript itself named a holding mode: without the
    // file, the default mode is unknowable too.
    [AvaloniaFact]
    public void AnUnreadableSettingsFileWithNoModeRowSaysNothing()
    {
        File.WriteAllText(SettingsPath, "not json");
        var session = Loaded(Session(Transcript(User("u1", "hello")), new SettingsFake()));

        Assert.Null(session.InboundOffer);
    }

    // --- live changes -------------------------------------------------------

    // A hand edit to settings.json shows up on the next scan; a scan with
    // nothing changed neither re-reads the file nor raises the event again.
    [AvaloniaFact]
    public void AnEditToSettingsShowsUpOnTheNextScanAndAnUnchangedScanIsQuiet()
    {
        File.WriteAllText(SettingsPath, "{}");
        var settings = new SettingsFake();
        var transcript = Transcript(Mode("bypassPermissions"));
        var session = Loaded(Session(transcript, settings));
        Assert.NotNull(session.InboundOffer);

        var raised = 0;
        session.InboundChanged += () => raised++;
        var readsBefore = settings.Reads;

        var status = new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = transcript,
            Shape = LocalSessionShape.Background,
            Presence = OrbPresence.NeedsInput,
            State = "idle",
            Title = "job-hunter",
        };

        session.UpdateStatus(status);
        Assert.Equal(0, raised);
        Assert.Equal(readsBefore, settings.Reads);

        File.WriteAllText(SettingsPath, "{\"crossSessionInbound\":\"accept\"}");
        session.UpdateStatus(status);

        Assert.Equal(1, raised);
        Assert.Null(session.InboundOffer);
    }

    // A mode row arriving on the live tail moves the offer.
    [AvaloniaFact]
    public void AModeRowOnTheLiveTailMovesTheOffer()
    {
        var transcript = Transcript(Mode("bypassPermissions"), User("u1", "hello"));
        var session = Loaded(Session(transcript, new SettingsFake()));
        Assert.NotNull(session.InboundOffer);

        var raised = 0;
        session.InboundChanged += () => raised++;

        File.AppendAllText(transcript, Mode("acceptEdits") + "\n");
        Invoke(session, "Pump");
        PumpUntil(() => raised == 1, "the live tail");

        Assert.Null(session.InboundOffer);

        // The same mode again changes nothing.
        File.AppendAllText(transcript, Mode("acceptEdits") + "\n" + User("u2", "again") + "\n");
        Invoke(session, "Pump");
        PumpUntil(() => session.History.Count == 2, "the second append");
        Assert.Equal(1, raised);
    }

    // An older page is older: a mode row in it must not overwrite the one the
    // newest rows carry.
    [AvaloniaFact]
    public async Task AnOlderPageDoesNotWindTheModeBack()
    {
        var padding = Enumerable.Range(0, 700)
            .Select(i => User("pad" + i, new string('x', 1000)))
            .ToArray();
        var rows = new List<string> { Mode("plan") };
        rows.AddRange(padding);
        rows.Add(Mode("bypassPermissions"));

        var transcript = Transcript(rows.ToArray());
        var session = Loaded(Session(transcript, new SettingsFake()));
        Assert.True(session.HasMore);
        Assert.NotNull(session.InboundOffer);

        while (session.HasMore) await session.LoadOlderAsync(CancellationToken.None);

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = transcript,
            Shape = LocalSessionShape.Background,
            Presence = OrbPresence.NeedsInput,
            State = "idle",
            Title = "job-hunter",
        });

        Assert.NotNull(session.InboundOffer);
    }

    // --- sending ------------------------------------------------------------

    [AvaloniaFact]
    public async Task ASendThatWouldBeHeldSaysSoInsteadOfHandedTo()
    {
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), new SettingsFake()));
        var raised = 0;
        session.InboundChanged += () => raised++;

        var outcome = await session.SendAsync("check the deploy");

        Assert.Equal(ChatSendOutcome.Sent, outcome);
        Assert.Contains(session.History, t => t.Text == PeerInbound.HeldNote("job-hunter"));
        Assert.DoesNotContain(session.History, t => t.Text.StartsWith("Handed to", StringComparison.Ordinal));
        Assert.True(session.HasHeldMessage);
        Assert.Equal(1, raised);
    }

    [AvaloniaFact]
    public async Task ASendThatGoesStraightInStillSaysHandedTo()
    {
        File.WriteAllText(SettingsPath, "{\"crossSessionInbound\":\"accept\"}");
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), new SettingsFake()));

        await session.SendAsync("check the deploy");

        Assert.Contains(session.History, t => t.Text.StartsWith("Handed to job-hunter", StringComparison.Ordinal));
        Assert.False(session.HasHeldMessage);
    }

    // An unreadable settings.json is not claimed either way.
    [AvaloniaFact]
    public async Task AnUnreadableSettingsFileDoesNotClaimTheMessageWasHeld()
    {
        File.WriteAllText(SettingsPath, "not json");
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), new SettingsFake()));

        await session.SendAsync("check the deploy");

        Assert.Contains(session.History, t => t.Text.StartsWith("Handed to job-hunter", StringComparison.Ordinal));
        Assert.False(session.HasHeldMessage);
    }

    // Turned on, then "Send it again": the same bytes go out once more, into the
    // bubble already on screen — no second user turn — and the wrapped row that
    // eventually lands settles that bubble.
    [AvaloniaFact]
    public async Task ResendingAfterTurningItOnReusesTheBubbleAndClearsTheHeldMessage()
    {
        var wire = new Wire();
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), new SettingsFake(), wire));

        await session.SendAsync("check the deploy");
        var bubble = session.History.Single(t => t.Role == ChatRole.User);

        Assert.True(await session.EnableInboundAsync());
        Assert.True(session.HasHeldMessage);

        var outcome = await session.ResendHeldAsync();

        Assert.Equal(ChatSendOutcome.Sent, outcome);
        Assert.Equal(2, wire.Writes);
        Assert.Single(session.History, t => t.Role == ChatRole.User);
        Assert.False(session.HasHeldMessage);
        Assert.Contains(session.History, t => t.Text.StartsWith("Handed to job-hunter", StringComparison.Ordinal));

        var updated = new List<ChatTurn>();
        session.TurnUpdated += updated.Add;
        Invoke(session, "Add", new ChatTurn { Role = ChatRole.User, Text = Wrapped("check the deploy") });

        Assert.Same(bubble, Assert.Single(updated));
        Assert.Single(session.History, t => t.Role == ChatRole.User);
    }

    // Still held (nothing turned on): still held, and said so again.
    [AvaloniaFact]
    public async Task ResendingWhileStillHeldKeepsItHeld()
    {
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), new SettingsFake()));
        await session.SendAsync("check the deploy");

        await session.ResendHeldAsync();

        Assert.True(session.HasHeldMessage);
        Assert.Equal(2, session.History.Count(t => t.Text == PeerInbound.HeldNote("job-hunter")));
    }

    // A socket that refuses the resend keeps "Send it again" alive.
    [AvaloniaFact]
    public async Task ARefusedResendKeepsTheHeldMessage()
    {
        var wire = new Wire();
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), new SettingsFake(), wire));
        await session.SendAsync("check the deploy");
        await session.EnableInboundAsync();

        wire.Accepts = false;
        var outcome = await session.ResendHeldAsync();

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.True(session.HasHeldMessage);
    }

    // A new message that goes straight in does not drop the one still held —
    // the button is about that message, not the latest.
    [AvaloniaFact]
    public async Task ANewMessageGoingInLeavesTheHeldOneResendable()
    {
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), new SettingsFake()));
        await session.SendAsync("check the deploy");
        await session.EnableInboundAsync();

        await session.SendAsync("and the tests");

        Assert.True(session.HasHeldMessage);
    }

    [AvaloniaFact]
    public async Task ResendingWithNothingHeldDoesNothing()
    {
        var wire = new Wire();
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), new SettingsFake(), wire));
        var before = session.History.Count;

        var outcome = await session.ResendHeldAsync();

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.Equal(0, wire.Writes);
        Assert.Equal(before, session.History.Count);
    }

    [AvaloniaFact]
    public async Task ResendingWithReplyingOffSaysSoAndSendsNothing()
    {
        var wire = new Wire();
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), new SettingsFake(), wire));
        await session.SendAsync("check the deploy");

        OrbweaverSettings.ClaudeCodeReplyEnabled = false;
        var outcome = await session.ResendHeldAsync();

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.Equal(1, wire.Writes);
        Assert.Equal(LocalCliChatSession.ReplyingOffNote, session.History[^1].Text);
    }

    // --- turning it on ------------------------------------------------------

    [AvaloniaFact]
    public async Task TurningItOnWritesTheRootsSettingsAndTheOfferGoes()
    {
        var settings = new SettingsFake();
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), settings));
        var raised = 0;
        session.InboundChanged += () => raised++;

        Assert.True(await session.EnableInboundAsync());

        Assert.Equal((_root, true), Assert.Single(settings.Applies));
        Assert.Null(session.InboundOffer);
        Assert.Equal(1, raised);
    }

    // Already on (someone beat the click to it) is still success.
    [AvaloniaFact]
    public async Task AnUnchangedWriteCountsAsOn()
    {
        var settings = new SettingsFake { Result = new(PeerInboundWriteKind.Unchanged, null) };
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), settings));

        Assert.True(await session.EnableInboundAsync());
    }

    [AvaloniaFact]
    public async Task AFailedWriteSaysWhyAndTheOfferStays()
    {
        var settings = new SettingsFake
        {
            Result = new(PeerInboundWriteKind.Failed, "the file is locked"),
            WriteAccept = false,
        };
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), settings));

        Assert.False(await session.EnableInboundAsync());

        var note = session.History[^1].Text;
        Assert.Contains("the file is locked", note, StringComparison.Ordinal);
        Assert.Contains("nothing was turned on", note, StringComparison.Ordinal);
        Assert.NotNull(session.InboundOffer);
    }

    [AvaloniaFact]
    public async Task ARefusedWriteWithNoReasonStillSaysSomething()
    {
        var settings = new SettingsFake { Result = new(PeerInboundWriteKind.Refused, null), WriteAccept = false };
        var session = Loaded(Session(Transcript(Mode("bypassPermissions")), settings));

        Assert.False(await session.EnableInboundAsync());

        Assert.Equal(
            LocalCliChatSession.EnableFailedNote(
                PeerInbound.DisplayPath(SettingsPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                null),
            session.History[^1].Text);
        Assert.Contains("the write did not go through", session.History[^1].Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task TurningItOnWithNoKnownRootWritesNothing()
    {
        var settings = new SettingsFake();
        var elsewhere = TranscriptAt(Path.Combine(_root, "loose", "t.jsonl"), Mode("bypassPermissions"));
        var session = Loaded(Session(elsewhere, settings));

        Assert.False(await session.EnableInboundAsync());
        Assert.Empty(settings.Applies);
    }
}
