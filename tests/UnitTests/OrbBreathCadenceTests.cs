using Xunit;

namespace Orbweaver.Tests;

// When the shared pulse tick may move an orb's scale, and the curve it moves
// it along. The OrbWindow half — the avatar's frame stepping the breath
// instead — is in tests/UiTests/OrbAvatarTests.
public class OrbBreathCadenceTests
{
    [Theory]
    [InlineData(true, false, 1000, 0, false)]    // animated avatar: its own frames carry the breath
    [InlineData(true, true, 1000, 0, false)]
    [InlineData(false, false, 1000, 990, true)]  // a working pulse keeps every tick
    [InlineData(false, true, 1000, 0, true)]     // idle, long since the last step
    [InlineData(false, true, 1100, 1000, false)] // idle, one tick after a step: skipped
    [InlineData(false, true, 1150, 1000, true)]  // a late tick still lands a step
    [InlineData(false, true, 1149, 1000, false)]
    [InlineData(false, true, 1200, 1000, true)]
    public void OnlyAnOrbWithoutAnAnimatedAvatarStepsOnThePulseAndTheIdleBreathEveryOtherTick(
        bool avatarAnimating, bool idle, long now, long lastStepAt, bool steps) =>
        Assert.Equal(steps, OrbBreathCadence.PulseTickSetsScale(avatarAnimating, idle, now, lastStepAt));

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(2200, 1.06)]    // halfway through a 4.4 s idle breath: the full swell
    [InlineData(4400, 1.0)]
    [InlineData(6600, 1.06)]
    public void TheBreathSwellsToItsPeakHalfwayAndBack(long elapsedMs, double scale) =>
        Assert.Equal(scale, OrbBreathCadence.ScaleAt(elapsedMs, 4400, 1.06), precision: 9);
}
