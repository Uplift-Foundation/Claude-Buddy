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
    [Fact]
    public void ChoosingANewSoundCutsOffThePreviewAlreadyPlaying()
    {
        if (!OperatingSystem.IsMacOS()) return; // afplay is the only real target this test drives

        var dir = Path.Combine(Path.GetTempPath(), "cb-chime-race1-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var first = WriteSilentWav(Path.Combine(dir, "first.wav"), seconds: 4);
            var second = WriteSilentWav(Path.Combine(dir, "second.wav"), seconds: 4);

            ChimePlayer.PlayForTests = null; // the real path — see the comment above
            ChimePlayer.PlayPreview(first);

            // Wait for the worker to genuinely be mid-playback (not merely
            // queued) before the second request lands — this is what
            // makes _previewWorkerRunning true and _currentPreview
            // non-null at the same time, the exact state the regression
            // needed to reproduce.
            Process? firstAfplay = null;
            var findDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < findDeadline)
            {
                var candidates = Process.GetProcessesByName("afplay");
                if (candidates.Length > 0)
                {
                    firstAfplay = candidates[0];
                    break;
                }

                Thread.Sleep(20);
            }

            Assert.NotNull(firstAfplay);
            Assert.False(firstAfplay!.HasExited, "the first preview was not actually still playing when the second request was made");

            ChimePlayer.PlayPreview(second);

            // On 84f445a6 this took roughly the file's own duration (about
            // 4.7s measured); on the fix (matching a123e831's two
            // independent tasks) it takes on the order of tens of
            // milliseconds. 1.5s is generous slack above that, and still
            // nowhere near the 4s file duration or the 5s cap.
            Assert.True(firstAfplay.WaitForExit(1500),
                "the previous preview was not cut off promptly by the new one");
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

    // QA round 5, finding 2: Play tracked a process in _live before
    // Start() actually ran, the same shape PlayOnePreview already fixed
    // in round 4. Reproduced statistically, the same way the finding was
    // originally measured (racing two real threads many times) rather
    // than forced — Play has no seam to pause it at the exact instant
    // between TryStart and the tracking lock, so a real race across many
    // trials is the honest way to show this closed rather than merely
    // argued closed.
    [Fact]
    public void AStopAllRacingAChimesStartLeavesNoChimeRunning()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var dir = Path.Combine(Path.GetTempPath(), "cb-chime-race2-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            ChimePlayer.PlayForTests = null; // the real path

            for (var i = 0; i < 30; i++)
            {
                var path = WriteSilentWav(Path.Combine(dir, $"chime-{i}.wav"), seconds: 2);
                var before = Process.GetProcessesByName("afplay").Select(p => p.Id).ToHashSet();

                var playTask = Task.Run(() => ChimePlayer.Play(path));
                ChimePlayer.StopAll(); // racing Play's own TryStart/track sequence, as tightly as the runtime allows

                // Reset only once Play has returned: _stopped is sticky in
                // production, and Play's post-start re-check is exactly what
                // this race depends on, so un-sticking it while Play is still
                // between TryStart and that check measures a state the app
                // can never be in.
                Assert.True(playTask.Wait(TimeSpan.FromSeconds(4)), $"iteration {i}: Play did not return");
                ChimePlayer.ResetStoppedForTests();
                Thread.Sleep(100); // let any process that did start actually appear

                var started = Process.GetProcessesByName("afplay").Where(p => !before.Contains(p.Id)).ToList();
                foreach (var proc in started)
                {
                    try
                    {
                        Assert.True(proc.WaitForExit(500), $"iteration {i}: a chime survived a StopAll that raced its own start");
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
        }
        finally
        {
            ChimePlayer.PlayForTests = null;
            ChimePlayer.ResetStoppedForTests();
            try { Directory.Delete(dir, true); } catch { }
        }
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
