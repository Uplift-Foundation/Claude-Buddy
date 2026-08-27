using Avalonia;
using Xunit;

namespace ClaudeBuddy.Tests
{
    // The band split, and the grouped arrangement's own promises.
    //
    // The 41472 grouped cases the sweep now walks assert what must not happen —
    // nothing off screen, nothing overlapping, nothing that ignores a drag. This
    // file asserts what must, which a sweep of invariants cannot: that the
    // biggest group gets the most room, that a group of one still gets enough
    // for one, and that the shape each group was given is the shape it is drawn
    // in. A layout can satisfy every invariant in the sweep and still put the
    // crons' star where the heartbeats' circle belongs.
    public class OrbArrangementBandTests
    {
        private static readonly PixelRect Work = new(0, 0, 1920, 1080);
        private const int Window = 56;

        // --- Bands ---

        [Theory]
        [InlineData(5, 0, 0)]
        [InlineData(0, 5, 0)]
        [InlineData(0, 0, 5)]
        public void OneOccupiedGroupGetsTheWholeWorkArea(int a, int b, int c)
        {
            // Including when the one group in use is not group 0 — the case that
            // separates "how many slots are occupied" from "how many exist", and
            // the one where a naive count would cut three bands and draw the only
            // shape in a third of the screen for no reason.
            var bands = OrbArrangement.Bands(Work, new[] { a, b, c }, Window);

            Assert.All(bands, band => Assert.Equal(Work, band));
        }

        [Fact]
        public void BandsTileTheWorkAreaLeftToRightWithNoGapAndNoOverlap()
        {
            var bands = OrbArrangement.Bands(Work, new[] { 7, 7, 6 }, Window);

            // Group order is left-to-right order: chats, then heartbeats, then
            // crons. Asserted because it is what the sweep's centroid check
            // relies on, and what makes the arrangement stable to look at — a
            // band order that varied with the orb counts would have the whole
            // screen rearrange itself when one cron finished.
            Assert.Equal(Work.X, bands[0].X);
            Assert.Equal(bands[0].Right, bands[1].X);
            Assert.Equal(bands[1].Right, bands[2].X);
            Assert.Equal(Work.Right, bands[2].Right);

            Assert.All(bands, band =>
            {
                Assert.Equal(Work.Y, band.Y);
                Assert.Equal(Work.Height, band.Height);
            });
        }

        [Fact]
        public void ABandIsWidthInProportionToTheOrbsItHolds()
        {
            var bands = OrbArrangement.Bands(Work, new[] { 16, 4, 0 }, Window);

            // A band's height is the full work area either way, so its width
            // *is* its share of the room. Four times the orbs, four times the
            // width — which is what keeps the chats' shape close to the size it
            // would have had on a screen of its own.
            Assert.True(
                bands[0].Width > bands[1].Width * 3,
                $"16 orbs got {bands[0].Width}px beside 4 orbs' {bands[1].Width}px");

            Assert.Equal(Work.Width, bands[0].Width + bands[1].Width);
        }

        [Fact]
        public void AGroupOfOneStillGetsABandItsOrbFitsInside()
        {
            // 1 of 31 orbs is 62px by proportion alone, which is narrower than
            // the 56px window its single orb is drawn in — so the fit pass would
            // shrink the shape to nothing and the separation pass would push the
            // orb into its neighbour, which looks exactly like the overlap bands
            // exist to prevent.
            var bands = OrbArrangement.Bands(Work, new[] { 30, 1, 0 }, Window);

            Assert.True(
                bands[1].Width >= Window * 2,
                $"a lone orb was given a {bands[1].Width}px band");

            // And the floor is paid for out of the bigger group rather than out
            // of the screen: the bands still tile it exactly.
            Assert.Equal(Work.Width, bands[0].Width + bands[1].Width);
        }

