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
