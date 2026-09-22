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
}
