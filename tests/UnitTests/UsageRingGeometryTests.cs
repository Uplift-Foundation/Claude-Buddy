using System;
using Avalonia;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers the first arc arithmetic in this app.
//
// Nothing here had ever drawn an ArcSegment before — the only geometry that
// existed was TeamLinkGeometry's straight-sided arrow — so every one of the
// classic ways to get a ring wrong is available and none of them announce
// themselves. Three in particular are what this file exists for:
//
//   - **The full ring.** An arc sweeping exactly 360 degrees has coincident
//     endpoints, and a renderer handed one draws an empty figure. The account at
//     100% would be the one account showing no ring at all.
//   - **The large-arc flag.** Get it backwards and every arc draws its
//     complement: 70% renders as 30%. A screenshot of any single value looks
//     entirely reasonable.
//   - **Wrapping past the cap.** Usage legitimately exceeds 100%, and unclamped
//     that draws 104% as 4% — the most over-committed account on the machine
//     rendering as the healthiest one.
//
// All three are invisible to the eye on a still image, which is why they are
// asserted on numbers here rather than left to the screenshot suite.
public class UsageRingGeometryTests
{
    private static readonly Point Centre = new(42, 42);
    private const double Radius = 38;

    [Fact]
    public void UsageStartsAtTwelveOClock()
    {
        var arc = UsageRingGeometry.ArcFor(Centre, Radius, 25);

        // Straight up: same x as the centre, a radius above it. In Avalonia's
        // space y grows downwards, which is why the start angle is -90 and not
        // +90 — the sign error that would start the ring at six o'clock.
        Assert.Equal(Centre.X, arc.Start.X, 6);
        Assert.Equal(Centre.Y - Radius, arc.Start.Y, 6);
    }

    [Fact]
    public void AQuarterSweepsNinetyDegreesClockwise()
    {
        var arc = UsageRingGeometry.ArcFor(Centre, Radius, 25);

        Assert.Equal(90, arc.SweepDegrees, 6);

        // Three o'clock: to the right of the centre, level with it. Clockwise.
        // Anti-clockwise would put this at nine o'clock and read as 75%.
        Assert.Equal(Centre.X + Radius, arc.End.X, 6);
        Assert.Equal(Centre.Y, arc.End.Y, 6);
    }

    [Fact]
    public void NothingUsedDrawsNoArcRatherThanAZeroLengthOne()
    {
        var arc = UsageRingGeometry.ArcFor(Centre, Radius, 0);

        Assert.True(arc.IsEmpty);
        Assert.False(arc.IsFull);
        Assert.Equal(0, arc.SweepDegrees);
    }

    [Fact]
    public void EverythingUsedIsFlaggedFullSoTheCallerDrawsACircle()
    {
        var arc = UsageRingGeometry.ArcFor(Centre, Radius, 100);

        Assert.True(arc.IsFull);
        Assert.False(arc.IsEmpty);
        Assert.Equal(360, arc.SweepDegrees);
    }

