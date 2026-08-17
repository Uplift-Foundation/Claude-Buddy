namespace ClaudeBuddy
{
    // A colour per agent, for the sessions that don't get to choose one.
    //
    // A Claude Code session picks its own with /color, out of the dozen names
    // Claude Code knows. An OpenClaw agent has no such notion, so every gateway
    // orb was drawn with the plain ring and the plain glyph — which is fine for
    // one and useless for six, where telling Lilibeth from Zara at a glance is
    // the entire job the ring does for local sessions.
    //
    // **Derived, not random, and not stored.** "Random per agent, but the same
    // colour every time" is exactly a hash: the agent id goes in, a hue comes
    // out, and it is the same hue on the next launch, on another machine, and
    // after the settings file is deleted. Picking randomly and persisting the
    // choice would mean a new file to write, migrate and keep in step with a
    // gateway's agent list — for a worse result, since two agents could still
    // draw the same colour.
    //
    // Unrestricted in hue, deliberately. Claude Code has about eight usable
    // colour names; an agent list is not limited that way, and quantising to
    // eight would put two agents on the same colour as soon as there were nine
    // of them.
    //
    // Pure and hex-valued so it can be tested without a UI toolkit.
    public static class AgentPalette
    {
        // Saturation and value are fixed, and these two numbers are not
        // arbitrary — they are Claude Code's own. #D75F5F, #5F87D7, #875FD7 and
        // #D787AF are all exactly S=0.558, V=0.843, differing only in hue. So a
        // generated colour sitting on that surface is the same *kind* of colour
        // as a /color one: it reads as native beside them, is legible on the
        // dark orb, and never comes out as either a pastel or a neon.
        private const double Saturation = 0.558;
        private const double Value = 0.843;

        // How far apart two agents' hues have to be to be told apart at the size
        // an orb's ring is drawn. Below roughly this, two rings read as "the
        // same colour, maybe" — which is worse than no colour, because it
        // invites a distinction that isn't there.
        private const int MinGap = 24;

        // The colour an agent would like, before anyone else is considered.
        public static string HexFor(string key) => Hex(PreferredHue(key), Saturation, Value);

        // Colours for a whole set of agents at once.
        //
        // Hashing alone is not enough, and this is not a theoretical worry — in
        // the first sixteen ids tried, "warden" and "main-3" both hashed to hue
        // 60 and drew *identical* orbs, with "main-1" two degrees away. With 360
        // hues and eight agents there are 28 pairs, so a near-collision is the
        // expected case rather than bad luck. An agent list whose whole purpose
        // is telling six agents apart cannot ship that.
        //
        // So each agent starts at its own derived hue and is moved only if it
        // would land too close to one already placed. The trade-off is stated
        // plainly: a colour is stable for as long as the set is, and adding an
        // agent can nudge one that collides with it. Nudging the few is better
        // than two orbs nobody can tell apart, and the alternative — storing an
        // assignment — has the same problem the moment a new agent arrives, plus
        // a file to keep in step.
        //
        // Sorted, so the answer depends on *which* agents exist and not on the
        // order the gateway happened to list them in.
        public static Dictionary<string, string> Assign(IEnumerable<string> agentIds)
        {
            var ids = agentIds.Where(id => !string.IsNullOrEmpty(id))
                              .Distinct(StringComparer.Ordinal)
                              .OrderBy(id => id, StringComparer.Ordinal)
                              .ToList();

            // With enough agents the gap has to give, or there is no assignment
            // at all. Shrinking it keeps every agent a distinct colour when they
            // can no longer all be an obviously distinct one.
            //
            // Divided by count + 1 rather than count, because exactly
            // count × gap degrees leaves no slack anywhere on the circle and the
            // last agent has nowhere legal to go — which showed up as a
            // duplicate colour at forty agents rather than as a failure.
            var gap = ids.Count > 0 ? Math.Min(MinGap, Math.Max(1, 360 / (ids.Count + 1))) : MinGap;

            var taken = new List<int>();
            var colours = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var id in ids)
            {
                var preferred = (int)PreferredHue(id);

                // Walk forward to the first hue far enough from every hue already
                // placed. Forward rather than to the nearest free slot, so the
                // result reads the same however the list is traversed.
                var hue = Find(preferred, taken, gap) ?? Find(preferred, taken, 1) ?? preferred;

                taken.Add(hue);
                colours[id] = Hex(hue, Saturation, Value);
            }

            return colours;
        }

        // The first hue at or after `preferred` that clears `gap` from every hue
        // already placed, or null if the circle is too crowded for that.
        //
        // Called twice: once at the real gap, and once at 1° if that failed, so
        // that beyond the point where every agent can look obviously different
        // they at least still look different. Only past ~360 agents does the
        // second attempt fail too, and then a repeat is the honest outcome.
        private static int? Find(int preferred, List<int> taken, int gap)
        {
            for (var step = 0; step < 360; step++)
            {
                var hue = (preferred + step) % 360;
                if (taken.All(t => Separation(t, hue) >= gap)) return hue;
            }

            return null;
        }

        // Hue is a circle, so 350 and 10 are twenty apart, not three hundred.
        private static int Separation(int a, int b)
        {
            var d = Math.Abs(a - b) % 360;
            return Math.Min(d, 360 - d);
        }

        private static uint PreferredHue(string key) => Hash(key ?? "") % 360u;

        // FNV-1a. Chosen for being stable rather than for being good: this value
        // decides what colour an agent is, so it has to survive a .NET upgrade.
        // string.GetHashCode() is explicitly randomised per process and would
        // repaint every orb on every launch — which is the one thing this must
        // not do.
        private static uint Hash(string text)
        {
            const uint Offset = 2166136261;
            const uint Prime = 16777619;

            var hash = Offset;
            foreach (var c in text)
            {
                hash ^= c;
                hash *= Prime;
            }

            return hash;
        }

        private static string Hex(double hue, double saturation, double value)
        {
            var c = value * saturation;
            var x = c * (1 - Math.Abs(hue / 60.0 % 2 - 1));
            var m = value - c;

            var (r, g, b) = hue switch
            {
                < 60 => (c, x, 0.0),
                < 120 => (x, c, 0.0),
                < 180 => (0.0, c, x),
                < 240 => (0.0, x, c),
                < 300 => (x, 0.0, c),
                _ => (c, 0.0, x)
            };

            return "#" + Byte(r + m) + Byte(g + m) + Byte(b + m);
        }

        private static string Byte(double v) =>
            ((int)Math.Round(Math.Clamp(v, 0, 1) * 255)).ToString("X2");
    }
}
