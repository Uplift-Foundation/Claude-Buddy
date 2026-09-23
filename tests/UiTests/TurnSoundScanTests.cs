using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-167's turn sounds over a real scan, the way SessionScanTests proves the
// orb lifecycle over one: TurnSignalsTests and TurnSoundPolicyTests already
// cover the rule in isolation, but nothing there proves the scan actually
// wires Observe/Prune/Deliver together correctly — that a real
// generating→idle transition read off two real status files plays a real
// chime exactly once, and that the cases the plan calls out by name (a
// startup burst, a backgrounded husk, a manual reset) genuinely stay quiet
// rather than merely being argued to.
//
// ChimePlayer.PlayForTests stands in for the sound card, same as
// SpeechRequest.UtteranceForTests does for the speech engine elsewhere in
// this suite — every test here proves what *would* have played without a
// process, a speaker or /usr/bin/afplay anywhere near the test host.
[Collection("Settings")]
public class TurnSoundScanTests : IDisposable
{
    private static readonly int LivePid = Environment.ProcessId;

    private readonly List<string> _played = new();

    public TurnSoundScanTests()
    {
        // A fresh settings directory per test, the same isolation
        // SessionScanTests' sibling suites use — this class does not touch
        // it directly, but Snapshot() inside TurnSounds reads
        // ClaudeBuddySettings, and a stale TurnSoundsEnabled=false left by
        // another test would make every case here read as "silent for the
        // wrong reason".
        Environment.SetEnvironmentVariable(
            "CLAUDE_BUDDY_SETTINGS_DIR",
            Path.Combine(Path.GetTempPath(), "cb-turnsound-settings-" + Guid.NewGuid()));
        ClaudeBuddySettings.ReloadForTests();

        // The rate-limit clock TurnSounds owns is process-wide, so a test
        // left mid-gap by whichever case ran before this one would make an
        // otherwise-correct chime silently vanish into "too soon".
        TurnSounds.ResetForTests();

        ChimePlayer.PlayForTests = path => _played.Add(path);
    }

    public void Dispose() => ChimePlayer.PlayForTests = null;

    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } =
            Path.Combine(Path.GetTempPath(), "cb-turnsound-scan-" + Guid.NewGuid());

        public Scratch() => Directory.CreateDirectory(Dir);

        public void Write(
            string sessionId, string state, string title = "", string cwd = "/Users/user/project",
            int? pid = null, string termProgram = "iTerm.app", string tty = "/dev/ttys004",
            string transcriptPath = "")
        {
            var path = Path.Combine(Dir, sessionId + ".txt");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                State = state,
                Title = title,
                Cwd = cwd,
                SessionPid = pid ?? LivePid,
                TermProgram = termProgram,
                Tty = tty,
                TranscriptPath = transcriptPath,
            }));
        }

        public string WriteTranscript(string sessionId, params string[] rows)
        {
            var path = Path.Combine(Dir, sessionId + ".jsonl");
            var lines = new List<string> { "{\"type\":\"user\",\"message\":{\"content\":\"hi\"}}" };
            lines.AddRange(rows);
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static SessionManager Scan(Scratch scratch)
    {
        ClaudeBuddySettings.ClaudeCodeEnabled = true;
        ClaudeBuddySettings.CodexEnabled = true;

        var manager = new SessionManager(scratch.Dir);
        manager.ScanAndUpdate();
        return manager;
    }

    private static IReadOnlyCollection<string> OrbIds(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        var windows = (System.Collections.IDictionary)field!.GetValue(manager)!;
        return windows.Keys.Cast<string>().ToList();
    }

    // --- generating -> idle ---

    [AvaloniaFact]
    public void GeneratingToIdlePlaysExactlyOnce()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", state: "generating");

        var manager = Scan(scratch);
        Assert.Empty(_played); // the first scan of a generating session is a baseline

        scratch.Write("session-a", state: "idle");
        manager.ScanAndUpdate();

        Assert.Single(_played);
    }

    [AvaloniaFact]
    public void ARepeatScanOfTheSameIdleStateDoesNotReplay()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", state: "generating");
        var manager = Scan(scratch);

        scratch.Write("session-a", state: "idle");
        manager.ScanAndUpdate();
        Assert.Single(_played);

        // The file on disk is unchanged, so this scan finds idle again — the
        // no-repeat rule TurnSignalsTests already covers in isolation, now
        // proven over a real second ScanAndUpdate().
        manager.ScanAndUpdate();
        Assert.Single(_played);
    }

    // --- startup burst ---

    [AvaloniaFact]
    public void AStartupScanWithIdleAndWaitingOrbsAlreadyOnScreenStaysSilent()
    {
        using var scratch = new Scratch();
        scratch.Write("idle-one", state: "idle");
        scratch.Write("waiting-one", state: "waiting");

        Scan(scratch);

        Assert.Empty(_played);
    }

    // --- a backgrounded husk ---

    // Verbatim off the same real Claude Code row SessionScanTests' own husk
    // test uses, for the same reason: this is what a real backgrounded turn
    // actually writes, not an invented fixture.
    private const string BackgroundingMarker =
        """{"parentUuid":"1b5cf160-79bf-4e2b-a01f-6511aee6b36b","isSidechain":false,"type":"system","subtype":"informational","content":"Backgrounding after the current tool finishes…","isMeta":false,"timestamp":"2026-08-28T17:53:15.295Z","uuid":"4f19d42a-80a5-4f9e-afe6-f234587acbf5","level":"warning","userType":"external","entrypoint":"cli","cwd":"/Users/w/project","sessionId":"6d3a9d57-10c6-4e9d-bf25-38194fae23c0","version":"2.1.251","gitBranch":"develop"}""";

    [AvaloniaFact]
    public void ABackgroundedHuskFrozenAtWaitingStaysSilent()
    {
        using var scratch = new Scratch();

        var huskMarker = BackgroundingMarker.Replace(
            "6d3a9d57-10c6-4e9d-bf25-38194fae23c0", "husk", StringComparison.Ordinal);
        var huskTail = scratch.WriteTranscript("husk", huskMarker);
        scratch.Write("husk", state: "generating", transcriptPath: huskTail);

        var manager = Scan(scratch);
        Assert.DoesNotContain("husk", OrbIds(manager));

        // The husk's own file moves from generating to waiting while it is
        // hidden — exactly what a permission prompt appearing behind a
        // backgrounded turn looks like on disk. "Husks can't chime by
        // construction" means the scan never even calls Observe for a
        // session JudgeReachability has already dropped, so this must never
        // signal regardless of what the file says.
        scratch.Write("husk", state: "waiting", transcriptPath: huskTail);
        manager.ScanAndUpdate();

        Assert.Empty(_played);
    }

    // --- reset to idle ---

    [AvaloniaFact]
    public void ResetToIdlePlaysNothingEvenThoughTheSessionWasGenerating()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", state: "generating");
        var manager = Scan(scratch);

        manager.ResetSessionToIdle("session-a");

        Assert.Empty(_played);

        // And the tracker now reads "idle" the way Settle recorded it, so
        // the very next scan finding idle on disk is not a transition either
        // — the manual reset must not leave a live generating→idle armed for
        // the next ordinary tick to fire.
        scratch.Write("session-a", state: "idle");
        manager.ScanAndUpdate();

        Assert.Empty(_played);
    }
}
