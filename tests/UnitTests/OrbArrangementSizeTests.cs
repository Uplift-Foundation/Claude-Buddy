using Avalonia;
using Xunit;

namespace ClaudeBuddy.Tests
{
    // Orbs of different sizes in one arrangement (CB-198), and the two fixes
    // that fell out of adding them.
    //
    // The sweep asserts what must not happen at every size — nothing off
    // screen, nothing overlapping, nothing that ignores a drag. This file
    // asserts what must: that a small orb sits in the middle of the slot a big
    // one would have had, that the size argument's defaults mean what they say,
    // that a shape is drawn around its anchor on a Retina screen and not 28px to
    // one side of it, and that a row with more orbs than the screen has room for
    // becomes a grid rather than a pile.
    public class OrbArrangementSizeTests
    {
        private static readonly PixelRect Work = new(0, 0, 1920, 1080);

        private static int[] NoTeams(int n) => Enumerable.Repeat(-1, n).ToArray();

        private static OrbArrangement.Layout Layout(
            string shape = "heart", double scale = 1.0, double spacing = 0.85, PixelPoint? anchor = null,
            PixelRect? work = null)
            => new(work ?? Work, scale, shape, spacing, anchor);

        private static (double X, double Y) CentreOf(PixelPoint topLeft, double scale, double size)
        {
            var window = OrbArrangement.WindowFor(scale, size);
            return (topLeft.X + window / 2.0, topLeft.Y + window / 2.0);
        }

        // --- SizesFor ---

        [Fact]
        public void NoSizesMeansEveryOrbIsTheSizeOrbsAlwaysWere()
            => Assert.Equal(new[] { 1.0, 1.0, 1.0 }, OrbArrangement.SizesFor(3, null));

        [Fact]
        public void ASizeArrayShorterThanTheOrbsIsPaddedWithTheDefault()
            => Assert.Equal(new[] { 2.0, 0.6, 1.0, 1.0 }, OrbArrangement.SizesFor(4, new[] { 2.0, 0.6 }));

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.5)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void ASizeThatCannotBeDrawnReadsAsTheDefault(double nonsense)
            => Assert.Equal(new[] { 1.0, 2.0 }, OrbArrangement.SizesFor(2, new[] { nonsense, 2.0 }));

        // --- WindowFor ---

        [Theory]
        [InlineData(1.0, 1.0, 56)]
        [InlineData(2.0, 1.0, 112)]
        [InlineData(1.0, 2.0, 112)]
        [InlineData(2.0, 2.0, 224)]
        [InlineData(1.0, 0.6, 34)]   // 33.6, rounded as the single-size code always rounded
        public void AWindowIsFiftySixDipTimesTheDisplayTimesTheUserSize(double scale, double size, int expected)
            => Assert.Equal(expected, OrbArrangement.WindowFor(scale, size));

        // --- Compute with sizes ---

        [Fact]
        public void EveryOrbAtOneIsBitForBitTheArrangementWithNoSizes()
        {
            var leads = new[] { -1, 0, 0, -1, -1, 3, -1, -1 };

            var without = OrbArrangement.Compute(8, leads, Layout());
            var withOnes = OrbArrangement.Compute(8, leads, Layout(), Enumerable.Repeat(1.0, 8).ToArray());

            Assert.Equal(without, withOnes);
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(2.0)]
        public void ASmallOrbIsCentredInTheSlotABigOneWouldHaveHad(double scale)
        {
            // The rule, directly: the same arrangement with one orb shrunk puts
            // that orb's centre exactly where the big orb's centre was, and
            // leaves every other orb where it was.
            var leads = new[] { -1, 0, -1, -1, -1, -1 };
            var allBig = Enumerable.Repeat(2.0, 6).ToArray();
            var oneSmall = allBig.ToArray();
            oneSmall[1] = 0.6;   // a team member, which also carries MemberScale
            oneSmall[4] = 0.6;   // and a point of the shape

            var big = OrbArrangement.Compute(6, leads, Layout(scale: scale), allBig);
            var mixed = OrbArrangement.Compute(6, leads, Layout(scale: scale), oneSmall);

            for (var i = 0; i < 6; i++)
            {
                var (bx, by) = CentreOf(big[i], scale, allBig[i]);
                var (mx, my) = CentreOf(mixed[i], scale, oneSmall[i]);

                // Within half a pixel: the slot's centre can fall on a half
                // pixel and a window has to start on a whole one.
                Assert.InRange(mx - bx, -0.5, 0.5);
                Assert.InRange(my - by, -0.5, 0.5);
            }
        }

        [Fact]
        public void TheGroupedEntryPointHonoursSizesToo()
        {
            var groups = new[] { 0, 1, 2, 0, 1, 2 };
            var shapes = new[] { "heart", "circle", "star" };
            var sizes = new[] { 2.0, 0.6, 1.0, 0.6, 2.0, 1.0 };

            var placed = OrbArrangement.Compute(6, NoTeams(6), groups, shapes, Layout(), sizes);
            var asBig = OrbArrangement.Compute(6, NoTeams(6), groups, shapes, Layout(), Enumerable.Repeat(2.0, 6).ToArray());

            for (var i = 0; i < 6; i++)
            {
                var (px, py) = CentreOf(placed[i], 1.0, sizes[i]);
                var (bx, by) = CentreOf(asBig[i], 1.0, 2.0);

                Assert.InRange(px - bx, -0.5, 0.5);
                Assert.InRange(py - by, -0.5, 0.5);
            }
        }

        // --- Centre: half a window in pixels, not in DIP ---

