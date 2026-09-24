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
[Collection("Settings")]
public class ChimePlayerTests
{
    // CB-168: shared by every real-process race test in this file below —
    // AStopAllSweptAcrossPlaysSpawnLeavesNoChimeRunning,
    // ChoosingANewSoundCutsOffThePreviewAlreadyPlaying,
    // ASecondPreviewLandingDuringTheFirstsSpawnStillCutsItOff, and
    // TheNewestPreviewStillPlaysWhenItLandsDuringTheFirstsSpawn. All four
    // were seen failing once each, on both origin/develop and a feature
    // branch, under CPU contention from other worktrees' own test suites
    // running at the same time (rowan-achterberg, QA). They share one root
    // cause: each decides "was this process killed promptly / found
    // promptly / still running" from whether some Process/Thread wait
    // *returned inside a short fixed window* (300ms-1500ms), which is a
    // proxy for the real question, not the real question. KillTree's kill
    // call (ChimePlayer.cs) and Process.Start are both asynchronous at the
    // OS level — asking for a kill, or asking for a process to exist, is
    // not the same moment as the OS finishing either — so both the
    // teardown after a kill and a fresh process becoming visible in the
    // process table are always racing against however long the scheduler
    // takes to get around to it, ordinarily milliseconds but stretched
    // arbitrarily far by whatever else the machine is doing. A short fixed
    // window can't tell "still finishing, under load" from "genuinely
    // never happened" apart the moment that stretch exceeds the window —
    // exactly the "timing assumption that holds at idle and fails under
    // contention" CLAUDE.md's "automated suite" section already names,
    // and a bigger fixed window is the same wrong measurement with a wider
    // margin, which that same section says is never the right fix.
    //
    // The fix measures the thing that actually distinguishes "load delay"
    // from "genuinely didn't happen": how long it took relative to when
    // the race actually began, compared against a real ceiling this
    // production code itself imposes — ChimePlayer.MaxDuration, the 5s
    // cap WaitAndKillIfStillRunning falls back to if nothing else killed
    // the process first (see that method's own comment). A silent wav
    // written longer than MaxDuration means a genuinely-never-killed
    // process is still bounded at MaxDuration by production's own safety
    // net, not by the file's length — so the true "did this survive"
    // ceiling is always MaxDuration, and SurvivedThresholdMs sits a full
    // second under it: comfortably below where a real bug would show up,
    // comfortably above any reap/spawn delay this file's history has ever
    // measured (0-6ms at idle; this fix's own testing measured single-
    // digit seconds of load-induced delay as still well inside this
    // margin on a machine running several sibling worktrees' full test
    // suites at once).
    //
    // A silent wav's own duration is written from this, wherever the test
    // needs "long enough that MaxDuration, not the file, is the ceiling" —
    // see the class header comment above for why that matters.
    // QA (team-lead, CB-168 round 3): measured directly under this
    // machine's heaviest observed load — a full `dotnet test
    // tests/UnitTests` run, thousands of tests contending for the same
    // cores — a genuinely correct kill took 5014ms, 14ms *past*
    // MaxDuration's nominal 5000ms. That is not reap delay after the kill
    // signal; it is the kill signal itself: StopAll's own KillEverythingLive
    // call runs on an ordinary managed thread, and under sufficiently
    // extreme contention that thread can be starved of CPU time for
    // seconds before it ever reaches KillTree, the same way KillTree's own
    // OS-level teardown can be delayed once it does run. MaxDuration is a
    // real ceiling for how long *this app* will wait before its own
    // fallback intervenes, but that fallback is itself an ordinary
    // Process.WaitForExit(ms) call subject to the identical scheduling
    // pressure — so it is not a hard bound the machine's scheduler is
    // obligated to respect, only the nominal one. The margins below both
    // sides of it accordingly: comfortable headroom *past* MaxDuration for
    // the kill signal itself to still land late and be correct, and a
    // wav written long enough past that for "truly never killed" to
    // remain a distinct, later outcome.
    private static readonly double CeilingMs = ChimePlayer.MaxDuration.TotalMilliseconds + 3_000;
    private static readonly double SurvivedThresholdMs = ChimePlayer.MaxDuration.TotalMilliseconds + 1_500;

