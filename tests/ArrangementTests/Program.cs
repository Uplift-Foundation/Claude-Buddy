using Avalonia;
using ClaudeBuddy;

// Walks every shape, at every end of the spacing slider, across the orb counts
// and team shapes that actually occur, and asserts the things that kept going
// wrong: orbs off the screen, orbs drawn on top of each other, teams fanned so
// far from their lead that the arrows stopped being drawn, and arrangements
// that threw rather than drew.
//
// Then walks the whole thing again with a saved anchor, which is the input that
// says "draw the shape here" instead of in the middle of the screen. That
// second sweep exists because the anchor shipped with no coverage at all and
// the shape went on recentering itself on every orb that joined or left — the
// exact class of bug the first sweep was written for, reintroduced by a new
// argument nothing checked. A hostile anchor (off-screen, negative) is included
// deliberately: honouring it must never win over keeping orbs on the screen.
//
// Run it with `dotnet run --project tests/ArrangementTests`. Non-zero exit means
// something regressed, and each failure prints the exact case so it can be
// reproduced by hand.

var shapes = new[] { "heart", "circle", "diamond", "star", "grid", "line" };
var spacings = new[] { 0.3, 0.85, 2.0 };
var counts = new[] { 1, 2, 3, 5, 8, 13, 20, 30 };

var screens = new (string Name, PixelRect Work, double Scale)[]
{
    ("14in retina", new PixelRect(0, 0, 3024, 1890), 2.0),
    ("1080p", new PixelRect(0, 0, 1920, 1080), 1.0),
    ("small laptop", new PixelRect(0, 0, 1280, 800), 1.0),
};

var teamShapes = new (string Name, Func<int, int[]> Build)[]
{
    ("no teams", n => Enumerable.Repeat(-1, n).ToArray()),

    ("one team of 3", n =>
    {
        var leads = Enumerable.Repeat(-1, n).ToArray();
        for (var i = 1; i < Math.Min(4, n); i++) leads[i] = 0;
        return leads;
    }),

    ("two teams of 6", n =>
    {
        var leads = Enumerable.Repeat(-1, n).ToArray();
        for (var i = 2; i < Math.Min(8, n); i++) leads[i] = 0;
        for (var i = 8; i < Math.Min(14, n); i++) leads[i] = 1;
        return leads;
    }),

    ("nested: A leads B, B leads C and D", n =>
    {
        var leads = Enumerable.Repeat(-1, n).ToArray();
        if (n > 1) leads[1] = 0;
        if (n > 2) leads[2] = 1;
        if (n > 3) leads[3] = 1;
        return leads;
    }),

    ("everything in one team", n =>
    {
        var leads = Enumerable.Repeat(0, n).ToArray();
        if (n > 0) leads[0] = -1;
        return leads;
    }),

    ("lead cycle: A leads B, B leads A", n =>
    {
        var leads = Enumerable.Repeat(-1, n).ToArray();
        if (n > 1) { leads[0] = 1; leads[1] = 0; }
        return leads;
    }),

    ("lead points at itself", n =>
    {
        var leads = Enumerable.Repeat(-1, n).ToArray();
        if (n > 0) leads[0] = 0;
        return leads;
    }),

    ("lead points at an orb that isn't there", n =>
    {
        var leads = Enumerable.Repeat(-1, n).ToArray();
        if (n > 0) leads[0] = n + 5;
        return leads;
    }),
};

var failures = new List<string>();
var cases = 0;

