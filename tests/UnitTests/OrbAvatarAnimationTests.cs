using Xunit;

namespace ClaudeBuddy.Tests;

// CB-218: an orb's avatar steps through its frames only when it has frames to
// step through and someone can see it.
public class OrbAvatarAnimationTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]    // hidden: "Show orbs" off
    [InlineData(false, true, false)]    // a still picture, or none
    [InlineData(false, false, false)]
    public void AnAvatarAnimatesOnlyWhenAnimatedAndVisible(bool animated, bool visible, bool runs) =>
        Assert.Equal(runs, OrbAvatarAnimation.ShouldRun(animated, visible));
}
