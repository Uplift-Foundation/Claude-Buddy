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

        // The widest separation worth insisting on. Beyond about this, two hues
        // are already unmistakable and holding out for more only moves agents
        // further from the colour their own id asked for.
        private const int IdealGap = 55;

        // The colour an agent would like, before anyone else is considered. Any
        // hue at all — the whole wheel is in play, not a fixed set of names.
        public static string HexFor(string key) => Hex(PreferredHue(key), Saturation, Value);

        private static uint PreferredHue(string key) => Hash(key ?? "") % 360u;

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

            // How far apart is *enough* depends on how many are competing, which
            // is the thing a fixed number can't express.
            //
            // A first attempt insisted on 24° between any two agents and still
            // produced two rings that were called the same colour — 24° in the
            // blues is barely a step. The instinct was then to quantise the
            // wheel to a dozen hand-picked hues, which fixed that and gave up
            // the spectrum to do it.
            //
            // Neither is necessary. With six agents there is room for 51°
            // between neighbours, which is unmistakable; with twenty there is
            // room for 17°, and cramped-but-distinct is the honest best on offer
            // at that point. Scaling to the count gives the widest separation
            // the wheel can actually deliver, at any number of agents, while
            // every hue stays available.
            //
            // The largest gap that actually works for *this* set, found by
            // trying and backing off rather than by arithmetic.
            //
            // Arithmetic gets it wrong, and did: 360 / (count + 1) looks like
            // the right ceiling — eight agents, 40° each — but a placed hue
            // blocks the gap on *both* sides, and placements start from wherever
            // each id hashes to rather than from an even grid. So the circle
            // fills up long before count × gap reaches 360, the last few agents
            // fail their gap, fall back, and land 13° from a neighbour. Which is
            // exactly the "these two are the same colour" this was fixing.
            //
            // Walking down from the ideal costs at most fifty cheap attempts and
            // gives the widest separation greedy placement can genuinely deliver
            // for this particular set of ids.
            for (var gap = IdealGap; gap > 1; gap--)
            {
                if (TryAssign(ids, gap) is { } spread) return spread;
            }

            // One degree apart is the floor: distinct values, no promises about
            // telling them apart. Only reachable with hundreds of agents.
            return TryAssign(ids, 1) ?? ids.ToDictionary(id => id, id => HexFor(id), StringComparer.Ordinal);
        }

        // Places every id at or after its own preferred hue, keeping `gap`
        // between all of them. Null the moment one cannot be placed — a partial
        // assignment is no use, and the caller is about to try a smaller gap.
        private static Dictionary<string, string>? TryAssign(List<string> ids, int gap)
        {
            var taken = new List<int>();
            var colours = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var id in ids)
            {
                // Forward from the agent's own hue rather than to the nearest
                // free slot, so the result reads the same however the list is
                // traversed.
                if (Find((int)PreferredHue(id), taken, gap) is not { } hue) return null;

                taken.Add(hue);
                colours[id] = Hex(hue, Saturation, Value);
            }

            return colours;
        }

        // The first hue at or after `preferred` that clears `gap` from every hue
        // already placed, or null if the circle is too crowded for that.
        //
        // Null is not a failure here — it is the signal that this gap is too
        // ambitious for this set, which is how Assign finds the one that isn't.
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
