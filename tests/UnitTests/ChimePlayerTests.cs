using System.Diagnostics;
using Xunit;

namespace ClaudeBuddy.Tests;

// QA (CB-167): ChimePlayer.Play itself is [ExcludeFromCodeCoverage] — it
// starts a real audio subprocess — but WindowsStartInfoFor and WindowsScript
// are pure enough to assert on directly, which is what actually proves the
// smart-quote injection QA found cannot come back: the path is never
// interpolated into script text, only ever passed through the environment,
// so no escaping question about its contents is even reachable.
//
// [Collection("Settings")] as of round 4: _stopped (like _live before it)
// is a process-wide static, and TurnSoundsTests — in the same collection —
// exercises ChimePlayer.Play through the exact same seam. Without sharing
// a collection, xUnit is free to run the two classes' tests in parallel
// within this assembly, and a StopAll call from one could make an
// unrelated Play call in the other silently no-op mid-test. Each test that
// calls StopAll also resets the flag itself in a finally block, which
// covers running order within the collection; sharing the collection is
// what covers the two classes never overlapping in time at all.
//
// CB-168 (Ines, round 2 of this ticket's ChimePlayerTests work): this class
// used to hold four tests that swept a real StopAll or a real PlayPreview
// across the spawn of a real afplay, deciding "killed promptly" from
// whether a Process/Thread wait returned inside a short fixed window. QA
// measured all four flaky under real load — a genuine, correct kill can
// take several seconds once the machine is contended, and a fixed window
// can't tell that apart from "never happened." Rather than widen the
// window again (the same wrong fix in a wider margin — see the class
// header this replaced, and CLAUDE.md's "automated suite" section), the
// thing under test (which process gets a kill request, in what order) and
// the thing that made the old tests flaky (how long the OS takes to tear a
// process down) are now split apart. ChimePlayer.IChimeProcess is the seam
// that makes that possible: everything below asserts kill/track decisions
// against FakeChimeProcess, a fake with no OS underneath it and no wall
// clock anywhere in the assertion. What a fake cannot prove — that
// RealChimeProcess's three OS-facing members actually do what they claim
// against a real subprocess — is what the one or two tests in
// ChimePlayerIntegrationTests.cs are for instead, with a generous ceiling
// and no fine-grained timing.
[Collection("Settings")]
public class ChimePlayerTests
{
    [Theory]
    // The straight apostrophe the original interpolated version escaped by
    // doubling.
    [InlineData(@"C:\Users\warre\Music\it's a chime.wav")]
    // The Unicode "smart" apostrophes PowerShell's tokenizer also accepts
    // as string delimiters — the actual injection QA found, since the old
    // code only escaped U+0027.
    [InlineData("C:\\Users\\warre\\Music\\chime\u2018s.wav")]
    [InlineData("C:\\Users\\warre\\Music\\chime\u2019s.wav")]
    [InlineData("C:\\Users\\warre\\Music\\chime\u201As.wav")]
    [InlineData("C:\\Users\\warre\\Music\\chime\u201Bs.wav")]
    // A leading dash — the shape a PowerShell parameter name has, which
    // parameter binding could only ever misread if this were literal script
    // text rather than a variable's runtime value.
    [InlineData(@"-weirdly-named-chime.wav")]
    // Spaces, for good measure — the case every quoting scheme has to get
    // right first.
    [InlineData(@"C:\Users\warre\My Sounds\chime with spaces.wav")]
    public void WindowsStartInfoFor_PassesThePathThroughTheEnvironmentUnchanged(string path)
    {
        var startInfo = ChimePlayer.WindowsStartInfoFor(path);

        Assert.Equal(path, startInfo.EnvironmentVariables[ChimePlayer.ChimeEnvVar]);
    }

    // The negative control every one of the cases above actually depends
    // on: no matter what the path contains, the script text handed to
    // powershell.exe is the same fixed string, because the path never
    // reaches it. A version that still interpolated somewhere would fail
    // this even if the individual escaping happened to be correct.
    [Theory]
    [InlineData(@"C:\Users\warre\it's a chime.wav")]
    [InlineData("C:\\Users\\warre\\chime\u2019s.wav")]
    [InlineData(@"-leading-dash.wav")]
    public void WindowsStartInfoFor_TheScriptTextNeverChangesWithThePath(string path)
    {
        var withThisPath = ChimePlayer.WindowsStartInfoFor(path);
        var withAnotherPath = ChimePlayer.WindowsStartInfoFor(@"C:\Windows\Media\Glass.wav");

        Assert.Equal(
            withAnotherPath.ArgumentList[2],
            withThisPath.ArgumentList[2]);
        Assert.DoesNotContain(path, withThisPath.ArgumentList[2]);
    }

