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
        public void Write(
            string sessionId, string state = "idle", string cli = "",
            string title = "", string cwd = "/Users/warren/project",
            int? pid = null, string termProgram = "iTerm.app",
            DateTime? written = null)
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
                Tty = "/dev/ttys004",
            }));

            if (written is not null) File.SetLastWriteTimeUtc(path, written.Value);
        }

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

            // The listing is handed over rather than fetched, which it did not
            // used to need to be. The lead below names no terminal, and the scan
            // now asks the daemon about exactly that shape of session — see
            // SessionPresence.WorthAskingTheDaemon — so without this seam this
            // test would run `claude agents --json` against whatever daemon the
            // machine happens to be running. Empty rather than null: this lead
            // is not a job, and saying so is what the rest of the test is about.
            var manager = new SessionManager(scratch.Dir, () => Listing());
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

    // --- background jobs: presence, and the hygiene sweep -------------------
    //
    // The scan's half of CB-13. The rules are pure and covered case by case in
    // tests/UnitTests (SessionPresenceTests, SweepRulesTests, JobPhaseTests);
    // what is left — and what the bug actually was — is whether the scan wires
    // them to anything. Fifteen orbs breathing on an idle machine was not a
    // wrong rule, it was a right answer that was parsed and then discarded.
    //
    // Every test below hands over the daemon's listing through the constructor
    // seam rather than letting the scan fetch one. That is not only about speed:
    // the machine this suite runs on is the one that exhibited the bug, and its
    // real listing names the user's real background sessions. A test that read
    // it would be asserting about those.

    private static Dictionary<string, string> Listing(params (string SessionId, string State)[] rows)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (sessionId, state) in rows) map[sessionId] = state;
        return map;
    }

    private static SessionManager Manager(
        Scratch scratch,
        Func<Dictionary<string, string>?>? jobListing = null,
        TimeSpan? sweepGrace = null)
    {
        // Both CLIs on, for the reason Scan above states at length.
        ClaudeBuddySettings.ClaudeCodeEnabled = true;
        ClaudeBuddySettings.CodexEnabled = true;

        var manager = new SessionManager(scratch.Dir, jobListing);
        if (sweepGrace is not null) manager.SweepGrace = sweepGrace.Value;
        return manager;
    }

    // A status file shaped the way a background worker's is: a real pid of its
    // own, and no terminal anywhere — not even a tty, because the hook's walk
    // runs under a daemon that has no controlling terminal to find. That shape
    // is load-bearing in both directions and worth stating: it is what makes a
    // *live* job depend on the daemon's exemption for its orb (JudgeReachability
    // would otherwise call it a dead click), and it is what already drops a
    // *finished* one's orb, which is the behaviour the sweep is bolted onto.
    //
    // The empty cwd is what keeps AgentTeamViewer.TryAdopt's real ps/lsof scan
    // out of this, exactly as the sibling test above does — WantsAgentViewer
    // fires for this shape by design, and TryAdopt's own first check is what
    // declines.
    private static void WriteBackgroundFile(Scratch scratch, string sessionId, string state = "idle")
    {
        File.WriteAllText(Path.Combine(scratch.Dir, sessionId + ".txt"),
            System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                State = state,
                SessionPid = LivePid,
            }));
    }

    [AvaloniaFact]
    public void ABlockedBackgroundJobIsDimmedAndBadgedRatherThanRemoved()
    {
        // The orb on the screenshot this ticket came from. "blocked" is a pooled
        // worker between turns: alive, resumable, and doing nothing — and its
        // own status file says "idle", exactly as a job mid-turn's does.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "bg-parked");

        var manager = Manager(scratch, () => Listing(("bg-parked", "blocked")));
        manager.ScanAndUpdate();

        // Kept, first of all. Parking dims an orb and must never remove one.
        Assert.Contains("bg-parked", OrbIds(manager));

        var status = manager.StatusFor("bg-parked");
        Assert.NotNull(status);
        Assert.Equal(LocalSessionShape.Background, status!.Shape);
        Assert.True(status.Parked);
        Assert.Equal(SessionKind.Background, status.Kind);
    }

    [AvaloniaFact]
    public void AWorkingBackgroundJobIsBadgedButNotDimmed()
    {
        // The wedged session on the same screenshot, and the reason the badge is
        // not conditional: this one is genuinely at work as far as the daemon is
        // concerned, and the orb has to say so.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "bg-working", state: "generating");

        var manager = Manager(scratch, () => Listing(("bg-working", "working")));
        manager.ScanAndUpdate();

        var status = manager.StatusFor("bg-working");
        Assert.NotNull(status);
        Assert.Equal(LocalSessionShape.Background, status!.Shape);
        Assert.False(status.Parked);
        Assert.Equal(SessionKind.Background, status.Kind);
    }

    [AvaloniaFact]
    public void AResumedJobUnDimsOnTheNextScanWhileTheListingIsStillCatchingUp()
    {
        // The cache-lag fix, end to end. The daemon's listing is cached for ten
        // seconds; the hook rewrites the status file the instant a turn starts.
        // So the file is the fresher source, and the same "blocked" row must
        // stop parking the orb as soon as the file says work resumed — otherwise
        // an orb sits dim for ten seconds while the user watches the work happen.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "bg-resumed");

        var manager = Manager(scratch, () => Listing(("bg-resumed", "blocked")));
        manager.ScanAndUpdate();
        Assert.True(manager.StatusFor("bg-resumed")!.Parked);

        WriteBackgroundFile(scratch, "bg-resumed", state: "generating");
        manager.ScanAndUpdate();

        var status = manager.StatusFor("bg-resumed");
        Assert.Equal(LocalSessionShape.Background, status!.Shape);
        Assert.False(status.Parked);
    }

    [AvaloniaFact]
    public void AListingThatCouldNotBeReadParksNothingAndRemovesNothing()
    {
        // Fail open, at the level that matters: the `claude` CLI being briefly
        // unavailable must not dim every background orb on screen at once, and
        // must not delete any file either.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "bg-unknown");

        var manager = Manager(scratch, () => null, sweepGrace: TimeSpan.Zero);
        manager.ScanAndUpdate();
        manager.ScanAndUpdate();

        var status = manager.StatusFor("bg-unknown");
        Assert.NotNull(status);
        Assert.False(status!.Parked);

        // An unreadable listing keeps the orb (BackgroundJobs.IsLive answers
        // true) and cannot be evidence of anything, so the file survives even
        // with no grace period at all.
        Assert.True(File.Exists(Path.Combine(scratch.Dir, "bg-unknown.txt")));
        Assert.Contains("bg-unknown", OrbIds(manager));
    }

    // --- the sweep ---

    // A pid that was never allocated: 2147483646 is far above any platform's
    // pid_max, so it cannot name a process on the machine running this. Used
    // rather than spawning and reaping a real child, which
    // ProcessLivenessTests does because it is asking about a *reaped* pid
    // specifically — here all that is wanted is a pid the kernel says nothing
    // is behind, and inventing one nothing can ever collide with is both
    // cheaper and safer. Nothing in this suite signals it.
    private const int NeverAllocatedPid = int.MaxValue - 1;

    [AvaloniaFact]
    public void ADeadPidsFileIsSweptOnceTheGraceHasPassed()
    {
        // The Ctrl+C case. SessionEnd never fired, so nothing has ever deleted
        // this file — the orb goes on the first scan and the file stayed for
        // good, on every machine, until this sweep existed.
        using var scratch = new Scratch();
        scratch.Write("ctrl-c-ed", pid: NeverAllocatedPid);
        var path = Path.Combine(scratch.Dir, "ctrl-c-ed.txt");

        var manager = Manager(scratch, () => Listing(), sweepGrace: TimeSpan.Zero);

        // The first pass records the sighting and deletes nothing, whatever the
        // grace is: evidence has to survive two consecutive scans before a file
        // goes, which is what a momentarily unreadable pid is protected by.
        manager.ScanAndUpdate();
        Assert.DoesNotContain("ctrl-c-ed", OrbIds(manager));
        Assert.True(File.Exists(path));

        manager.ScanAndUpdate();
        Assert.False(File.Exists(path));
    }

    [AvaloniaFact]
    public void AFinishedJobsFileIsSweptEvenThoughItsWorkerIsStillAlive()
    {
        // The case no liveness rule can ever reach, and the reason the sweep
        // consults the job phase at all: a finished job's pooled worker is kept
        // alive on purpose, so its pid answers forever. Before this, every job
        // anyone ever ran left a file behind permanently.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "bg-done");
        var path = Path.Combine(scratch.Dir, "bg-done.txt");

        var manager = Manager(scratch, () => Listing(("bg-done", "done")), sweepGrace: TimeSpan.Zero);

        manager.ScanAndUpdate();
        Assert.True(File.Exists(path));

        // Its orb is already gone — a finished job has nothing to show, which
        // BackgroundJobs was written for — and now the file goes too.
        Assert.DoesNotContain("bg-done", OrbIds(manager));

        manager.ScanAndUpdate();
        Assert.False(File.Exists(path));
    }

    [AvaloniaFact]
    public void AJobThatComesBackRestartsTheGraceClock()
    {
        // Evidence that goes away resets the clock rather than being remembered.
        // Without this a job that finishes, is resumed, and finishes again would
        // be swept on the strength of the accumulated total of its quiet spells —
        // and a resumed job's file is the only place its identity lives.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "bg-flapping");
        var path = Path.Combine(scratch.Dir, "bg-flapping.txt");

        var state = "done";
        var manager = Manager(
            scratch, () => Listing(("bg-flapping", state)), sweepGrace: TimeSpan.Zero);

        manager.ScanAndUpdate();            // sighting recorded
        state = "working";
        manager.ScanAndUpdate();            // ...and forgotten again
        state = "done";
        manager.ScanAndUpdate();            // sighting recorded afresh

        Assert.True(File.Exists(path));

        // Only now, on a second consecutive sighting, does it go.
        manager.ScanAndUpdate();
        Assert.False(File.Exists(path));
    }

    [AvaloniaFact]
    public void AQuietSessionIsNeverSweptHoweverLongItHasBeenQuiet()
    {
        // Expiry is the user's own display setting, not evidence about the
        // session — and this is the one that would be a disaster: the file is
        // the only place a live session's terminal coordinates and colour live,
        // and the hook writes nothing more until its next event. Deleting it
        // would take the orb away and leave the session running.
        var before = ClaudeBuddySettings.OrbLifetimeMinutes;
        try
        {
            ClaudeBuddySettings.OrbLifetimeMinutes = 5;

            using var scratch = new Scratch();
            scratch.Write("quiet", written: DateTime.UtcNow - TimeSpan.FromHours(2));
            var path = Path.Combine(scratch.Dir, "quiet.txt");

            var manager = Manager(scratch, () => Listing(), sweepGrace: TimeSpan.Zero);
            manager.ScanAndUpdate();
            manager.ScanAndUpdate();

            // Orb gone, file kept.
            Assert.DoesNotContain("quiet", OrbIds(manager));
            Assert.True(File.Exists(path));
        }
        finally
        {
            ClaudeBuddySettings.OrbLifetimeMinutes = before;
        }
    }

    [AvaloniaFact]
    public void ADeadPidsFileSurvivesUntilTheGraceHasActuallyPassed()
    {
        // The default ten minutes, so the second sighting is not yet due. The
        // grace exists because the evidence can be momentarily wrong in one
        // direction that matters — a recycled or briefly unreadable pid — and ten
        // minutes of a file nobody is looking at costs nothing, while deleting a
        // file the app was wrong about costs the user their session's identity.
        using var scratch = new Scratch();
        scratch.Write("recently-dead", pid: NeverAllocatedPid);
        var path = Path.Combine(scratch.Dir, "recently-dead.txt");

        var manager = Manager(scratch, () => Listing());

        manager.ScanAndUpdate();
        manager.ScanAndUpdate();
        manager.ScanAndUpdate();

        Assert.True(File.Exists(path));
    }

    // --- orphaned team members ---

    // The green cluster on the screenshot: real claude processes in a detached
    // tmux socket, with status files carrying tmux fields, so KnowsATerminal
    // keeps them forever. Their arrows had already silently vanished with the
    // lead they pointed at — TeamLinks draws nothing to a lead that is not on
    // screen — which left the orb as the only thing still claiming anything
    // about them.
    //
    // Membership is seeded into AgentTeam's cache rather than read off a real
    // process, the same way the sibling test above does it: the real answer comes
    // from a pid's command line, which a test cannot arrange without spawning a
    // `claude` process carrying agent-team arguments.
    private static Dictionary<int, (AgentTeam.Membership Value, long Stamp)> TeamCache() =>
        (Dictionary<int, (AgentTeam.Membership Value, long Stamp)>)typeof(AgentTeam)
            .GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    [AvaloniaFact]
    public void ATeamMemberWhoseLeadHasGoneIsDimmed()
    {
        var cache = TeamCache();
        lock (cache) cache.Clear();
        try
        {
            lock (cache)
            {
                cache[LivePid] = (
                    new AgentTeam.Membership("absent-lead", "QA", ""), Environment.TickCount64);
            }

            using var scratch = new Scratch();
            scratch.Write("orphan");

            var manager = Manager(scratch, () => Listing());
            manager.ScanAndUpdate();

            var status = manager.StatusFor("orphan");
            Assert.NotNull(status);
            Assert.Equal("absent-lead", status!.Lead);
            Assert.Equal(LocalSessionShape.Teammate, status.Shape);
            Assert.True(status.Parked);

            // Dimmed, not removed — for the same reason a parked job is kept.
            Assert.Contains("orphan", OrbIds(manager));
        }
        finally
        {
            lock (cache) cache.Clear();
        }
    }

    [AvaloniaFact]
    public void ATeamMemberWhoseLeadIsOnScreenIsNotDimmed()
    {
        // The other half, and the one that would be the worse failure: dimming a
        // live team says four agents at work are doing nothing.
        //
        // Two files on one pid without colliding in Superseded, because they name
        // different CLIs — the trick the sibling tests use. AgentTeam.Of caches
        // by pid alone, so the one seeded membership answers for both: the Codex
        // entry never consults it (Lead is only derived for Claude Code), and the
        // Claude Code entry reads its lead out of it.
        var cache = TeamCache();
        lock (cache) cache.Clear();
        try
        {
            lock (cache)
            {
                cache[LivePid] = (
                    new AgentTeam.Membership("present-lead", "QA", ""), Environment.TickCount64);
            }

            using var scratch = new Scratch();
            scratch.Write("present-lead", cli: "codex");
            scratch.Write("member");

            var manager = Manager(scratch, () => Listing());
            manager.ScanAndUpdate();

            var status = manager.StatusFor("member");
            Assert.NotNull(status);
            Assert.Equal(LocalSessionShape.Teammate, status!.Shape);
            Assert.False(status.Parked);
        }
        finally
        {
            lock (cache) cache.Clear();
        }
    }

    // --- dismiss and end ---

    [AvaloniaFact]
    public void DismissingAnOrbDeletesItsFileAndTheNextScanTakesTheOrb()
    {
        // Deleting the file is the whole of it: in the app the watcher's Deleted
        // event drives the debounced rescan, which is driven directly here for
        // the reason this suite never calls Start().
        using var scratch = new Scratch();
        scratch.Write("dismiss-me");

        var manager = Scan(scratch);
        Assert.Contains("dismiss-me", OrbIds(manager));

        manager.DismissSession("dismiss-me");
        Assert.False(File.Exists(Path.Combine(scratch.Dir, "dismiss-me.txt")));

        manager.ScanAndUpdate();
        Assert.DoesNotContain("dismiss-me", OrbIds(manager));
        Assert.Null(manager.StatusFor("dismiss-me"));
    }

    [AvaloniaFact]
    public void DismissingASessionThatLivesSomewhereElseTouchesNothing()
    {
        // A gateway session's orb comes from a socket, and the path this would
        // build from its namespaced key is not one this app should write to.
        // Seeded through _statuses directly rather than by standing up a gateway,
        // the same way OrbIds reaches _windows: what is being tested is the
        // guard, and the guard reads exactly this dictionary.
        using var scratch = new Scratch();
        var manager = Manager(scratch, () => Listing());

        var statuses = (Dictionary<string, SessionStatus>)typeof(SessionManager)
            .GetField("_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;

        const string key = "openclaw:agent:main:discord:channel:1";
        statuses[key] = new SessionStatus { Source = SessionSource.OpenClaw };

        // A file named to prove the guard returns before any delete: were the
        // key spliced into a path, this is what it would have found.
        var decoy = Path.Combine(scratch.Dir, "decoy.txt");
        File.WriteAllText(decoy, "{}");

        manager.DismissSession(key);

        Assert.True(File.Exists(decoy));
    }

    [AvaloniaFact]
    public void TheSweepForgetsAnIdWhoseFileGoesAwayByOtherMeans()
    {
        // A file can leave the directory while the sweep is still counting down
        // on it: SessionEnd fires, or the user dismisses the orb. Without this
        // the dictionary keeps the id for the life of the process — a slow leak
        // keyed on every session the app has ever seen die, and a stale clock if
        // the same id ever came back.
        //
        // The default ten-minute grace, deliberately, so nothing is swept and
        // the only thing under test is the forgetting.
        using var scratch = new Scratch();
        scratch.Write("gone-elsewhere", pid: NeverAllocatedPid);

        var manager = Manager(scratch, () => Listing());
        manager.ScanAndUpdate();

        var deadSince = (Dictionary<string, DateTime>)typeof(SessionManager)
            .GetField("_deadSince", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;

        Assert.Contains("gone-elsewhere", deadSince.Keys);

        scratch.Delete("gone-elsewhere");
        manager.ScanAndUpdate();

        Assert.Empty(deadSince);
    }

    [AvaloniaFact]
    public void AStatusFileThatCannotBeDeletedIsLeftAloneRatherThanThrowing()
    {
        // Both the sweep and Dismiss go through one delete that swallows its
        // failure, because there is nowhere to report one: a file this process
        // may not delete is simply found again on the next scan.
        //
        // Provoked with a read-only status directory, which is a POSIX
        // permission — on Windows a read-only directory does not stop a delete,
        // so there is no equivalent to arrange and this returns instead of
        // asserting something untrue. That leaves the catch measured on the
        // macOS and Linux legs and unmeasured on the Windows one, which is the
        // same shape as every other platform split in this repo.
        if (OperatingSystem.IsWindows()) return;

        using var scratch = new Scratch();
        scratch.Write("undeletable");
        var path = Path.Combine(scratch.Dir, "undeletable.txt");

        var manager = Scan(scratch);
        Assert.Contains("undeletable", OrbIds(manager));

        // Readable and listable, not writable: the scan can still read the file,
        // and only the unlink fails.
        File.SetUnixFileMode(scratch.Dir,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            manager.DismissSession("undeletable");

            Assert.True(File.Exists(path));
        }
        finally
        {
            File.SetUnixFileMode(scratch.Dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // ...and the orb is still there on the next scan, which is the point of
        // failing this way rather than optimistically removing it.
        manager.ScanAndUpdate();
        Assert.Contains("undeletable", OrbIds(manager));
    }

    [AvaloniaFact]
    public void DismissingAnIdTheManagerHasNeverSeenIsHarmless()
    {
        // No status, so no guard to consult — it falls through to a delete of a
        // file that is not there, which is the catch's whole job.
        using var scratch = new Scratch();
        var manager = Manager(scratch, () => Listing());

        manager.DismissSession("never-heard-of-it");
    }

    [AvaloniaFact]
    public void EndingASessionRefusesEveryShapeItCannotEnd()
    {
        // The one irreversible thing in the app, so what is asserted here is the
        // refusals. An id with no status at all, and a session with no pid
        // recorded (a hook older than that field) — neither reaches
        // SessionTerminator, which is what makes this safe to run at all.
        using var scratch = new Scratch();
        var manager = Manager(scratch, () => Listing());

        var statuses = (Dictionary<string, SessionStatus>)typeof(SessionManager)
            .GetField("_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;

        statuses["pidless"] = new SessionStatus { Source = SessionSource.ClaudeCode, SessionPid = 0 };
        statuses["gateway"] = new SessionStatus { Source = SessionSource.OpenClaw, SessionPid = 4321 };

        manager.EndSession("never-heard-of-it");
        manager.EndSession("pidless");
        manager.EndSession("gateway");
    }

    [AvaloniaFact]
    public void EndingASessionSignalsThePidTheStatusNames()
    {
        // The one path that does reach SessionTerminator, driven with a pid
        // nothing can be behind: 2147483646 is far above any platform's pid_max,
        // so the signal lands on ESRCH (or, on Windows, an ArgumentException from
        // GetProcessById) and is swallowed — which is also the documented
        // "already gone counts as success" behaviour.
        //
        // This is deliberately not driven with a real pid. A test that ended a
        // real process on the machine running it would be a worse bug than the
        // one this ticket is about, and the machine this suite runs on has the
        // user's own background sessions on it.
        using var scratch = new Scratch();
        var manager = Manager(scratch, () => Listing());

        var statuses = (Dictionary<string, SessionStatus>)typeof(SessionManager)
            .GetField("_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;

        statuses["ends"] = new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            SessionPid = NeverAllocatedPid,
        };

        manager.EndSession("ends");

        // Nothing is removed from screen by the call itself — the orb goes when
        // the next scan sees the pid stop answering, which is the same path any
        // other ending session takes.
        Assert.Null(manager.StatusFor("no-orb-was-touched"));
    }
}
