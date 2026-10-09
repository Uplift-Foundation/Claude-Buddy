namespace Orbweaver
{
    // When an orb's avatar should be stepping through its frames (CB-218).
    //
    // Only a picture with more than one frame, and only on an orb that is on
    // screen. An orb hidden by "Show orbs" off has no one to show a frame to,
    // and before this its timer kept swapping frames anyway, at the GIF's own
    // rate, in every hidden orb window.
    //
    // What this deliberately does not decide: whether the animation plays on a
    // visible orb. It always does. A persona that moves on screen is the
    // feature, and the measurement that opened this ticket found that freezing
    // every avatar did not move Buddy's CPU at all (34.6% animating against
    // 34.0% frozen), so there is nothing to trade the animation for.
    internal static class OrbAvatarAnimation
    {
        internal static bool ShouldRun(bool animated, bool visible) => animated && visible;
    }
}
