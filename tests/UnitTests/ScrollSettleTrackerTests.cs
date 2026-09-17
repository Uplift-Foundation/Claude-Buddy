using ClaudeBuddy;
using Xunit;

namespace ClaudeBuddy.Tests;

// The pure rule CB-160 replaced ChatPanel.ScrollToEndAfterLayout's fixed
// two-tick settle with: keep watching an extent until it stops moving,
// rather than assuming a fixed number of dispatcher ticks is always enough.
// No window, no ScrollViewer, no dispatcher — the same reason OrbArrangement
// and OrbGlyph stay pure, so the rule is asserted here rather than only
// inferred from how many render ticks a headless ChatPanel test happens to
// need.
public class ScrollSettleTrackerTests
{
    // The plainest case: an extent that never moves settles once it has been
    // seen enough times in a row to prove it, and not one reading sooner.
    // The first reading has nothing to compare against — it can only set the
    // baseline, never confirm stability — so it takes one more reading than
    // StableReadingsRequired to actually settle: the baseline, then that many
    // matching readings after it.
    [Fact]
    public void AnExtentThatNeverMovesSettlesAfterTheRequiredStableReadings()
    {
        var tracker = new ScrollSettleTracker();

        tracker.Observe(500); // the baseline — proves nothing yet on its own
        Assert.True(tracker.ShouldKeepWatching);

        for (var i = 0; i < ScrollSettleTracker.StableReadingsRequired - 1; i++)
        {
            tracker.Observe(500);
            Assert.True(tracker.ShouldKeepWatching, $"matching reading {i} should not yet be enough");
        }

        tracker.Observe(500);
        Assert.False(tracker.ShouldKeepWatching);
    }

    // The case CB-160 exists for: a late-arriving row (an image finishing
    // decode, a markdown block reflowing) grows the extent well after what a
    // fixed two-tick settle would have looked at. The tracker must not have
    // given up before that growth arrives.
    [Fact]
    public void AGrowthAfterSeveralStableReadingsResetsTheStableCount()
    {
        var tracker = new ScrollSettleTracker();

        tracker.Observe(500);
        tracker.Observe(500);
        Assert.True(tracker.ShouldKeepWatching, "one matching reading after the baseline is not yet enough");

        tracker.Observe(812); // the late-arriving row's real height lands
        Assert.True(tracker.ShouldKeepWatching, "a changed extent should restart the stable count");

        tracker.Observe(812);
        Assert.True(tracker.ShouldKeepWatching, "one matching reading after the change is not yet two");

        tracker.Observe(812);
        Assert.False(tracker.ShouldKeepWatching, "two matching readings after the change should settle");
    }

    // A transient pause is not mistaken for settled — the tracker requires a
    // run of StableReadingsRequired readings in a row, so one quiet layout
    // pass in the middle of still-growing content must not end the loop
    // early only for the very next reading to prove it was still moving.
    [Fact]
    public void ATransientPauseIsNotMistakenForSettled()
    {
        var tracker = new ScrollSettleTracker();

        tracker.Observe(500);
        tracker.Observe(500); // one stable reading in — not yet two
        tracker.Observe(640); // moved again before reaching the requirement

        Assert.True(tracker.ShouldKeepWatching, "a single stable reading should not have ended the loop");
    }

    // The ceiling: content that never stops changing — which should not
    // happen in practice, since streaming text has its own per-turn scroll
    // call rather than reaching this tracker at all — still cannot pin a
    // settle loop open forever.
    [Fact]
    public void AnExtentThatNeverStopsChangingStopsAtTheReadingCeiling()
    {
        var tracker = new ScrollSettleTracker();

        for (var i = 0; i < ScrollSettleTracker.MaxReadings - 1; i++)
        {
            tracker.Observe(i); // a different value every time, so it never settles on its own
            Assert.True(tracker.ShouldKeepWatching, $"reading {i} of {ScrollSettleTracker.MaxReadings} should still watch");
        }

        tracker.Observe(ScrollSettleTracker.MaxReadings);
        Assert.False(tracker.ShouldKeepWatching, "the ceiling should stop the loop even without stability");
    }

    // Once settled, further readings are inert rather than something the
    // caller has to remember to stop sending — SettleScrollToEnd unsubscribes
    // from LayoutUpdated as soon as ShouldKeepWatching goes false, but this
    // is what makes that unsubscribe a tidiness rather than a correctness
    // requirement.
    [Fact]
    public void ObserveAfterSettlingIsANoOp()
    {
        var tracker = new ScrollSettleTracker();

        tracker.Observe(500);
        tracker.Observe(500);
        tracker.Observe(500);
        Assert.False(tracker.ShouldKeepWatching);

        tracker.Observe(9999);
        Assert.False(tracker.ShouldKeepWatching, "a reading after settling must not reopen the loop");
    }
}