    [Theory]
    [InlineData(100.0001)]
    [InlineData(104.5)]
    [InlineData(1000)]
    public void PastTheCapStaysFullRatherThanWrappingRoundToNearlyEmpty(double percent)
    {
        var arc = UsageRingGeometry.ArcFor(Centre, Radius, percent);

        Assert.True(arc.IsFull);
        Assert.Equal(360, arc.SweepDegrees);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void NonsenseDrawsNothing(double percent)
    {
        Assert.True(UsageRingGeometry.ArcFor(Centre, Radius, percent).IsEmpty);
    }

    // Exactly half is the small arc; a hair over is the large one. Both sides
    // asserted because the boundary is the whole content of the flag.
    [Theory]
    [InlineData(25, false)]
    [InlineData(50, false)]
    [InlineData(50.001, true)]
    [InlineData(75, true)]
    [InlineData(99, true)]
    public void TheLargeArcFlagFollowsTheHalfway(double percent, bool expected)
    {
        Assert.Equal(expected, UsageRingGeometry.ArcFor(Centre, Radius, percent).LargeArc);
    }

    [Fact]
    public void EveryPointOfTheArcSitsOnTheRing()
    {
        // A cheap guard against an arithmetic slip that would otherwise only
        // show up as a ring that visibly is not round.
        for (var percent = 1; percent < 100; percent++)
        {
            var arc = UsageRingGeometry.ArcFor(Centre, Radius, percent);
            var dx = arc.End.X - Centre.X;
            var dy = arc.End.Y - Centre.Y;

            Assert.Equal(Radius, Math.Sqrt(dx * dx + dy * dy), 6);
        }
    }

    // Colour means headroom. The thresholds are asserted on both sides because a
    // >= written as > moves every boundary by one account's worth of anxiety.
    [Theory]
    [InlineData(0, "calm")]
    [InlineData(59.99, "calm")]
    [InlineData(60, "warn")]
    [InlineData(84.99, "warn")]
    [InlineData(85, "danger")]
    [InlineData(140, "danger")]
    [InlineData(double.NaN, "calm")]
    public void ColourFollowsHeadroom(double percent, string expected)
    {
        Assert.Equal(expected, UsageRingGeometry.ColourFor(percent, "calm", "warn", "danger"));
    }

    // The colours arrive as arguments and are never looked up, so this function
    // gives the same answers on a machine whose settings have been changed —
    // the rule OrbGlyph states and the reason it is testable at all.
    [Fact]
    public void ColoursAreWhateverTheCallerPassed()
    {
        Assert.Equal("#123456", UsageRingGeometry.ColourFor(90, "a", "b", "#123456"));
        Assert.Equal("teal", UsageRingGeometry.ColourFor(10, "teal", "b", "c"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(84.99, false)]
    [InlineData(85, true)]
    [InlineData(120, true)]
    [InlineData(double.NaN, false)]
    public void OnlyTheDangerBandBreathes(double percent, bool expected)
    {
        Assert.Equal(expected, UsageRingGeometry.ShouldBreathe(percent));
    }

    // --- what to do about it (CB-121) ---------------------------------------
    // ShouldBreathe above answers whether a reading has earned motion.
    // BreathChangeFor answers the question the window used to answer for itself:
    // given a ring that is or is not already breathing, what should change.
    //
    // Worth its own function rather than an `if` at the call site because the
    // interesting answer is the one that does nothing. A ring already breathing
    // must be left strictly alone — restarting it resets the animation's phase,
    // and an account orb polls five minutes apart, so the result is a stutter at
    // no explicable interval rather than anything a reader would connect to a
    // poll. That rule was previously implicit in a dictionary lookup, and later
    // in Classes.Set happening to be a no-op; here it is a named outcome with a
    // case of its own.

    [Theory]
    // Not breathing, and the reading says it should be: the only Start.
    [InlineData(false, 85d, UsageRingGeometry.BreathChange.Start)]
    [InlineData(false, 92d, UsageRingGeometry.BreathChange.Start)]
    [InlineData(false, 140d, UsageRingGeometry.BreathChange.Start)]
    // Already breathing and still should be. This is the no-restart rule.
    [InlineData(true, 85d, UsageRingGeometry.BreathChange.Leave)]
    [InlineData(true, 92d, UsageRingGeometry.BreathChange.Leave)]
    // Breathing, and the reading has come back down. A green ring left pulsing
    // like an emergency is a worse lie than one that never pulsed, and every
    // account crosses this boundary downwards when its week resets.
    [InlineData(true, 84.99d, UsageRingGeometry.BreathChange.Stop)]
    [InlineData(true, 0d, UsageRingGeometry.BreathChange.Stop)]
    // Calm and staying calm: the overwhelmingly common poll, and it must be
    // free of writes to a shape.
    [InlineData(false, 0d, UsageRingGeometry.BreathChange.Leave)]
    [InlineData(false, 84.99d, UsageRingGeometry.BreathChange.Leave)]
    // No reading at all — expired, or never sent. The ring is not drawn, so it
    // must not be breathing either.
    [InlineData(true, null, UsageRingGeometry.BreathChange.Stop)]
    [InlineData(false, null, UsageRingGeometry.BreathChange.Leave)]
    // NaN is not a small number, and ShouldBreathe already refuses it; this
    // pins that the refusal survives the wrapper rather than being re-derived.
    [InlineData(true, double.NaN, UsageRingGeometry.BreathChange.Stop)]
    [InlineData(false, double.NaN, UsageRingGeometry.BreathChange.Leave)]
    internal void BreathChangeCoversEveryOutcome(
        bool breathing, double? percent, UsageRingGeometry.BreathChange expected)
    {
        Assert.Equal(expected, UsageRingGeometry.BreathChangeFor(breathing, percent));
    }

    // The whole point of the enum, stated once on its own: asking twice with
    // nothing changed in between must never produce a second Start. A window
    // acting on Start twice is a phase reset; acting on Leave twice is nothing
    // at all, which is what a re-poll answering the same reading deserves.
    [Fact]
    public void APollThatChangesNothingAsksForNothing()
    {
        Assert.Equal(UsageRingGeometry.BreathChange.Start,
            UsageRingGeometry.BreathChangeFor(breathing: false, percent: 92));

        // ...and now it is breathing, so the same reading arriving again, and
        // again, and a slightly higher one after that, all leave it alone.
        Assert.Equal(UsageRingGeometry.BreathChange.Leave,
            UsageRingGeometry.BreathChangeFor(breathing: true, percent: 92));
        Assert.Equal(UsageRingGeometry.BreathChange.Leave,
            UsageRingGeometry.BreathChangeFor(breathing: true, percent: 92));
        Assert.Equal(UsageRingGeometry.BreathChange.Leave,
            UsageRingGeometry.BreathChangeFor(breathing: true, percent: 99));
    }
}
