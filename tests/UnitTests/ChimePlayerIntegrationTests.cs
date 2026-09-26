using System.Diagnostics;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-168 (Ines): the real-process half of ChimePlayerTests' old sweep tests.
// ChimePlayer.IChimeProcess lets ChimePlayerTests.cs prove every kill/track
// DECISION (which process, in what order, whether at all) against
// FakeChimeProcess, with no OS underneath it and no wall clock anywhere.
// What a fake cannot prove is that RealChimeProcess's three OS-facing
// members — Id, WaitForExit, Kill — actually do what they claim against a
// real subprocess. That is all these two tests are for, and it is why there
// are only two of them rather than the four sweeps this file used to hold:
// once the logic is proven once, deterministically, elsewhere, a real
// process only needs to prove the OS half still works, not re-prove the
// logic under a dozen synthetic delays.
//
// Both tests use a single generous ceiling and no fine-grained elapsed-time
// assertion at all — the opposite of the old sweeps' short fixed windows,
// which is exactly what made them flaky under load (see the git history on
// this file, and CLAUDE.md's "automated suite" section on why a wider fixed
// window is the same wrong fix). QA measured a genuinely correct kill
// landing 14ms past ChimePlayer.MaxDuration's nominal 5s under this
// machine's heaviest observed contention (a full `dotnet test
// tests/UnitTests` run). KillCeilingMs below gives that scenario ten more
// seconds of headroom on top, which is the only number these tests ever
// check a duration against — "did it die within a ceiling this generous,"
// never "how fast."
[Collection("Settings")]
public class ChimePlayerIntegrationTests
{
    private static readonly int KillCeilingMs = (int)ChimePlayer.MaxDuration.TotalMilliseconds + 15_000;

