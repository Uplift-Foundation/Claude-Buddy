using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Xunit;

namespace ClaudeBuddy.Tests;

// What CB-14 actually looks like, on both runners.
//
// The visible surface of this feature is *where the orbs end up*, and that is
// the one surface the rest of this project cannot photograph: an arrangement is
// a list of window positions across the whole desktop, and a headless runner has
// no desktop to place windows on. So these two capture the arrangement as a
// plot — one circle per orb at the position OrbArrangement.Compute returned,
// scaled down to fit the page — which is the same information a person gets by
// glancing at their screen and is the only form of it a screenshot can hold.
//
// Worth having as a picture rather than only as the assertions in
// tests/UnitTests/OrbArrangementBandTests.cs, because "three shapes, side by
// side, none on top of another" is a judgement a reviewer makes in a second from
// an image and makes badly from a list of coordinates. The pair is the point:
// the same orbs, the same screen, with the setting off and on.
[Collection("Settings")]
public class OrbClusterScreenshots
{
    // A 1080p screen at 1x, so the plot's own arithmetic is the identity and
    // what is drawn is what Compute returned.
    private static readonly PixelRect Work = new(0, 0, 1920, 1080);

    // Enough orbs that each shape is recognisable, and an uneven split so the
    // bands are visibly unequal — which is the behaviour that keeps the chats'
    // shape close to the size it would have had on a screen of its own.
    private static readonly int[] Groups =
        { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2 };

    [AvaloniaFact]
    public void OneShapeWhenBothTimerGroupsRideWithTheChats()
        => Plot(
            new int[Groups.Length],
            new[] { "heart", "circle", "line" },
            "arrangement-one-shape.png");

    [AvaloniaFact]
    public void ThreeShapesWhenBothTimerGroupsAreGivenTheirOwn()
        => Plot(
            Groups,
            new[] { "heart", "circle", "line" },
            "arrangement-three-shapes.png");

    // Heartbeats separated and crons left with the chats — the mixed case, and
    // the one that shows the bands are cut from the groups actually in use
    // rather than from all three slots.
    [AvaloniaFact]
    public void TwoShapesWhenOnlyOneTimerGroupIsSeparated()
        => Plot(
            Groups.Select(g => g == 2 ? 0 : g).ToArray(),
            new[] { "heart", "circle", "line" },
            "arrangement-two-shapes.png");

    private static void Plot(int[] groups, string[] shapes, string fileName)
    {
        var leads = Enumerable.Repeat(-1, groups.Length).ToArray();
        var layout = new OrbArrangement.Layout(Work, 1.0, shapes[0], 0.85, null);

        var placed = OrbArrangement.Compute(groups.Length, leads, groups, shapes, layout);

        // A third of life size, so a 1920x1080 desktop fits a page and the orbs
        // stay big enough to tell apart.
        const double scale = 1.0 / 3;
        const double orb = OrbArrangement.CircleDip * scale;

        var canvas = new Canvas
        {
            Width = Work.Width * scale,
            Height = Work.Height * scale,
            Background = new SolidColorBrush(Color.FromRgb(0x1b, 0x1b, 0x1f))
        };

        // One colour per group, so which shape an orb belongs to is readable
        // from the picture rather than inferred from where it happens to be.
        var colours = new[]
        {
            Color.FromRgb(0xd9, 0x77, 0x57),   // chats — the app's own orange
            Color.FromRgb(0x6f, 0xa8, 0xdc),   // heartbeats
            Color.FromRgb(0x93, 0xc4, 0x7d)    // crons
        };

        for (var i = 0; i < placed.Length; i++)
        {
            var g = groups[i];
            var dot = new Ellipse
            {
                Width = orb,
                Height = orb,
                Fill = new SolidColorBrush(colours[g % colours.Length])
            };

            // Compute returns window top-left corners; the circle a person sees
            // is CircleDip inside a WindowDip window, so the drawn dot is offset
            // by half the difference. Same arithmetic OrbWindow's own layout
            // does, and getting it wrong here would draw a picture that is
            // subtly not what the app does.
            const double inset = (OrbArrangement.WindowDip - OrbArrangement.CircleDip) / 2 * scale;

            Canvas.SetLeft(dot, (placed[i].X - Work.X) * scale + inset);
            Canvas.SetTop(dot, (placed[i].Y - Work.Y) * scale + inset);

            canvas.Children.Add(dot);
        }

        var window = new Window { Content = canvas, Width = canvas.Width, Height = canvas.Height };

        // Never closed, for the reason SettingsWindowScreenshots states: a
        // Close() in this suite once corrupted the process-wide Avalonia
        // FontManager cache.
        window.Show();
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureControl(canvas, fileName);
    }

    // And the rows that turn it on, which sit below the fold of the settings
    // page — so captured as a control rather than trusting the whole-window
    // shot, exactly as the Claude Desktop group is.
    //
    // Both groups on Own shape, because that is the state with the most rows in
    // it: two mode pickers and the two shape pickers that only exist in this
    // state. A capture of the default would show the two pickers and prove
    // nothing about the ones that appear.
    [AvaloniaFact]
    public void TheOpenClawSectionShowsBothModeRowsAndBothShapeRows()
    {
        var mode = ClaudeBuddySettings.OpenClawEnabled;
        var heartbeats = ClaudeBuddySettings.OpenClawHeartbeatMode;
        var crons = ClaudeBuddySettings.OpenClawCronMode;

        try
        {
            ClaudeBuddySettings.OpenClawEnabled = true;
            ClaudeBuddySettings.OpenClawHeartbeatMode = ClusterMode.OwnShape;
            ClaudeBuddySettings.OpenClawCronMode = ClusterMode.OwnShape;

            var ctor = typeof(SettingsWindow).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                types: Type.EmptyTypes)
                ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

            var window = (Window)ctor.Invoke(null);

            window.Show();
            ScreenshotHelper.Flush();

            // Anchored on the first of the four new rows rather than on the
            // section heading, so the capture starts at the thing under review.
            var anchor = window.GetLogicalDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(block => block.Text == "Heartbeat sessions");

            Assert.NotNull(anchor);

            var card = anchor!.GetLogicalAncestors().OfType<Control>()
                .FirstOrDefault(control => control.Bounds.Height > 200 && control.Bounds.Width > 200)
                ?? (Control)anchor;

            ScreenshotHelper.CaptureControl(card, "settings-openclaw-cluster-rows.png");
        }
        finally
        {
            ClaudeBuddySettings.OpenClawCronMode = crons;
            ClaudeBuddySettings.OpenClawHeartbeatMode = heartbeats;
            ClaudeBuddySettings.OpenClawEnabled = mode;
        }
    }
}
