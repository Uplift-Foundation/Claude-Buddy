using System;
using Avalonia;

namespace ClaudeBuddy
{
    // Where a usage ring's arc starts and stops, and what colour it is.
    //
    // Pure, and separated from the window that draws it for the reason
    // OrbArrangement and TeamLinkGeometry already are: everything it needs
    // arrives as arguments and everything it decides comes back as a value, so
    // the arithmetic can be tested exhaustively without a display, a screen or a
    // settings file. This is the first arc drawing in the app — nothing here has
    // ever had an ArcSegment or a PathGeometry before — so it is also the first
    // chance to get the trig wrong somewhere nobody can see it.
    //
    // The threshold colours are **parameters, not lookups**. OrbGlyph states the
    // rule and this file is bound by it: a function that reads a user setting
    // can only be tested on a machine whose settings happen to suit, and the one
    // thing this file exists to be is testable.
    internal static class UsageRingGeometry
    {
        // Usage begins at twelve o'clock and runs clockwise, because a clock is
        // the only shared intuition for "a window filling up" and every
        // ring-shaped gauge anyone has already seen works that way. In Avalonia's
        // coordinate space, where y grows downwards, that is -90 degrees.
        internal const double StartAngleDegrees = -90;

        // Where the colour changes, in percent.
        //
        // Not evenly spaced, and deliberately: the interesting part of this scale
        // is the top. Below 60% the answer is "fine" and the exact number does
        // not change anyone's behaviour; between 60 and 85 it is worth knowing;
        // above 85 it is worth acting on. Even thirds would spend a whole colour
        // on the half of the range nobody needs to think about.
        internal const double WarnAtPercent = 60;
        internal const double DangerAtPercent = 85;

        // What to draw for one ring.
        //
        // IsFull is not a convenience — it is the difference between a ring and
        // nothing at all. An arc that sweeps exactly 360 degrees has coincident
        // endpoints, and a renderer handed one draws an empty figure rather than
        // a circle. So a full ring has to become an ellipse instead, and the
        // caller has to be told which it is holding.
        //
        // IsEmpty is the mirror: zero usage is no arc, not a zero-length one.
        internal readonly record struct Arc(
            Point Start, Point End, double SweepDegrees, bool LargeArc, bool IsFull, bool IsEmpty);

        // The arc for a percentage on a ring of this radius about this centre.
        //
        // Clamped at both ends, and the top end is the one that matters: usage
        // legitimately runs past a window's cap, so a reading of 104% arrives in
        // the ordinary course of events rather than as corruption. Left
        // unclamped it would wrap past twelve o'clock and draw as 4% — the most
        // over-committed account on the machine rendering as the healthiest.
        internal static Arc ArcFor(Point centre, double radius, double percent)
        {
            if (double.IsNaN(percent) || percent <= 0)
            {
                var origin = PointOnRing(centre, radius, StartAngleDegrees);
                return new Arc(origin, origin, 0, false, false, true);
            }

            if (percent >= 100)
            {
                var origin = PointOnRing(centre, radius, StartAngleDegrees);
                return new Arc(origin, origin, 360, true, true, false);
            }

            var sweep = percent / 100.0 * 360.0;
            var start = PointOnRing(centre, radius, StartAngleDegrees);
            var end = PointOnRing(centre, radius, StartAngleDegrees + sweep);

            // Over half the ring is the "large" of the two arcs sharing these
            // endpoints. Getting this backwards draws the complement — 70% would
            // render as 30% — and it is invisible in a still screenshot of any
            // single value, which is why it has a test per side of the boundary.
            return new Arc(start, end, sweep, sweep > 180, false, false);
        }

        internal static Point PointOnRing(Point centre, double radius, double angleDegrees)
        {
            var radians = angleDegrees * Math.PI / 180.0;
            return new Point(
                centre.X + radius * Math.Cos(radians),
                centre.Y + radius * Math.Sin(radians));
        }

        // Which of the caller's three colours a reading has earned.
        //
        // Colour means headroom here, and nothing else. It deliberately does not
        // mean identity — two accounts at 30% are the same green, and telling
        // them apart is the job of the letters in the middle and the dot on the
        // card. An account's own colour on its own ring would make the whole row
        // unreadable at a glance, which is the one thing the row is for.
        internal static string ColourFor(double percent, string calm, string warn, string danger)
        {
            if (double.IsNaN(percent)) return calm;
            if (percent >= DangerAtPercent) return danger;
            return percent >= WarnAtPercent ? warn : calm;
        }

        // Whether this reading should draw attention to itself by moving.
        //
        // Only the danger band breathes, and only ever one ring at a time on any
        // one orb, because motion that is always present is not a signal. The
        // orb's own pulse ticker already establishes the rule that stillness is
        // the resting state.
        internal static bool ShouldBreathe(double percent) =>
            !double.IsNaN(percent) && percent >= DangerAtPercent;
    }
}