    [Fact]
    public void WindowsStartInfoFor_LaunchesPowershellNoProfile()
    {
        var startInfo = ChimePlayer.WindowsStartInfoFor(@"C:\Windows\Media\Glass.wav");

        Assert.Equal("powershell", startInfo.FileName);
        Assert.Equal("-NoProfile", startInfo.ArgumentList[0]);
        Assert.Equal("-Command", startInfo.ArgumentList[1]);
    }

    // QA (CB-167): "if the variable is empty, the script must exit with an
    // error, not play silently." Asserted against the actual script text
    // rather than merely trusted, since this is the one guarantee that has
    // to hold even if something upstream ever calls Play with nothing to
    // give it.
    [Fact]
    public void WindowsScript_GuardsAgainstAnEmptyEnvironmentVariable()
    {
        Assert.Contains("IsNullOrEmpty($env:" + ChimePlayer.ChimeEnvVar + ")", ChimePlayer.WindowsScript);
        Assert.Contains("exit 1", ChimePlayer.WindowsScript);
    }

    // The path only ever appears via $env:, never as a literal — the actual
    // mechanism that makes every case above true, checked once directly on
    // the constant itself rather than only inferred from the parameterised
    // cases.
    [Fact]
    public void WindowsScript_ReadsThePathOnlyThroughTheEnvironment()
    {
        Assert.Contains("$env:" + ChimePlayer.ChimeEnvVar, ChimePlayer.WindowsScript);
    }

    // QA round 5, finding 3: BuildProcess was excluded along with the rest
    // of Play, but building a Process/ProcessStartInfo has no OS side
    // effect of its own — nothing here calls Start — so there is nothing
    // that needs a real machine to prove, the same reasoning
    // WindowsStartInfoFor's own tests already rest on. Only the branch
    // this runner can actually take (macOS) is asserted on directly.
    [Fact]
    public void BuildProcess_OnMacUsesAfplayWithThePathAsItsSoleArgument()
    {
        if (!OperatingSystem.IsMacOS()) return; // the only branch this runner can take

        using var proc = ChimePlayer.BuildProcess("/System/Library/Sounds/Glass.aiff");

        Assert.NotNull(proc);
        Assert.Equal("/usr/bin/afplay", proc!.StartInfo.FileName);
        Assert.Equal(new[] { "/System/Library/Sounds/Glass.aiff" }, proc.StartInfo.ArgumentList);
        Assert.False(proc.StartInfo.UseShellExecute);
    }

    // A fake IChimeProcess: no OS underneath it at all. HasExited only ever
    // becomes true via Kill() — nothing here simulates a chime finishing on
    // its own — so WaitForExit's immediate `return HasExited` means every
    // test below runs in the time it takes managed code to run, never the
    // time it takes an OS process to spawn, play, or tear down. KillLog, when
    // given, is what lets a test assert *order* ("the previous one was
    // killed before the next one was tracked") rather than only "eventually
    // killed" — the fake-based tests' whole point over the real-process
    // sweeps this file used to hold.
    private sealed class FakeChimeProcess : ChimePlayer.IChimeProcess
    {
        private static int _nextId;
        private readonly List<string>? _killLog;
        private readonly string _label;

        internal FakeChimeProcess(string label = "", List<string>? killLog = null)
        {
            Id = System.Threading.Interlocked.Increment(ref _nextId);
            _label = label.Length > 0 ? label : $"fake{Id}";
            _killLog = killLog;
        }

        public int Id { get; }
        public bool HasExited { get; private set; }
        public bool Killed { get; private set; }
        public int KillCallCount { get; private set; }

        public bool WaitForExit(int milliseconds) => HasExited;

        public void Kill()
        {
            Killed = true;
            HasExited = true;
            KillCallCount++;
            _killLog?.Add(_label);
        }

        public void Dispose() { }
    }

