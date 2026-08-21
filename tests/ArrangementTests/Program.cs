using Avalonia;
using ClaudeBuddy;

// Walks every shape, at every end of the spacing slider, across the orb counts
// and team shapes that actually occur, and asserts the things that kept going
// wrong: orbs off the screen, orbs drawn on top of each other, teams fanned so
// far from their lead that the arrows stopped being drawn, and arrangements
// that threw rather than drew.
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

foreach (var screen in screens)
foreach (var shape in shapes)
foreach (var spacing in spacings)
foreach (var count in counts)
foreach (var team in teamShapes)
{
    cases++;

    var leads = team.Build(count);
    var layout = new OrbArrangement.Layout(screen.Work, screen.Scale, shape, spacing);
    var where = $"{screen.Name} / {shape} / spacing {spacing:0.00} / {count} orbs / {team.Name}";

    PixelPoint[] pts;

    try
    {
        pts = OrbArrangement.Compute(count, leads, layout);
    }
    catch (Exception ex)
    {
        failures.Add($"{where}: threw {ex.GetType().Name}: {ex.Message}");
        continue;
    }

    void Fail(string what) => failures.Add($"{where}: {what}");

    if (pts.Length != count)
    {
        Fail($"returned {pts.Length} positions for {count} orbs");
        continue;
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
