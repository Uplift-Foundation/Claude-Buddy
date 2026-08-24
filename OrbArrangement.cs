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

        // Center, when given, is where the shape is drawn instead of the
        // middle of Work — the saved anchor that keeps a shape from
        // recentering every time an orb joins or leaves. Work still bounds
        // where Fit/Slide are allowed to put it.
        internal readonly record struct Layout(
            PixelRect Work,
            double Scale,
            string Shape,
            double Spacing,
            PixelPoint? Center = null);

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

            double Offset(int i) => count == 1
                ? 0
                : spread * (i - (count - 1) / 2.0) / Math.Max(count - 1, 1);

            PixelPoint At(double from, int i)
            {
                var angle = from + Offset(i);

                return new PixelPoint(
                    (int)Math.Round(lead.X + radius * Math.Cos(angle)),
                    (int)Math.Round(lead.Y + radius * Math.Sin(angle)));
            }

            // Which way the fan points, when pointing outward would hang it off
            // the screen. The radius is not negotiable — it is the distance the
            // arrow needs and the distance that keeps members off each other —
            // so direction is the only thing left to choose, and turning the fan
            // is strictly better than the clamp that used to happen here.
            //
            // Clamping each member back inside independently collapses any two
            // whose positions differed only along the clamped axis onto the same
            // edge pixel, so a lead against a corner had its whole team drawn as
            // a single orb. That went unseen because a lead only reaches a
            // corner once the shape has a saved anchor and the anchor is dragged
            // there; before that the shape was always centred and no lead ever
            // got close enough to an edge for the clamp to fire.
            //
            // Sixteenths of a turn, tried nearest-first so the fan stays as
            // close to outward as it can, and the least-bad angle kept if none
            // is clean — on a screen too small to hold the fan at all there is
            // no correct answer, only a least wrong one.
            var best = baseAngle;
            var fewestOutside = int.MaxValue;

            for (var step = 0; step < 16 && fewestOutside > 0; step++)
            {
                // 0, +1, -1, +2, -2 … so a small turn always beats a large one.
                var turn = (step + 1) / 2 * (step % 2 == 0 ? 1 : -1) * (Math.PI * 2 / 16);
                var candidate = baseAngle + turn;
                var outside = 0;

                for (var i = 0; i < count; i++)
                {
                    var p = At(candidate, i);

                    if (p.X < work.X || p.Y < work.Y
                        || p.X > work.Right - window || p.Y > work.Bottom - window)
                    {
                        outside++;
                    }
                }

                if (outside < fewestOutside) { fewestOutside = outside; best = candidate; }
            }

            var placed = new PixelPoint[count];

            for (var i = 0; i < count; i++)
            {
                var p = At(best, i);

                placed[i] = new PixelPoint(
                    Clamp(p.X, work.X, work.Right - window),
                    Clamp(p.Y, work.Y, work.Bottom - window));
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
                "line" => Line(n),
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

        // A single row, centred on the middle of the work area like every other
        // shape. Fit does the rest: it scales the spacing up to whatever the
        // slider asks for and then down again to whatever the screen allows, so
        // a long row of orbs closes up rather than running off the edge.
        //
        // The plainest arrangement there is, and the one worth having for that
        // reason — a heart is a nice thing to look at and a row is a thing you
        // can read along.
        //
        // A row and not a column, though the column was written first and looked
        // like the obvious pair. The geometry sweep threw it out: a column has
        // only the screen's height to spend, and thirty orbs at 56 DIP need more
        // of it than any of the three test screens has, so orbs overlapped by up
        // to 21px on every one of them. A row spends the width instead, which is
        // the larger budget on every display anyone has, and passes the same
        // sweep at the same counts. Wrapping a column would have fixed it and
        // produced a grid, which already exists.
        private static (double X, double Y)[] Line(int n)
        {
            var pts = new (double X, double Y)[n];
            for (var i = 0; i < n; i++) pts[i] = (i - (n - 1) / 2.0, 0.0);
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

            var centerX = layout.Center is { } c1 ? c1.X : layout.Work.X + layout.Work.Width / 2.0;
            var centerY = layout.Center is { } c2 ? c2.Y : layout.Work.Y + layout.Work.Height / 2.0;

            var cx = centerX - window / 2.0;
            var cy = centerY - window / 2.0;

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
            //
            // Was sixty, which was enough for as long as the shape was always
            // drawn in the middle of the screen. A shape with a saved anchor can
            // be dragged into a corner, and thirty orbs in one team pressed into
            // a corner are the slowest thing this loop has to untangle — every
            // pass has the pull toward the lead working against the push apart,
            // and from a standing pile it took just under a hundred. It costs
            // nothing in the ordinary case: the loop leaves the moment a pass
            // changes nothing, which is within a handful of passes for a shape
            // that was never crowded.
            for (var pass = 0; pass < 150; pass++)
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

                if (!moved) break;
            }

            // Back onto the screen once, as one piece — not every orb on every
            // pass, which is what this used to do and is why overlaps against
            // an edge were never resolved. Clamping inside the loop made the
            // clamp the strongest of the three forces in it: a member pushed
            // off the edge to clear its neighbour was put straight back on top
            // of it, the next pass pushed it off again, and sixty passes later
            // the pair was still overlapping with nothing left to try.
            //
            // Invisible until the shape got a saved anchor. Centred, the shape
            // never came near an edge and the clamp never fired; anchored into
            // a corner, a lead sits in the corner and its whole team is pressed
            // into it.
            //
            // Sliding the cluster keeps every gap the passes just opened, and
            // does it for the same reason Slide exists for the shape itself.
            // The per-orb clamp still follows as a last resort, for a cluster
            // genuinely wider than the screen — there the honest answer is that
            // there is no arrangement, only the least bad one.
            var lowX = x.Min();
            var lowY = y.Min();
            var shiftX = 0.0;
            var shiftY = 0.0;

            if (lowX < work.X) shiftX = work.X - lowX;
            else if (x.Max() + window > work.Right) shiftX = work.Right - (x.Max() + window);

            if (lowY < work.Y) shiftY = work.Y - lowY;
            else if (y.Max() + window > work.Bottom) shiftY = work.Bottom - (y.Max() + window);

            return Enumerable.Range(0, pts.Length)
                .Select(i => new PixelPoint(
                    (int)Math.Round(Math.Clamp(x[i] + shiftX, work.X, Math.Max(work.X, work.Right - window))),
                    (int)Math.Round(Math.Clamp(y[i] + shiftY, work.Y, Math.Max(work.Y, work.Bottom - window)))))
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
    }
}