    // QA (rowan-achterberg / team-lead, CB-168 round 2): the first version
    // of this fix made the healthy path itself slow — about 3m05s for this
    // class, up from a few seconds — because every wait here ran to a
    // 10-second ceiling regardless of whether the condition it was waiting
    // for had already been met. Measured directly (temporary
    // instrumentation, since reverted) rather than guessed at: the actual
    // cost was FindAfplayByArgument's discovery loop burning its full
    // ceiling in roughly 25 of 31 iterations of
    // ASecondPreviewLandingDuringTheFirstsSpawnStillCutsItOff — not
    // because discovery was slow, but because in the *healthy*, correct
    // case the first preview is routinely killed by the interleaving
    // before it ever becomes visible at all, so "not found" is the normal
    // outcome there, not a load symptom to wait out.
    //
    // The fix is two constants instead of one, because discovery and
    // teardown are genuinely different kinds of asynchrony:
    //
    // - A fresh process becoming visible in the process table happens as
    //   soon as the kernel registers the fork(), well before exec() or the
    //   audio itself starts — fast even under heavy contention, and
    //   DiscoveryWaitMs only needs to be generous relative to *that*, not
    //   to a kill's teardown. Where a caller expects the target to
    //   reliably exist (ChoosingANewSoundCutsOffThePreviewAlreadyPlaying,
    //   which deliberately waits for genuine steady-state playback first;
    //   TheNewestPreviewStillPlaysWhenItLandsDuringTheFirstsSpawn's newest,
    //   nothing-supersedes-it request), this is what actually distinguishes
    //   "load delayed it" from "gave up too early." Where not finding it at
    //   all is an expected, common, and *fast* outcome
    //   (ASecondPreviewLandingDuringTheFirstsSpawnStillCutsItOff's first
    //   preview, raced against its own spawn), the same constant would
    //   have meant waiting out the ceiling on every such iteration for no
    //   reason — which is exactly what happened.
    // - Teardown after a real kill signal is the slower, genuinely load-
    //   sensitive asynchrony this fix's first pass was written for —
    //   SurvivedThresholdMs is still the pass/fail decision (comfortably
    //   under MaxDuration's cap, comfortably over any reap delay measured),
    //   and ExitPollDeadlineMs is only a margin past it: once elapsed time
    //   already exceeds SurvivedThresholdMs the outcome is decided, so
    //   there is nothing to gain from polling further, only latency to
    //   lose on the rare failing iteration.
    private const int DiscoveryWaitMs = 2_000;

    // QA (team-lead, CB-168 round 2): a third constant, smaller still,
    // for a discovery that is expected to *often come up empty* even in
    // the healthy case — ASecondPreviewLandingDuringTheFirstsSpawnStillCutsItOff's
    // own search for the first preview's process, which the interleaving
    // under test frequently kills before it ever becomes visible at all.
    // DiscoveryWaitMs is generous because the caller expects the target to
    // reliably exist (ChoosingANewSoundCutsOffThePreviewAlreadyPlaying,
    // TheNewestPreviewStillPlaysWhenItLandsDuringTheFirstsSpawn's newest
    // request) — spending that same 2s on every iteration where "not
    // found" is the normal, fast, healthy answer is exactly the mistake
    // this round of the fix corrects. A process that does exist becomes
    // visible within single-digit milliseconds of Process.Start even under
    // this file's own measured contention; 300ms is ample headroom above
    // that without paying seconds for the common absent case.
    private const int BestEffortDiscoveryWaitMs = 300;

    private static readonly int ExitPollDeadlineMs = (int)SurvivedThresholdMs + 1_000;

    // Polls `subject.HasExited` directly rather than a single blocking
    // Process.WaitForExit(ms) call — a foreign process handle (one this
    // test obtained via Process.GetProcessesByName rather than the
    // Process object that actually called Start()) is not guaranteed the
    // same prompt, event-driven wakeup a self-started child gets, so this
    // returns the moment the condition is actually true instead of
    // whatever granularity that fallback path happens to poll at. Reports
    // how long that took relative to `raceStartedAt` — the moment the
    // caller's own race actually began, not Process.Start, which can
    // itself be delayed by contention. Never leaves `subject` running past
    // the call: a survivor this reports is killed here rather than left
    // for a later iteration's `before` snapshot to trip over.
    private static double ElapsedMsUntilExit(Process subject, Stopwatch raceStartedAt)
    {
        var deadline = Stopwatch.StartNew();
        while (!subject.HasExited && deadline.ElapsedMilliseconds < ExitPollDeadlineMs)
        {
            Thread.Sleep(5);
        }

        var elapsedMs = raceStartedAt.Elapsed.TotalMilliseconds;
        if (!subject.HasExited) { try { subject.Kill(); } catch { } }
        return elapsedMs;
    }

