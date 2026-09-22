using ClaudeBuddy;
using Xunit;

namespace ClaudeBuddy.IntegrationTests;

// SingleInstance.Claim against a real named mutex — the seam
// tests/UnitTests' SingleInstanceTests cannot reach, because that suite only
// asserts the pure ShouldProceed rule against a SingleInstanceClaim someone
// already computed. This is the "someone" — a real OS-backed named
// synchronization object, exactly like the one Program.cs takes on
// MutexName, and it is the reason Claim takes the name as a parameter rather
// than reading MutexName itself: a per-test, GUID-suffixed name here means
// this suite can never collide with a real running Buddy on this machine, nor
// with a parallel CI leg exercising the same code (CB-178).
//
// All of these run in-process, on purpose — not as two separate `dotnet`
// invocations. The harness proved that matters: on this platform a named
// mutex is scoped to one POSIX session (see MutexName's own comment), and
// two commands started from two separate shell invocations land in
// different sessions and simply cannot see each other's mutex at all — a
// cross-process version of this test would pass, but for the wrong reason.
//
// Not covered here: SingleInstanceClaim.AbandonedByPreviousOwner. That arm
// only fires when `WaitOne` throws `AbandonedMutexException`, and
// SingleInstance.cs's own comment records the actual measurement: on this
// machine (macOS, .NET 10.0.400) that exception was never thrown, for
// either way a holder can go away without releasing — killed outright, or
// exited normally past the point where it should have called
// ReleaseMutex. `Release_lets_a_later_claimant_succeed` and
// `Recovers_after_the_previous_holder_never_released_it` below exercise
// exactly those two shapes and both land on Acquired, which is the real,
// measured crash-recovery path on this platform. Reproducing the
// AbandonedMutexException arm itself would mean asserting behaviour this
// runtime does not exhibit; the CB-178 PR body has the harness (a second
// real process, `kill -9`'d) and its output instead.
public class SingleInstanceTests
{
    private static string FreshName() => "CB178_IT_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void First_claimant_acquires_an_uncontested_mutex()
    {
        var name = FreshName();

        var (claim, mutex) = SingleInstance.Claim(name);
        try
        {
            Assert.Equal(SingleInstanceClaim.Acquired, claim);
            Assert.True(SingleInstance.ShouldProceed(claim));
        }
        finally
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    [Fact]
    public void Second_claimant_observes_it_held_while_the_first_is_still_alive()
    {
        // Deliberately a real, separate Thread — not a second Claim() call
        // on this same thread. A Mutex's ownership in .NET is per *thread*,
        // not per handle instance, and recursive for the thread that already
        // owns it: this test's first draft called Claim() twice in a row on
        // the test method's own thread and got Acquired both times, because
        // the second WaitOne was the same thread re-entering a lock it
        // already held — exactly the kind of false pass CLAUDE.md warns
        // about, measuring something adjacent to the real question. A
        // background thread that actually blocks on the mutex is what makes
        // "someone else holds it" a fact about a different owner rather than
        // an artifact of how the test called the API.
        var name = FreshName();
        using var holderReady = new ManualResetEventSlim(false);
        using var releaseHolder = new ManualResetEventSlim(false);

        var holder = new Thread(() =>
        {
            var (claim, mutex) = SingleInstance.Claim(name);
            Assert.Equal(SingleInstanceClaim.Acquired, claim);
            holderReady.Set();
            releaseHolder.Wait();
            mutex.ReleaseMutex();
            mutex.Dispose();
        });
        holder.Start();

        try
        {
            Assert.True(holderReady.Wait(TimeSpan.FromSeconds(5)), "holder thread never acquired the mutex");

            var (secondClaim, secondMutex) = SingleInstance.Claim(name);
            try
            {
                Assert.Equal(SingleInstanceClaim.HeldByAnother, secondClaim);
                Assert.False(SingleInstance.ShouldProceed(secondClaim));
            }
            finally
            {
                // The loser never owns the mutex, so there is nothing to
                // release — only the handle to close. This mirrors exactly
                // what Program.cs's claimSingleInstance delegate does on the
                // HeldByAnother arm.
                secondMutex.Dispose();
            }
        }
        finally
        {
            releaseHolder.Set();
            holder.Join();
        }
    }

    [Fact]
    public void Release_lets_a_later_claimant_succeed()
    {
        var name = FreshName();

        var (firstClaim, firstMutex) = SingleInstance.Claim(name);
        Assert.Equal(SingleInstanceClaim.Acquired, firstClaim);
        firstMutex.ReleaseMutex();
        firstMutex.Dispose();

        // With the first claimant's handle closed and the mutex released,
        // the name is free again — a later launch (the ordinary case: the
        // running Buddy quit, and the next login-item launch takes the same
        // name) should acquire cleanly, not read the prior claimant's now-
        // closed handle as still holding it.
        var (secondClaim, secondMutex) = SingleInstance.Claim(name);
        try
        {
            Assert.Equal(SingleInstanceClaim.Acquired, secondClaim);
            Assert.True(SingleInstance.ShouldProceed(secondClaim));
        }
        finally
        {
            secondMutex.ReleaseMutex();
            secondMutex.Dispose();
        }
    }

    [Fact]
    public void Recovers_after_the_previous_holder_never_released_it()
    {
        // The actual crash-recovery path on this platform (CB-178's
        // acceptance criteria: "the keep-alive still restarts Buddy after a
        // genuine crash"). A real crash is a process disappearing without
        // ever calling ReleaseMutex; the closest in-process stand-in is
        // disposing the Mutex without releasing it first, which was checked
        // by hand against a real second, kill -9'd process before writing
        // this test (see SingleInstance.cs's comments and the CB-178 PR
        // body) and behaves identically here: the next claimant still gets
        // Acquired, not an exception and not HeldByAnother. This is the arm
        // that would go silently missing if AbandonedByPreviousOwner were
        // mistaken for where crash recovery lives — it does not fire on this
        // runtime at all (see SingleInstance's own comments), so this test,
        // not that arm, is what actually guards the acceptance criterion.
        var name = FreshName();

        var (firstClaim, firstMutex) = SingleInstance.Claim(name);
        Assert.Equal(SingleInstanceClaim.Acquired, firstClaim);

        // Deliberately no ReleaseMutex() here — that omission is the point.
        firstMutex.Dispose();

        var (secondClaim, secondMutex) = SingleInstance.Claim(name);
        try
        {
            Assert.Equal(SingleInstanceClaim.Acquired, secondClaim);
            Assert.True(SingleInstance.ShouldProceed(secondClaim));
        }
        finally
        {
            secondMutex.ReleaseMutex();
            secondMutex.Dispose();
        }
    }
}
