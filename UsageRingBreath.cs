using System;

namespace ClaudeBuddy
{
    // How a usage ring in the danger band breathes, as a function of time (CB-219).
    //
    // The breath used to be an Avalonia style animation with
    // IterationCount="Infinite". An infinite animation keeps the compositor
    // rendering on every vsync for as long as it runs, and on an account in the
    // danger band that is all the time. Measured on a MacBook in one regime:
    // Buddy at 34.8% CPU with the rings breathing, against 9.5% with the
    // breathing class never applied, and about 6,500 context switches a second
    // against 813. It was most of the app's idle cost.
    //
    // So the same breath is now stepped by AccountOrbWindow's shared ticker at
    // FrameInterval, and this is the curve it samples: the one the XAML
    // declared, 1.0 to 0.55 and back, a sine ease each way, 2.6 s each way. At
    // 20 frames a second a 2.6 s fade is still smooth to the eye, which is the
    // rate the session orbs' own pulse has always run at.
    internal static class UsageRingBreath
    {
        internal const double HalfPeriodMs = 2600;
        internal const double Floor = 0.55;

        internal static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(50);

        // Full opacity at 0 ms, the floor at HalfPeriodMs, full again at twice
        // that, and so on. SineEaseInOut, as the XAML used.
        internal static double OpacityAt(double elapsedMs)
        {
            var cycle = elapsedMs % (2 * HalfPeriodMs);
            if (cycle < 0) cycle += 2 * HalfPeriodMs;

            var t = cycle < HalfPeriodMs ? cycle / HalfPeriodMs : 2 - (cycle / HalfPeriodMs);
            var eased = -(Math.Cos(Math.PI * t) - 1) / 2;

            return 1 - ((1 - Floor) * eased);
        }
    }
}