    // Replaces StopAllKillsEveryConcurrentlyTrackedProcessNotJustOne's old
    // real-process version. Proves the same thing the comment on _live
    // always described — a set, not a slot, so StopAll reaches every
    // concurrently-tracked chime — with no real process and no wait at all:
    // Kill() on a fake is synchronous, so the assertion is immediate.
    [Fact]
    public void StopAllKillsEveryConcurrentlyTrackedFakeProcess()
    {
        try
        {
            var a = new FakeChimeProcess();
            var b = new FakeChimeProcess();
            ChimePlayer.TrackForTests(a);
            ChimePlayer.TrackForTests(b);

            Assert.False(a.Killed);
            Assert.False(b.Killed);

            ChimePlayer.StopAll();

            Assert.True(a.Killed, "the first tracked process was never asked to die");
            Assert.True(b.Killed, "the second tracked process was never asked to die");
        }
        finally
        {
            // StopAll also sets the round-4 _stopped flag — reset it so a
            // later test's ordinary Play call is not silently a no-op.
            ChimePlayer.ResetStoppedForTests();
        }
    }

    // Round 4 (CB-167): once StopAll has run, nothing may start a new
    // chime — a chime already chained behind the stop (TurnSounds' own
    // _chimeChain, or a FirePending timer that happens to fire during the
    // app's own unwind) starting a fresh process right after StopAll just
    // killed every existing one would leave the app making noise after
    // Quit, which is the exact bug finding 5 fixed for the reactive case
    // and this closes for the "one more queued behind it" case. Driven
    // through the same PlayForTests seam every other Play test uses, per
    // Play's own comment on why the _stopped check sits ahead of that seam
    // rather than beside it.
    [Fact]
    public void PlayIsANoOpOnceStopAllHasRun()
    {
        try
        {
            var played = new List<string>();
            ChimePlayer.PlayForTests = p => { lock (played) played.Add(p); };

            ChimePlayer.StopAll(); // nothing tracked yet — just flips _stopped
            Assert.True(ChimePlayer.IsStopped);

            ChimePlayer.Play("/System/Library/Sounds/Glass.aiff");

            lock (played) Assert.Empty(played); // the seam itself was never reached
        }
        finally
        {
            ChimePlayer.PlayForTests = null;
            ChimePlayer.ResetStoppedForTests();
        }
    }

    // Round 4, item 2, rewritten for CB-168: the old version of this test
    // proved "Play bails under the lock before starting a real process" by
    // timing how fast Play() returned (< 200ms) — a real process spawn is
    // slower than an immediate return, but that is still a wall-clock proxy
    // for the real question. ProcessFactoryForTests answers the real
    // question directly: with _stopped already true, StartProcess (and so
    // the factory) must never even be called. No timing anywhere.
    [Fact]
    public void PlayNeverBuildsAProcessOnceStopAllHasRun()
    {
        try
        {
            var factoryCalls = 0;
            ChimePlayer.PlayForTests = null; // the real path, not the seam
            ChimePlayer.ProcessFactoryForTests = _ => { factoryCalls++; return new FakeChimeProcess(); };

            ChimePlayer.StopAll(); // nothing tracked yet — just flips _stopped

            ChimePlayer.Play("/System/Library/Sounds/Glass.aiff");

            Assert.Equal(0, factoryCalls);
        }
        finally
        {
            ChimePlayer.ProcessFactoryForTests = null;
            ChimePlayer.ResetStoppedForTests();
        }
    }

    // Replaces PlayPreviewKillsTheLivePreviewProcessBeforeTrackingTheNext's
    // old real-process version. The order log is what proves the sequence
    // KillPreviousAndTrackNewPreview's own comment describes — the previous
    // preview is killed, and only the previous one — rather than merely
    // "both eventually died," which a bug that killed things in the wrong
    // order could still pass.
    [Fact]
    public void PlayPreviewKillsThePreviousFakeBeforeTrackingTheNext()
    {
        try
        {
            var order = new List<string>();
            var first = new FakeChimeProcess("first", order);
            var second = new FakeChimeProcess("second", order);

            ChimePlayer.SetCurrentPreviewForTests(first);
            Assert.False(first.Killed);

            ChimePlayer.SetCurrentPreviewForTests(second);

            Assert.True(first.Killed, "the previous preview was never asked to die when a new one started");
            Assert.False(second.Killed);
            Assert.Equal(new[] { "first" }, order); // killed exactly once, and only the previous one

            // And still reachable by StopAll, same as any other tracked chime
            // — the fix's other half named explicitly: "the process must still
            // be tracked in _live so StopAll covers it."
            ChimePlayer.StopAll();
            Assert.True(second.Killed, "StopAll did not reach the tracked preview process");
        }
        finally
        {
            ChimePlayer.ResetStoppedForTests();
        }
    }

