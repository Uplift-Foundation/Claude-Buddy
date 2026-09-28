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
// kCVReturnInvalidArgument) during AppBuilder.Setup() — before App exists, so
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
        Assert.Equal(ScreenLockWaitPolicy.WaitForUnlock,
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

        // Locked: the window server's own answer, and since CB-215 it never
        // starts, however long it has been. Starting against it is a
        // guaranteed -6661 abort.
        Assert.False(ScreenLockWait.ShouldStartNow(ScreenLockState.Locked, false));
        Assert.False(ScreenLockWait.ShouldStartNow(ScreenLockState.Locked, true));
    }

    [Fact]
    public void Only_the_unknowable_state_has_a_cap()
    {
        // CB-215: the unknowable state waits a short while and then starts; a
        // reported lock waits for the unlock, so its "cap" is one the loop's
        // elapsed time can never reach.
        var cap = TimeSpan.FromHours(2);

        Assert.Equal(TimeSpan.Zero, ScreenLockWait.CapFor(ScreenLockWaitPolicy.StartNow, cap));
        Assert.Equal(cap, ScreenLockWait.CapFor(ScreenLockWaitPolicy.WaitUpToCap, cap));
        Assert.Equal(TimeSpan.MaxValue, ScreenLockWait.CapFor(ScreenLockWaitPolicy.WaitForUnlock, cap));
    }

    [Fact]
    public void Refuses_a_policy_CapFor_does_not_know()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScreenLockWait.CapFor((ScreenLockWaitPolicy)99, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Never_starts_on_a_reported_lock_however_long_and_starts_on_the_unlock()
    {
        // CB-215, the case that used to crash. A reported lock was capped at
        // twelve hours, and starting then was a guaranteed -6661 abort. The Mac
        // mini, headless and always locked, died that way every twelve hours
        // and dropped off every peer. Here the screen stays locked for a
        // simulated thirty days and nothing starts; the first unlocked reading
        // starts it on that probe.
        var clock = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
        var lockedFor = TimeSpan.FromDays(30);
        var probes = 0;

        ScreenLockWait.Wait(
            probe: () =>
            {
                probes++;
                return clock - new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc) < lockedFor
                    ? ScreenLockState.Locked
                    : ScreenLockState.Unlocked;
            },
            now: () => clock,
            sleep: slept => clock += slept,
            cap: TimeSpan.FromHours(2),
            interval: TimeSpan.FromHours(1));

        Assert.Equal(new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc), clock);
        Assert.Equal(30 * 24 + 1, probes);
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
        // The regression test for the crash. The clock runs far beyond the
        // *short* cap while the probe keeps answering Locked, and the loop
        // must not have returned — because returning is what hands a locked
        // screen to AppBuilder.Setup(). The long cap is what it is measured
        // against instead, and nine hours is inside it.
        var clock = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
        var probes = 0;

        ScreenLockWait.Wait(
            probe: () =>
            {
                probes++;
                // Nine hours of locked screen — four and a half *short* caps,
                // and still inside the twelve-hour one — then an unlock.
                // Before this change the wait returned at two hours and
                // startup crashed.
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
    public void A_transient_no_session_after_a_long_lock_does_not_start_immediately()
    {
        // The defect this suite could not see, because it lives in the
        // *sequence* rather than in any one state. Both caps used to be
        // measured from one `start` taken at entry, so three hours spent
        // correctly waiting out a reported lock also spent the whole of the
        // two-hour no-session cap. A single transient NoWindowServerSession
        // reading then found `capExpired` already true and started the UI at
        // once, into a context with no window server — the -6661 this whole
        // change exists to prevent, reached through the code meant to prevent
        // it. Every per-state test passed throughout.
        var clock = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
        var probes = 0;

        ScreenLockWait.Wait(
            probe: () =>
            {
                probes++;
                // Locked for three hours, then no session for ever after.
                return probes <= 3
                    ? ScreenLockState.Locked
                    : ScreenLockState.NoWindowServerSession;
            },
            now: () => clock,
            sleep: slept => clock += slept,
            cap: TimeSpan.FromHours(2),
            interval: TimeSpan.FromHours(1));

        // The no-session cap is measured from 03:00, when that state was first
        // seen, so it expires at 05:00 rather than instantly. Six probes:
        // three locked, then no-session at 03:00, 04:00 and 05:00.
        //
        // The number that matters is the clock, not the count. Before the fix
        // this returned at 03:00 having slept three times — and *that* is the
        // assertion, because a run which starts at 03:00 is the crash.
        Assert.Equal(6, probes);
        Assert.Equal(new DateTime(2026, 9, 22, 5, 0, 0, DateTimeKind.Utc), clock);
    }

    [Fact]
    public void A_sleep_across_the_transition_credits_the_state_it_was_spent_in()
    {
        // The case that separates this design from the one that looks
        // identical in a one-line description. Three hours are spent locked,
        // and the machine is asleep for the whole of the third — so the
        // transition to no-session is only *noticed* on the wake, with three
        // hours already on the clock.
        //
        // Credit that interval to the state observed at its **end** and all
        // three hours land on the no-session arm, which starts immediately
        // into a -6661: the same defect through a different path. Latching
        // each state at the moment it is first seen credits it to the state
        // it was actually spent in, so the no-session arm begins its own two
        // hours at the wake.
        var clock = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
        var probes = 0;

        ScreenLockWait.Wait(
            probe: () =>
            {
                probes++;
                return probes == 1
                    ? ScreenLockState.Locked
                    : ScreenLockState.NoWindowServerSession;
            },
            now: () => clock,
            // One three-hour sleep: Thread.Sleep is not scheduled during deep
            // sleep, so the whole stretch passes between two polls.
            sleep: slept => clock += slept,
            cap: TimeSpan.FromHours(2),
            interval: TimeSpan.FromHours(3));

        // Woke at 03:00 with no session, waited its own two hours from there,
        // started at 06:00. End-state crediting returns at 03:00 — and 03:00
        // is the crash.
        Assert.Equal(new DateTime(2026, 9, 22, 6, 0, 0, DateTimeKind.Utc), clock);
        Assert.Equal(3, probes);
    }

    [Fact]
    public void An_oscillating_reading_still_expires_rather_than_waiting_for_ever()
    {
        // The reason each state's clock is latched once rather than restarted
        // whenever the state changes. Restarting is the obvious fix for the
        // test above and a worse one: a reading that flips every poll would
        // reset its budget every poll and never expire either cap, which is
        // the permanent silent absence the caps exist to rule out. Latching
        // only accrues, so an alternating reading still terminates.
        var clock = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
        var probes = 0;

        ScreenLockWait.Wait(
            probe: () =>
            {
                probes++;
                return probes % 2 == 1
                    ? ScreenLockState.Locked
                    : ScreenLockState.NoWindowServerSession;
            },
            now: () => clock,
            sleep: slept => clock += slept,
            cap: TimeSpan.FromHours(2),
            interval: TimeSpan.FromHours(1));

        // No-session is first seen at 01:00, so its cap expires at 03:00 —
        // reached on an even probe, which is a no-session one. Had the clock
        // restarted on each change this would never have returned at all.
        Assert.Equal(new DateTime(2026, 9, 22, 3, 0, 0, DateTimeKind.Utc), clock);
    }

    [Fact]
    public void Lets_the_state_change_between_polls_rather_than_latching_the_first_answer()
    {
        // A screen can be locked, then the session can go away, then come
        // back. The policy — and with it the cap being measured against — is
        // re-derived from each probe rather than decided once at entry, so a
        // transition into the long-capped state is respected even though the
        // first reading was the short-capped one.
        var clock = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
        var states = new Queue<ScreenLockState>(new[]
        {
            ScreenLockState.NoWindowServerSession,  // short cap, not yet expired
            ScreenLockState.Locked,                 // long cap, though past the short one
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
