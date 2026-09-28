namespace ClaudeBuddy
{
    // When an orb's breath is allowed to move its scale.
    //
    // Every present of an orb window costs a Metal render session of its own,
    // and on the MacBook that session — not the drawing inside it — was most of
    // the render thread's time: SkiaMetalRenderSession.Dispose alone was four
    // samples in every seven the thread spent busy. So the cost of the orbs is,
    // to a first approximation, the number of presents, and this decides two
    // ways of spending fewer without anybody seeing a difference.
    //
    // An orb whose avatar is animating already presents at its GIF's rate. The
    // breath stepping on the shared ticker as well interleaved a second set of
    // presents between the frames — 25 + 10 a second on Jennifer's orb, where
    // 25 would do — so on such an orb the pulse tick leaves the scale alone and
    // the avatar's own frame applies the breath instead. The breath still reads
    // as the state channel it is; it just rides frames that were being drawn
    // anyway.
    //
    // The idle breath is a 6% swell over 4.4 s. At the ticker's 10 frames a
    // second it moves under a sixth of a point a step, and at 5 it moves under
    // a third, which is still below anything that reads as a step. The working
    // pulses are faster and larger and keep every tick.
    internal static class OrbBreathCadence
    {
        internal const long IdleStepMs = 200;

        // Ticks land late as well as early, so the next step is due a little
        // before a full IdleStepMs has passed: half the ticker's 100 ms either
        // way still puts every other tick on a step, and never two in a row.
        internal const long SlackMs = 50;

        internal static bool PulseTickSetsScale(bool avatarAnimating, bool idleBreath, long now, long lastStepAt) =>
            !avatarAnimating && (!idleBreath || now - lastStepAt >= IdleStepMs - SlackMs);

        // 1 at the start of each period, `to` halfway, 1 again at the end,
        // smooth at both ends.
        internal static double ScaleAt(long elapsedMs, double periodMs, double to)
        {
            var phase = elapsedMs % periodMs / periodMs;
            var eased = (1 - Math.Cos(phase * 2 * Math.PI)) / 2;
            return 1.0 + (to - 1.0) * eased;
        }
    }
}