    // Replaces PlayPreviewNeverKillsAConcurrentScanChime's old real-process
    // version. The preview channel's kill-then-replace logic is scoped to
    // _currentPreview specifically, not to "whatever is in _live" — a scan
    // chime is tracked in _live too (TrackForTests registers it exactly the
    // way Play() does) but is never assigned to _currentPreview, so starting
    // a preview while one is playing must leave it alone.
    [Fact]
    public void PlayPreviewNeverKillsAConcurrentFakeScanChime()
    {
        try
        {
            var scanChime = new FakeChimeProcess();
            ChimePlayer.TrackForTests(scanChime); // registered the way an ordinary Play() call tracks a scan chime — never as the current preview

            var preview = new FakeChimeProcess();
            ChimePlayer.SetCurrentPreviewForTests(preview);

            Assert.False(scanChime.Killed, "starting a preview killed an unrelated scan chime");
            Assert.False(preview.Killed);

            // Both still reachable by StopAll, since both are in _live —
            // proving the scan chime was spared, not merely untracked.
            ChimePlayer.StopAll();
            Assert.True(scanChime.Killed, "StopAll did not reach the scan chime");
            Assert.True(preview.Killed, "StopAll did not reach the preview");
        }
        finally
        {
            ChimePlayer.ResetStoppedForTests();
        }
    }

    // Replaces PlayPreviewEndToEndKillsARealPreviousPreview. Drives the real
    // PlayPreview -> RunPreviewWorker -> PlayOnePreview path (not
    // SetCurrentPreviewForTests's direct call), the same distinction the
    // method this replaces existed to cover, but against
    // ProcessFactoryForTests instead of a real afplay. The only wait here is
    // for the two independent background Tasks PlayPreview's own comment
    // describes (the kill, and the worker starting the next preview) to be
    // scheduled — ordinary async handoff, not OS process teardown. A fake's
    // Kill() and WaitForExit() are both synchronous, so a correctly-wired
    // path finishes in well under the five-second deadline below; only a
    // genuinely broken handoff would ever approach it.
    [Fact]
    public async Task PlayPreviewEndToEndDrivesTheRealWorkerAgainstAFake()
    {
        try
        {
            var previous = new FakeChimeProcess("previous");
            ChimePlayer.SetCurrentPreviewForTests(previous);

            ChimePlayer.PlayForTests = null; // the real Play/PlayOnePreview path
            var next = new FakeChimeProcess("next");
            ChimePlayer.ProcessFactoryForTests = _ => next;

            ChimePlayer.PlayPreview("does-not-matter.wav");

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((!previous.Killed || next.KillCallCount == 0) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(5);
            }

            Assert.True(previous.Killed, "the real worker path did not kill the previous preview");
            Assert.True(next.KillCallCount > 0, "the real worker path never reached the newly-started preview at all");
        }
        finally
        {
            ChimePlayer.PlayForTests = null;
            ChimePlayer.ProcessFactoryForTests = null;
            ChimePlayer.StopAll();
            ChimePlayer.ResetStoppedForTests();
        }
    }

    // Round 4, item 4: ShutdownRequested's own method must kill whatever
    // is live without leaving the app permanently deaf if the quit that
    // asked for it is later cancelled — StopAll's own sticky contract is
    // deliberately not this method's.
    [Fact]
    public void KillCurrentlyPlayingForCancellableShutdownKillsFakeWithoutStickingStopped()
    {
        var proc = new FakeChimeProcess();
        ChimePlayer.TrackForTests(proc);

        ChimePlayer.KillCurrentlyPlayingForCancellableShutdown();

        Assert.True(proc.Killed, "the tracked process was not asked to die");
        Assert.False(ChimePlayer.IsStopped, "a cancellable shutdown must not set the sticky stopped flag");
    }

    // Round 5(b): App.axaml.cs wires desktop.Exit to StopAll and
    // desktop.ShutdownRequested to KillCurrentlyPlayingForCancellableShutdown
    // — two different methods precisely because they must behave
    // differently. This is StopAll's own half of that contract, in one
    // test rather than split across the two that already exist above.
    [Fact]
    public void StopAllKillsAFakeProcessAndSetsStopped()
    {
        try
        {
            var proc = new FakeChimeProcess();
            ChimePlayer.TrackForTests(proc);

            ChimePlayer.StopAll();

            Assert.True(proc.Killed, "StopAll did not ask to kill the tracked process");
            Assert.True(ChimePlayer.IsStopped, "StopAll (the Exit path) must set the sticky stopped flag");
        }
        finally
        {
            ChimePlayer.ResetStoppedForTests();
        }
    }
}