    [Fact]
    public void StopAllKillsARealAfplayProcess()
    {
        if (!OperatingSystem.IsMacOS()) return; // afplay is the only real target this test drives

        var dir = Path.Combine(Path.GetTempPath(), "cb-chime-it1-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            // Long enough that MaxDuration's own 5s cap, not the file's
            // length, is what a genuinely-never-killed process would hit —
            // this test only cares that StopAll kills it well before that.
            var path = WriteSilentWav(Path.Combine(dir, "long.wav"), ChimePlayer.MaxDuration.TotalSeconds * 4);
            var before = Process.GetProcessesByName("afplay").Select(p => p.Id).ToHashSet();

            ChimePlayer.PlayForTests = null;
            ChimePlayer.ProcessFactoryForTests = null;

            // CB-168: SilenceForTests is on process-wide for every test
            // assembly (see TestBootstrap), and StartProcess checks it right
            // after the ProcessFactoryForTests seam — this test just cleared
            // that seam specifically to reach the real BuildProcess/TryStart
            // path RealChimeProcess.Kill's OS-facing half depends on, so it
            // has to clear this guard too, or Play would return before ever
            // starting a real afplay at all.
            ChimePlayer.SilenceForTests = false;
            var t = new Thread(() => ChimePlayer.Play(path)) { IsBackground = true };
            t.Start();

            var proc = FindAfplayByArgument(before, path, 10_000);
            Assert.NotNull(proc); // the real process actually started

            ChimePlayer.StopAll();

            Assert.True(WaitUntilExited(proc!, KillCeilingMs),
                "a real afplay process was not killed by StopAll within a generous ceiling");

            t.Join(KillCeilingMs);
        }
        finally
        {
            ChimePlayer.SilenceForTests = true;
            ChimePlayer.StopAll();
            ChimePlayer.ResetStoppedForTests();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void ChoosingANewSoundKillsARealPreviousPreview()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var dir = Path.Combine(Path.GetTempPath(), "cb-chime-it2-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var first = WriteSilentWav(Path.Combine(dir, "first.wav"), ChimePlayer.MaxDuration.TotalSeconds * 4);
            var second = WriteSilentWav(Path.Combine(dir, "second.wav"), 0.2);
            var before = Process.GetProcessesByName("afplay").Select(p => p.Id).ToHashSet();

            ChimePlayer.PlayForTests = null;
            ChimePlayer.ProcessFactoryForTests = null;
            ChimePlayer.SilenceForTests = false; // CB-168: needed to reach BuildProcess/TryStart at all
            ChimePlayer.PlayPreview(first);

            var firstProc = FindAfplayByArgument(before, first, 10_000);
            Assert.NotNull(firstProc); // the first preview actually started

            ChimePlayer.PlayPreview(second);

            Assert.True(WaitUntilExited(firstProc!, KillCeilingMs),
                "a real previous preview was not killed within a generous ceiling");
        }
        finally
        {
            ChimePlayer.PlayForTests = null;
            ChimePlayer.SilenceForTests = true;
            ChimePlayer.StopAll();
            ChimePlayer.ResetStoppedForTests();
            WaitForNoPreviewWorker();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // Polls HasExited directly rather than a single blocking
    // Process.WaitForExit(ms) call — a foreign process handle (obtained via
    // Process.GetProcessesByName rather than the Process object that
    // actually called Start()) is not guaranteed the same prompt,
    // event-driven wakeup a self-started child gets. This still makes no
    // claim about HOW LONG it took, only whether it happened inside the
    // ceiling.
    private static bool WaitUntilExited(Process subject, int ceilingMs)
    {
        var deadline = Stopwatch.StartNew();
        while (!subject.HasExited && deadline.ElapsedMilliseconds < ceilingMs)
        {
            Thread.Sleep(20);
        }

        var exited = subject.HasExited;
        if (!exited) { try { subject.Kill(); } catch { } }
        return exited;
    }

    // Finds a newly-spawned afplay process by its command-line argument.
    // Generous relative to spawn-VISIBILITY latency only (a fresh process
    // becoming visible in the process table is fast even under heavy
    // contention — it happens as soon as the kernel registers the fork(),
    // well before exec() or the audio itself starts) — not a claim about
    // anything else this file measures.
    private static Process? FindAfplayByArgument(HashSet<int> before, string argument, int deadlineMs)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(deadlineMs);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var p in Process.GetProcessesByName("afplay").Where(x => !before.Contains(x.Id)))
            {
                if (ArgsOf(p.Id).Contains(argument)) return p;
            }

            Thread.Sleep(10);
        }

        return null;
    }

    private static string ArgsOf(int pid)
    {
        try
        {
            using var ps = Process.Start(new ProcessStartInfo("/bin/ps", $"-o args= -p {pid}")
            { RedirectStandardOutput = true, UseShellExecute = false })!;
            var s = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit();
            return s;
        }
        catch { return ""; }
    }

    // Gives a leftover preview worker a bounded moment to notice its process
    // died and exit its own loop, so it can never bleed into a later test's
    // own _previewWorkerRunning/_currentPreview state.
    private static void WaitForNoPreviewWorker()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && Process.GetProcessesByName("afplay").Length > 0)
        {
            Thread.Sleep(50);
        }
    }

    // A minimal, valid, silent WAV file of the given duration — real
    // enough for afplay to actually run it for that long, silent so the
    // test suite makes no noise. 8kHz mono 8-bit unsigned PCM, value 128
    // throughout (silence in that format).
    private static string WriteSilentWav(string path, double seconds)
    {
        const int sampleRate = 8000;
        var sampleCount = (int)(sampleRate * seconds);

        using var stream = new FileStream(path, FileMode.Create);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + sampleCount);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate); // byte rate: 1 byte/sample * sampleRate
        writer.Write((short)1); // block align
        writer.Write((short)8); // bits per sample
        writer.Write("data"u8.ToArray());
        writer.Write(sampleCount);

        var silence = new byte[sampleCount];
        Array.Fill(silence, (byte)128);
        writer.Write(silence);

        return path;
    }
}
