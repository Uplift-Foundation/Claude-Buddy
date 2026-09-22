using System.Diagnostics;
using Xunit;

namespace ClaudeBuddy.Tests;

// The screen-lock wait against the two things it does not own: the real
// wall clock and the real scheduler.
//
// tests/UnitTests' ScreenLockWaitTests drives the loop with a fake clock and a
// fake sleep, which is the right place for the rule's arms — but a fake clock
// can make a deadline expire without a single real millisecond passing, so it
// says nothing about whether `now() + cap` and `Thread.Sleep` actually compose
// into a wait. That is the half this file covers, with the identical
// delegates Program.cs wires in (`() => DateTime.UtcNow` and `Thread.Sleep`)
// and only the probe faked, since the probe is four DllImports into
// CoreGraphics whose answer on a runner is whatever the runner happens to be.
//
// The seam matters because the bug was a return value nobody read. Both
// halves have to hold for the fix to be real: the rule must refuse to start,
// and the wait must actually wait.
public class ScreenLockWaitSeamTests
{
    // Short enough to keep the suite quick, long enough that the assertions
    // are about real elapsed time rather than timer resolution.
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(40);

    [Fact]
    public void Really_sleeps_between_polls_of_a_locked_screen()
    {
        // Five polls at 40ms with a real Thread.Sleep. Asserted as a floor
        // only — a loaded CI runner can take arbitrarily longer, and an upper
        // bound here would be a flake rather than a check. CLAUDE.md's rule
        // about widening tolerances cuts the other way for a *lower* bound:
        // the thing being measured is that time passed at all.
        var probes = 0;
        var clock = Stopwatch.StartNew();

        ScreenLockWait.Wait(
            probe: () => ++probes <= 5 ? ScreenLockState.Locked : ScreenLockState.Unlocked,
            now: () => DateTime.UtcNow,
            sleep: Thread.Sleep,
            cap: TimeSpan.FromMilliseconds(1),
            interval: Interval);

        clock.Stop();
        Assert.Equal(6, probes);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(5 * 40),
            $"five 40ms sleeps should take at least 200ms; took {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void A_one_millisecond_cap_really_does_expire_against_the_wall_clock()
    {
        // The paired positive: the same real clock and real sleep, with a
        // probe that reports no window server session, must *stop* waiting.
        // Without this, the test above is equally consistent with a loop that
        // can never exit — which is the negative control CLAUDE.md asks for,
        // and here the two cases differ only in what the probe says.
        var probes = 0;

        ScreenLockWait.Wait(
            probe: () => { probes++; return ScreenLockState.NoWindowServerSession; },
            now: () => DateTime.UtcNow,
            sleep: Thread.Sleep,
            cap: TimeSpan.FromMilliseconds(1),
            interval: Interval);

        // One probe if the first clock read already crossed the 1ms deadline,
        // two if it did not. Either is correct; more than a handful would mean
        // the cap was not being consulted.
        Assert.InRange(probes, 1, 3);
    }

    [Fact]
    public void An_unlocked_screen_costs_no_real_time_at_all()
    {
        var clock = Stopwatch.StartNew();

        ScreenLockWait.Wait(
            probe: () => ScreenLockState.Unlocked,
            now: () => DateTime.UtcNow,
            sleep: Thread.Sleep,
            cap: TimeSpan.FromHours(2),
            interval: TimeSpan.FromHours(1));

        clock.Stop();
        // A wait that slept before its first probe would have parked here for
        // an hour. This is the assertion that keeps every ordinary launch free.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5),
            $"an unlocked screen must not sleep; took {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Reports_a_defined_state_from_the_real_probe_on_either_platform()
    {
        // The only assertion about the real CoreGraphics probe that is safe to
        // make anywhere: that it answers with a defined state and does not
        // throw. Deliberately not an assertion about *which* — this runs both
        // on a developer Mac with a live session and on a CI runner whose
        // session context nobody here has measured, and asserting either
        // answer would be asserting a property of the machine.
        //
        // Not behind a platform attribute, because both arms of ProbeState's
        // OperatingSystem.IsMacOS() guard are worth exercising and CI gives us
        // one runner for each. Off macOS the answer is pinned exactly: the
        // guard is the only thing standing between this call and four
        // DllImports against a framework that is not there. On macOS the
        // marshalling itself is what gets covered — a wrong DllImport
        // signature or a CFString released too early shows up here as a throw
        // or an undefined value, and this is the only test that calls the real
        // thing at all.
        var state = MacOSScreenLock.ProbeState();

        if (!OperatingSystem.IsMacOS())
        {
            Assert.Equal(ScreenLockState.Unlocked, state);
            return;
        }

        Assert.Contains(state, new[]
        {
            ScreenLockState.NoWindowServerSession,
            ScreenLockState.Unlocked,
            ScreenLockState.Locked
        });

        // Stable across repeats, which a use-after-free would not reliably be.
        Assert.Equal(state, MacOSScreenLock.ProbeState());
    }
}
