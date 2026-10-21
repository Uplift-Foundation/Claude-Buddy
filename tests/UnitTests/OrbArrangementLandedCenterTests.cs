using Avalonia;
using Xunit;

namespace Orbweaver.Tests
{
    // OrbArrangement.LandedCenter (CB-211): the anchor a shape actually landed
    // on, which is what SessionManager saves after every arrange so a drag back
    // from an edge is never spent paying off an overshoot nobody can see.
    //
    // The numbers are the real ones the ticket was confirmed with: a saved
    // anchor of (3040, 152) on a 2560x1392 work area, and a (-300, +200) drag
    // of which only the vertical half survived. Each round trip below also
    // replays that drag *without* the rebase and asserts the loss — a negative
    // control, so a fixture that happened not to reach the edge would fail
    // here rather than pass for the wrong reason.
    public class OrbArrangementLandedCenterTests
    {
        private static readonly PixelRect Warren = new(0, 0, 2560, 1392);
        private static readonly PixelPoint Stale = new(3040, 152);
        private static readonly PixelPoint Drag = new(-300, 200);

        private static int[] NoTeams(int n) => Enumerable.Repeat(-1, n).ToArray();

        private static OrbArrangement.Layout Layout(PixelPoint? anchor, string shape = "heart", PixelRect? work = null)
            => new(work ?? Warren, 1.0, shape, 0.85, anchor);

        private static PixelPoint[] Draw(int n, PixelPoint? anchor, double[]? sizes = null, string shape = "heart",
            int[]? leads = null, PixelRect? work = null)
            => OrbArrangement.Compute(
                n, leads ?? NoTeams(n), new int[n], new[] { shape }, Layout(anchor, shape, work), sizes);

        private static PixelPoint Landed(int n, PixelPoint? anchor, PixelPoint[] placed, double[]? sizes = null,
            string shape = "heart", int[]? leads = null, PixelRect? work = null)
            => OrbArrangement.LandedCenter(
                placed, n, leads ?? NoTeams(n), new int[n], new[] { shape }, Layout(anchor, shape, work), sizes);

        private static PixelPoint Plus(PixelPoint a, PixelPoint b) => new(a.X + b.X, a.Y + b.Y);

        private static void MovedByExactly(PixelPoint[] before, PixelPoint[] after, PixelPoint delta)
        {
            Assert.Equal(before.Length, after.Length);
            for (var i = 0; i < before.Length; i++)
                Assert.Equal(Plus(before[i], delta), after[i]);
        }

        // The round trip the ticket reports, fixed: arrange against the stale
        // anchor, drag, re-arrange — every orb moves by exactly the drag.
        [Theory]
        [InlineData("heart", 6)]
        [InlineData("circle", 8)]
        [InlineData("grid", 9)]
        [InlineData("star", 5)]
        public void ADragBackFromTheEdgeMovesEveryOrbByExactlyTheDrag(string shape, int n)
        {
            var placed = Draw(n, Stale, shape: shape);
            var landed = Landed(n, Stale, placed, shape: shape);

            // Negative control: without the rebase the horizontal half of the
            // drag is absorbed, which is the defect as it was measured.
            var unrebased = Draw(n, Plus(Stale, Drag), shape: shape);
            Assert.Equal(placed[0].X, unrebased[0].X);

            // The landed anchor draws the very shape that landed...
            Assert.Equal(placed, Draw(n, landed, shape: shape));

            // ...and a drag from it is honoured in full.
            MovedByExactly(placed, Draw(n, Plus(landed, Drag), shape: shape), Drag);
        }

        // A stale out-of-range value is repaired, not merely prevented from
        // growing: the anchor comes back inside the screen on x, where the
        // shape was pushed, and untouched on y, where it was not — which is
        // exactly why the vertical half of the real drag survived.
        [Fact]
        public void AStaleAnchorPastTheRightEdgeIsPulledBackOnlyAlongX()
        {
            var placed = Draw(6, Stale);
            var landed = Landed(6, Stale, placed);

            Assert.True(landed.X < Warren.Right, $"landed.X {landed.X} still past the screen");
            Assert.True(landed.X < Stale.X);
            Assert.Equal(Stale.Y, landed.Y);
        }

        // Same repair along the other axis, and both at once, so each arm of
        // the per-axis choice is exercised with the other held fixed.
        [Fact]
        public void AnAnchorPastTheBottomIsPulledBackOnlyAlongY()
        {
            var asked = new PixelPoint(1280, 2400);
            var placed = Draw(6, asked);
            var landed = Landed(6, asked, placed);

            Assert.Equal(asked.X, landed.X);
            Assert.True(landed.Y < asked.Y);
            Assert.Equal(placed, Draw(6, landed));
        }

        [Fact]
        public void AnAnchorOffACornerIsPulledBackAlongBothAxes()
        {
            var asked = new PixelPoint(-900, -700);
            var placed = Draw(6, asked);
            var landed = Landed(6, asked, placed);

            Assert.True(landed.X > asked.X);
            Assert.True(landed.Y > asked.Y);
            Assert.Equal(placed, Draw(6, landed));
            MovedByExactly(placed, Draw(6, Plus(landed, new PixelPoint(300, 200))), new PixelPoint(300, 200));
        }

