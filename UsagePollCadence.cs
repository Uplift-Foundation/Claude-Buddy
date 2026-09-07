using System;

namespace ClaudeBuddy
{
    // How often to ask again, and what counts as an answer worth speeding up for.
    //
    // Pure, and separated from the orbs that act on it for the reason
    // OrbArrangement, OrbGlyph and UsageRingGeometry already are: everything it
    // needs arrives as arguments and everything it decides comes back as a
    // value, so every transition can be named in a test rather than inferred
    // from how long a real poll happened to wait.
    //
    // It exists because a fixed five-minute floor is wrong in exactly the
    // situation the rings matter most, and that was measured rather than
    // reasoned about (CB-122). Usage does not climb steadily. Idle, an account
    // sits on the same integer for ten minutes at a stretch; mid-burst it moved
    // 19% to 23% in fifty-six seconds, about 4.3 points a minute. A ring five
    // minutes behind a burst is roughly twenty-one points wrong, which on a
    // 32-dip radius is about seventy-five degrees of arc — not a subtlety
    // anybody has to squint at, and precisely the "usage not reflected" a user
    // reported. Meanwhile the flat case is the common one, and paying burst
    // prices for it all day would be a poor trade.
    //
    // So the cadence follows the data instead of guessing: fast while the
    // numbers are moving, backing off to today's interval once they stop.
    internal static class UsagePollCadence
    {
        // As fast as this ever polls, and where it starts.
        //
        // Sixty seconds is not the floor the CLI imposes — there is no such
        // floor, which is the correction at the heart of CB-122; a fresher
        // number comes back twelve seconds later, measured. It is the floor
        // *this app* imposes, because a full CompositeUsageSource.Read() is
        // three subprocesses and ran 4.5s to 7.7s of wall clock, median 5.6s,
        // on a busy machine. At sixty seconds that is roughly a 10% duty cycle,
        // which is a real cost honestly paid for a ring that tracks a burst;
        // at ten seconds it would be most of a core, for a number that reports
        // whole percentage points.
        internal static readonly TimeSpan Fast = TimeSpan.FromSeconds(60);

        // Where it settles, and what the app did unconditionally before.
        internal static readonly TimeSpan Slow = TimeSpan.FromMinutes(5);

        // The next interval, given the one just used and whether anything on
        // screen actually changed.
        //
        // Doubling rather than dropping straight back to Slow, and starting at
        // Fast rather than Slow, are both about the shape of a burst rather
        // than tidiness. A burst is not one changed poll — it is a run of them
        // with gaps, so a cadence that gave up after the first unchanged answer
        // would spend the whole burst oscillating between right and five
        // minutes stale. 60 → 120 → 240 → 300 keeps a recently-active account
        // close for a few minutes after it goes quiet, then costs exactly what
        // it used to. And an app that has just launched knows nothing at all,
        // which is the one moment it must not assume calm: starting at Slow
        // would let a launch sleep through the first five minutes of a burst,
        // drawing a first-poll number the whole time.
        internal static TimeSpan Next(TimeSpan current, bool changed)
        {
            if (changed) return Fast;

            var doubled = current + current;
            return doubled >= Slow ? Slow : doubled;
        }

        // Whether a new reading would draw anything different from the one it
        // replaces.
        //
        // **Record equality is the trap here, and it fails silently in the
        // direction that looks like success.** AccountUsage is a record, so
        // `before != after` is one keystroke away and compiles — but ReadAt is
        // one of its members and moves on every poll for every live source, so
        // every reading would compare unequal, every poll would report a change,
        // and the cadence would pin itself at Fast forever. That is not a crash
        // or a wrong ring; it is a machine quietly polling five times as often
        // as intended, which nothing on screen would ever reveal. Hence a
        // hand-written comparison of exactly the three numbers the orb draws,
        // and a test named for the case that would catch it.
        //
        // A first reading counts as a change. There is nothing to compare it
        // to, and an account that has just appeared is the one most worth
        // following closely.
        internal static bool DrawnValuesDiffer(AccountUsage? before, AccountUsage after)
        {
            if (before is null) return true;

            return before.Session?.Percent != after.Session?.Percent
                || before.Weekly?.Percent != after.Weekly?.Percent
                || before.Extra?.RingPercent != after.Extra?.RingPercent;
        }
    }
}
