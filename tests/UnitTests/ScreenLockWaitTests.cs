using Xunit;

namespace ClaudeBuddy.Tests;

// The screen-lock wait, which used to be a bool and crashed startup for it.
//
// The bug this pins down: `IsScreenLocked()` answered true both for "the
// window server says the screen is locked" and for "there is no window server
// session to ask", and `WaitForUnlock` reported "the cap expired while still
// locked" through a return value that Startup.Run's `Action waitForUnlock`
// signature discarded. So a machine whose screen was authoritatively locked
// slept two hours and then started the UI anyway, which is an
// InvalidOperationException out of AvaloniaNativeRenderTimer (-6661,
// kCVReturnInvalidDisplay) during AppBuilder.Setup() — before App exists, so
// before there is anything to catch it with.
//
// The rule is three-way now, and these are its arms. What the tests cannot
// reach is MacOSScreenLock.ProbeState itself: four DllImports into
// CoreGraphics, whose answer on a CI runner would be whatever the runner
// happens to be rather than anything asserted. That is why the decision moved
// out of that class — see ScreenLockWait's header comment, and
// SingleInstance.ShouldProceed for the same split made for the same reason.
public class ScreenLockWaitTests
{
    // Asserted as a table in the body rather than through [InlineData],
    // because both enums are internal to the app assembly: xUnit only
    // discovers public methods, and a public method cannot take a parameter
    // of a less-accessible type (CS0051). Making the enums public to suit the
    // test harness would be the wrong way round.
    [Fact]
    public void Maps_each_state_to_its_own_policy()
    {
        Assert.Equal(ScreenLockWaitPolicy.StartNow,
            ScreenLockWait.PolicyFor(ScreenLockState.Unlocked));
        Assert.Equal(ScreenLockWaitPolicy.WaitUpToCap,
            ScreenLockWait.PolicyFor(ScreenLockState.NoWindowServerSession));
        Assert.Equal(ScreenLockWaitPolicy.WaitWithoutCap,
            ScreenLockWait.PolicyFor(ScreenLockState.Locked));
    }

    [Fact]
    public void Refuses_a_state_that_is_not_one_of_the_three()
    {
        // Not defensive decoration: the states exist precisely because a bool
        // collapsed three answers into two, so a fourth value arriving here
        // silently and being treated as one of the three is the same class of
        // bug again.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScreenLockWait.PolicyFor((ScreenLockState)99));
    }