        [Fact]
        public void ThreeGroupsOfOneOnANarrowScreenStillTileIt()
        {
            // The case where the floor cannot be paid: three lone orbs each want
            // 112px of a 300px screen, which is more than there is. The floor is
            // capped at an equal share for exactly this, so the bands still tile
            // the area rather than overflowing it.
            var narrow = new PixelRect(0, 0, 300, 400);
            var bands = OrbArrangement.Bands(narrow, new[] { 1, 1, 1 }, Window);

            Assert.Equal(narrow.X, bands[0].X);
            Assert.Equal(narrow.Right, bands[2].Right);
            Assert.All(bands, band => Assert.True(band.Width > 0));
        }

        [Fact]
        public void BandsHonourAWorkAreaThatDoesNotStartAtTheOrigin()
        {
            // A second monitor to the right of the first, which is where the
            // work area's X stops being zero — and where arithmetic that used a
            // width as though it were a right edge would put every band on the
            // wrong screen.
            var second = new PixelRect(1920, 200, 1600, 900);
            var bands = OrbArrangement.Bands(second, new[] { 3, 2, 0 }, Window);

            Assert.Equal(second.X, bands[0].X);
            Assert.Equal(second.Right, bands[1].Right);
            Assert.All(bands, band => Assert.Equal(second.Y, band.Y));
        }

        // --- the grouped arrangement ---

        private static PixelPoint[] Arrange(
            int[] groups, string[] shapes, PixelPoint? centre = null, double spacing = 0.85)
        {
            var leads = Enumerable.Repeat(-1, groups.Length).ToArray();
            var layout = new OrbArrangement.Layout(Work, 1.0, shapes[0], spacing, centre);

            return OrbArrangement.Compute(groups.Length, leads, groups, shapes, layout);
        }

        [Fact]
        public void EveryOrbGetsAPositionWhateverItsGroupSays()
        {
            // Including a group index nothing has a shape for, and a groupOf
            // array shorter than the orb count — both of which a caller produces
            // by getting the settings and the shapes array out of step. Losing
            // an orb here means an orb left wherever it happened to be while
            // everything around it moved, which reads as the app ignoring the
            // arrange button.
            var placed = Arrange(
                new[] { 0, 1, 2, 9, -1, 0 },
                new[] { "circle", "star", "line" });

            Assert.Equal(6, placed.Length);
            Assert.All(placed, p => Assert.True(
                p.X >= Work.X && p.Y >= Work.Y
                && p.X + Window <= Work.Right && p.Y + Window <= Work.Bottom,
                $"orb at ({p.X},{p.Y}) is off the work area"));
        }

        [Fact]
        public void ThreeGroupsAreDrawnAsThreeSeparateClusters()
        {
            // Six orbs, two per group. What must be visible is that they read as
            // three things: every orb is nearer to its own group than to either
            // of the others.
            var groups = new[] { 0, 0, 1, 1, 2, 2 };
            var placed = Arrange(groups, new[] { "circle", "circle", "circle" });

            double Gap(int a, int b)
            {
                double dx = placed[a].X - placed[b].X, dy = placed[a].Y - placed[b].Y;
                return Math.Sqrt(dx * dx + dy * dy);
            }

            for (var i = 0; i < groups.Length; i++)
            for (var j = 0; j < groups.Length; j++)
            {
                if (i == j || groups[i] != groups[j]) continue;

                var withinGroup = Gap(i, j);

                for (var k = 0; k < groups.Length; k++)
                {
                    if (groups[k] == groups[i]) continue;

                    Assert.True(
                        Gap(i, k) > withinGroup,
                        $"orb {i} is closer to orb {k} in another group "
                            + $"({Gap(i, k):0}px) than to orb {j} in its own ({withinGroup:0}px)");
                }
            }
        }

        [Fact]
        public void EachGroupIsDrawnInTheShapeItWasGiven()
        {
            // The check a sweep of invariants cannot make: a layout can keep
            // every orb on screen and apart while drawing the crons' line where
            // the heartbeats' shape belongs. Read off the *aspect* of each
            // group's bounding box, because a line of orbs is unmistakably wide
            // and flat where a circle of the same orbs is not.
            var groups = new[] { 0, 0, 0, 0, 0, 1, 1, 1, 1, 1 };
            var placed = Arrange(groups, new[] { "line", "circle", "star" });

            double Aspect(int group)
            {
                var mine = Enumerable.Range(0, groups.Length)
                    .Where(i => groups[i] == group)
                    .Select(i => placed[i])
                    .ToArray();

                var width = mine.Max(p => p.X) - mine.Min(p => p.X) + 1.0;
                var height = mine.Max(p => p.Y) - mine.Min(p => p.Y) + 1.0;

                return width / height;
            }

            Assert.True(Aspect(0) > 4, $"group 0's line came out {Aspect(0):0.0} wide per tall");
            Assert.True(Aspect(1) < 3, $"group 1's circle came out {Aspect(1):0.0} wide per tall");
        }

