using Avalonia;

namespace ClaudeBuddy
{
    // Where every orb goes when you arrange them into a shape.
    //
    // Pulled out of SessionManager and made pure — no windows, no screens, no
    // settings — because it could not be tested where it was, and it needed to
    // be. A run of bugs here were all of a kind that a person notices only by
    // looking at the screen and squinting: patterns half off the edge, orbs
    // drawn on top of each other, a team fanned so far from its lead that the
    // arrows stopped being drawn at all. Each fix broke a case the last one had
    // fixed, because nothing checked the other cases.
    //
    // Everything it needs arrives as arguments, and everything it decides comes
    // back as an array. tests/ArrangementTests walks every shape at every
    // spacing across a range of counts and team shapes and asserts the things
    // that were going wrong.
    //
    // Positions are window top-left corners in physical pixels, which is what
    // OrbWindow.Position takes. The circle a person sees is 36 DIP inside a
    // 56 DIP window, and a team member's is 72% of that again — those three
    // numbers, mixed up, caused most of the bugs.
    internal static class OrbArrangement
    {
        internal const double WindowDip = 56;
        internal const double CircleDip = 36;
        internal const double MemberScale = 0.72;

        internal readonly record struct Layout(
            PixelRect Work,
            double Scale,
            string Shape,
            double Spacing);

        // leadOf[i] is the index of orb i's team lead, or -1 if it leads itself.
        internal static PixelPoint[] Compute(int count, int[] leadOf, Layout layout)
        {
            if (count <= 0) return Array.Empty<PixelPoint>();

            var anchors = new List<int>();
            var members = new Dictionary<int, List<int>>();

            Classify(count, leadOf, anchors, members);

            var window = (int)Math.Round(WindowDip * layout.Scale);
            var circle = CircleDip * layout.Scale;

            var shape = Fit(ShapeFor(anchors.Count, layout), layout.Work, window, MinGap(circle, layout.Spacing));

            var result = new PixelPoint[count];
            var placed = new bool[count];

            for (var i = 0; i < anchors.Count; i++)
            {
                result[anchors[i]] = shape[i];
                placed[anchors[i]] = true;
            }

            var nearest = Nearest(shape);

            // Breadth-first from the anchors, so a lead that is itself somebody's
            // member is positioned before its own members are hung off it.
            // Walking only the anchors left those grandchildren wherever they
            // happened to be, which read as orbs ignoring the arrangement.
            var queue = new Queue<int>(anchors);
            var centre = Centre(shape);

            while (queue.Count > 0)
            {
                var lead = queue.Dequeue();
                if (!members.TryGetValue(lead, out var team)) continue;

                var positions = FanOut(result[lead], centre, team.Count, circle, nearest, layout.Work, window);

                for (var i = 0; i < team.Count; i++)
                {
                    result[team[i]] = positions[i];
                    placed[team[i]] = true;
                    queue.Enqueue(team[i]);
                }
            }

            // Anything a cycle left unreachable still needs somewhere to be.
            for (var i = 0; i < count; i++)
            {
                if (!placed[i]) result[i] = shape[0];
            }

            return Separate(result, leadOf, circle, layout.Work, window, layout.Spacing);
        }

        // Who is an anchor and who hangs off whom. A lead that cannot be
        // resolved — pointing at itself, at a missing orb, or round a cycle —
        // becomes an anchor, because a shape with nothing to draw is worse than
        // a team drawn as separate orbs.
        private static void Classify(
            int count, int[] leadOf, List<int> anchors, Dictionary<int, List<int>> members)
        {
            for (var i = 0; i < count; i++)
            {
                var lead = i < leadOf.Length ? leadOf[i] : -1;

                if (!Resolves(i, leadOf, count))
                {
                    anchors.Add(i);
                    continue;
                }

                if (!members.TryGetValue(lead, out var team)) members[lead] = team = new List<int>();
                team.Add(i);
            }

            // Every orb in a team means no shape at all. One of them leads.
            if (anchors.Count == 0 && count > 0)
            {
                anchors.Add(0);
                foreach (var team in members.Values) team.Remove(0);
                members.Remove(0);
            }
        }