    [Fact]
    public void Refuses_a_policy_that_is_not_one_of_the_three()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScreenLockWait.ShouldStartNow((ScreenLockWaitPolicy)99, capExpired: true));
    }

    [Fact]
    public void Decides_per_state_and_clock()
    {
        // Unlocked starts regardless of the clock — there is nothing to wait
        // for.
        Assert.True(ScreenLockWait.ShouldStartNow(ScreenLockState.Unlocked, false));
        Assert.True(ScreenLockWait.ShouldStartNow(ScreenLockState.Unlocked, true));

        // No session: unknowable, so the cap governs. This is the arm the
        // cap's comment in Program.cs was always actually about.
        Assert.False(
            ScreenLockWait.ShouldStartNow(ScreenLockState.NoWindowServerSession, false));
        Assert.True(
            ScreenLockWait.ShouldStartNow(ScreenLockState.NoWindowServerSession, true));

        // Locked: the window server's own answer, so an expired cap changes
        // nothing. The second of these is the crash — it used to start.
        Assert.False(ScreenLockWait.ShouldStartNow(ScreenLockState.Locked, false));
        Assert.False(ScreenLockWait.ShouldStartNow(ScreenLockState.Locked, true));
    }

    [Fact]
    public void Never_starts_on_a_reported_lock_however_long_it_has_been()
    {
        // Stated on its own, away from the table, because it is the whole
        // point of the change rather than one row of six. A cap that expires
        // into a start is only defensible while the reading might be wrong.
        Assert.False(ScreenLockWait.ShouldStartNow(ScreenLockState.Locked, capExpired: true));
    }

    [Fact]
    public void Starts_immediately_on_an_unlocked_screen_without_sleeping()
    {
        // The overwhelmingly common case, and it must cost nothing: one probe,
        // no delay. A wait that slept first would add its interval to every
        // single launch.
        var probes = 0;
        var sleeps = 0;

        ScreenLockWait.Wait(
            probe: () => { probes++; return ScreenLockState.Unlocked; },
            now: () => new DateTime(2026, 9, 22, 2, 46, 5, DateTimeKind.Utc),
            sleep: _ => sleeps++,
            cap: TimeSpan.FromHours(2),
            interval: TimeSpan.FromSeconds(2));

        Assert.Equal(1, probes);
        Assert.Equal(0, sleeps);
    }

    [Fact]
    public void Keeps_polling_a_locked_screen_until_it_unlocks()
    {
        var states = new Queue<ScreenLockState>(new[]
        {
            ScreenLockState.Locked, ScreenLockState.Locked, ScreenLockState.Locked,
            ScreenLockState.Unlocked
        });
        var sleeps = new List<TimeSpan>();

        ScreenLockWait.Wait(
            probe: states.Dequeue,
            now: () => new DateTime(2026, 9, 22, 2, 46, 5, DateTimeKind.Utc),
            sleep: sleeps.Add,
            cap: TimeSpan.FromHours(2),
            interval: TimeSpan.FromSeconds(2));

        Assert.Empty(states);
        Assert.Equal(3, sleeps.Count);
        Assert.All(sleeps, slept => Assert.Equal(TimeSpan.FromSeconds(2), slept));
    }

    [Fact]
    public void Keeps_polling_a_locked_screen_long_past_the_cap()
    {
        // The regression test for the crash. The clock runs far beyond the cap
        // while the probe keeps answering Locked, and the loop must not have
        // returned — because returning is what hands a locked screen to
        // AppBuilder.Setup().
        var clock = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
        var probes = 0;

        ScreenLockWait.Wait(
            probe: () =>
            {
                probes++;
                // Nine hours of locked screen — four and a half caps — and only
                // then an unlock. Before this change the wait returned at two
                // hours and startup crashed.
                return probes <= 9 ? ScreenLockState.Locked : ScreenLockState.Unlocked;
            },
            now: () => clock,
            sleep: slept => clock += slept,
            cap: TimeSpan.FromHours(2),
            interval: TimeSpan.FromHours(1));

        Assert.Equal(10, probes);
        Assert.Equal(new DateTime(2026, 9, 22, 9, 0, 0, DateTimeKind.Utc), clock);
    }

    [Fact]
    public void Starts_anyway_once_the_cap_expires_with_no_window_server_session()
    {
        // The arm the cap is kept for: a daemon context where the answer is
        // genuinely unknowable. Starting is right here, because the
        // alternative is a launch agent that never brings Buddy back — and
        // wrongly reading "no session" as "locked" is what this whole change
        // separates out.
        var clock = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
        var probes = 0;

        ScreenLockWait.Wait(
            probe: () => { probes++; return ScreenLockState.NoWindowServerSession; },
            now: () => clock,
            sleep: slept => clock += slept,
            cap: TimeSpan.FromHours(2),
            interval: TimeSpan.FromMinutes(30));

        // Probes at 0:00, 0:30, 1:00, 1:30 and 2:00; the fifth is the first at
        // or past the deadline, so it starts.
        Assert.Equal(5, probes);
        Assert.Equal(new DateTime(2026, 9, 22, 2, 0, 0, DateTimeKind.Utc), clock);
    }

    [Fact]
    public void Starts_on_a_deadline_already_in_the_past_when_there_is_no_session()
    {
        // Thread.Sleep is not scheduled during deep sleep, so a machine that
        // slept through its own cap notices only on the next wake and finds
        // the deadline behind it. The 2026-09-22 crash landed 1.5 seconds into
        // a DarkWake for exactly this reason. The loop re-probes before acting
        // on that, so the state decides — here, no session, so it starts.
        var calls = 0;
        var probes = 0;

        ScreenLockWait.Wait(
            probe: () => { probes++; return ScreenLockState.NoWindowServerSession; },
            // First read sets the deadline; the next is hours later, as though
            // the process had been asleep in between.
            now: () => calls++ == 0
                ? new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc)
                : new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc),
            sleep: _ => Assert.Fail("must not sleep once the deadline is already past"),
            cap: TimeSpan.FromHours(2),
            interval: TimeSpan.FromSeconds(2));

        Assert.Equal(1, probes);
    }

    [Fact]
    public void Waits_out_a_lock_reported_after_the_deadline_has_already_passed()
    {
        // The other half of the DarkWake case, and the one that is the fix: a
        // deadline in the past plus a reported lock still waits. The old code
        // could only see "cap expired" here and started.
        var clock = new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc);
        var probes = 0;

        ScreenLockWait.Wait(
            probe: () =>
            {
                probes++;
                return probes == 1 ? ScreenLockState.Locked : ScreenLockState.Unlocked;
            },
            // The deadline is set from the first read, so it is already long
            // past on the second.
            now: () => clock,
            sleep: slept => clock += slept,
            cap: TimeSpan.Zero,
            interval: TimeSpan.FromSeconds(2));

        Assert.Equal(2, probes);
    }

    [Fact]
    public void Lets_the_state_change_between_polls_rather_than_latching_the_first_answer()
    {
        // A screen can be locked, then the session can go away, then come
        // back. The policy is re-derived from each probe rather than decided
        // once at entry, so a transition into the uncapped state is respected
        // even though the first reading was the capped one.
        var clock = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
        var states = new Queue<ScreenLockState>(new[]
        {
            ScreenLockState.NoWindowServerSession,  // capped, cap not yet expired
            ScreenLockState.Locked,                 // now uncapped, and past the cap
            ScreenLockState.Locked,
            ScreenLockState.Unlocked
        });

        ScreenLockWait.Wait(
            probe: states.Dequeue,
            now: () => clock,
            sleep: slept => clock += slept,
            cap: TimeSpan.FromMinutes(1),
            interval: TimeSpan.FromHours(1));

        // All four consumed: had the first reading latched the capped policy,
        // the second probe would have started instead of waiting.
        Assert.Empty(states);
    }
}