        // The control: an anchor the screen honours comes back exactly as it
        // went in, odd offsets from the middle included — the case a rounding
        // would betray first, and the one that would make a saved anchor creep
        // a pixel on every arrange if the helper ever recomputed it.
        [Theory]
        [InlineData(1280, 696)]
        [InlineData(1281, 697)]
        [InlineData(900, 500)]
        [InlineData(1703, 811)]
        public void AnAnchorTheScreenHonoursIsReturnedUntouched(int x, int y)
        {
            var asked = new PixelPoint(x, y);
            var placed = Draw(6, asked);

            Assert.Equal(asked, Landed(6, asked, placed));

            // And the drag behaves exactly as it always did.
            MovedByExactly(placed, Draw(6, Plus(asked, Drag)), Drag);
        }

        // A 200% orb widens every slot, so the lattice meets the edge from an
        // anchor that uniform orbs would still have room at. The clamp is
        // correct there — it is what keeps the big orb on the screen — and the
        // rebase has to measure against the slot, not the small orbs inset in
        // theirs.
        [Fact]
        public void MixedSizesRoundTripExactlyAndMeetTheEdgeSooner()
        {
            var sizes = new[] { 2.0, 1.0, 0.6, 1.0, 1.0, 0.6 };

            var mixed = Draw(6, Stale, sizes);
            var mixedLanded = Landed(6, Stale, mixed, sizes);
            var uniformLanded = Landed(6, Stale, Draw(6, Stale));

            Assert.True(mixedLanded.X < uniformLanded.X,
                $"mixed {mixedLanded.X} should meet the edge before uniform {uniformLanded.X}");

            Assert.Equal(mixed[0].X, Draw(6, Plus(Stale, Drag), sizes)[0].X);   // the defect, with sizes
            Assert.Equal(mixed, Draw(6, mixedLanded, sizes));
            MovedByExactly(mixed, Draw(6, Plus(mixedLanded, Drag), sizes), Drag);
        }

        // A small orb sits inset in the slot a big one fills; jammed against
        // the edge by its slot it still shows a margin of its own. Measuring
        // orbs rather than slots would read that margin as "nothing pushed"
        // and keep the overshoot. A vertical line pushed off the top, with the
        // big orb at the bottom well clear of it: the only orb near the top
        // edge is a 60% one, sitting a few pixels below it.
        [Fact]
        public void ASmallOrbInsetInItsSlotStillCountsAsTouchingTheEdge()
        {
            var sizes = new[] { 0.6, 2.0 };
            var asked = new PixelPoint(1280, -1500);

            var placed = Draw(2, asked, sizes, shape: "vline");
            var landed = Landed(2, asked, placed, sizes, shape: "vline");

            Assert.True(placed[0].Y > Warren.Y, "fixture: the small orb itself must stop short of the top");
            Assert.True(placed[1].Y + OrbArrangement.WindowFor(1.0, 2.0) < Warren.Bottom,
                "fixture: the big orb must be clear of the bottom");

            Assert.Equal(asked.X, landed.X);
            Assert.True(landed.Y > asked.Y);
            Assert.Equal(placed, Draw(2, landed, sizes, shape: "vline"));
        }

        // A team fan near an edge turns rather than translates, so it is the
        // case the per-orb average exists for; what is owed is only that the
        // saved anchor comes back inside the honoured range.
        [Fact]
        public void ATeamAgainstTheEdgeStillLandsItsAnchorOnTheScreen()
        {
            var leads = new[] { -1, 0, 0, 0, -1, -1 };
            var placed = Draw(6, Stale, leads: leads);
            var landed = Landed(6, Stale, placed, leads: leads);

            Assert.True(landed.X < Warren.Right);
            Assert.Equal(Stale.Y, landed.Y);
        }

        [Fact]
        public void NoAnchorMeansTheMiddleOfTheWorkArea()
        {
            var placed = Draw(4, null);
            Assert.Equal(new PixelPoint(1280, 696), Landed(4, null, placed));
        }

        [Fact]
        public void NoOrbsOrAShortAnswerHandsTheAnchorBackAsAsked()
        {
            Assert.Equal(Stale, OrbArrangement.LandedCenter(
                Array.Empty<PixelPoint>(), 0, Array.Empty<int>(), Array.Empty<int>(), new[] { "heart" }, Layout(Stale)));

            Assert.Equal(Stale, Landed(3, Stale, Draw(2, Stale)));
        }

        // More than one group: the anchor is applied once to all the shapes
        // together (Compact), so a stale one is repaired the same way.
        [Fact]
        public void SeveralGroupsRoundTripThroughTheRebasedAnchor()
        {
            var groups = new[] { 0, 0, 0, 1, 1, 2 };
            var shapes = new[] { "heart", "circle", "line" };

            PixelPoint[] DrawG(PixelPoint a) =>
                OrbArrangement.Compute(6, NoTeams(6), groups, shapes, Layout(a));

            var placed = DrawG(Stale);
            var landed = OrbArrangement.LandedCenter(placed, 6, NoTeams(6), groups, shapes, Layout(Stale));

            Assert.True(landed.X < Stale.X);
            Assert.Equal(placed, DrawG(landed));
            MovedByExactly(placed, DrawG(Plus(landed, Drag)), Drag);
        }
    }
}
