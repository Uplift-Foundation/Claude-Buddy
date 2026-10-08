using Xunit;

namespace ClaudeBuddy.Tests;

// The pure decision CB-178 is actually about: given the outcome of a mutex
// claim, does this process go on to start the UI or exit cleanly? See
// SingleInstance.cs for why this is an enum and not a bool, and Startup.cs
// for where the answer gets used to short-circuit Startup.Run.
public class SingleInstanceTests
{
    [Fact]
    public void Proceeds_when_it_acquired_the_mutex_outright()
    {
        Assert.True(SingleInstance.ShouldProceed(SingleInstanceClaim.Acquired));
    }

    [Fact]
    public void Exits_when_another_live_process_already_holds_it()
    {
        // The whole point of the ticket: this is the ordinary duplicate-launch
        // case, and it must answer "stop", not "throw" and not "proceed" —
        // a duplicate instance doing nothing further is the fix.
        Assert.False(SingleInstance.ShouldProceed(SingleInstanceClaim.HeldByAnother));
    }

    [Fact]
    public void Proceeds_after_inheriting_an_abandoned_mutex()
    {
        // The genuine-crash-recovery arm the acceptance criteria name
        // explicitly: a previous owner that died without releasing the mutex
        // must still let this process become the running Buddy, or the
        // keep-alive would be permanently dead after the first real crash —
        // a worse bug than the one this ticket fixes. This is deliberately
        // asserted separately from Acquired, even though both answer `true`,
        // so a future change that collapses this arm onto HeldByAnother
        // (the wrong direction to collapse it) fails a test with a name that
        // says why.
        Assert.True(SingleInstance.ShouldProceed(SingleInstanceClaim.AbandonedByPreviousOwner));
    }

    [Fact]
    public void Rejects_a_claim_outside_the_named_outcomes()
    {
        // Not a real caller path — every real Claim() returns one of the
        // three named outcomes — but a rule this small earns an explicit
        // "and nothing else" rather than an unstated default that would
        // silently start (or silently stop) the app for a value nobody
        // named.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SingleInstance.ShouldProceed((SingleInstanceClaim)99));
    }

    // CB-206: off Windows the mutex is one per user across every session, so a
    // Buddy launched from a terminal or ssh sees the login item's claim.
    [Fact]
    public void Off_windows_the_claim_spans_sessions_but_not_users()
    {
        var options = SingleInstance.OptionsFor(onWindows: false);

        Assert.NotNull(options);
        Assert.True(options.Value.CurrentUserOnly);
        Assert.False(options.Value.CurrentSessionOnly);
    }

    // And Windows keeps the bare name and its logon-session scope: the
    // cross-session options would mean the Global namespace there, where two
    // users on one PC would block each other.
    [Fact]
    public void On_windows_the_claim_keeps_its_old_scope()
    {
        Assert.Null(SingleInstance.OptionsFor(onWindows: true));
    }

    // --- CB-255: the legacy name and the new one, both ----------------------
    //
    // ShouldProceedAll is ShouldProceed over the claims ClaimAll has made so
    // far, in ClaimNames order (legacy, then new). Each case below is one
    // shape an upgrade can be in.

    [Fact]
    public void Proceeds_when_both_names_were_acquired()
    {
        Assert.True(SingleInstance.ShouldProceedAll(
            [SingleInstanceClaim.Acquired, SingleInstanceClaim.Acquired]));
    }

    [Fact]
    public void Exits_when_an_old_build_holds_the_legacy_name()
    {
        // A pre-rename build is running: it knows only the legacy name, and
        // the new build must not start beside it (every orb drawn twice).
        Assert.False(SingleInstance.ShouldProceedAll([SingleInstanceClaim.HeldByAnother]));
        Assert.False(SingleInstance.ShouldProceedAll(
            [SingleInstanceClaim.HeldByAnother, SingleInstanceClaim.Acquired]));
    }

    [Fact]
    public void Exits_when_another_new_build_holds_the_new_name()
    {
        // The legacy name came free (or was never held) but a new build holds
        // the new one — the ordinary duplicate launch after the upgrade.
        Assert.False(SingleInstance.ShouldProceedAll(
            [SingleInstanceClaim.Acquired, SingleInstanceClaim.HeldByAnother]));
    }

    [Fact]
    public void Proceeds_when_one_name_was_inherited_from_a_crashed_holder()
    {
        // Abandoned counts as proceeding per claim, exactly as ShouldProceed
        // has it, in either position — or the keep-alive would stop restarting
        // the app after a genuine crash of whichever build held that name.
        Assert.True(SingleInstance.ShouldProceedAll(
            [SingleInstanceClaim.AbandonedByPreviousOwner, SingleInstanceClaim.Acquired]));
        Assert.True(SingleInstance.ShouldProceedAll(
            [SingleInstanceClaim.Acquired, SingleInstanceClaim.AbandonedByPreviousOwner]));
    }

    [Fact]
    public void Does_not_proceed_on_no_claims_at_all()
    {
        // Not All's vacuous true: claiming nothing proves nothing about who
        // else is running.
        Assert.False(SingleInstance.ShouldProceedAll([]));
    }

    [Fact]
    public void Rejects_an_unnamed_outcome_among_several()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SingleInstance.ShouldProceedAll(
            [SingleInstanceClaim.Acquired, (SingleInstanceClaim)99]));
    }

    [Fact]
    public void Claims_the_legacy_name_first_and_then_the_new_one()
    {
        // Legacy first so that losing to an old build costs nothing to give
        // back. Pinned as the strings, not as the constants, so the order and
        // the names are both on the page.
        Assert.Equal(
            new[] { "ClaudeBuddy_SingleInstance_Mutex", "Orbweaver_SingleInstance_Mutex" },
            SingleInstance.ClaimNames);
    }
}