    // Finds a newly-spawned afplay process by its command-line argument,
    // generous only relative to spawn-visibility latency (see the class
    // header comment on why that is a smaller number than teardown's own
    // margin), and returning null promptly the moment `deadlineMs` passes
    // rather than only after chasing a much longer one. `deadlineMs` is
    // the caller's own choice between DiscoveryWaitMs (the target is
    // expected to reliably exist) and BestEffortDiscoveryWaitMs (not
    // finding one is itself a common, healthy outcome) — see each
    // constant's own comment.
    private static Process? FindAfplayByArgument(HashSet<int> before, string argument, int deadlineMs)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(deadlineMs);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var p in Process.GetProcessesByName("afplay").Where(x => !before.Contains(x.Id)))
            {
                if (ArgsOf(p.Id).Contains(argument)) return p;
            }

            Thread.Sleep(5);
        }

        return null;
    }

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

    // Round 3(d): a Settings preview and the summary-fallback chime both
    // call ChimePlayer.Play directly, and can be running at the same time
    // as an ordinary scan chime — none of them serialized against any of
    // the others. A single Process? slot (pre-round-2) could only ever
    // remember one of them, so StopAll on Quit would kill whichever "won"
    // that slot and leave the other still making noise; _live is a set for
    // exactly this reason. This proves the set, not the audio: two real
    // processes, registered through the same Add path Play itself uses
    // (TrackForTests — PlayForTests bypasses that machinery entirely, since
    // there is no real process behind a substituted chime to track), are
    // BOTH still killed by one StopAll call. Neither process plays any
    // audio — a harmless, long-running command stands in for "still mid-
    // playback when Quit happens," which is all StopAll actually cares
    // about killing.
    [Fact]
    public void StopAllKillsEveryConcurrentlyTrackedProcessNotJustOne()
    {
        try
        {
            using var a = StartLongRunningProcessForTests();
            using var b = StartLongRunningProcessForTests();
            ChimePlayer.TrackForTests(a);
            ChimePlayer.TrackForTests(b);

            Assert.False(a.HasExited);
            Assert.False(b.HasExited);

            ChimePlayer.StopAll();

            Assert.True(a.WaitForExit(3000), "the first tracked process was not killed by StopAll");
            Assert.True(b.WaitForExit(3000), "the second tracked process was not killed by StopAll");
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

    // QA round 3, finding 4 (LOW): stepping through the Settings sound
    // picker at an ordinary human pace — well past the 250ms debounce,
    // each choice its own settled request — used to start a fresh,
    // independent ChimePlayer.Play call every time, with nothing to stop
    // the previous one still playing underneath it. Four choices a person
    // actually paused on sounded like four overlapping chimes.
    //
    // This proves the mechanism with real processes rather than the
    // PlayForTests seam, the same reason StopAllKillsEveryConcurrently
    // TrackedProcessNotJustOne above does: a seam has nothing real for
    // KillTree to act on, so the only way to prove a process actually
    // dies is to give it one. SetCurrentPreviewForTests reaches the exact
    // kill-then-replace path PlayPreview's own production code uses
    // (KillPreviousAndTrackNewPreview), registering each harmless,
    // long-running process — never real audio — the same way Play() would
    // register a real preview process.
    [Fact]
    public void PlayPreviewKillsTheLivePreviewProcessBeforeTrackingTheNext()
    {
        try
        {
            using var first = StartLongRunningProcessForTests();
            using var second = StartLongRunningProcessForTests();

            ChimePlayer.SetCurrentPreviewForTests(first);
            Assert.False(first.HasExited);

            ChimePlayer.SetCurrentPreviewForTests(second);

            Assert.True(first.WaitForExit(3000), "the previous preview process was not killed when a new one started");
            Assert.False(second.HasExited);

            // And still reachable by StopAll, same as any other tracked chime
            // — the fix's other half named explicitly: "the process must still
            // be tracked in _live so StopAll covers it."
            ChimePlayer.StopAll();
            Assert.True(second.WaitForExit(3000), "StopAll did not reach the tracked preview process");
        }
        finally
        {
            // Round 4: StopAll also sets _stopped — reset it so a later
            // test's ordinary Play/PlayPreview call is not silently a no-op.
            ChimePlayer.ResetStoppedForTests();
        }
    }

    // Round 4: the preview channel's kill-then-replace logic is scoped to
    // _currentPreview specifically, not to "whatever is in _live" — a scan
    // chime (or the summary-fallback chime) is tracked in _live too
    // (TrackForTests registers it exactly the way Play() does) but is
    // never assigned to _currentPreview, so starting a preview while one
    // is playing must leave it alone. Killing a scan chime because someone
    // opened Settings and arrowed through the sound picker would be a
    // second, worse bug than the one this channel was built to fix.
    [Fact]
    public void PlayPreviewNeverKillsAConcurrentScanChime()
    {
        try
        {
            using var scanChime = StartLongRunningProcessForTests();
            ChimePlayer.TrackForTests(scanChime); // registered the way an ordinary Play() call tracks a scan chime — never as the current preview

            using var preview = StartLongRunningProcessForTests();
            ChimePlayer.SetCurrentPreviewForTests(preview);

            Assert.False(scanChime.HasExited, "starting a preview killed an unrelated scan chime");
            Assert.False(preview.HasExited);

            // Both still reachable by StopAll, since both are in _live —
            // proving the scan chime was spared, not merely untracked.
            ChimePlayer.StopAll();
            Assert.True(scanChime.WaitForExit(3000), "StopAll did not reach the scan chime");
            Assert.True(preview.WaitForExit(3000), "StopAll did not reach the preview");
        }
        finally
        {
            // Round 4: StopAll also sets _stopped — reset it so a later
            // test's ordinary Play/PlayPreview call is not silently a no-op.
            ChimePlayer.ResetStoppedForTests();
        }
    }

    // Round 4, item 1: closes a real time-of-check-to-time-of-use gap —
    // the real (non-seam) Play path used to read IsStopped, release that
    // lock, and only then add to _live and Start; StopAll running in that
    // exact window would find _live still empty and never learn about a
    // process Play went on to start right afterward. The fix moved the
    // check inside the same lock as the Add, so once _stopped is true no
    // new process can ever be added at all. Not reproduced as an actual
    // race here (this codebase has no seam inside the lock to pause on) —
    // proved instead by timing: with _stopped already true before Play is
    // called at all, the real path must bail before ever reaching
    // BuildProcess/Start, and a real afplay spawn-and-run takes
    // meaningfully longer than an immediate return does.
    [Fact]
    public void PlayNeverStartsARealProcessOnceStopAllHasRun()
    {
        try
        {
            ChimePlayer.PlayForTests = null; // the real path, not the seam
            ChimePlayer.StopAll(); // nothing tracked yet — just flips _stopped

            var sw = Stopwatch.StartNew();
            ChimePlayer.Play("/System/Library/Sounds/Glass.aiff");
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 200,
                $"Play took {sw.ElapsedMilliseconds}ms — it should have bailed under the lock, before ever starting a process");
        }
        finally
        {
            ChimePlayer.ResetStoppedForTests();
        }
    }

    // Round 4, item 2, end to end: a real "current" preview (already
    // started, the way KillPreviousAndTrackNewPreview's own new contract
    // requires) is killed by a real PlayPreview call, not just by
    // SetCurrentPreviewForTests's own direct path — this is what actually
    // exercises PlayOnePreview's new ordering (Start the new process,
    // THEN track it and kill the old one), rather than only the
    // already-started components PlayPreviewKillsTheLivePreviewProcess-
    // BeforeTrackingTheNext proves in isolation.
    [Fact]
    public async Task PlayPreviewEndToEndKillsARealPreviousPreview()
    {
        try
        {
            using var previous = StartLongRunningProcessForTests();
            ChimePlayer.SetCurrentPreviewForTests(previous);

            ChimePlayer.PlayForTests = null; // the real path
            ChimePlayer.PlayPreview("/System/Library/Sounds/Ping.aiff");

            Assert.True(previous.WaitForExit(3000),
                "a real PlayPreview call did not kill the previous preview process");

            // Give the worker a moment to reach PlayOnePreview's own
            // tracking lock before proving StopAll can still reach whatever
            // it started.
            await Task.Delay(300);
            ChimePlayer.StopAll();
        }
        finally
        {
            ChimePlayer.PlayForTests = null;
            ChimePlayer.ResetStoppedForTests();
        }
    }

    // Round 4, item 4: ShutdownRequested's own method must kill whatever
    // is live without leaving the app permanently deaf if the quit that
    // asked for it is later cancelled — StopAll's own sticky contract is
    // deliberately not this method's.
    [Fact]
    public void KillCurrentlyPlayingForCancellableShutdownKillsWithoutStickingStopped()
    {
        using var proc = StartLongRunningProcessForTests();
        ChimePlayer.TrackForTests(proc);

        ChimePlayer.KillCurrentlyPlayingForCancellableShutdown();

        Assert.True(proc.WaitForExit(3000), "the tracked process was not killed");
        Assert.False(ChimePlayer.IsStopped, "a cancellable shutdown must not set the sticky stopped flag");
    }

    // Round 5(b): App.axaml.cs wires desktop.Exit to StopAll and
    // desktop.ShutdownRequested to KillCurrentlyPlayingForCancellableShutdown
    // — two different methods precisely because they must behave
    // differently. This is StopAll's own half of that contract, in one
    // test rather than split across the two that already exist
    // (StopAllKillsEveryConcurrentlyTrackedProcessNotJustOne, which never
    // checks IsStopped, and PlayIsANoOpOnceStopAllHasRun, which never
    // tracks a live process): a real process is both killed AND the flag
    // is set, together, the way Exit actually needs both to be true.
    [Fact]
    public void StopAllKillsALiveProcessAndSetsStopped()
    {
        try
        {
            using var proc = StartLongRunningProcessForTests();
            ChimePlayer.TrackForTests(proc);

            ChimePlayer.StopAll();

            Assert.True(proc.WaitForExit(3000), "StopAll did not kill the tracked process");
            Assert.True(ChimePlayer.IsStopped, "StopAll (the Exit path) must set the sticky stopped flag");
        }
        finally
        {
            ChimePlayer.ResetStoppedForTests();
        }
    }

    // QA round 5, finding 1: reproduces the regression round 5(a)'s own
    // _previewChain introduced. A silent WAV rather than a system sound —
    // long enough (several seconds) that "still playing" is genuinely
    // true when the second request lands, and silent so the run doesn't
    // make noise. Drives the real PlayPreview/PlayOnePreview path, not
    // the seam: the seam never populates _currentPreview at all (its
    // branch inside PlayOnePreview returns before
    // KillPreviousAndTrackNewPreview is ever reached), so it has no
    // "victim" for the chain to have queued a kill behind in the first
    // place — only a real process, tracked the real way, can show this.
    //
    // CB-168: the "cut off promptly" check used to be `WaitForExit(1500)`
    // — a short fixed window, the same shape (and the same measured
    // failure under load) the class header comment describes. Rewritten
    // to the shared elapsed-time-vs-SurvivedThresholdMs mechanism.
    [Fact]
    public void ChoosingANewSoundCutsOffThePreviewAlreadyPlaying()
    {
        if (!OperatingSystem.IsMacOS()) return; // afplay is the only real target this test drives

        var dir = Path.Combine(Path.GetTempPath(), "cb-chime-race1-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var first = WriteSilentWav(Path.Combine(dir, "first.wav"), CeilingMs / 1000.0);
            var second = WriteSilentWav(Path.Combine(dir, "second.wav"), CeilingMs / 1000.0);
            var before = Process.GetProcessesByName("afplay").Select(p => p.Id).ToHashSet();

            ChimePlayer.PlayForTests = null; // the real path — see the comment above
            ChimePlayer.PlayPreview(first);

            // Wait for the worker to genuinely be mid-playback (not merely
            // queued) before the second request lands — this is what
            // makes _previewWorkerRunning true and _currentPreview
            // non-null at the same time, the exact state the regression
            // needed to reproduce. DiscoveryWaitMs rather than a short
            // fixed deadline for the same reason as the class header
            // comment: a fresh process becoming visible in the table is
            // itself asynchronous and can be delayed by contention — this
            // is the "expected to reliably exist" case that constant is
            // for, unlike ASecondPreviewLandingDuringTheFirstsSpawn
            // StillCutsItOff's own discovery, where not finding one is the
            // common, fast, healthy outcome.
            //
            // QA (felix-marchetti / team-lead, CB-168 round 3): this used
            // to grab Process.GetProcessesByName("afplay")[0] with no
            // filtering at all — the only discovery in this file that
            // didn't check against a `before` snapshot. Harmless on a
            // quiet machine (there is only ever one afplay: ours), but
            // under real full-suite load with several worktrees' own
            // ChimePlayerTests potentially running afplay at the same
            // moment, `[0]` could be a completely unrelated process — a
            // sibling worktree's, or a stale one from an earlier iteration
            // already mid-exit — which is exactly what a failure at 29ms
            // elapsed with "not actually still playing" looks like: the
            // wrong process's HasExited, not this test's own. Routed
            // through the same before-snapshot-plus-argument-match
            // FindAfplayByArgument every other discovery in this file
            // already uses, which cannot pick up anything but this
            // iteration's own `first`.
            var firstAfplay = FindAfplayByArgument(before, first, DiscoveryWaitMs);

            Assert.NotNull(firstAfplay);
            Assert.False(firstAfplay!.HasExited, "the first preview was not actually still playing when the second request was made");

            var sw = Stopwatch.StartNew();
            ChimePlayer.PlayPreview(second);

            var elapsedMs = ElapsedMsUntilExit(firstAfplay, sw);
            Assert.True(elapsedMs <= SurvivedThresholdMs,
                $"the previous preview was not cut off promptly by the new one ({elapsedMs:F0}ms)");
        }
        finally
        {
            ChimePlayer.PlayForTests = null;
            ChimePlayer.StopAll(); // unblocks the worker if it is still mid-wait on `second`
            ChimePlayer.ResetStoppedForTests();
            WaitForNoPreviewWorker();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // QA round 6, F1 (CB-167): sweeps a second PlayPreview across the
    // first one's spawn. Landing while the worker is still inside
    // BuildProcess/TryStart used to find nothing tracked to kill, so the
    // first preview played its full length; PlayOnePreview's superseded
    // check is what cuts it off now.
    //
    // CB-168: discovery and the "cut off promptly" check both used to be
    // short fixed windows (800ms discovery, 1500ms cut-off) — the same
    // shape, and the same measured failure under load, the class header
    // comment describes. Both rewritten to the shared mechanism.
    [Fact]
    public void ASecondPreviewLandingDuringTheFirstsSpawnStillCutsItOff()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var dir = Path.Combine(Path.GetTempPath(), "cb-chime-r6-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var misses = new List<string>();
        try
        {
            ChimePlayer.PlayForTests = null;
            for (var delayUs = 0; delayUs <= 30000; delayUs += 1000)
            {
                var first = WriteSilentWav(Path.Combine(dir, $"a{delayUs}.wav"), CeilingMs / 1000.0);
                var second = WriteSilentWav(Path.Combine(dir, $"b{delayUs}.wav"), CeilingMs / 1000.0);
                var before = Process.GetProcessesByName("afplay").Select(p => p.Id).ToHashSet();

                var sw = Stopwatch.StartNew();
                ChimePlayer.PlayPreview(first);
                while (sw.Elapsed.TotalMilliseconds * 1000 < delayUs) Thread.SpinWait(50);

                var swSecond = Stopwatch.StartNew();
                ChimePlayer.PlayPreview(second);

                var firstProc = FindAfplayByArgument(before, first, BestEffortDiscoveryWaitMs);
                if (firstProc is not null)
                {
                    var elapsedMs = ElapsedMsUntilExit(firstProc, swSecond);
                    if (elapsedMs > SurvivedThresholdMs) misses.Add($"{delayUs / 1000}ms ({elapsedMs:F0}ms)");
                }

                ChimePlayer.StopAll();
                ChimePlayer.ResetStoppedForTests();
                // Confirming teardown of whatever StopAll just killed —
                // the same asynchrony ElapsedMsUntilExit's own comment
                // describes, so it shares that deadline rather than
                // discovery's shorter one.
                var quiet = DateTime.UtcNow + TimeSpan.FromMilliseconds(ExitPollDeadlineMs);
                while (DateTime.UtcNow < quiet &&
                       Process.GetProcessesByName("afplay").Any(p => !before.Contains(p.Id)))
                    Thread.Sleep(20);
                Thread.Sleep(50);
            }
        }
        finally
        {
            ChimePlayer.PlayForTests = null;
            ChimePlayer.StopAll();
            ChimePlayer.ResetStoppedForTests();
            try { Directory.Delete(dir, true); } catch { }
        }

        Assert.True(misses.Count == 0, "first preview survived a second request at: " + string.Join(", ", misses));
    }

    // QA round 7, F3 (CB-167): the other half of F1. Cutting the first
    // preview off is only right if the newest request then plays; with the
    // superseded read forced to true, the F1 test above still passed while
    // nothing played at all. This one fails on that mutant at every delay.
    //
    // CB-168: the discovery deadline (used to be a fixed 2s) is now the
    // same generous, load-independent wait the class header comment
    // describes — a fresh process becoming visible in the table is itself
    // asynchronous, and 2s was measured too tight under contention from
    // sibling worktrees' own test suites. The "still playing" check itself
    // (700ms) is left as-is: it is a blocking wait, not a poll for
    // something to become visible, so it does not share that race — a
    // genuinely-cut-off process exits near-instantly, nowhere near 700ms,
    // regardless of what else the machine is doing.
    [Fact]
    public void TheNewestPreviewStillPlaysWhenItLandsDuringTheFirstsSpawn()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var dir = Path.Combine(Path.GetTempPath(), "cb-chime-r7-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var silent = new List<string>();
        try
        {
            ChimePlayer.PlayForTests = null;
            for (var delayUs = 0; delayUs <= 30000; delayUs += 1000)
            {
                var first = WriteSilentWav(Path.Combine(dir, $"a{delayUs}.wav"), CeilingMs / 1000.0);
                var second = WriteSilentWav(Path.Combine(dir, $"b{delayUs}.wav"), CeilingMs / 1000.0);
                var before = Process.GetProcessesByName("afplay").Select(p => p.Id).ToHashSet();

                var sw = Stopwatch.StartNew();
                ChimePlayer.PlayPreview(first);
                while (sw.Elapsed.TotalMilliseconds * 1000 < delayUs) Thread.SpinWait(50);
                ChimePlayer.PlayPreview(second);

                var secondProc = FindAfplayByArgument(before, second, DiscoveryWaitMs);

                // QA (team-lead, CB-168 round 2): a positive check that it
                // is alive *at the moment of the check*, not "it stayed
                // alive for N seconds" — WaitForExit(ms) on a foreign
                // process handle is not a reliable way to ask "is this
                // still running", the same reasoning ElapsedMsUntilExit's
                // own comment gives for polling HasExited directly instead.
                // Waiting first and then reading HasExited once is a real
                // point-in-time answer regardless of that handle's own
                // wakeup behaviour.
                bool stillPlaying;
                if (secondProc is null)
                {
                    stillPlaying = false;
                }
                else
                {
                    Thread.Sleep(700);
                    stillPlaying = !secondProc.HasExited;
                }

                if (!stillPlaying) silent.Add($"{delayUs / 1000}ms");

                ChimePlayer.StopAll();
                ChimePlayer.ResetStoppedForTests();
                var quiet = DateTime.UtcNow + TimeSpan.FromMilliseconds(ExitPollDeadlineMs);
                while (DateTime.UtcNow < quiet && Process.GetProcessesByName("afplay").Any(p => !before.Contains(p.Id)))
                    Thread.Sleep(20);
                Thread.Sleep(50);
            }
        }
        finally
        {
            ChimePlayer.PlayForTests = null;
            ChimePlayer.StopAll();
            ChimePlayer.ResetStoppedForTests();
            try { Directory.Delete(dir, true); } catch { }
        }
        Assert.True(silent.Count == 0, "newest preview never played (or was cut off) at: " + string.Join(", ", silent));
    }

    // QA round 6, F2 (CB-167): replaces a test that could not fail — a
    // StopAll on the test thread always beat Task.Run's start-up, so every
    // Play returned at its entry guard and the post-track re-check was
    // never reached (mutating it to `if (false)` still passed). Gating Play
    // on its own thread and sweeping StopAll across its spawn makes the
    // entry-check -> StopAll -> track interleaving actually happen, and
    // only that re-check kills the chime there.
    //
    // CB-168: rewritten to use the class header's shared, load-independent
    // mechanism — see that comment for why a short fixed window (this
    // test's own version used to be 1000ms/300ms) can't tell "still
    // tearing down under load" from "genuinely never killed" apart, and
    // measured this test failing under exactly that contention.
    [Fact]
    public void AStopAllSweptAcrossPlaysSpawnLeavesNoChimeRunning()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var dir = Path.Combine(Path.GetTempPath(), "cb-chime-r6s-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var survivors = new List<string>();
        try
        {
            ChimePlayer.PlayForTests = null;
            for (var delayUs = 0; delayUs <= 30000; delayUs += 500)
            {
                var path = WriteSilentWav(Path.Combine(dir, $"c{delayUs}.wav"), CeilingMs / 1000.0);
                var before = Process.GetProcessesByName("afplay").Select(p => p.Id).ToHashSet();
                using var go = new ManualResetEventSlim();
                var t = new Thread(() => { go.Wait(); ChimePlayer.Play(path); }) { IsBackground = true };
                t.Start();
                Thread.Sleep(20);
                var sw = Stopwatch.StartNew();
                go.Set();
                while (sw.Elapsed.TotalMilliseconds * 1000 < delayUs) Thread.SpinWait(20);
                ChimePlayer.StopAll();

                // Ground truth for the thread: how long from the chime's own
                // start until Play() actually returned, not whether it
                // returned inside an arbitrary short window. A real .NET
                // Thread.Join wakes up promptly on completion (unlike a
                // foreign process handle's WaitForExit — see
                // ElapsedMsUntilExit's comment), so a single bounded join
                // is fine here; ExitPollDeadlineMs bounds it for the same
                // reason it bounds everything else in this file — once
                // elapsed time already exceeds SurvivedThresholdMs the
                // outcome is decided.
                t.Join(ExitPollDeadlineMs);
                var threadElapsedMs = sw.Elapsed.TotalMilliseconds;
                if (threadElapsedMs > SurvivedThresholdMs)
                {
                    survivors.Add($"{delayUs}us (thread {threadElapsedMs:F0}ms)");
                }

                ChimePlayer.ResetStoppedForTests();

                // Same ground truth for the OS process itself: Play()'s own
                // thread returning is proof KillTree issued the kill, not
                // proof the OS has finished tearing the process down (see
                // the class header comment) — so any afplay that appeared
                // during this iteration gets the same generous wait and the
                // same real-duration comparison, rather than a second short
                // fixed window making the same mistake independently.
                foreach (var p in Process.GetProcessesByName("afplay").Where(x => !before.Contains(x.Id)))
                {
                    var processElapsedMs = ElapsedMsUntilExit(p, sw);
                    if (processElapsedMs > SurvivedThresholdMs)
                    {
                        survivors.Add($"{delayUs}us (proc {processElapsedMs:F0}ms)");
                    }
                }
            }
        }
        finally { ChimePlayer.StopAll(); ChimePlayer.ResetStoppedForTests(); try { Directory.Delete(dir, true); } catch { } }
        Assert.True(survivors.Count == 0, "chime survived StopAll at: " + string.Join(", ", survivors));
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

    // Gives a leftover preview worker (from a test that intentionally
    // raced or force-stopped one) a bounded moment to notice its process
    // died and exit its own loop, so it can never bleed into a later
    // test's own _previewWorkerRunning/_currentPreview state.
    private static void WaitForNoPreviewWorker()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
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

    private static Process StartLongRunningProcessForTests()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "ping -n 30 127.0.0.1 >NUL" } }
            : new ProcessStartInfo("/bin/sleep") { ArgumentList = { "30" } };

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        return Process.Start(startInfo)!;
    }
}
