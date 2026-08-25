using Avalonia;
using Xunit;

namespace ClaudeBuddy.Tests
{
    // Where a team arrow goes, and when there isn't one.
    //
    // This is the arithmetic that used to sit inline in TeamLinks.LinkWindow.
    // Apply, between measuring two live windows and setting a real window's
    // position — so it was unreachable for the usual reason rather than any real
    // one. It now lives next to the clearance rule it has to agree with, which is
    // the point: TeamLinkGeometry exists because the two once disagreed, orbs were
    // fanned closer than an arrow can be drawn, and every arrow was silently
    // parked so a team looked like unrelated orbs sitting near each other.
    //
    // The null cases matter more than the geometry. A wrong position draws an
    // arrow in the wrong place, which is visible; a wrong null draws nothing at
    // all, which is the failure that shipped.
    public class TeamLinkPlacementTests
    {
        private const double MemberRadius = 12;
        private const double LeadRadius = 16;

        private static TeamLinkGeometry.ArrowPlacement? Place(
            double fromX, double fromY, double toX, double toY, double scale = 1) =>
            TeamLinkGeometry.Place(
                new Point(fromX, fromY), new Point(toX, toY), MemberRadius, LeadRadius, scale);

        // --- when there is an arrow ---

        [Fact]
        public void TwoOrbsFarEnoughApartGetAnArrow()
        {
            Assert.NotNull(Place(100, 100, 400, 100));
        }

        // The shaft starts clear of the member's edge and stops short of the
        // lead's, by the gaps the shared rule defines — the lead end gets more so
        // the arrowhead reads as pointing *at* the orb rather than touching it.
        [Fact]
        public void TheShaftStartsAndStopsClearOfBothOrbs()
        {
            var placement = Place(100, 100, 400, 100)!.Value;

            // Back into absolute coordinates to compare against the centres.
            var startX = placement.Start.X + placement.Position.X;
            var endX = placement.End.X + placement.Position.X;

            Assert.Equal(100 + MemberRadius + TeamLinkGeometry.MemberGap, startX, 1);
            Assert.Equal(400 - LeadRadius - TeamLinkGeometry.LeadGap, endX, 1);
        }

        // The unit vector points from the member at the lead, which is what the
        // arrowhead is oriented by. An inverted one would draw every arrow
        // backwards — each member appearing to be led by its own reports.
        [Theory]
        [InlineData(400, 100, 1, 0)]
        [InlineData(100, 400, 0, 1)]
        [InlineData(100, -400, 0, -1)]
        [InlineData(-400, 100, -1, 0)]
        public void TheDirectionPointsFromTheMemberAtTheLead(
            double toX, double toY, double ux, double uy)
        {
            var placement = Place(100, 100, toX, toY)!.Value;

            Assert.Equal(ux, placement.Ux, 3);
            Assert.Equal(uy, placement.Uy, 3);
        }

        // The window has to be big enough to hold the whole shape, head included,
        // or the arrow is clipped by its own window — which looks like a shorter
        // arrow rather than like a bug.
        [Fact]
        public void TheWindowLeavesRoomForTheHeadOnBothSides()
        {
            var placement = Place(100, 100, 400, 100)!.Value;

            // A horizontal arrow: the height is entirely padding, two half-widths
            // plus the rounding pixel on each side.
            Assert.True(
                placement.Height >= 2 * TeamLinkGeometry.HeadHalfWidth,
                $"height {placement.Height} cannot hold a head {TeamLinkGeometry.HeadHalfWidth * 2} across");
        }

        // Both endpoints land inside the window, expressed relative to it. If
        // either were negative the geometry would be drawn outside its own
        // surface and simply not appear.
        [Theory]
        [InlineData(400, 100)]
        [InlineData(100, 400)]
        [InlineData(-400, -400)]
        [InlineData(430, 380)]
        public void BothEndpointsFallInsideTheWindow(double toX, double toY)
        {
            var placement = Place(100, 100, toX, toY)!.Value;

            foreach (var point in new[] { placement.Start, placement.End })
            {
                Assert.InRange(point.X, 0, placement.Width);
                Assert.InRange(point.Y, 0, placement.Height);
            }
        }