        private static bool Resolves(int start, int[] leadOf, int count)
        {
            var at = start;

            for (var hops = 0; hops <= count; hops++)
            {
                var lead = at < leadOf.Length ? leadOf[at] : -1;

                if (lead < 0 || lead >= count) return hops > 0;
                if (lead == at) return false;

                at = lead;
                if (at == start) return false;   // cycled back to where we began
            }

            return false;
        }

        // The gap the pattern aims for between neighbouring orbs. Measured on
        // the circle that is drawn rather than the window it sits in — the
        // window is more than half as wide again, and using it inflated every
        // arrangement by that margin.
        internal static double MinGap(double circle, double spacing) => circle * (0.35 + spacing);

        // How far a team member sits from its lead, at minimum: close enough to
        // read as a group, far enough that TeamLinks will still draw the arrow.
        // Asking TeamLinks rather than choosing a number, because a gap that
        // merely stops the two circles overlapping is well short of what an
        // arrow needs, and the two used to disagree.
        internal static double ArrowRadius(double circle, double scale)
        {
            var leadRadiusDip = CircleDip / 2;
            var memberRadiusDip = leadRadiusDip * MemberScale;

            return (TeamLinkGeometry.MinimumCentreDistance(memberRadiusDip, leadRadiusDip) + 3) * scale;
        }

        private static PixelPoint[] FanOut(
            PixelPoint lead, (double X, double Y) centre, int count,
            double circle, double nearest, PixelRect work, int window)
        {
            var memberCircle = circle * MemberScale;

            // A small team fans off one side of its lead; a large one has to go
            // most of the way round it. Capping the arc at about half a turn
            // meant nineteen members could not fit at the distance their arrows
            // need, however hard they were pushed apart — they were squeezed
            // into an arc too short to hold them, and the last pixel of overlap
            // simply could not be resolved.
            var spread = count <= 2
                ? Math.PI / 3
                : Math.Min(Math.PI * 2 * 0.92, Math.PI / 3 + (count - 2) * 0.35);

            var scale = circle / CircleDip;
            var radius = ArrowRadius(circle, scale);

            if (count > 1)
            {
                var step = spread / (count - 1);
                radius = Math.Max(radius, memberCircle * 1.15 / (2 * Math.Sin(step / 2)));
            }

            // Kept inside the lead's own share of the pattern where there is
            // room, but never closer than the arrow needs — a team with no
            // arrows is not readable as a team, which is the whole point of
            // drawing it as one.
            if (nearest > 0) radius = Math.Min(radius, Math.Max(ArrowRadius(circle, scale), nearest * 0.42));

            // Outward from the middle of the pattern, so a team hangs off the
            // outside of the shape rather than into it.
            var half = window / 2.0;
            var dx = lead.X + half - centre.X;
            var dy = lead.Y + half - centre.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);

            if (dist < 1) { dx = 0; dy = -1; dist = 1; }

            var baseAngle = Math.Atan2(dy / dist, dx / dist);
            var placed = new PixelPoint[count];

            for (var i = 0; i < count; i++)
            {
                var offset = count == 1
                    ? 0
                    : spread * (i - (count - 1) / 2.0) / Math.Max(count - 1, 1);

                var angle = baseAngle + offset;

                placed[i] = new PixelPoint(
                    Clamp((int)Math.Round(lead.X + radius * Math.Cos(angle)), work.X, work.Right - window),
                    Clamp((int)Math.Round(lead.Y + radius * Math.Sin(angle)), work.Y, work.Bottom - window));
            }

            return placed;
        }

        private static int Clamp(int value, int low, int high) =>
            high < low ? low : Math.Clamp(value, low, high);