// One copy of the invariants, run by both sweeps. `anchor` is the shape's
// saved centre, or null for "wherever the arrangement would put it" — the
// checks below are the same either way, because an anchor is not allowed to
// buy an exemption from any of them.
void Check(
    (string Name, PixelRect Work, double Scale) screen,
    string shape,
    double spacing,
    int count,
    (string Name, Func<int, int[]> Build) team,
    (string Name, PixelPoint At)? anchor)
{
    cases++;

    var leads = team.Build(count);
    var layout = new OrbArrangement.Layout(
        screen.Work, screen.Scale, shape, spacing, anchor?.At);
    var where = $"{screen.Name} / {shape} / spacing {spacing:0.00} / {count} orbs / {team.Name}"
              + (anchor is null ? "" : $" / anchor {anchor.Value.Name}");

    PixelPoint[] pts;

    try
    {
        pts = OrbArrangement.Compute(count, leads, layout);
    }
    catch (Exception ex)
    {
        failures.Add($"{where}: threw {ex.GetType().Name}: {ex.Message}");
        return;
    }

    void Fail(string what) => failures.Add($"{where}: {what}");

    if (pts.Length != count)
    {
        Fail($"returned {pts.Length} positions for {count} orbs");
        return;
    }

    var window = (int)Math.Round(OrbArrangement.WindowDip * screen.Scale);
    var circle = OrbArrangement.CircleDip * screen.Scale;
    var memberCircle = circle * OrbArrangement.MemberScale;

    // 1. Every orb fully on screen. An orb you cannot see is worse than an
    //    arrangement you do not like.
    foreach (var (p, i) in pts.Select((p, i) => (p, i)))
    {
        if (p.X < screen.Work.X || p.Y < screen.Work.Y
            || p.X + window > screen.Work.Right || p.Y + window > screen.Work.Bottom)
        {
            Fail($"orb {i} at ({p.X},{p.Y}) is outside the work area");
            break;
        }
    }

    // 2. Nothing sitting on top of anything else. Measured on the circles that
    //    are drawn, at their own sizes — a member's is smaller than a lead's.
    double RadiusOf(int i) => (leads[i] >= 0 && leads[i] < count ? memberCircle : circle) / 2;

    var worst = double.MaxValue;
    var worstPair = (-1, -1);

    for (var i = 0; i < count; i++)
    for (var j = i + 1; j < count; j++)
    {
        var dx = pts[i].X - pts[j].X;
        var dy = pts[i].Y - pts[j].Y;
        var gap = Math.Sqrt(dx * dx + dy * dy) - RadiusOf(i) - RadiusOf(j);

        if (gap < worst) { worst = gap; worstPair = (i, j); }
    }

    // At the very bottom of the slider the shape is meant to be a tight
    // cluster, so a little overlap there is the setting doing its job. Anywhere
    // else, circles must not touch.
    var allowedOverlap = spacing <= 0.3 ? -circle * 0.45 : 0;

    if (count > 1 && worst < allowedOverlap)
    {
        Fail($"orbs {worstPair.Item1} and {worstPair.Item2} overlap by {-worst:0}px "
           + $"(allowed {-allowedOverlap:0})");
    }

    // 3. Every team member close enough to its lead that TeamLinks will draw
    //    the arrow. Without the arrow there is nothing saying they are a team.
    var needed = TeamLinkGeometry.MinimumCentreDistance(memberCircle / 2 / screen.Scale, circle / 2 / screen.Scale)
                 * screen.Scale;

    for (var i = 0; i < count; i++)
    {
        var lead = leads[i];
        if (lead < 0 || lead >= count || lead == i) continue;

        // Cycles are broken by the arrangement, so only check pairs it still
        // treats as a team: a member sits within a sane distance of its lead.
        var dx = pts[i].X - pts[lead].X;
        var dy = pts[i].Y - pts[lead].Y;
        var apart = Math.Sqrt(dx * dx + dy * dy);

        if (apart > screen.Work.Width * 0.6)
        {
            Fail($"member {i} is {apart:0}px from lead {lead} — too far to read as a team");
            break;
        }
    }

    // 4. Deterministic. An arrangement that moved on its own would make every
    //    other check meaningless.
    var again = OrbArrangement.Compute(count, team.Build(count), layout);
    if (!pts.SequenceEqual(again)) Fail("not deterministic — two runs disagreed");

    // 5. Moving the anchor moves the arrangement with it, by the same amount.
    //
    //    This is the invariant the shipped bug actually broke, and it took three
    //    tries to state. Comparing the arrangement's middle against the anchor
    //    looks like the obvious check and is not one: a shape has size, so an
    //    anchor in a screen corner is a centre nothing can have; "line" spans
    //    the full width however it is anchored; and with two orbs a heart fills
    //    two of its points, whose middle is nowhere near the heart's. All three
    //    are the arrangement behaving correctly and the assertion being wrong.
    //
    //    What the user does is drag the shape, which nudges the saved anchor by
    //    the same delta (SessionManager.ShiftArrangementAnchor) and must nudge
    //    the orbs by that delta next time round. Before the anchor existed the
    //    shape was recomputed around the middle of the screen every time, so
    //    the drag was thrown away the moment an orb joined or left.
    //
    //    Scoped to teamless arrangements, where every orb is a point of the
    //    shape. A team member is placed at a fixed distance from its lead in
    //    whichever direction fits on the screen, so near an edge a nudge can
    //    legitimately turn the fan instead of moving it, and no delta is owed.
    //    The other checks still run for every team shape; this one cannot.
    if (anchor is { } a && leads.All(l => l < 0))
    {
        // Not equal, so swapping the axes cannot pass, and both **even**, which
        // is what makes the assertion below exact rather than approximate.
        // Points are laid out from the anchor in floating point and rounded to
        // whole pixels twice — once building the shape, once scaling it to fit —
        // and Math.Round sends an exact .5 to the even side, so an odd shift
        // flips which side that is and the delta lands a pixel out. An even
        // shift leaves the integer part's parity alone, so every rounding falls
        // the same way and the whole arrangement moves by exactly the delta.
        // (With an odd delta the error is not even bounded at one pixel: Fit
        // scales the shape about its middle after rounding it, so at the top of
        // the spacing slider a one-pixel rounding comes out multiplied.)
        const int dx = 38, dy = 24;

        var moved = OrbArrangement.Compute(
            count,
            team.Build(count),
            layout with { Center = new PixelPoint(a.At.X + dx, a.At.Y + dy) });

        // Only where neither arrangement was pushed back onto the screen. Fit
        // scales the shape about its own middle, which does not depend on the
        // anchor, and Slide then translates whatever hangs off the edge back
        // inside — so against an edge the anchor is *supposed* to stop being
        // honoured, and demanding the delta there would be demanding orbs walk
        // off the screen. "Slid" is read off the result rather than asked of
        // Slide: a margin on all four sides means there was nothing to push.
        bool Clear(PixelPoint[] p)
            => p.Min(q => q.X) > screen.Work.X
            && p.Min(q => q.Y) > screen.Work.Y
            && p.Max(q => q.X) + window < screen.Work.Right
            && p.Max(q => q.Y) + window < screen.Work.Bottom;

        if (moved.Length == count && Clear(pts) && Clear(moved))
        {
            for (var i = 0; i < count; i++)
            {
                var gotX = moved[i].X - pts[i].X;
                var gotY = moved[i].Y - pts[i].Y;

                if (gotX == dx && gotY == dy) continue;

                Fail($"moving the anchor by ({dx},{dy}) moved orb {i} by ({gotX},{gotY}) instead");
                break;
            }
        }
    }
}

