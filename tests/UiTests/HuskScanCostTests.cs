using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-22: the real per-scan cost of the FileInfo stat CouldBeABackgroundedHusk's
// widened admission opened up.
//
// CB-20 changed CouldBeABackgroundedHusk from "the daemon confirmed this
// session is a job it knows about" to "the daemon has not ruled a job out"
// (NotAJob or Unknown) — which is every ordinary terminal session on a
// machine that has nothing background-ish running at all, not only genuine
// husks. QA flagged, non-blocking, on PR #83 that nobody had actually run
// that cost at a realistic orb count — only reasoned that a stat is
// "sub-millisecond on local disk", which is the estimate this test replaces
// with a real number.
//
// Fifteen live ClaudeCode sessions, each naming its own transcript on disk and
// its own genuinely-running pid, borrowed from whatever else happens to be
// running on the machine this test executes on: ProcessLiveness.IsRunning is
// the real kill(pid, 0)/GetProcessById here, with no seam to fake it the way
// jobListing and attachClients have one, so JudgeLiveness needs a pid that
// really is alive or the session reads as ProcessGone before the husk check
// ever runs. Every pid is distinct and every status file names a terminal
// (TermProgram), which is what keeps SessionPresence.WorthAskingTheDaemon
// false for all fifteen — two ClaudeCode sessions sharing one pid would read
// as an Agent View lead and send the scan to a real `claude agents --json`
// subprocess (see SessionScanTests' own note on this), which would measure
// that subprocess rather than the stat CB-20 added. One transcript grows on
// every iteration, standing in for the mid-generation session the ticket
// asks for: it is the one file whose TranscriptHandoff answer can never be
// served from the length+mtime cache, because it never stops changing.
[Collection("Settings")]
public class HuskScanCostTests
{
    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } =
            Path.Combine(Path.GetTempPath(), "cb-husk-cost-" + Guid.NewGuid());

        public Scratch() => Directory.CreateDirectory(Dir);

        public void Write(string sessionId, int pid, string? transcriptPath) =>
            File.WriteAllText(Path.Combine(Dir, sessionId + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    State = "generating",
                    Cli = "",
                    Title = "CB-22 scan cost",
                    Cwd = "/Users/user/project-" + sessionId,
                    SessionPid = pid,
                    TermProgram = "iTerm.app",
                    Tty = "/dev/ttys" + (pid % 1000).ToString("D3"),
                    TranscriptPath = transcriptPath ?? "",
                }));

        public string WriteTranscript(string sessionId, string firstLine)
        {
            var path = Path.Combine(Dir, sessionId + ".jsonl");
            File.WriteAllText(path, firstLine + "\n");
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    // Real, distinct, currently-running pids — anything already alive on the
    // machine running this test, not a fabricated number ProcessLiveness
    // would have to take on faith. kill(pid, 0) (or GetProcessById on
    // Windows) needs the real thing, or the session reads as ProcessGone
    // before CouldBeABackgroundedHusk is ever asked.
    private static int[] FifteenLivePids()
    {
        var pids = Process.GetProcesses()
            .Select(p => p.Id)
            .Where(id => id > 0)
            .Distinct()
            .Take(15)
            .ToArray();
        Assert.True(pids.Length == 15,
            $"need 15 distinct live pids to measure a realistic scan; this machine only offered {pids.Length}");
        return pids;
    }

    // Scans the same 15-session, one-mid-generation shape `iterations` times,
    // appending to the growing transcript before each pass, and returns the
    // average milliseconds per scan. `withTranscripts` toggles whether the
    // status files name a transcript at all: false is the control this test
    // reads its answer from — CouldBeABackgroundedHusk's own gate short-
    // circuits on an empty TranscriptPath before either a stat or a read, so
    // that run pays for everything else ScanAndUpdate does (JSON parse, orb
    // creation and update, the pid/terminal bookkeeping) and nothing this
    // ticket is about. The difference between the two is the number CB-22
    // asks for.
    private static double MeasureScan(int[] pids, bool withTranscripts, int iterations)
    {
        using var scratch = new Scratch();

        string? growing = null;
        for (var i = 0; i < pids.Length; i++)
        {
            var id = $"scan-cost-{i}";
            string? transcript = null;
            if (withTranscripts)
            {
                transcript = scratch.WriteTranscript(id,
                    "{\"type\":\"assistant\",\"message\":{\"content\":\"hello\"}}");
                if (i == 0) growing = transcript;
            }
            scratch.Write(id, pids[i], transcript);
        }

        var manager = new SessionManager(scratch.Dir);
        manager.ScanAndUpdate(); // first pass: JIT, window creation, cold caches

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            // The mid-generation session: its transcript grows every scan, so
            // TranscriptHandoff's length+mtime cache can never save the tail
            // read for this one — the case CB-22 asks be measured, rather
            // than the all-cached steady state the other fourteen settle
            // into after the first pass. No-op when there is no transcript
            // to grow, i.e. in the control run.
            if (growing is not null)
            {
                File.AppendAllText(growing,
                    $"{{\"type\":\"assistant\",\"message\":{{\"content\":\"tick {i}\"}}}}\n");
            }

            manager.ScanAndUpdate();
        }
        stopwatch.Stop();

        return stopwatch.Elapsed.TotalMilliseconds / iterations;
    }

    [AvaloniaFact]
    public void FifteenLiveSessionsOneMidGenerationScanInARealisticTime()
    {
        ClaudeBuddySettings.ClaudeCodeEnabled = true;
        ClaudeBuddySettings.CodexEnabled = true;

        var pids = FifteenLivePids();
        const int iterations = 300;

        var withTranscripts = MeasureScan(pids, withTranscripts: true, iterations);
        var withoutTranscripts = MeasureScan(pids, withTranscripts: false, iterations);
        var huskCheckCost = withTranscripts - withoutTranscripts;

        // A sanity ceiling, not a tight budget. Real numbers from two runs on
        // the Mac this was written on: 1.12ms and 1.16ms per scan with
        // transcripts named, against 0.75ms and 0.82ms without — a husk-check
        // cost of roughly 0.34-0.37ms per scan of 15 sessions with one
        // growing transcript, copied by hand into ScanAndUpdate's closure
        // comment and the README internals section rather than estimated.
        // 25ms is about twenty times that, so this exists to catch something
        // turning pathological later (an accidental O(n^2), a stat added per
        // line rather than per file) rather than to hold today's number in
        // place.
        Assert.True(withTranscripts < 25.0,
            $"scan of {pids.Length} sessions (1 mid-generation, transcripts named) averaged " +
            $"{withTranscripts:F4}ms/scan over {iterations} iterations ({withoutTranscripts:F4}ms/scan " +
            $"without transcripts, {huskCheckCost:F4}ms/scan attributable to the husk check), " +
            $"over the 25ms sanity ceiling");
    }
}
