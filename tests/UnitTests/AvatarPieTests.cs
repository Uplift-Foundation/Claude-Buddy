using Xunit;

namespace ClaudeBuddy.Tests
{
    // How a circle is divided between several pictures.
    //
    // Pure arithmetic, and covered here rather than through the orb for the
    // reason CLAUDE.md gives for OrbArrangement: the mistakes it can make are
    // silent. A wedge that starts where the one before it ended is invisible in
    // the code and obvious on screen, and a rectangle that is a hair off centre
    // crops a portrait to an ear without anything failing.
    public class AvatarPieTests
    {
        private const double Tolerance = 0.0001;

        private static void Near(double expected, double actual, string what) =>
            Assert.True(Math.Abs(expected - actual) < Tolerance,
                $"{what}: expected {expected}, got {actual}");

        // --- angles ---------------------------------------------------------

        // Twelve o'clock, so two members split the orb left and right. Skia's
        // arcs put zero at three o'clock, which makes the top -90.
        [Fact]
        public void TheFirstWedgeStartsAtTheTop()
        {
            var (start, _) = AvatarPie.Angles(0, 4);
            Near(-90, start, "the first wedge's start");
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void TheWedgesTileTheCircleWithNoGapAndNoOverlap(int count)
        {
            var total = 0.0;
            var previousEnd = -90.0;

            for (var i = 0; i < count; i++)
            {
                var (start, sweep) = AvatarPie.Angles(i, count);

                Near(previousEnd, start, $"wedge {i} of {count} starts where {i - 1} ended");
                Assert.True(sweep > 0, "a wedge with no sweep would draw nothing");

                previousEnd = start + sweep;
                total += sweep;
            }

            Near(360, total, $"{count} wedges together");
        }

        // Nobody in the room is not a division of anything. Answering with the
        // whole circle rather than dividing by zero is what lets Render's own
        // empty-parts guard be the only place that decides what to do about it.
        [Fact]
        public void NoMembersIsOneWholeCircle()
        {
            var (start, sweep) = AvatarPie.Angles(0, 0);

            Near(-90, start, "start");
            Near(360, sweep, "sweep");
        }

        // --- bounds ---------------------------------------------------------

        // One picture takes the whole square, which is what an orb with a single
        // avatar has always drawn.
        [Fact]
        public void OneMemberFillsTheSquare()
        {
            var (x, y, width, height) = AvatarPie.Bounds(0, 1, 100);

            Near(0, x, "x");
            Near(0, y, "y");
            Near(100, width, "width");
            Near(100, height, "height");
        }

        // The 50/50 case the feature was asked for: each portrait is centred in
        // its own half rather than being the corresponding half of a portrait
        // centred in the whole.
        [Fact]
        public void TwoMembersSplitLeftAndRight()
        {
            var (rx, ry, rw, rh) = AvatarPie.Bounds(0, 2, 100);

            Near(50, rx, "the first wedge's left edge");
            Near(0, ry, "the first wedge's top");
            Near(50, rw, "the first wedge's width");
            Near(100, rh, "the first wedge's height");

            var (lx, ly, lw, lh) = AvatarPie.Bounds(1, 2, 100);

            Near(0, lx, "the second wedge's left edge");
            Near(0, ly, "the second wedge's top");
            Near(50, lw, "the second wedge's width");
            Near(100, lh, "the second wedge's height");
        }

        // Four is quadrants, clockwise from the top right — which is where the
        // arc bulging out to three o'clock puts the widest point of the first
        // wedge, and the reason the cardinal directions are checked rather than
        // only the two straight edges.
        [Theory]
        [InlineData(0, 50, 0)]
        [InlineData(1, 50, 50)]
        [InlineData(2, 0, 50)]
        [InlineData(3, 0, 0)]
        public void FourMembersTakeAQuadrantEach(int index, double x, double y)
        {
            var bounds = AvatarPie.Bounds(index, 4, 100);

            Near(x, bounds.X, $"wedge {index} x");
            Near(y, bounds.Y, $"wedge {index} y");
            Near(50, bounds.Width, $"wedge {index} width");
            Near(50, bounds.Height, $"wedge {index} height");
        }

        // Thirds have no tidy answer to check against, so the invariants are
        // checked instead: a wedge is inside the orb, it is not empty, and it
        // touches the centre — every wedge does, since every wedge is a slice
        // with its point there.
        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void EveryWedgeIsInsideTheOrbAndTouchesItsCentre(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var (x, y, width, height) = AvatarPie.Bounds(i, count, 144);

                Assert.True(x >= -Tolerance, $"wedge {i} of {count} starts left of the orb");
                Assert.True(y >= -Tolerance, $"wedge {i} of {count} starts above the orb");
                Assert.True(x + width <= 144 + Tolerance, $"wedge {i} of {count} runs off the right");
                Assert.True(y + height <= 144 + Tolerance, $"wedge {i} of {count} runs off the bottom");

                Assert.True(width > 0 && height > 0, $"wedge {i} of {count} is empty");

                Assert.True(x <= 72 + Tolerance && x + width >= 72 - Tolerance,
                    $"wedge {i} of {count} does not reach the centre horizontally");
                Assert.True(y <= 72 + Tolerance && y + height >= 72 - Tolerance,
                    $"wedge {i} of {count} does not reach the centre vertically");
            }
        }

        // Scale-free, so the 144pt frames a composite is cut at and the 36pt an
        // orb draws them at cannot disagree about where a face sits.
        [Fact]
        public void BoundsScaleWithTheSquare()
        {
            var small = AvatarPie.Bounds(1, 3, 100);
            var large = AvatarPie.Bounds(1, 3, 400);

            Near(small.X * 4, large.X, "x");
            Near(small.Y * 4, large.Y, "y");
            Near(small.Width * 4, large.Width, "width");
            Near(small.Height * 4, large.Height, "height");
        }

        // Four, and the reason is legibility rather than arithmetic — see the
        // constant's own comment. Asserted so that raising it is a deliberate
        // act with a test to change rather than a one-character edit.
        [Fact]
        public void AtMostFourPicturesShareAnOrb() =>
            Assert.Equal(4, AvatarPie.MaxParts);
    }
}