foreach (var screen in screens)
foreach (var shape in shapes)
foreach (var spacing in spacings)
foreach (var count in counts)
foreach (var team in teamShapes)
    Check(screen, shape, spacing, count, team, null);

// The same sweep with the shape pinned somewhere. Two anchors well inside the
// work area, one hard against a corner, and two that cannot be honoured at all.
foreach (var screen in screens)
{
    var anchors = new (string Name, PixelPoint At)[]
    {
        ("upper left quarter", new PixelPoint(
            screen.Work.X + screen.Work.Width / 4, screen.Work.Y + screen.Work.Height / 4)),
        ("lower right quarter", new PixelPoint(
            screen.Work.X + screen.Work.Width * 3 / 4, screen.Work.Y + screen.Work.Height * 3 / 4)),
        ("hard against the corner", new PixelPoint(screen.Work.X, screen.Work.Y)),
        ("off the right edge", new PixelPoint(screen.Work.Right + 800, screen.Work.Y + 40)),
        ("negative", new PixelPoint(-4000, -4000)),
    };

    foreach (var shape in shapes)
    foreach (var spacing in spacings)
    foreach (var count in counts)
    foreach (var team in teamShapes)
    foreach (var anchor in anchors)
        Check(screen, shape, spacing, count, team, anchor);
}

Console.WriteLine($"{cases} cases");

if (failures.Count == 0)
{
    Console.WriteLine("all passed");
    return 0;
}

Console.WriteLine($"{failures.Count} failures\n");

// Grouped, because one broken rule usually fails hundreds of cases and the
// list is only useful if the shape of the problem is visible.
foreach (var group in failures
    .GroupBy(f => f[(f.IndexOf(": ", StringComparison.Ordinal) + 2)..])
    .OrderByDescending(g => g.Count()))
{
    Console.WriteLine($"  {group.Count(),5}x  {group.Key}");
    Console.WriteLine($"         e.g. {group.First()}");
}

return 1;