        [Theory]
        [InlineData("grid", 0.3)]
        [InlineData("grid", 2.0)]
        [InlineData("circle", 0.85)]
        [InlineData("circle", 2.0)]
        public void ASymmetricShapeIsDrawnAroundItsAnchorOnARetinaScreen(string shape, double spacing)
        {
            // Measured before the fix: the 9-orb grid at spacing 2.0 had its
            // centroid (73, 73) px off this anchor, because the pivot Fit scales
            // about was 28 DIP-as-pixels from the middle of a 112px window
            // rather than 56. A grid and a circle of nine are symmetric about
            // their middle, so their centroid is the anchor or it is a bug.
            var retina = new PixelRect(0, 0, 3024, 1890);
            var anchor = new PixelPoint(1512, 945);

            var placed = OrbArrangement.Compute(9, NoTeams(9), Layout(shape, 2.0, spacing, anchor, retina));

            var cx = placed.Average(p => CentreOf(p, 2.0, 1.0).X);
            var cy = placed.Average(p => CentreOf(p, 2.0, 1.0).Y);

            Assert.InRange(cx - anchor.X, -1.0, 1.0);
            Assert.InRange(cy - anchor.Y, -1.0, 1.0);
        }

        // --- A lone lead's fan points where the room is ---

        [Fact]
        public void ALoneLeadInACornerFansItsTeamTowardTheMiddleOfTheScreen()
        {
            // One anchor, three members, anchored hard into the top-left
            // corner. The lead is the whole of its shape, so it has no outward;
            // the fan goes toward the middle of the screen — down and right —
            // rather than straight up into the edge it is standing on.
            var leads = new[] { -1, 0, 0, 0 };
            var placed = OrbArrangement.Compute(4, leads, Layout(anchor: new PixelPoint(0, 0)));

            var lead = placed[0];
            var members = placed.Skip(1).ToArray();

            Assert.True(members.Average(p => p.X) > lead.X, "the fan should hang right of a top-left lead");
            Assert.True(members.Average(p => p.Y) > lead.Y, "the fan should hang below a top-left lead");
        }

        [Fact]
        public void ALoneLeadInTheMiddleOfTheScreenStillFansUp()
        {
            // Standing on the middle of the screen as well as the middle of its
            // shape, there is no direction to prefer; straight up is what it
            // always did.
            var leads = new[] { -1, 0, 0, 0 };
            var placed = OrbArrangement.Compute(4, leads, Layout());

            Assert.True(placed.Skip(1).Average(p => p.Y) < placed[0].Y, "the fan should hang above a centred lead");
        }

        // --- A shape that cannot hold its orbs becomes a grid ---

        [Fact]
        public void ARowTooLongForTheScreenBecomesAGridRatherThanAPile()
        {
            // Thirty 72-DIP circles in a row need about 2220px; this screen is
            // 1920. Squeezed into one row they overlap by tens of pixels. As a
            // grid they fit with room to spare.
            var sizes = Enumerable.Repeat(2.0, 30).ToArray();
            var placed = OrbArrangement.Compute(30, NoTeams(30), Layout("line"), sizes);

            Assert.True(placed.Select(p => p.Y).Distinct().Count() > 1, "a line that no longer fits should wrap into rows");
            Assert.True(
                OrbArrangement.WorstGap(placed, NoTeams(30), OrbArrangement.CircleDip * 2.0) >= 0,
                "no two circles should overlap once the row is a grid");
        }

        [Fact]
        public void ARowThatFitsStaysARow()
        {
            // The same row at the size orbs always were fits the screen, and is
            // not touched: the grid is only ever the answer to an overlap.
            var placed = OrbArrangement.Compute(30, NoTeams(30), Layout("line"));

            Assert.Single(placed.Select(p => p.Y).Distinct());
        }

        [Fact]
        public void WhenNothingFitsTheLeastBadArrangementIsKeptOnScreen()
        {
            // Thirty 200% orbs on a 640x400 screen overlap in any shape. As a
            // grid already, the retry redraws the same grid, finds it no
            // better, and keeps what it had — which must still be every orb on
            // the screen, and the same answer every time. (Across the sweep's
            // 1076 retries the grid always won; this is the input where it
            // cannot.)
            var work = new PixelRect(0, 0, 640, 400);
            var sizes = Enumerable.Repeat(2.0, 30).ToArray();
            var layout = Layout("grid", work: work);

            var placed = OrbArrangement.Compute(30, NoTeams(30), layout, sizes);

            Assert.True(
                OrbArrangement.WorstGap(placed, NoTeams(30), OrbArrangement.CircleDip * 2.0) < 0,
                "this case is only worth having if the orbs really cannot all fit");

            Assert.All(placed, p =>
            {
                Assert.InRange(p.X, work.X, work.Right - 112);
                Assert.InRange(p.Y, work.Y, work.Bottom - 112);
            });

            Assert.Equal(placed, OrbArrangement.Compute(30, NoTeams(30), layout, sizes));
        }

        // --- WorstGap ---

        [Fact]
        public void WorstGapMeasuresBetweenCirclesAndCountsAMemberSmaller()
        {
            // Two leads 100px apart with 36px circles: 100 - 18 - 18 = 64.
            Assert.Equal(64, OrbArrangement.WorstGap(
                new[] { new PixelPoint(0, 0), new PixelPoint(100, 0) }, NoTeams(2), 36), 6);

            // The second a member of the first: 100 - 18 - 18*0.72.
            Assert.Equal(100 - 18 - 18 * 0.72, OrbArrangement.WorstGap(
                new[] { new PixelPoint(0, 0), new PixelPoint(100, 0) }, new[] { -1, 0 }, 36), 6);

            // A lead table shorter than the orbs reads the missing entries as
            // leads, as Separate does.
            Assert.Equal(64, OrbArrangement.WorstGap(
                new[] { new PixelPoint(0, 0), new PixelPoint(100, 0) }, new[] { -1 }, 36), 6);
        }
    }
}