        [Fact]
        public void SwappingTheGroupsShapesSwapsWhatIsDrawn()
        {
            // The same assertion from the other side, and the one that catches a
            // shapes array read off by one: give group 1 the line instead and the
            // flat cluster has to move with it.
            var groups = new[] { 0, 0, 0, 0, 0, 1, 1, 1, 1, 1 };

            double Aspect(PixelPoint[] placed, int group)
            {
                var mine = Enumerable.Range(0, groups.Length)
                    .Where(i => groups[i] == group)
                    .Select(i => placed[i])
                    .ToArray();

                return (mine.Max(p => p.X) - mine.Min(p => p.X) + 1.0)
                     / (mine.Max(p => p.Y) - mine.Min(p => p.Y) + 1.0);
            }

            var lineFirst = Arrange(groups, new[] { "line", "circle", "star" });
            var lineSecond = Arrange(groups, new[] { "circle", "line", "star" });

            Assert.True(Aspect(lineFirst, 0) > Aspect(lineSecond, 0));
            Assert.True(Aspect(lineSecond, 1) > Aspect(lineFirst, 1));
        }

        [Fact]
        public void AGroupJoiningOrLeavingDoesNotMoveTheWholeArrangementOffItsAnchor()
        {
            // What the saved anchor is for, in the grouped case. The composite is
            // centred on the anchor, so a cron finishing has to leave the
            // arrangement roughly where it was rather than sliding it across the
            // screen — the bug the anchor was added to fix for one shape, which
            // three shapes are a fresh chance to reintroduce.
            var anchor = new PixelPoint(700, 400);

            var withCron = Arrange(new[] { 0, 0, 0, 1, 1, 2 }, new[] { "circle", "star", "line" }, anchor);
            var without = Arrange(new[] { 0, 0, 0, 1, 1 }, new[] { "circle", "star", "line" }, anchor);

            double Centre(PixelPoint[] p) => p.Average(q => (double)q.X);

            Assert.True(
                Math.Abs(Centre(withCron) - Centre(without)) < Work.Width / 8.0,
                $"losing one cron moved the arrangement from {Centre(withCron):0} "
                    + $"to {Centre(without):0}");
        }

        [Fact]
        public void MovingTheAnchorMovesEveryGroupByExactlyTheSameAmount()
        {
            // A whole-shape drag nudges the one saved anchor
            // (SessionManager.ShiftArrangementAnchor) and every group has to
            // move with it. The sweep asserts this across its whole matrix; this
            // is the same rule stated where somebody reading OrbArrangement will
            // find it, because the first draft failed it for exactly one group
            // out of three and it took the sweep to notice.
            var groups = new[] { 0, 0, 0, 1, 1, 2, 2 };
            var shapes = new[] { "circle", "star", "line" };

            var at = Arrange(groups, shapes, new PixelPoint(800, 500));
            var moved = Arrange(groups, shapes, new PixelPoint(838, 524));

            for (var i = 0; i < groups.Length; i++)
            {
                Assert.Equal(38, moved[i].X - at[i].X);
                Assert.Equal(24, moved[i].Y - at[i].Y);
            }
        }

        [Fact]
        public void NoOrbsIsAnEmptyAnswerFromEitherEntryPoint()
        {
            // Reachable for real: SessionManager calls this with whatever orbs
            // are visible, and every orb can be hidden — set both timer groups
            // to Hidden on a gateway that has nothing else on it and this is what
            // arrives. An exception here would take the arrange button down.
            var layout = new OrbArrangement.Layout(Work, 1.0, "heart", 0.85, null);

            Assert.Empty(OrbArrangement.Compute(0, Array.Empty<int>(), layout));
            Assert.Empty(OrbArrangement.Compute(
                0, Array.Empty<int>(), Array.Empty<int>(), new[] { "heart" }, layout));
        }