        // Unit outlines, sampled so that consecutive points are equally far
        // apart *along the outline*. Stepping the parameter instead bunches
        // points wherever the curve moves fastest, and the spacing pass then
        // inflates the whole shape to separate the tightest pair.
        internal static (double X, double Y)[] Unit(string shape, int n)
        {
            if (n <= 0) return Array.Empty<(double, double)>();
            if (n == 1) return new[] { (0.0, 0.0) };

            return shape switch
            {
                "circle" => Sampled(CircleAt, n),
                "diamond" => Sampled(DiamondAt, n),
                "star" => Sampled(StarAt, n),
                "grid" => Grid(n),
                _ => Sampled(HeartAt, n)
            };
        }

        private static (double X, double Y) CircleAt(double t) =>
            (10 * Math.Cos(t - Math.PI / 2), 10 * Math.Sin(t - Math.PI / 2));

        private static (double X, double Y) DiamondAt(double t)
        {
            var a = t - Math.PI / 2;
            double cos = Math.Cos(a), sin = Math.Sin(a);
            var r = 10 / Math.Max(Math.Abs(cos) + Math.Abs(sin), 0.01);
            return (r * cos, r * sin);
        }

        private static (double X, double Y) StarAt(double t)
        {
            const int verts = 10;
            const double outer = 12, inner = outer * 0.4;

            var pos = t / (2 * Math.PI) * verts;
            var idx = (int)pos;
            var frac = pos - idx;

            (double X, double Y) Vertex(int v)
            {
                var angle = 2 * Math.PI * v / verts - Math.PI / 2;
                var r = v % 2 == 0 ? outer : inner;
                return (r * Math.Cos(angle), r * Math.Sin(angle));
            }

            var a = Vertex(idx % verts);
            var b = Vertex((idx + 1) % verts);

            return (a.X * (1 - frac) + b.X * frac, a.Y * (1 - frac) + b.Y * frac);
        }

