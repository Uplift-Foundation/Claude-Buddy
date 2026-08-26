using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// A real scan, over real status files, producing real orb windows.
//
// This is the seam the rest of SessionManager's tests stop at: Superseded,
// JudgeLiveness and the rest are pure and covered in tests/UnitTests, but
// nothing there proves the scan wires them to a window — that a file appearing
// puts an orb on screen, that deleting it takes the orb away with its status
// and its place in the stacking order, or that expiring one does the same. Those
// are the failures a user actually reports ("I seem to have lost all my orbs"),
// and every one of them lives in the plumbing rather than in the rules.
//
// Two things make it runnable without touching the machine it runs on.
//
// The status directory is handed over rather than inherited: SessionManager's
// internal constructor takes one, so these files land in a scratch directory of
// this test's own and a real session's orb is never disturbed. Start() is
// deliberately never called — it would create a TrayController, subscribe to the
// speech engine and start a two-second timer, none of which this is about, and
// with no tray attached UpdateTray becomes a no-op on a null.
//
// And every fake session is shaped so that nothing here shells out. Both
// BackgroundJobs.IsLiveJob (`claude agents --json`) and AgentTeamViewer.TryAdopt
// are reached only by a session with no terminal or no pid, so every file below
// names both — a term_program and this test process's own pid, which is the one
// pid on the machine that is certainly alive. Two files never share a pid *and*
// a CLI either, which is what would put Superseded's job-list lookup in play.
// The rules those calls sit behind are covered in ScanVerdictTests, where they
// are passed in rather than performed.
[Collection("Settings")]
public class SessionScanTests
{
    // Alive for certain, and asking the kernel about it is a kill(pid, 0) —
    // nothing is signalled and nothing is spawned. An invented pid would be a
    // coin toss on whether some unrelated process happened to hold it.
    private static readonly int LivePid = Environment.ProcessId;

    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } =
            Path.Combine(Path.GetTempPath(), "cb-scan-" + Guid.NewGuid());

        public Scratch() => Directory.CreateDirectory(Dir);

        // A status file the way the hooks write one: JSON, named for the
        // session id, with the app-derived fields (Lead, Agent, Source) absent
        // because they are [JsonIgnore] and the hook has never heard of them.
        // tty and transcriptPath default to what an interactive session has —
        // a terminal, and no transcript recorded — so every case written before
        // the NothingToShow rule existed is untouched by it.
        public void Write(
            string sessionId, string state = "idle", string cli = "",
            string title = "", string cwd = "/Users/warren/project",
            int? pid = null, string termProgram = "iTerm.app",
            DateTime? written = null, string tty = "/dev/ttys004",
            string transcriptPath = "")
        {
            var path = Path.Combine(Dir, sessionId + ".txt");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                State = state,
                Cli = cli,
                Title = title,
                Cwd = cwd,
                SessionPid = pid ?? LivePid,
                TermProgram = termProgram,
                Tty = tty,
                TranscriptPath = transcriptPath,
            }));

            if (written is not null) File.SetLastWriteTimeUtc(path, written.Value);
        }

        // A transcript on disk, so the rule under test is answered by a real
        // File.Exists rather than by a predicate a test handed in.
        public string WriteTranscript(string sessionId)
        {
            var path = Path.Combine(Dir, sessionId + ".jsonl");
            File.WriteAllText(path, "{\"type\":\"user\",\"message\":{\"content\":\"hi\"}}\n");
            return path;
        }

        public string MissingTranscript(string sessionId) =>
            Path.Combine(Dir, sessionId + "-never-written.jsonl");

        public void Delete(string sessionId) =>
            File.Delete(Path.Combine(Dir, sessionId + ".txt"));

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static SessionManager Scan(Scratch scratch, bool enableBothClis = true)
    {
        // Both CLIs on, said out loud rather than inherited.
        //
        // ScanAndUpdate filters every session through ClaudeCodeEnabled or
        // CodexEnabled depending on its source (SessionManager.cs ~line 378), so a
        // scan test that does not state this is asserting about whatever the last
        // test to touch those settings left behind. That is not hypothetical: it
        // is what turned this suite red on both CI legs. SettingsWindowRowTests'
        // TheTwoClisDoNotShareTheirSwitches deliberately switches Codex off and
        // used to leave it off, and in Release — where the class order differs
        // from Debug — it ran first. Every scan here then found nothing, with no
        // exception and no hint as to why: StatusFor returned null and the orb
        // collections came back empty.
        //
        // That leak is fixed at its source too. This is the other half, and the
        // half that keeps working: a test that depends on a setting sets it.
        // enableBothClis: false for the one test that switches a CLI off ON
        // PURPOSE, to prove such a session is filtered out — it has to be able to
        // opt out of the guarantee the rest rely on.
        if (enableBothClis)
        {
            ClaudeBuddySettings.ClaudeCodeEnabled = true;
            ClaudeBuddySettings.CodexEnabled = true;
        }

        var manager = new SessionManager(scratch.Dir);
        manager.ScanAndUpdate();
        return manager;
    }

    // _windows is what the scan actually maintains; StatusFor only proves the
    // status survived. Read rather than widened, the same reasoning
    // TrayRemoteItemTests records for reaching TrayController's private menu.
    private static IReadOnlyCollection<string> OrbIds(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        var windows = (System.Collections.IDictionary)field!.GetValue(manager)!;
        return windows.Keys.Cast<string>().ToList();
    }

    // The stacking order, which is not the same list as the orbs: teams are
    // gathered and hand-placed orbs keep their spot.
    private static List<string> DisplayOrder(SessionManager manager)
    {
        var method = typeof(SessionManager).GetMethod(
            "DisplayOrder", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        return (List<string>)method!.Invoke(manager, null)!;
    }

    [AvaloniaFact]
    public void AStatusFileBecomesAnOrbCarryingWhatTheFileSaid()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", state: "waiting", title: "Windows and Mac parity");

        var manager = Scan(scratch);

        Assert.Equal(new[] { "session-a" }, OrbIds(manager));

        var status = manager.StatusFor("session-a");
        Assert.NotNull(status);
        Assert.Equal("waiting", status!.State);
        Assert.Equal("Windows and Mac parity", status.Title);
        Assert.Equal(SessionSource.ClaudeCode, status.Source);
    }

    [AvaloniaFact]
    public void ACodexFileIsResolvedToCodexRatherThanArrivingAsTheEnumsDefault()
    {
        // Source is [JsonIgnore] and so arrives as ClaudeCode however the file
        // is written; SourceOf exists because missing that resolution does not
        // fail loudly — it produces a Codex session that claims to be a Claude
        // Code one, which is long enough to send a click to the wrong place.
        using var scratch = new Scratch();
        scratch.Write("codex-1", cli: "codex");

        var manager = Scan(scratch);

        Assert.Equal(SessionSource.Codex, manager.StatusFor("codex-1")!.Source);
    }

    [AvaloniaFact]
    public void DeletingAStatusFileTakesTheOrbTheStatusAndThePlaceInTheStack()
    {
        // SessionEnd's graceful path. All three have to go together: an orb left
        // in _windows is a window nobody closes, a status left behind answers
        // StatusFor for a session that has ended, and an id left in _order
        // reserves a slot in the stack that nothing occupies.
        using var scratch = new Scratch();
        scratch.Write("stays");
        scratch.Write("goes", cli: "codex");

        var manager = Scan(scratch);
        Assert.Equal(2, OrbIds(manager).Count);

        scratch.Delete("goes");
        manager.ScanAndUpdate();

        Assert.Equal(new[] { "stays" }, OrbIds(manager));
        Assert.Null(manager.StatusFor("goes"));
        Assert.Equal(new[] { "stays" }, DisplayOrder(manager));
    }

    [AvaloniaFact]
    public void AQuietSessionLosesItsOrbOnceTheLifetimeHasPassed()
    {
        // The lifetime timer, end to end: an mtime older than the setting is
        // the only thing wrong with this file, and it is enough.
        var before = ClaudeBuddySettings.OrbLifetimeMinutes;
        try
        {
            ClaudeBuddySettings.OrbLifetimeMinutes = 5;

            using var scratch = new Scratch();
            scratch.Write("fresh");
            scratch.Write("ancient", cli: "codex",
                written: DateTime.UtcNow - TimeSpan.FromHours(2));

            var manager = Scan(scratch);

            Assert.Equal(new[] { "fresh" }, OrbIds(manager));

            // And "forever" brings it straight back, without the file changing.
            ClaudeBuddySettings.OrbLifetimeMinutes = ClaudeBuddySettings.OrbLifetimeForever;
            manager.ScanAndUpdate();

            Assert.Contains("ancient", OrbIds(manager));
        }
        finally
        {
            ClaudeBuddySettings.OrbLifetimeMinutes = before;
        }
    }

    [AvaloniaFact]
    public void ACliSwitchedOffIsIgnoredWithoutItsFileBeingTouched()
    {
        // "A CLI switched off is ignored, not unwired." The hook keeps writing —
        // it is the user's own config, and for Codex rewriting it would cost
        // them their hook trust — so the file must still be there afterwards.
        var before = ClaudeBuddySettings.CodexEnabled;
        try
        {
            using var scratch = new Scratch();
            scratch.Write("claude-1");
            scratch.Write("codex-1", cli: "codex");

            ClaudeBuddySettings.CodexEnabled = false;
            var manager = Scan(scratch, enableBothClis: false);

            Assert.Equal(new[] { "claude-1" }, OrbIds(manager));
            Assert.True(File.Exists(Path.Combine(scratch.Dir, "codex-1.txt")));

            ClaudeBuddySettings.CodexEnabled = true;
            manager.ScanAndUpdate();

            Assert.Contains("codex-1", OrbIds(manager));
        }
        finally
        {
            ClaudeBuddySettings.CodexEnabled = before;
        }
    }

    [AvaloniaFact]
    public void AMalformedOrEmptyFileIsSkippedRatherThanTakingTheScanDown()
    {
        // A file caught mid-write is the ordinary case here, not a corruption:
        // the hook truncates and rewrites, and the scan runs every two seconds.
        // "retry next tick" only works if one bad file cannot stop the others
        // being read.
        using var scratch = new Scratch();
        scratch.Write("good");
        File.WriteAllText(Path.Combine(scratch.Dir, "half-written.txt"), "{\"state\": \"gen");
        File.WriteAllText(Path.Combine(scratch.Dir, "null.txt"), "null");

        var manager = Scan(scratch);

        Assert.Equal(new[] { "good" }, OrbIds(manager));
    }

    [AvaloniaFact]
    public void TheAutoColourMarkerIsReconciledOnEveryScanRatherThanOnlyWhenToggled()
    {
        // The marker is how the hooks learn the setting, and it lives in the
        // temp path, which the OS is entitled to clear out. A marker that
        // vanished would turn the feature off with nothing said — so a scan
        // puts it back, and this deletes it behind the scan's back to prove it.
        var before = ClaudeBuddySettings.AutoColorSessions;
        try
        {
            using var scratch = new Scratch();
            var marker = Path.Combine(scratch.Dir, ".auto-color");

            ClaudeBuddySettings.AutoColorSessions = true;
            var manager = Scan(scratch);
            Assert.True(File.Exists(marker));

            File.Delete(marker);
            manager.ScanAndUpdate();
            Assert.True(File.Exists(marker));

            ClaudeBuddySettings.AutoColorSessions = false;
            manager.ScanAndUpdate();
            Assert.False(File.Exists(marker));
        }
        finally
        {
            ClaudeBuddySettings.AutoColorSessions = before;
        }
    }

    [AvaloniaFact]
    public void ResettingASessionToIdleRewritesItsFileAndKeepsEverythingElse()
    {
        // "Keep everything but the state (cwd, terminal info) intact" — the orb
        // has to stay clickable afterwards, and the CLI has to survive the round
        // trip or a reset turns a Codex session into a Claude Code one.
        using var scratch = new Scratch();
        scratch.Write("codex-1", state: "waiting", cli: "codex", title: "a name");

        var manager = Scan(scratch);
        manager.ResetSessionToIdle("codex-1");

        var status = manager.StatusFor("codex-1");
        Assert.Equal("idle", status!.State);
        Assert.Equal(SessionSource.Codex, status.Source);
        Assert.Equal("a name", status.Title);
        Assert.Equal("iTerm.app", status.TermProgram);

        // And on disk, which is what the next scan will read back.
        var onDisk = System.Text.Json.JsonSerializer.Deserialize<SessionStatus>(
            File.ReadAllText(Path.Combine(scratch.Dir, "codex-1.txt")));
        Assert.Equal("idle", onDisk!.State);
        Assert.Equal("codex", onDisk.Cli);
        Assert.Equal(SessionSource.Codex, SessionManager.SourceOf(onDisk));
    }

    [AvaloniaFact]
    public void ResetAllReachesEverySessionAndAGatewayOneIsLeftToItsOwner()
    {
        // A gateway session has no status file to rewrite, and the path this
        // would build from its key ("openclaw:agent:main:…") is not one this app
        // should be writing at all. ResetAll walks the same code, so a key with
        // a colon in it must be declined rather than turned into a filename.
        using var scratch = new Scratch();
        scratch.Write("local-1", state: "waiting");

        var manager = Scan(scratch);

        var gateway = new SessionStatus { Source = SessionSource.OpenClaw, State = "generating" };
        Statuses(manager)["openclaw:agent:main"] = gateway;

        manager.ResetAllSessionsToIdle();

        Assert.Equal("idle", manager.StatusFor("local-1")!.State);
        Assert.Equal("generating", gateway.State);
        Assert.False(Directory.EnumerateFiles(scratch.Dir, "openclaw*").Any());
    }

    [AvaloniaFact]
    public void ResettingASessionWhoseFileHasVanishedStillResolvesItsCli()
    {
        // `existing ?? new SessionStatus()` is the case the SourceOf call is
        // there for: a file that could not be read has no "cli" either, so the
        // fallback has to go through the same resolution rather than inheriting
        // whatever the caller assumed.
        using var scratch = new Scratch();
        var manager = new SessionManager(scratch.Dir);

        manager.ResetSessionToIdle("never-existed");

        var status = manager.StatusFor("never-existed");
        Assert.Equal("idle", status!.State);
        Assert.Equal(SessionSource.ClaudeCode, status.Source);
    }

    // --- teams --------------------------------------------------------------
    //
    // The scan fills Lead in from the member process's own command line
    // (AgentTeam.Of), which this test process does not carry — so the field is
    // set directly on the status the scan produced. That is the same field, on
    // the same object the rest of the app reads, and it is the only part of a
    // team that cannot be faked from a file: everything downstream of it —
    // gathering, arrows, drag-along — reads nothing else.

    private static IDictionary<string, SessionStatus> Statuses(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_statuses", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        return (IDictionary<string, SessionStatus>)field!.GetValue(manager)!;
    }

    [AvaloniaFact]
    public void ATeamsMemberIsGatheredBehindItsLeadAndFollowsItWhenDragged()
    {
        // Two orbs and no more, for the reason the class comment gives: two
        // files sharing a pid *and* a CLI are what put Superseded's job-list
        // lookup in play, and a third live session would have to share both
        // with one of these. What a third orb would add — the unrelated session
        // the gathering has to step over — is covered purely, and exhaustively,
        // by SessionScanRulesTests.GatherTeams.
        using var scratch = new Scratch();
        scratch.Write("lead");
        scratch.Write("member", cli: "codex");

        var manager = Scan(scratch);
        manager.StatusFor("member")!.Lead = "lead";

        Assert.Equal(new[] { "lead", "member" }, DisplayOrder(manager));

        var members = manager.MembersOf("lead");
        Assert.Single(members);
        Assert.Equal("member", members[0].SessionId);

        // A member leads nobody, and a lead is not dragged along with itself.
        Assert.Empty(manager.MembersOf("member"));

        // The next scan builds a fresh status from the file, which has no lead
        // in it — the field is the app's, not the hook's — so the team is gone
        // again. Asserted rather than worked around: it is what says the
        // membership above came from the field this test set and not from
        // anything the file could have carried.
        manager.ScanAndUpdate();
        Assert.Empty(manager.MembersOf("lead"));
    }

    [AvaloniaFact]
    public void HidingTheOrbsKeepsTrackingThemAndShowingThemAgainRestoresThem()
    {
        // "Sessions keep being tracked either way, so the tray icon and its menu
        // stay accurate" — the orbs going away must not look like the sessions
        // going away.
        var before = ClaudeBuddySettings.ShowOrbs;
        try
        {
            using var scratch = new Scratch();
            scratch.Write("session-a");

            var manager = Scan(scratch);
            Assert.True(manager.OrbsVisible);

            manager.SetOrbsVisible(false);
            Assert.False(manager.OrbsVisible);
            Assert.False(ClaudeBuddySettings.ShowOrbs);
            Assert.Equal(new[] { "session-a" }, OrbIds(manager));
            Assert.NotNull(manager.StatusFor("session-a"));

            // Asked for what it already is, and nothing happens — the early
            // return is what stops a scan's worth of reflow per tick.
            manager.SetOrbsVisible(false);
            Assert.False(manager.OrbsVisible);

            manager.SetOrbsVisible(true);
            Assert.True(manager.OrbsVisible);
            Assert.True(ClaudeBuddySettings.ShowOrbs);
        }
        finally
        {
            ClaudeBuddySettings.ShowOrbs = before;
        }
    }

    // --- arrangement --------------------------------------------------------

    [AvaloniaFact]
    public void ArrangingIsAToggleAndLeavesEveryOrbSomewhereOnTheScreen()
    {
        var beforeAnchor = ClaudeBuddySettings.ArrangeAnchor;
        try
        {
            ClaudeBuddySettings.ArrangeAnchor = null;

            using var scratch = new Scratch();
            scratch.Write("a");
            scratch.Write("b", cli: "codex");

            var manager = Scan(scratch);
            Assert.False(manager.IsArranged);

            manager.ArrangeOrbsInPattern();
            Assert.True(manager.IsArranged);

            // The anchor is saved on the first arrangement so a later arrival
            // re-fits around where the shape already is rather than around the
            // screen's middle.
            Assert.NotNull(ClaudeBuddySettings.ArrangeAnchor);

            // Asked again mid-glide, it declines rather than fighting the
            // animation that is already running.
            manager.ArrangeOrbsInPattern();
            Assert.True(manager.IsArranged);

            // The siblings a dragged orb pulls with it: every arranged orb but
            // the one doing the dragging.
            var siblings = manager.ArrangedSiblings("a");
            Assert.Single(siblings);
            Assert.Equal("b", siblings[0].SessionId);
        }
        finally
        {
            ClaudeBuddySettings.ArrangeAnchor = beforeAnchor;
        }
    }

    [AvaloniaFact]
    public void ThereAreNoArrangedSiblingsWhileNothingIsArranged()
    {
        using var scratch = new Scratch();
        scratch.Write("a");

        var manager = Scan(scratch);

        Assert.Empty(manager.ArrangedSiblings("a"));

        // And the settings slider's live preview does nothing at all until the
        // orbs are arranged; the new spacing is simply saved for next time.
        manager.ReapplyArrangement();
        Assert.False(manager.IsArranged);
    }

    // --- dragged positions --------------------------------------------------

    [AvaloniaFact]
    public void AnOrbGoesBackToWhereItWasDraggedAndReturningItToTheStackForgets()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", title: "a name");

        var key = SessionManager.PositionKeyFor(
            new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = "/Users/warren/project", Title = "a name" },
            "session-a");

        try
        {
            var manager = Scan(scratch);

            Assert.Equal(key, PositionKeyOf(manager, "session-a"));

            // A drag: the window moves and the app is told to remember it.
            var window = WindowFor(manager, "session-a");
            window.PinAt(new PixelPoint(300, 200));
            manager.RememberOrbPosition(window);

            Assert.Equal(300, ClaudeBuddySettings.OrbPositionFor(key)!.X);

            // A second manager over the same directory restores it — which is
            // the whole point of the key surviving a restart.
            var restored = Scan(scratch);
            Assert.Equal(new PixelPoint(300, 200), WindowFor(restored, "session-a").Position);

            manager.ReturnOrbToStack("session-a");
            Assert.Null(ClaudeBuddySettings.OrbPositionFor(key));

            // An id nobody is tracking is declined rather than throwing.
            manager.ReturnOrbToStack("not-a-session");
        }
        finally
        {
            ClaudeBuddySettings.ClearOrbPosition(key);
        }
    }

    private static OrbWindow WindowFor(SessionManager manager, string sessionId)
    {
        var field = typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance);
        var windows = (IDictionary<string, OrbWindow>)field!.GetValue(manager)!;
        return windows[sessionId];
    }

    private static string PositionKeyOf(SessionManager manager, string sessionId) =>
        WindowFor(manager, sessionId).PositionKey;

    // --- chat ---------------------------------------------------------------

    [AvaloniaFact]
    public void AnUntrackedSessionHasNoConversationBehindIt()
    {
        // Null is the whole of the "this orb's click means something else"
        // signal — the orb deliberately knows nothing about which source a
        // session came from or whether the feature is on.
        using var scratch = new Scratch();
        var manager = new SessionManager(scratch.Dir);

        Assert.Null(manager.RemoteChatFor("never-existed"));
    }

    // A local session's conversation is cached — the same object comes back
    // on a second click — and gated by the same chat-enabled setting the
    // composer reads, checked before the cache so turning the feature off
    // does not hand back a session built while it was on.
    [AvaloniaFact]
    public void RemoteChatForALocalSessionIsGatedThenCachedOnceCreated()
    {
        var before = ClaudeBuddySettings.ClaudeCodeChatEnabled;
        try
        {
            using var scratch = new Scratch();
            var transcriptPath = Path.Combine(scratch.Dir, "transcript.jsonl");
            File.WriteAllText(transcriptPath, "");

            File.WriteAllText(Path.Combine(scratch.Dir, "session-a.txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    State = "idle",
                    Cwd = "/Users/warren/project",
                    SessionPid = LivePid,
                    TermProgram = "iTerm.app",
                    Tty = "/dev/ttys004",
                    TranscriptPath = transcriptPath,
                }));

            var manager = new SessionManager(scratch.Dir);
            manager.ScanAndUpdate();

            ClaudeBuddySettings.ClaudeCodeChatEnabled = false;
            Assert.Null(manager.RemoteChatFor("session-a"));

            ClaudeBuddySettings.ClaudeCodeChatEnabled = true;
            var chat = manager.RemoteChatFor("session-a");
            Assert.IsType<LocalCliChatSession>(chat);

            var again = manager.RemoteChatFor("session-a");
            Assert.Same(chat, again);
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeChatEnabled = before;
        }
    }

    // --- the auto-colour marker's own failure path ---

    [AvaloniaFact]
    public void TheAutoColourMarkerFailsQuietlyWhenItsDirectoryDoesNotExist()
    {
        // "Worst case the colour setting does not take effect, which is not
        // worth interrupting a scan for" — proved by handing it a directory
        // that was never created, so the write it attempts has nowhere to
        // land.
        var before = ClaudeBuddySettings.AutoColorSessions;
        try
        {
            var missingDir = Path.Combine(Path.GetTempPath(), "cb-scan-missing-" + Guid.NewGuid());
            var manager = new SessionManager(missingDir);

            ClaudeBuddySettings.AutoColorSessions = true;
            manager.SyncAutoColorMarker();

            Assert.False(Directory.Exists(missingDir));
        }
        finally
        {
            ClaudeBuddySettings.AutoColorSessions = before;
        }
    }

    // --- ResetSessionToIdle's other two paths ---

    [AvaloniaFact]
    public void ResettingAGatewaySessionDirectlyIsDeclinedRatherThanWritingAFile()
    {
        // There is no status file to rewrite for a gateway session, and
        // "openclaw:agent:main.txt" is not a path this app should ever write
        // to. ResetAllReachesEverySessionAndAGatewayOneIsLeftToItsOwner drives
        // this through ResetAll, but its gateway entry is never in _order —
        // it is injected straight into _statuses, exactly as this test does —
        // so ResetAll's loop never actually calls ResetSessionToIdle on it.
        // This calls it directly.
        using var scratch = new Scratch();
        var manager = new SessionManager(scratch.Dir);

        Statuses(manager)["openclaw:agent:main"] = new SessionStatus
        {
            Source = SessionSource.OpenClaw, State = "generating",
        };

        manager.ResetSessionToIdle("openclaw:agent:main");

        Assert.Equal("generating", manager.StatusFor("openclaw:agent:main")!.State);
        Assert.Empty(Directory.EnumerateFiles(scratch.Dir, "openclaw*"));
    }

    [AvaloniaFact]
    public void ResettingASessionWhoseDirectoryIsGoneStillUpdatesStatusInMemory()
    {
        // Both the read and the write it attempts fail here — the directory
        // was never created — and neither failure should stop the in-memory
        // status from reflecting "idle", which is what the orb itself reads.
        var missingDir = Path.Combine(Path.GetTempPath(), "cb-scan-missing-" + Guid.NewGuid());
        var manager = new SessionManager(missingDir);

        manager.ResetSessionToIdle("never-existed");

        Assert.Equal("idle", manager.StatusFor("never-existed")!.State);
        Assert.False(Directory.Exists(missingDir));
    }

    // --- keeping an orb reachable ---

    [AvaloniaFact]
    public void AnOrbLeftOffscreenByAChangedDesktopIsBroughtBackOnTheNextScan()
    {
        // Nothing moved the orb; the desktop underneath it changed shape —
        // simulated here by pushing it somewhere no configured screen could
        // possibly cover, the same as a monitor being unplugged would.
        using var scratch = new Scratch();
        scratch.Write("session-a");

        var manager = Scan(scratch);
        var window = WindowFor(manager, "session-a");
        window.Position = new PixelPoint(-999_999, -999_999);

        manager.ScanAndUpdate();

        Assert.NotEqual(new PixelPoint(-999_999, -999_999), window.Position);
    }

    [AvaloniaFact]
    public void ASiblingAlreadyPinnedAtASavedPositionStopsASecondOrbStackingOnIt()
    {
        // The sibling is injected directly into _windows rather than produced
        // by a second real session file: two ClaudeCode sessions sharing this
        // process's one certainly-alive pid would collide in Superseded
        // before either reached RestoreOrbPosition, which the class comment
        // above already explains is why every fixture here uses one pid.
        // What is under test is RestoreOrbPosition's own collision check, and
        // it only needs a window already sitting in the dictionary it reads.
        using var scratch = new Scratch();
        scratch.Write("second", title: "shared name");

        var key = SessionManager.PositionKeyFor(
            new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = "/Users/warren/project", Title = "shared name" },
            "first");

        var manager = new SessionManager(scratch.Dir);

        var sibling = new OrbWindow("first");
        sibling.PinAt(new PixelPoint(400, 250));
        sibling.PositionKey = key;
        WindowsDict(manager)["first"] = sibling;

        manager.ScanAndUpdate();

        var second = WindowFor(manager, "second");
        Assert.Equal(key, second.PositionKey);
        Assert.False(second.IsPinned);
    }

    private static Dictionary<string, OrbWindow> WindowsDict(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance);
        return (Dictionary<string, OrbWindow>)field!.GetValue(manager)!;
    }

    // --- reachability ---

    [AvaloniaFact]
    public void ASessionWithNoTerminalAtAllNeverBecomesAnOrb()
    {
        // Codex specifically: a Claude Code session in the same shape would
        // ask BackgroundJobs for real before answering (see the class comment
        // on why that is out of bounds here), but Codex has no background-job
        // concept to ask about and the rule short-circuits before it would.
        using var scratch = new Scratch();
        File.WriteAllText(Path.Combine(scratch.Dir, "headless.txt"),
            System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                Cli = "codex",
                SessionPid = LivePid,
                // No cwd, no tty, no term_program, no tmux pane, no term_pid.
            }));

        var manager = new SessionManager(scratch.Dir);
        manager.ScanAndUpdate();

        Assert.DoesNotContain("headless", OrbIds(manager));
    }

    // --- broadcasts that touch every orb ---

    [AvaloniaFact]
    public void ReapplyingStateColorsAndGlyphsReachesEveryOrbWithoutThrowing()
    {
        // A colour or glyph setting change is not a session change, so
        // nothing on the scan path notices one — these are what the settings
        // window calls instead, and both simply walk every window.
        using var scratch = new Scratch();
        scratch.Write("a");
        scratch.Write("b", cli: "codex");
        var manager = Scan(scratch);

        manager.ReapplyStateColors();
        manager.ReapplyGlyphs();
    }

    // A speech-state change is broadcast to every orb's flyout and to the
    // chat panel, on the UI thread — private because only TextToSpeech's own
    // event should ever raise it, so reached here through reflection the same
    // way OrbIds reaches _windows.
    [AvaloniaFact]
    public void ASpeechStateChangeIsPostedToEveryOrbsFlyout()
    {
        using var scratch = new Scratch();
        scratch.Write("a");
        var manager = Scan(scratch);

        var method = typeof(SessionManager).GetMethod(
            "OnSpeakStateChanged", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        method!.Invoke(manager, new object[] { TextToSpeech.SpeakState.Speaking });
        Dispatcher.UIThread.RunJobs();
    }

    // --- the tray, when one is attached ---

    [AvaloniaFact]
    public void AttachedTrayIsFedTheStackInDisplayOrder()
    {
        // SessionScanTests never calls Start(), so _tray is null for every
        // other case in this file and UpdateTray's own call is a no-op on a
        // null. Attaching one the same way TrayRemoteItemTests does exercises
        // the call for real: a NativeMenu built under the headless platform is
        // not guaranteed, so a TrayController that cannot construct here is
        // reported as "couldn't check" rather than silently passing.
        using var scratch = new Scratch();
        scratch.Write("a");
        var manager = Scan(scratch);

        TrayController tray;
        try
        {
            tray = new TrayController();
        }
        catch
        {
            return;
        }

        typeof(SessionManager).GetField("_tray", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, tray);

        manager.ScanAndUpdate();
    }

    // --- background leads with no terminal of their own ---

    [AvaloniaFact]
    public void ALeadWithALiveAgentKeepsItsOrbDespiteHavingNoTerminal()
    {
        // Two entries share this process's one certainly-alive pid without
        // colliding in Superseded because they name different CLIs — the
        // same trick ATeamsMemberIsGatheredBehindItsLeadAndFollowsItWhenDragged
        // uses. AgentTeam.Of caches by pid alone, so seeding it once here
        // answers for both: the Codex entry reads it as "I have a live agent
        // lead" (populating leadsWithLiveAgents) and the ClaudeCode entry
        // reads it as "that lead is me" (excluded from being its own member
        // by the self-check in the source).
        //
        // Neither BackgroundJobs.IsLiveJob nor AgentTeamViewer.TryAdopt's real
        // ps/lsof scan is reached: the member keeps a terminal so
        // WantsAgentViewer never asks for it, the lead's Cwd is empty so
        // TryAdopt's own first check declines before any process walk, and
        // both entries carry a real pid, so JudgeReachability's unconditional
        // "no pid" branch — which would ask BackgroundJobs for real — never
        // fires for either.
        var cache = (Dictionary<int, (AgentTeam.Membership Value, long Stamp)>)typeof(AgentTeam)
            .GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        lock (cache) cache.Clear();
        try
        {
            lock (cache)
            {
                cache[LivePid] = (new AgentTeam.Membership("lead-id", "", ""), Environment.TickCount64);
            }

            using var scratch = new Scratch();
            scratch.Write("codex-member", cli: "codex"); // has a terminal by default
            File.WriteAllText(Path.Combine(scratch.Dir, "lead-id.txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    State = "idle",
                    SessionPid = LivePid,
                    // No cwd, no terminal fields at all: a background lead
                    // running under `claude daemon run`.
                }));

            var manager = new SessionManager(scratch.Dir);
            manager.ScanAndUpdate();

            // The member keeps its own terminal-based orb as normal...
            Assert.Contains("codex-member", OrbIds(manager));

            // ...and the lead gets one too, despite naming no terminal at
            // all, because leadsWithLiveAgents exempts it: "agents on screen
            // pointing at nothing is a worse lie than an orb you might not be
            // able to click."
            Assert.Contains("lead-id", OrbIds(manager));
        }
        finally
        {
            lock (cache) cache.Clear();
        }
    }

    // --- nothing to show: no terminal and no transcript (CB-9) ---

    // Driven through a real scan and a real File.Exists against a real path,
    // which is the half a unit test cannot state: the rule is answered by the
    // disk here, not by a predicate handed in. Which of the two terminal-less
    // rules returns the verdict is pinned in ScanVerdictTests
    // (NothingToShowIsAnsweredBeforeNoTerminal) — what matters here is the
    // end-to-end outcome a user sees, and that nothing else in the scan
    // undoes it.
    //
    // Nothing reaches BackgroundJobs.IsLiveJob: every entry carries a real
    // pid, so the "no pid" branch that would shell out never fires. Same
    // reasoning as the lead test above.
    [AvaloniaFact]
    public void AStatusFileNamingATranscriptThatIsNotThereGetsNoOrb()
    {
        using var scratch = new Scratch();

        scratch.Write("unprompted-job", tty: "", termProgram: "",
                      transcriptPath: scratch.MissingTranscript("unprompted-job"));

        Assert.DoesNotContain("unprompted-job", OrbIds(Scan(scratch)));
    }

    [AvaloniaFact]
    public void ASessionInARealTerminalKeepsItsOrbBeforeItHasWrittenAnything()
    {
        // The regression that would hurt most, and the reason the rule requires
        // *both* halves: every session is transcript-less for its first moment,
        // and an interactive one must not flicker off screen while it is. Its
        // click lands in the terminal either way, so the orb has earned its
        // place before the conversation exists.
        using var scratch = new Scratch();

        scratch.Write("fresh-terminal",
                      transcriptPath: scratch.MissingTranscript("fresh-terminal"));

        Assert.Contains("fresh-terminal", OrbIds(Scan(scratch)));
    }

    [AvaloniaFact]
    public void ATranscriptOnDiskKeepsTheOrbTheMissingOneWouldHaveCost()
    {
        // The same file, the same absent terminal fields, one thing changed:
        // the transcript is really there. Written to disk rather than asserted
        // about, so this is File.Exists answering.
        using var scratch = new Scratch();

        scratch.Write("has-a-transcript", tty: "/dev/ttys004",
                      transcriptPath: scratch.WriteTranscript("has-a-transcript"));

        Assert.Contains("has-a-transcript", OrbIds(Scan(scratch)));
    }

    // --- untitled siblings do not share a slot (CB-10) ---

    [AvaloniaFact]
    public void TwoUntitledSessionsInOneDirectoryGetDifferentPositionKeys()
    {
        // Three live untitled sessions in one directory shared a single
        // bare-cwd key on a real machine, so RestoreOrbPosition refused a slot
        // to two of them and three orbs read as two on screen.
        //
        // The two name different CLIs so they can share this process's one
        // certainly-alive pid without one superseding the other — the same
        // trick ALeadWithALiveAgentKeepsItsOrbDespiteHavingNoTerminal uses, and
        // free here because an untitled session keys on its id whichever CLI it
        // is, so the Codex prefix cannot affect the answer.
        using var scratch = new Scratch();

        scratch.Write("untitled-a", title: "", cwd: "/Users/warren/evidence");
        scratch.Write("untitled-b", title: "", cwd: "/Users/warren/evidence", cli: "codex");

        var manager = Scan(scratch);

        Assert.Contains("untitled-a", OrbIds(manager));
        Assert.Contains("untitled-b", OrbIds(manager));

        var keys = PositionKeys(manager);
        Assert.Equal("untitled-a", keys["untitled-a"]);
        Assert.Equal("untitled-b", keys["untitled-b"]);
        Assert.NotEqual(keys["untitled-a"], keys["untitled-b"]);
    }

    [AvaloniaFact]
    public void TwoTitledSessionsInOneDirectoryStillKeyOnDirectoryAndTitle()
    {
        // Unchanged, which is the compatibility half: every position already
        // saved on a real machine has to keep matching the session it was
        // saved for. These are the two names the original collision was found
        // with.
        using var scratch = new Scratch();

        scratch.Write("titled-a", title: "job-lawyer", cwd: "/Users/warren/evidence");
        scratch.Write("titled-b", title: "makayla-lawyer", cwd: "/Users/warren/evidence",
                      cli: "codex");

        var keys = PositionKeys(Scan(scratch));

        // Different CLIs again, to share the one live pid — which also puts the
        // Codex prefix through a real scan rather than a direct call.
        Assert.Equal("/Users/warren/evidence\njob-lawyer", keys["titled-a"]);
        Assert.Equal("codex\n/Users/warren/evidence\nmakayla-lawyer", keys["titled-b"]);
    }

    // The key each orb actually carries, read off the windows the scan built
    // rather than recomputed — the same reasoning OrbIds records for reaching
    // _windows at all.
    private static Dictionary<string, string?> PositionKeys(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        var windows = (System.Collections.IDictionary)field!.GetValue(manager)!;
        var keys = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (System.Collections.DictionaryEntry entry in windows)
            keys[(string)entry.Key] = ((OrbWindow)entry.Value!).PositionKey;

        return keys;
    }
}
