using System;
using Xunit;

namespace ClaudeBuddy.Tests;

// The rule that decides how often to ask again, with no orbs and no clock
// behind it.
//
// Worth testing exhaustively rather than sampling, for the reason the whole
// class exists: the interval it produces is invisible. A cadence that backs off
// too fast draws a stale ring, a cadence that never backs off quietly spends
// five times the process launches it should, and neither shows up on screen as
// anything a person could report. The only place either is legible is here.
public class UsagePollCadenceTests
{
    // --- Next ---------------------------------------------------------------

    // The whole point of the change. A poll that moved a number earns the fast
    // cadence, from wherever it had backed off to — including from the cap,
    // which is the burst-after-a-quiet-hour case and the one that matters.
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(240)]
    [InlineData(300)]
    public void AChangedReadingGoesStraightBackToFast(int currentSeconds)
    {
        Assert.Equal(
            UsagePollCadence.Fast,
            UsagePollCadence.Next(TimeSpan.FromSeconds(currentSeconds), changed: true));
    }

    // 60 → 120 → 240 → 300, and then it stays there. Written out as a sequence
    // rather than as four independent cases because the sequence is the
    // behaviour: each step's input is the previous step's output, so a case per
    // pair could all pass while the chain still failed to reach the cap.
    [Fact]
    public void AnUnchangedReadingDoublesUpToTheCapAndStops()
    {
        var interval = UsagePollCadence.Fast;
        var seen = new System.Collections.Generic.List<double>();

        for (var poll = 0; poll < 6; poll++)
        {
            interval = UsagePollCadence.Next(interval, changed: false);
            seen.Add(interval.TotalSeconds);
        }

        Assert.Equal(new double[] { 120, 240, 300, 300, 300, 300 }, seen);
    }

    // The cap is a ceiling, not a step it lands on exactly: doubling 240 gives
    // 480, and the orb must wait 300 rather than eight minutes. Getting this
    // backwards would be invisible — the ring would simply be staler than
    // anyone intended, with nothing to attribute it to.
    [Fact]
    public void DoublingPastTheCapClampsToItRatherThanOvershooting()
    {
        Assert.Equal(
            UsagePollCadence.Slow,
            UsagePollCadence.Next(TimeSpan.FromSeconds(240), changed: false));

        Assert.Equal(
            UsagePollCadence.Slow,
            UsagePollCadence.Next(UsagePollCadence.Slow, changed: false));
    }

    // Slow is exactly what the app used to do unconditionally, so a quiet
    // machine costs precisely what it did before this change. Pinned because
    // that equivalence is the argument for the change being safe, and a later
    // tweak to either constant should have to say so out loud.
    [Fact]
    public void TheQuietCadenceIsStillTheOldFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), UsagePollCadence.Slow);
        Assert.Equal(TimeSpan.FromSeconds(60), UsagePollCadence.Fast);
        Assert.True(UsagePollCadence.Fast < UsagePollCadence.Slow);
    }

    // --- DrawnValuesDiffer --------------------------------------------------

    private static readonly DateTimeOffset Now =
        new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static AccountUsage Usage(
        double? session = 10,
        double? weekly = 40,
        ExtraUsage? extra = null,
        DateTimeOffset? readAt = null,
        string label = "board",
        bool available = true) =>
        new(
            ConfigDir: null,
            Label: label,
            Available: available,
            SubscriptionType: "team",
            Session: session is null ? null : new UsageWindow(session.Value, Now.AddHours(3)),
            Weekly: weekly is null ? null : new UsageWindow(weekly.Value, Now.AddDays(3)),
            Extra: extra,
            ReadAt: readAt ?? Now,
            Source: AccountUsageSource.ClaudeCode);

    // **The trap, and the reason this function is hand-written rather than an
    // `!=` on the record.** ReadAt moves on every poll for every live source. If
    // the comparison were record equality, every poll would report a change,
    // the cadence would pin at Fast forever, and the machine would quietly poll
    // five times as often as intended — with nothing on screen to reveal it.
    // This is the case that catches that, and it is named for it.
    [Fact]
    public void APollThatOnlyMovedReadAtIsNotAChange()
    {
        var before = Usage(readAt: Now);
        var after = Usage(readAt: Now.AddMinutes(5));

        Assert.NotEqual(before, after);                       // the record disagrees...
        Assert.False(UsagePollCadence.DrawnValuesDiffer(before, after));  // ...the rings do not
    }

    // Same argument, for the other members that move without moving a ring.
    [Fact]
    public void LabelAndAvailabilityAreNotDrawnValues()
    {
        var before = Usage(label: "board");

        Assert.False(UsagePollCadence.DrawnValuesDiffer(before, Usage(label: "renamed")));
        Assert.False(UsagePollCadence.DrawnValuesDiffer(
            before, Usage() with { SubscriptionType = "max" }));
    }

    // A first reading has nothing to compare against, and an account that has
    // just appeared is the one most worth following closely.
    [Fact]
    public void AFirstReadingCountsAsAChange()
    {
        Assert.True(UsagePollCadence.DrawnValuesDiffer(null, Usage()));
    }

    // One case per ring, because each is read off a different member and a
    // dropped clause in the middle would leave two of the three working.
    [Fact]
    public void EachRingIsWatchedIndependently()
    {
        var before = Usage(session: 10, weekly: 40);

        Assert.True(UsagePollCadence.DrawnValuesDiffer(before, Usage(session: 11, weekly: 40)));
        Assert.True(UsagePollCadence.DrawnValuesDiffer(before, Usage(session: 10, weekly: 41)));
        Assert.False(UsagePollCadence.DrawnValuesDiffer(before, Usage(session: 10, weekly: 40)));
    }

    [Fact]
    public void TheExtraRingIsWatchedThroughRingPercentNotTheWholeRecord()
    {
        var spent = new ExtraUsage(
            Enabled: false, UsedMinor: null, LimitMinor: null, Currency: "USD",
            DecimalPlaces: 2, DisabledReason: "org_level_disabled_until",
            UserDisabled: false, SpendLimitReached: true);

        var gauge = new ExtraUsage(true, 1000, 2000, "USD", 2, null);

        Assert.True(UsagePollCadence.DrawnValuesDiffer(Usage(extra: gauge), Usage(extra: spent)));
        Assert.True(UsagePollCadence.DrawnValuesDiffer(Usage(extra: null), Usage(extra: gauge)));
        Assert.False(UsagePollCadence.DrawnValuesDiffer(Usage(extra: spent), Usage(extra: spent)));

        // Both accounts on the machine that prompted CB-122 sit here
        // permanently: spend_limit_reached maps to a full ring, so this pair
        // must read as unchanged or the cadence would never back off at all.
        var alsoSpent = spent with { DisabledReason = "something_else" };
        Assert.False(UsagePollCadence.DrawnValuesDiffer(Usage(extra: spent), Usage(extra: alsoSpent)));
    }

    // A window going away, and coming back, are both changes — an expired
    // reading stops being drawn, which is as visible as any number moving.
    [Fact]
    public void AWindowAppearingOrDisappearingIsAChange()
    {
        Assert.True(UsagePollCadence.DrawnValuesDiffer(Usage(weekly: 40), Usage(weekly: null)));
        Assert.True(UsagePollCadence.DrawnValuesDiffer(Usage(weekly: null), Usage(weekly: 40)));
        Assert.False(UsagePollCadence.DrawnValuesDiffer(Usage(weekly: null), Usage(weekly: null)));
    }

    // The burst this whole change exists for, driven end to end: a run of
    // moving readings holds the cadence at Fast, and only sustained quiet walks
    // it back to Slow.
    [Fact]
    public void ABurstHoldsTheFastCadenceAndOnlyQuietWalksItBack()
    {
        var interval = UsagePollCadence.Slow;

        // 19 → 20 → 21 → 22, the measured shape of a burst.
        foreach (var pct in new double[] { 20, 21, 22 })
        {
            var changed = UsagePollCadence.DrawnValuesDiffer(Usage(session: pct - 1), Usage(session: pct));
            interval = UsagePollCadence.Next(interval, changed);
            Assert.Equal(UsagePollCadence.Fast, interval);
        }

        // Then it stops moving, and three quiet polls are what it takes.
        for (var poll = 0; poll < 3; poll++)
        {
            var changed = UsagePollCadence.DrawnValuesDiffer(Usage(session: 22), Usage(session: 22));
            interval = UsagePollCadence.Next(interval, changed);
        }

        Assert.Equal(UsagePollCadence.Slow, interval);
    }
}
