using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-106: a live `sample` caught the main thread pinned for 8-11 seconds
// inside a raw sync() syscall, reached through Avalonia's native macOS
// run-loop signaller into managed code the dispatcher was running. The
// strongest candidate found by inspection was SessionManager.ScanAndUpdate's
// file-reading half (Directory.EnumerateFiles, File.GetLastWriteTimeUtc,
// FileStream opens, plus the transcript-repair hunt) — the one piece of scan
// work that ran unconditionally on the Avalonia dispatcher every two seconds,
// forever, with no backgrounding, unlike the Grok-refresh tick right next to
// it in Start() which was already moved off the UI thread for the same
// reason.
//
// This suite covers the fix: ScheduleScan, the new entry point Start() wires
// the poll timer and the watcher's debounce to instead of ScanAndUpdate
// directly. It runs the disk read on a background thread and only resumes
// the window/tray reconciliation on the thread that called it (the UI
// thread, in production).
//
// ScanAndUpdate itself is untouched behaviourally — see SessionScanTests,
// RemoteScanTests and LocalPersonaUiTests, all of which call it directly and
// all of which stayed green through this refactor — so this suite is only
// about the new asynchronous entry point and its re-entrancy guard, not about
// re-proving the scan's own rules.
[Collection("Settings")]
public class ScheduleScanTests
{
    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } =
            Path.Combine(Path.GetTempPath(), "cb-schedulescan-" + Guid.NewGuid());

        public Scratch() => Directory.CreateDirectory(Dir);

        public void Write(string sessionId, string state = "idle")
        {
            File.WriteAllText(
                Path.Combine(Dir, sessionId + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    State = state,
                    Cli = "",
                    Title = "",
                    Cwd = "/Users/user/project",
                    SessionPid = Environment.ProcessId,
                    TermProgram = "iTerm.app",
                    Tty = "/dev/ttys004",
                }));
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static SessionManager Manager(Scratch scratch)
    {
        ClaudeBuddySettings.ClaudeCodeEnabled = true;
        ClaudeBuddySettings.CodexEnabled = true;
        return new SessionManager(scratch.Dir);
    }

    // The regression check: if ScheduleScan were ever changed back to running
    // its file reads inline (the CB-106 bug), the task it returns would
    // already be complete by the time the call returns, since there would be
    // no thread hop to suspend on. Task.Run never inlines its delegate on the
    // calling thread, so the awaited task is guaranteed pending at this point
    // — deterministic, not a timing assumption, and exactly the property that
    // matters here: nothing about this scan's file I/O runs before control
    // returns to whichever thread called it.
    [AvaloniaFact]
    public void ScheduleScanReturnsBeforeTheDiskReadCompletes()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a");
        var manager = Manager(scratch);

        var task = manager.ScheduleScan();

        Assert.False(task.IsCompleted);
    }

    // Once the background read and the reconciliation it feeds have both run,
    // the result is identical to a synchronous ScanAndUpdate: the same orb,
    // carrying the same status. ScheduleScan is a different path to the same
    // answer, not a different answer.
    [AvaloniaFact]
    public async Task ScheduleScanProducesTheSameStateAsScanAndUpdate()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", state: "waiting");
        var manager = Manager(scratch);

        await manager.ScheduleScan();

        var status = manager.StatusFor("session-a");
        Assert.NotNull(status);
        Assert.Equal("waiting", status!.State);
    }

    // The re-entrancy guard: a second ScheduleScan while the first is still
    // reading disk must not start a second read on top of it — CB-106's own
    // finding was that heavy *ambient* disk contention is what turns an
    // ordinary read into a multi-second block, and piling a second read on
    // top of a slow one only adds to that contention. A guarded call returns
    // Task.CompletedTask synchronously, before ever touching Task.Run, so it
    // is already complete the instant it returns — unlike the first call,
    // which this test already proved above is never synchronously complete.
    [AvaloniaFact]
    public async Task ASecondScheduleScanWhileTheFirstIsInFlightIsSkipped()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a");
        var manager = Manager(scratch);

        var first = manager.ScheduleScan();
        var second = manager.ScheduleScan();

        Assert.False(first.IsCompleted);
        Assert.True(second.IsCompletedSuccessfully);

        await first;
    }

    // The guard releases once a scan finishes, so the *next* tick after one
    // completes is not permanently locked out by a scan that has long since
    // finished — a stuck flag would silently stop every future poll from
    // ever updating an orb again, which is a worse bug than the one this
    // fixes.
    [AvaloniaFact]
    public async Task TheGuardReleasesAfterAScanCompletes()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a");
        var manager = Manager(scratch);

        await manager.ScheduleScan();

        var next = manager.ScheduleScan();
        Assert.False(next.IsCompleted);

        await next;
    }
}