        private static (double X, double Y) HeartAt(double t)
        {
            var sin = Math.Sin(t);
            return (
                16 * sin * sin * sin,
                -(13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t)));
        }

        // Even spacing by arc length: walk the curve finely, total its length,
        // and take points at equal fractions of it.
        private static (double X, double Y)[] Sampled(Func<double, (double X, double Y)> curve, int n)
        {
            const int Steps = 2000;

            var walk = new (double X, double Y)[Steps + 1];
            var along = new double[Steps + 1];

            for (var i = 0; i <= Steps; i++) walk[i] = curve(2 * Math.PI * i / Steps);

            for (var i = 1; i <= Steps; i++)
            {
                var dx = walk[i].X - walk[i - 1].X;
                var dy = walk[i].Y - walk[i - 1].Y;
                along[i] = along[i - 1] + Math.Sqrt(dx * dx + dy * dy);
            }

            var total = along[Steps];
            var pts = new (double X, double Y)[n];
            var cursor = 0;

            for (var i = 0; i < n; i++)
            {
                var want = total * i / n;
                while (cursor < Steps && along[cursor + 1] < want) cursor++;
                pts[i] = walk[cursor];
            }

            return pts;
        }

        private static (double X, double Y)[] Grid(int n)
        {
            var cols = (int)Math.Ceiling(Math.Sqrt(n));
            var rows = (int)Math.Ceiling((double)n / cols);

            var pts = new (double X, double Y)[n];

            for (var i = 0; i < n; i++)
            {
                pts[i] = (i % cols - (cols - 1) / 2.0, i / cols - (rows - 1) / 2.0);
            }

            return pts;
        }

        private static PixelPoint[] ShapeFor(int n, Layout layout)
        {
            var window = (int)Math.Round(WindowDip * layout.Scale);
            var unit = Unit(layout.Shape, n);

            // Any starting size will do — the fit below sets the real one from
            // the gap the spacing asks for. Starting from the screen keeps the
            // arithmetic away from both extremes.
            var s = layout.Work.Height / 40.0;

            var cx = layout.Work.X + layout.Work.Width / 2.0 - window / 2.0;
            var cy = layout.Work.Y + layout.Work.Height / 2.0 - window / 2.0;

            return unit.Select(p => new PixelPoint(
                (int)Math.Round(cx + p.X * s),
                (int)Math.Round(cy + p.Y * s))).ToArray();
        }

        // Scale the pattern until neighbours clear each other, but never past
        // what the screen can hold, then slide it fully inside.
        private static PixelPoint[] Fit(PixelPoint[] pts, PixelRect work, int window, double minGap)
        {
            if (pts.Length == 0) return pts;
            if (pts.Length == 1) return new[] { Inside(pts[0], work, window) };

            var centre = Centre(pts);

            var closest = double.MaxValue;
            for (var i = 0; i < pts.Length; i++)
            {
                var next = pts[(i + 1) % pts.Length];
                var dx = pts[i].X - next.X;
                var dy = pts[i].Y - next.Y;
                closest = Math.Min(closest, Math.Sqrt(dx * dx + dy * dy));
            }

            var wanted = closest > 0.5 ? minGap / closest : 1.0;

            var halfW = pts.Max(p => Math.Abs(p.X + window / 2.0 - centre.X));
            var halfH = pts.Max(p => Math.Abs(p.Y + window / 2.0 - centre.Y));

            var roomW = Math.Max(1, (work.Width - window) / 2.0);
            var roomH = Math.Max(1, (work.Height - window) / 2.0);

            var fits = Math.Min(
                halfW > 1 ? roomW / halfW : double.MaxValue,
                halfH > 1 ? roomH / halfH : double.MaxValue);

            var factor = Math.Min(wanted, fits);

            var scaled = pts.Select(p => new PixelPoint(
                (int)Math.Round(centre.X + (p.X + window / 2.0 - centre.X) * factor - window / 2.0),
                (int)Math.Round(centre.Y + (p.Y + window / 2.0 - centre.Y) * factor - window / 2.0)))
                .ToArray();

            return Slide(scaled, work, window);
        }

        // Centres, not corners: scaling has to happen about the middle of the
        // drawn shape, and the corner is half a window away from it.
        private static (double X, double Y) Centre(PixelPoint[] pts)
        {
            if (pts.Length == 0) return (0, 0);

            return (pts.Average(p => (double)p.X) + WindowDip / 2, pts.Average(p => (double)p.Y) + WindowDip / 2);
        }

        private static PixelPoint[] Slide(PixelPoint[] pts, PixelRect work, int window)
        {
            var left = pts.Min(p => p.X);
            var right = pts.Max(p => p.X) + window;
            var top = pts.Min(p => p.Y);
            var bottom = pts.Max(p => p.Y) + window;

            var dx = 0;
            var dy = 0;

            if (left < work.X) dx = work.X - left;
            else if (right > work.Right) dx = work.Right - right;

            if (top < work.Y) dy = work.Y - top;
            else if (bottom > work.Bottom) dy = work.Bottom - bottom;

            if (dx == 0 && dy == 0) return pts;

            return pts.Select(p => Inside(new PixelPoint(p.X + dx, p.Y + dy), work, window)).ToArray();
        }

        private static PixelPoint Inside(PixelPoint p, PixelRect work, int window) =>
            new(Clamp(p.X, work.X, work.Right - window), Clamp(p.Y, work.Y, work.Bottom - window));

        // Nudge apart any two orbs whose circles overlap, and only those.
        //
        // The alternative — scaling the whole pattern until its worst pair
        // clears — is what made the smallest spacing setting fill the screen: a
        // heart's lobes nearly touch at the notch, a star's valleys sit close to
        // its neighbouring points, and one such pair anywhere dictated the size
        // of everything. Separating locally leaves the shape the size it was
        // asked to be and fixes the two orbs that are actually on top of each
        // other.
        //
        // Members are pulled back toward their lead afterwards, because being
        // pushed out of a collision must not take a team member beyond the
        // distance at which its arrow is drawn.
        private static PixelPoint[] Separate(
            PixelPoint[] pts, int[] leadOf, double circle, PixelRect work, int window, double spacing)
        {
            if (pts.Length < 2) return pts;

            var memberCircle = circle * MemberScale;
            var scale = circle / CircleDip;

            // The bottom of the slider is meant to be a tight cluster, so a
            // little overlap there is the setting doing its job rather than a
            // fault to correct.
            var slack = spacing <= 0.3 ? circle * 0.4 : 0;

            // How many members each lead is holding, so the pull-back below can
            // ask whether they fit.
            var teamSize = new int[pts.Length];
            for (var i = 0; i < pts.Length; i++)
            {
                var lead = i < leadOf.Length ? leadOf[i] : -1;
                if (lead >= 0 && lead < pts.Length && lead != i) teamSize[lead]++;
            }

            var x = pts.Select(p => (double)p.X).ToArray();
            var y = pts.Select(p => (double)p.Y).ToArray();

            double RadiusOf(int i) =>
                (i < leadOf.Length && leadOf[i] >= 0 && leadOf[i] < pts.Length ? memberCircle : circle) / 2;

            // Fixed iterations, no randomness: the same input has to produce the
            // same arrangement every time or the orbs would wander between one
            // press of the button and the next.
            for (var pass = 0; pass < 60; pass++)
            {
                var moved = false;

                // Pulled in first, then separated, so a pass ends having resolved
                // collisions rather than having just re-made one — the last two
                // pixels of overlap lived exactly there.
                // Back toward its lead, so separation can't strand a member
                // beyond the reach of its own arrow — but no tighter than the
                // team can physically fit. A ring only holds so many: twenty-nine
                // members each needing their own width around it need a radius
                // that follows from their number, and holding them closer than
                // that is asking for an overlap no amount of pushing can fix.
                var arrow = ArrowRadius(circle, scale) * 1.9;

                for (var i = 0; i < pts.Length; i++)
                {
                    var lead = i < leadOf.Length ? leadOf[i] : -1;
                    if (lead < 0 || lead >= pts.Length || lead == i) continue;

                    var dx = x[i] - x[lead];
                    var dy = y[i] - y[lead];
                    var dist = Math.Sqrt(dx * dx + dy * dy);

                    var team = teamSize[lead];
                    var ring = team > 1 ? team * (memberCircle + 2) / (2 * Math.PI) * 1.15 : 0;
                    var reach = Math.Max(arrow, ring);

                    if (dist <= reach || dist < 0.001) continue;

                    var pull = (dist - reach) / dist;
                    x[i] -= dx * pull;
                    y[i] -= dy * pull;
                    moved = true;
                }

                for (var i = 0; i < pts.Length; i++)
                for (var j = i + 1; j < pts.Length; j++)
                {
                    var want = RadiusOf(i) + RadiusOf(j) + 2 - slack;

                    var dx = x[j] - x[i];
                    var dy = y[j] - y[i];
                    var dist = Math.Sqrt(dx * dx + dy * dy);

                    if (dist >= want) continue;

                    // Exactly coincident: pick a direction rather than dividing
                    // by zero. Deterministic, so it stays put across runs.
                    if (dist < 0.001) { dx = 1 + i * 0.01; dy = j * 0.01; dist = Math.Sqrt(dx * dx + dy * dy); }

                    var push = (want - dist) / 2;
                    var ux = dx / dist * push;
                    var uy = dy / dist * push;

                    x[i] -= ux; y[i] -= uy;
                    x[j] += ux; y[j] += uy;
                    moved = true;
                }

                for (var i = 0; i < pts.Length; i++)
                {
                    x[i] = Math.Clamp(x[i], work.X, Math.Max(work.X, work.Right - window));
                    y[i] = Math.Clamp(y[i], work.Y, Math.Max(work.Y, work.Bottom - window));
                }

                if (!moved) break;
            }

            return Enumerable.Range(0, pts.Length)
                .Select(i => new PixelPoint((int)Math.Round(x[i]), (int)Math.Round(y[i])))
                .ToArray();
        }

        private static double Nearest(PixelPoint[] pts)
        {
            if (pts.Length < 2) return 0;

            var closest = double.MaxValue;

            for (var i = 0; i < pts.Length; i++)
            {
                for (var j = i + 1; j < pts.Length; j++)
                {
                    var dx = pts[i].X - pts[j].X;
                    var dy = pts[i].Y - pts[j].Y;
                    closest = Math.Min(closest, Math.Sqrt(dx * dx + dy * dy));
                }
            }

            return closest;
        }
        // Where newcomers go when the shape is already on screen and must not
        // move.
        //
        // Compute() answers "where do N orbs go", which is the right question
        // when the user asks for a shape and a different one when a session
        // merely starts. Re-fitting on every arrival moved every orb already on
        // screen: measured against real geometry, 33px on average when a sixth
        // joined a circle, 111px in a heart, up to 161px in a grid. The points
        // of a five-point ring and a six-point ring are simply not the same
        // points, so this is not a placement bug to fix — it is a question not
        // to ask. Matching each orb to its nearest new slot instead of by list
        // order was measured too and saves 34% on a circle, 9% on a heart: real,
        // and still tens of pixels of drift every time something starts.
        //
        // So the settled orbs are given, not computed. Each newcomer takes the
        // slot of the freshly computed shape nearest to where it would have
        // gone, skipping any slot something is already standing on — including
        // an earlier newcomer from this same call, which is why `taken` grows
        // as it goes.
        //
        // `apart` is the closest two orbs may sit before they read as one. A
        // newcomer with nowhere free falls back to its own computed slot: an
        // orb overlapping another is bad, and an orb at 0,0 is worse.
        internal static PixelPoint[] Absorb(
            PixelPoint[] settled, PixelPoint[] shape, PixelPoint[] wanted,
            double apart, PixelRect work, int window)
        {
            var taken = settled.ToList();
            var result = new PixelPoint[wanted.Length];

            for (var i = 0; i < wanted.Length; i++)
            {
                var target = Inside(wanted[i], work, window);

                if (Occupied(taken, target, apart))
                {
                    // A slot of the shape it is joining, if one is free. This
                    // is the case that keeps the pattern looking like a pattern.
                    var free = shape
                        .Select(t => Inside(t, work, window))
                        .Where(t => !Occupied(taken, t, apart))
                        .OrderBy(t => Apart(t, target))
                        .ToList();

                    // Every slot stood on already. Measured, not hypothetical:
                    // a three-orb heart has its settled orbs within an orb's
                    // width of all four slots of the four-orb heart, so there
                    // is nothing in the shape to give the newcomer. Search
                    // outward from where it wanted to be instead — the pattern
                    // is worth less than not stacking two orbs on one spot.
                    target = free.Count > 0 ? free[0] : Nearby(taken, target, apart, work, window);
                }

                result[i] = target;
                taken.Add(target);
            }

            return result;
        }

        private static bool Occupied(List<PixelPoint> taken, PixelPoint at, double apart) =>
            taken.Any(p => Apart(p, at) < apart);

        // The closest free spot to `from`, looked for in widening rings. Falls
        // back to `from` itself, because an orb overlapping another is bad and
        // an orb flung into a corner to avoid it is worse.
        private static PixelPoint Nearby(
            List<PixelPoint> taken, PixelPoint from, double apart, PixelRect work, int window)
        {
            var step = Math.Max(8.0, apart / 2);

            for (var ring = 1; ring <= 24; ring++)
            {
                var radius = ring * step;

                // More angles further out, so the search stays roughly as dense
                // however far it has to go.
                var steps = Math.Max(8, ring * 8);

                for (var a = 0; a < steps; a++)
                {
                    var angle = 2 * Math.PI * a / steps;
                    var candidate = Inside(new PixelPoint(
                        (int)Math.Round(from.X + radius * Math.Cos(angle)),
                        (int)Math.Round(from.Y + radius * Math.Sin(angle))), work, window);

                    if (!Occupied(taken, candidate, apart)) return candidate;
                }
            }

            return from;
        }

        private static double Apart(PixelPoint a, PixelPoint b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
