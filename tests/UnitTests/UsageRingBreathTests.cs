using Xunit;

namespace Orbweaver.Tests;

// CB-219: the breath a usage ring in the danger band takes, now sampled by a
// 20 fps ticker instead of run as an infinite style animation. These pin that it
// is the same breath the XAML declared: 1.0 to 0.55 and back, a sine ease, 2.6 s
// each way.
public class UsageRingBreathTests
{
    [Theory]
    [InlineData(0, 1.0)]         // starts at full
    [InlineData(2600, 0.55)]     // the floor, half a cycle in
    [InlineData(5200, 1.0)]      // full again: it alternates, it doesn't snap back
    [InlineData(7800, 0.55)]     // and keeps going
    [InlineData(1300, 0.775)]    // a sine ease is exactly halfway at the middle
    [InlineData(3900, 0.775)]    // on the way back up too
    public void TheBreathHitsItsMarks(double ms, double opacity) =>
        Assert.Equal(opacity, UsageRingBreath.OpacityAt(ms), precision: 9);

    [Fact]
    public void NeverFallsBelowTheFloorOrRisesAboveFull()
    {
        for (var ms = -6000.0; ms <= 12000; ms += 37)
            Assert.InRange(UsageRingBreath.OpacityAt(ms), UsageRingBreath.Floor, 1.0);
    }

    [Fact]
    public void FallsOnTheWayOutAndRisesOnTheWayBack()
    {
        for (var ms = 0.0; ms < 2600; ms += 50)
            Assert.True(UsageRingBreath.OpacityAt(ms + 50) <= UsageRingBreath.OpacityAt(ms));
        for (var ms = 2600.0; ms < 5200; ms += 50)
            Assert.True(UsageRingBreath.OpacityAt(ms + 50) >= UsageRingBreath.OpacityAt(ms));
    }

    [Fact]
    public void EasesInAndOutRatherThanMovingLinearly()
    {
        // A sine ease moves slowest at the ends of a stroke and fastest in the
        // middle: the first 10% of the way down covers much less than a tenth of
        // the drop.
        var drop = 1 - UsageRingBreath.Floor;
        var early = 1 - UsageRingBreath.OpacityAt(260);
        Assert.True(early < drop * 0.1);
    }

    [Fact]
    public void ANegativeClockGivesTheSameBreathAsItsPositiveTwin() =>
        Assert.Equal(UsageRingBreath.OpacityAt(3000), UsageRingBreath.OpacityAt(3000 - 5200), precision: 9);

    [Fact]
    public void ItIsSteppedAtTheSessionOrbsPulseRate() =>
        Assert.Equal(100, UsageRingBreath.FrameInterval.TotalMilliseconds);
}