        // --- when there is not ---

        // Exactly stacked: there is no direction to point in.
        [Fact]
        public void TwoOrbsAtTheSamePointGetNoArrow()
        {
            Assert.Null(Place(200, 200, 200, 200));
            Assert.Null(Place(200, 200, 200.5, 200));
        }

        // Closer than the shared clearance rule allows. This is the boundary the
        // arrangement is checked against — tests/ArrangementTests asserts every
        // team member sits at least MinimumCentreDistance from its lead — so the
        // two halves must agree about it, and this is that agreement asserted from
        // the drawing side.
        [Fact]
        public void OrbsCloserThanTheSharedClearanceGetNoArrow()
        {
            var needed = TeamLinkGeometry.MinimumCentreDistance(MemberRadius, LeadRadius);

            Assert.Null(Place(0, 0, needed - 1, 0));
            Assert.NotNull(Place(0, 0, needed + 1, 0));
        }

        // ...and the same rule holds diagonally, where it is the distance rather
        // than either axis that counts.
        [Fact]
        public void TheClearanceIsMeasuredAsADistanceNotPerAxis()
        {
            var needed = TeamLinkGeometry.MinimumCentreDistance(MemberRadius, LeadRadius);
            var leg = needed / Math.Sqrt(2);

            Assert.Null(Place(0, 0, leg - 2, leg - 2));
            Assert.NotNull(Place(0, 0, leg + 2, leg + 2));
        }

        // --- scale ---

        // Everything given in DIPs has to be multiplied up before it is compared
        // against screen units. Getting this from Avalonia's Scaling instead of
        // measuring it put the arrows half an orb off on a Retina Mac, per the
        // comment in Apply — so the scale is a real input, not a formality.
        [Fact]
        public void AtDoubleScaleTheGapsAreTwiceAsWide()
        {
            var single = Place(0, 0, 600, 0)!.Value;
            var doubled = Place(0, 0, 600, 0, scale: 2)!.Value;

            var singleStart = single.Start.X + single.Position.X;
            var doubledStart = (doubled.Start.X * 2) + doubled.Position.X;

            Assert.Equal(MemberRadius + TeamLinkGeometry.MemberGap, singleStart, 1);
            Assert.Equal((MemberRadius + TeamLinkGeometry.MemberGap) * 2, doubledStart, 1);
        }

        // A separation that is enough at scale 1 can be too little at scale 2,
        // because the minimum length is a DIP measurement too. Two orbs that far
        // apart on a Retina display really are closer together in DIPs.
        [Fact]
        public void TheMinimumLengthScalesWithTheDisplay()
        {
            var needed = TeamLinkGeometry.MinimumCentreDistance(MemberRadius, LeadRadius);

            Assert.NotNull(Place(0, 0, needed + 2, 0));
            Assert.Null(Place(0, 0, needed + 2, 0, scale: 2));
        }

        // The window's size is in DIPs while its position is in platform units,
        // which is the one place the two coordinate systems meet in this
        // function. Both consequences are asserted, and the second is the one
        // that is easy to get backwards.
        [Fact]
        public void TheWindowSizeIsInDipsWhileItsPositionIsNot()
        {
            var single = Place(0, 0, 600, 0)!.Value;
            var doubled = Place(0, 0, 600, 0, scale: 2)!.Value;

            // Fewer DIPs for the same platform span, because a DIP is bigger.
            Assert.True(
                doubled.Width < single.Width,
                $"at twice the scale the same span should be fewer DIPs wide, "
                    + $"got {doubled.Width} against {single.Width}");

            // And the origin moves *inward*, which is not an inconsistency: the
            // gap that holds the shaft clear of the member is a DIP measurement
            // too, so at twice the scale it is twice as many platform units, and
            // the window it sizes therefore starts further along. Asserted rather
            // than assumed equal — an earlier version of this test expected the
            // positions to match and was simply wrong about which quantity
            // scales.
            Assert.True(
                doubled.Position.X > single.Position.X,
                $"the shaft starts further in at scale 2, so the window does too; "
                    + $"got {doubled.Position.X} against {single.Position.X}");
        }
    }
}