        [Fact]
        public void AGroupBeyondTheShapesArrayIsFoldedInWithTheChats()
        {
            // The shapes array shorter than the group indices in play, which is
            // what a caller produces by adding a group to OrbClusters and
            // forgetting SessionManager.Shapes(). The number of shapes is what
            // says how many bands may be cut, so a group past the end is not
            // given a band of its own — its orbs join group 0, which is one
            // shape rather than an orb left behind or a throw.
            var groups = new[] { 0, 0, 0, 2, 2, 2 };
            var leads = Enumerable.Repeat(-1, groups.Length).ToArray();
            var layout = new OrbArrangement.Layout(Work, 1.0, "line", 0.85, null);

            var placed = OrbArrangement.Compute(
                groups.Length, leads, groups, new[] { "line", "circle" }, layout);

            Assert.Equal(groups.Length, placed.Length);

            // One shape, not two: every orb is in the line, so the whole set is
            // flat. Two bands would have put three of them in a circle.
            var height = placed.Max(p => p.Y) - placed.Min(p => p.Y);

            Assert.True(height < Window, $"the arrangement came out {height}px tall");
        }

        [Fact]
        public void NoShapesAtAllFallsBackToTheLayoutsOwnShape()
        {
            // An empty shapes array — the degenerate version of the same caller
            // error. There is still one band, and the shape it draws is the one
            // on the layout, which is where the chats' shape comes from anyway.
            var groups = new[] { 0, 0, 0, 1, 1 };
            var leads = Enumerable.Repeat(-1, groups.Length).ToArray();
            var layout = new OrbArrangement.Layout(Work, 1.0, "line", 0.85, null);

            var placed = OrbArrangement.Compute(
                groups.Length, leads, groups, Array.Empty<string>(), layout);

            Assert.Equal(groups.Length, placed.Length);

            var height = placed.Max(p => p.Y) - placed.Min(p => p.Y);
            Assert.True(height < Window, $"the arrangement came out {height}px tall");
        }

        [Fact]
        public void ABlankShapeNameFallsBackToTheChatsShapeRatherThanTheDefault()
        {
            // A hand-edited settings.json with `"openclawCronShape": ""`. An
            // empty string reaching OrbArrangement.Unit would draw a heart — its
            // answer for anything unrecognised — so a user who blanked the field
            // would get a shape they never chose. Falling back to the shape the
            // chats are using is the answer that surprises least.
            var groups = new[] { 0, 0, 0, 1, 1, 1 };
            var leads = Enumerable.Repeat(-1, groups.Length).ToArray();
            var layout = new OrbArrangement.Layout(Work, 1.0, "line", 0.85, null);

            var placed = OrbArrangement.Compute(
                groups.Length, leads, groups, new[] { "line", "   " }, layout);

            var mine = new[] { placed[3], placed[4], placed[5] };
            var height = mine.Max(p => p.Y) - mine.Min(p => p.Y);

            Assert.True(height < Window, $"the blank shape came out {height}px tall");
        }

        [Fact]
        public void PuttingEveryOrbInOneGroupIsIdenticalToNotGroupingAtAll()
        {
            // The compatibility promise the whole change rests on: nobody who
            // leaves both settings alone sees their orbs move by a pixel. Every
            // orb in group 0 has to come out where the ungrouped entry point puts
            // it, and the shapes array's other entries must not be consulted.
            var leads = new[] { -1, 0, 0, -1, -1, -1, 2 };
            var layout = new OrbArrangement.Layout(Work, 2.0, "heart", 1.2, new PixelPoint(640, 480));

            var ungrouped = OrbArrangement.Compute(leads.Length, leads, layout);

            var grouped = OrbArrangement.Compute(
                leads.Length, leads, new int[leads.Length],
                new[] { "heart", "line", "grid" }, layout);

            Assert.Equal(ungrouped, grouped);
        }
    }
}
