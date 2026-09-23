using Xunit;

namespace ClaudeBuddy.Tests;

// The rule behind CB-167's turn sounds, with no scan, no settings and no
// speaker anywhere near it: did this transition mean a turn just finished,
// does this session just start needing you, or does it mean nothing at all.
//
// Every arm here exists because the level-triggered version of this question
// — "is the state waiting right now" instead of "did it just become waiting"
// — is the bug this file is written against: it fires again on every poll a
// session sits in the same state, which for a real scan running every couple
// of seconds is a chime replaying for as long as somebody leaves a permission
// prompt up.
public class TurnSignalsClassifyTests
{
    [Fact]
    public void GeneratingToIdleIsFinished()
    {
        Assert.Equal(TurnSignal.Finished, TurnSignals.Classify("generating", "idle"));
    }

    // The no-repeat case: a scan that finds the same permission prompt still
    // up must not ding again, or an unanswered prompt would ding once per
    // scan for as long as it sits there.
    [Fact]
    public void WaitingToWaitingIsNotARepeatSignal()
    {
        Assert.Equal(TurnSignal.None, TurnSignals.Classify("waiting", "waiting"));
    }

    [Fact]
    public void GeneratingToWaitingIsNeedsAttention()
    {
        Assert.Equal(TurnSignal.NeedsAttention, TurnSignals.Classify("generating", "waiting"));
    }

    [Fact]
    public void IdleToWaitingIsNeedsAttention()
    {
        Assert.Equal(TurnSignal.NeedsAttention, TurnSignals.Classify("idle", "waiting"));
    }

    // No prior state is a baseline, never a signal — a first scan, a pruned
    // orb reappearing, anything that never had a "previous" to compare
    // against. Otherwise a relaunch with three idle orbs on screen would
    // announce three finished turns that happened before the app even
    // started.
    [Theory]
    [InlineData("idle")]
    [InlineData("generating")]
    [InlineData("waiting")]
    [InlineData("ended")]
    public void NoPriorStateIsAlwaysABaseline(string next)
    {
        Assert.Equal(TurnSignal.None, TurnSignals.Classify(null, next));
    }

    // "ended" is the hook's own marker for a status file about to be
    // removed, never a real destination a live orb settles into. A session
    // that quits mid-turn must not read as one that finished it.
    [Theory]
    [InlineData("generating")]
    [InlineData("waiting")]
    [InlineData("idle")]
    public void MovingToEndedIsNeverASignal(string previous)
    {
        Assert.Equal(TurnSignal.None, TurnSignals.Classify(previous, "ended"));
    }

    // The negative control for the Finished rule: only generating→idle counts.
    // Without this, a Classify that answered Finished for every arrival at
    // idle would pass the first test above too.
    [Fact]
    public void WaitingToIdleIsNotFinished()
    {
        Assert.Equal(TurnSignal.None, TurnSignals.Classify("waiting", "idle"));
    }

    [Fact]
    public void IdleToIdleIsNothing()
    {
        Assert.Equal(TurnSignal.None, TurnSignals.Classify("idle", "idle"));
    }

    [Fact]
    public void IdleToGeneratingIsNothing()
    {
        Assert.Equal(TurnSignal.None, TurnSignals.Classify("idle", "generating"));
    }
}

// The tracker: the memory Classify needs to have anything to compare
// against, and the two ways something other than an ordinary scan gets to
// touch it without that counting as a turn finishing.
public class TurnSignalTrackerTests
{
    [Fact]
    public void FirstScanOfIdleSessionsGivesZeroSignals()
    {
        var tracker = new TurnSignalTracker();

        Assert.Equal(TurnSignal.None, tracker.Observe("a", "idle"));
        Assert.Equal(TurnSignal.None, tracker.Observe("b", "idle"));
        Assert.Equal(TurnSignal.None, tracker.Observe("c", "idle"));
    }

    [Fact]
    public void ASecondObserveComparesAgainstTheFirst()
    {
        var tracker = new TurnSignalTracker();

        tracker.Observe("a", "generating");
        Assert.Equal(TurnSignal.Finished, tracker.Observe("a", "idle"));
    }

    // Once observed, a session does not signal again on the next identical
    // scan — Observe always records the new state, so a third call comparing
    // idle against idle is the no-repeat case, not a second Finished.
    [Fact]
    public void ARepeatScanOfTheSameStateDoesNotSignalAgain()
    {
        var tracker = new TurnSignalTracker();

        tracker.Observe("a", "generating");
        Assert.Equal(TurnSignal.Finished, tracker.Observe("a", "idle"));
        Assert.Equal(TurnSignal.None, tracker.Observe("a", "idle"));
    }

    // Pruned means forgotten, and forgotten means the next Observe has no
    // previous state — the same baseline a brand-new session gets. A husk
    // that dropped out of sight mid-generation and came back later must not
    // be compared across the gap.
    [Fact]
    public void PruneThenReappearIsABaseline()
    {
        var tracker = new TurnSignalTracker();

        tracker.Observe("a", "generating");
        tracker.Prune(new HashSet<string>()); // "a" was not in this scan

        Assert.Equal(TurnSignal.None, tracker.Observe("a", "idle"));
    }

    [Fact]
    public void PruneLeavesSessionsStillSeenAlone()
    {
        var tracker = new TurnSignalTracker();

        tracker.Observe("a", "generating");
        tracker.Observe("b", "generating");
        tracker.Prune(new HashSet<string> { "a" }); // "b" drops out

        Assert.Equal(TurnSignal.Finished, tracker.Observe("a", "idle"));
        Assert.Equal(TurnSignal.None, tracker.Observe("b", "idle")); // baseline, not Finished
    }

    // Settle is the silent update Reset-to-idle calls: it must not itself be
    // classified as a transition, and it must suppress the Finished a real
    // scan would otherwise report for the transition it papers over.
    [Fact]
    public void SettleSuppressesTheTransitionItRecords()
    {
        var tracker = new TurnSignalTracker();

        tracker.Observe("a", "generating");
        tracker.Settle("a", "idle"); // a manual reset, not a real turn ending

        // The next real scan compares against "idle", so an unrelated later
        // transition is judged on its own terms rather than against the
        // generating state the reset papered over.
        Assert.Equal(TurnSignal.None, tracker.Observe("a", "idle"));
    }
}
