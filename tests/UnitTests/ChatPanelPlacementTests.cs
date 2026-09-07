using Avalonia;
using Xunit;

namespace ClaudeBuddy.Tests
{
    // ChatPanelPlacement.Resolve: where a chat panel opens when other panels
    // are already pinned on screen.
    //
    // The first job of this suite is a regression lock, not a new-behaviour
    // check: with no pinned panels in the way, Resolve has to reproduce
    // ChatPanel.Reposition()'s own maths exactly, pixel for pixel, because
    // that is every session that existed before CB-110 made more than one
    // panel possible. RepositionReplica below is that maths, copied rather
    // than called, so a change to ChatPanel.axaml.cs (owned by a different
    // engineer on this ticket) can't quietly make this suite pass by
    // updating both sides of the comparison at once.
    //
    // The second job is the new behaviour: what happens once a candidate
    // position collides with something already pinned. Each fact below is
    // worked out by hand against the same formulas Resolve uses, rather than
    // asserted against whatever Resolve currently returns, so a regression
    // in the slide or the flip has something independent to fail against.
    public class ChatPanelPlacementTests
    {
        private static readonly PixelSize DefaultSize = new(400, 300);
        private static readonly PixelPoint DefaultAnchor = new(960, 500);

        // The three screen shapes ChatPanelPlacement has to agree with
        // Reposition() on: an ordinary 1080p screen at the origin, a screen
        // to the left of it (negative origin, as a second monitor reports),
        // and a small laptop screen with a menu bar eating into the top of
        // its work area.
        public static readonly TheoryData<PixelRect> WorkAreas = new()
        {
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(-1920, 25, 1920, 1055),
            new PixelRect(0, 25, 1280, 695),
        };

        // Reposition()'s own maths (ChatPanel.axaml.cs), copied verbatim in
        // spirit: below the anchor, centred, flipped above when below would
        // run off the bottom, then clamped into the work area. Kept here
        // rather than shared with ChatPanelPlacement.Clamp/Candidate so this
        // suite is checking Resolve against an independent restatement of
        // the requirement, not against its own helpers.
        private static PixelPoint RepositionReplica(PixelPoint anchor, PixelSize size, int gap, PixelRect work)
        {
            var y = anchor.Y + gap;
            if (y + size.Height > work.Bottom) y = anchor.Y - gap - size.Height;

            var x = System.Math.Clamp(anchor.X - size.Width / 2, work.X, System.Math.Max(work.X, work.Right - size.Width));
            y = System.Math.Clamp(y, work.Y, System.Math.Max(work.Y, work.Bottom - size.Height));

            return new PixelPoint(x, y);
        }

        [Theory]
        [MemberData(nameof(WorkAreas))]
        public void NoOccupied_MatchesRepositionExactly_WhenBelowFits(PixelRect work)
        {
            // An anchor near the top of the work area, where "below" always
            // has room — the ordinary case for every panel opened today.
            var anchor = new PixelPoint(work.X + work.Width / 2, work.Y + 40);

            var expected = RepositionReplica(anchor, DefaultSize, 12, work);
            var actual = ChatPanelPlacement.Resolve(anchor, DefaultSize, 12, work, System.Array.Empty<PixelRect>());

            Assert.Equal(expected, actual);
        }

        [Theory]
        [MemberData(nameof(WorkAreas))]
        public void NoOccupied_MatchesRepositionExactly_WhenBelowRunsOffTheBottom(PixelRect work)
        {
            // An anchor near the bottom, where Reposition() flips above
            // instead — the case this file's header calls out as needing
            // to be bit-identical, not merely equivalent.
            var anchor = new PixelPoint(work.X + work.Width / 2, work.Bottom - 40);

            var expected = RepositionReplica(anchor, DefaultSize, 12, work);
            var actual = ChatPanelPlacement.Resolve(anchor, DefaultSize, 12, work, System.Array.Empty<PixelRect>());

            Assert.Equal(expected, actual);
        }

        [Theory]
        [MemberData(nameof(WorkAreas))]
        public void NoOccupied_ClampsAgainstTheLeftEdge(PixelRect work)
        {
            // An anchor right at the left edge: the centred candidate would
            // put the panel's left half off screen, so both Reposition()
            // and Resolve have to clamp it back to work.X.
            var anchor = new PixelPoint(work.X + 5, work.Y + 200);

            var expected = RepositionReplica(anchor, DefaultSize, 12, work);
            var actual = ChatPanelPlacement.Resolve(anchor, DefaultSize, 12, work, System.Array.Empty<PixelRect>());

            Assert.Equal(expected, actual);
            Assert.Equal(work.X, actual.X);
        }

        [Theory]
        [MemberData(nameof(WorkAreas))]
        public void NoOccupied_ClampsAgainstTheRightEdge(PixelRect work)
        {
            var anchor = new PixelPoint(work.Right - 5, work.Y + 200);

            var expected = RepositionReplica(anchor, DefaultSize, 12, work);
            var actual = ChatPanelPlacement.Resolve(anchor, DefaultSize, 12, work, System.Array.Empty<PixelRect>());

            Assert.Equal(expected, actual);
            Assert.Equal(work.Right - DefaultSize.Width, actual.X);
        }

        // A single pinned panel sitting exactly where "below" would land.
        // Below collides, above does not (it is far enough from the pinned
        // rect that their Y ranges never overlap), so Resolve should flip —
        // the same flip Reposition() already does for "would run off the
        // bottom", now triggered by a collision instead.
        [Fact]
        public void OnePinnedRectDirectlyBelow_FlipsAbove()
        {
            var work = new PixelRect(0, 0, 1920, 1080);
            var occupied = new[] { new PixelRect(700, 500, 500, 400) };

            var actual = ChatPanelPlacement.Resolve(DefaultAnchor, DefaultSize, 12, work, occupied);

            Assert.Equal(new PixelPoint(760, 188), actual);
        }

        // Both vertical sides are now pinned — the flip above from the
        // previous case is itself occupied, as well as the original below
        // position. Neither vertical choice clears, so Resolve has to slide
        // sideways. It tries right before left, and here right clears first
        // once the slide is wide enough to leave the pinned rect's X range
        // entirely (a partial overlap, even one pixel, still collides).
        [Fact]
        public void PinnedAboveAndBelow_SlidesRight()
        {
            var work = new PixelRect(0, 0, 1920, 1080);
            var occupied = new[]
            {
                new PixelRect(760, 600, 400, 300), // exactly where "below" would land
                new PixelRect(760, 100, 400, 300), // exactly where the flip to "above" would land
            };

            var actual = ChatPanelPlacement.Resolve(DefaultAnchor, DefaultSize, 100, work, occupied);

            // Clears the below-pinned rect's X range (760-1160) only once the
            // slide reaches a full panel-width right, at the fourth 100px step.
            Assert.Equal(new PixelPoint(1160, 600), actual);
        }

        // The same trap as above, plus a wide rect blocking the entire right
        // half of the screen — every rightward slide, at every step, runs
        // into it. Only a leftward slide ever clears, so Resolve has to fall
        // through its right-then-left check to the left candidate.
        [Fact]
        public void PinnedAboveBelowAndToTheRight_SlidesLeft()
        {
            var work = new PixelRect(0, 0, 1920, 1080);
            var occupied = new[]
            {
                new PixelRect(760, 600, 400, 300),
                new PixelRect(760, 100, 400, 300),
                new PixelRect(1160, 0, 760, 1080), // the entire right side of the screen
            };

            var actual = ChatPanelPlacement.Resolve(DefaultAnchor, DefaultSize, 100, work, occupied);

            Assert.Equal(new PixelPoint(360, 600), actual);
        }

        // Nothing on the tried lattice ever clears — here because a single
        // pinned rect covers the entire work area. Resolve has to give up
        // and return the same clamped, un-slid candidate step 1 would have
        // chosen with no occupied rects at all: overlapping a pinned panel
        // beats vanishing off screen.
        [Fact]
        public void WorkAreaCompletelyOccupied_FallsBackToTheClampedPrimaryCandidate()
        {
            var work = new PixelRect(0, 0, 1920, 1080);
            var occupied = new[] { work };

            var withNothingPinned = ChatPanelPlacement.Resolve(
                DefaultAnchor, DefaultSize, 100, work, System.Array.Empty<PixelRect>());
            var withEverythingPinned = ChatPanelPlacement.Resolve(
                DefaultAnchor, DefaultSize, 100, work, occupied);

            Assert.Equal(withNothingPinned, withEverythingPinned);
            Assert.Equal(new PixelPoint(760, 600), withEverythingPinned);
        }

        // A seeded sweep across a grid of anchors, each paired with 0-3
        // pinned rects generated from a fixed-seed Random. Two invariants
        // hold everywhere on the grid rather than only at the hand-picked
        // points above:
        //
        //  1. The panel is smaller than the work area at every anchor, so
        //     the result always has to land fully inside it — Resolve must
        //     never return something Clamp should have caught.
        //  2. Whenever some position on the same lattice Resolve itself
        //     tries (the two vertical candidates and every slid position
        //     out to the edge of the work area) does not collide with any
        //     pinned rect, Resolve's actual answer does not collide either.
        //     A failure here means Resolve settled for an overlap when a
        //     clear position was available on the very lattice it walks.
        [Fact]
        public void SeededSweep_StaysOnScreenAndFindsAnyAvailableClearSpot()
        {
            var work = new PixelRect(0, 0, 1920, 1080);
            var size = new PixelSize(400, 300);
            var rng = new System.Random(20260906);

            for (var ax = work.X + 100; ax <= work.Right - 100; ax += 200)
            {
                for (var ay = work.Y + 100; ay <= work.Bottom - 100; ay += 200)
                {
                    var anchor = new PixelPoint(ax, ay);
                    var gap = rng.Next(4, 60);
                    var occupied = SeedOccupied(rng, work);

                    var result = ChatPanelPlacement.Resolve(anchor, size, gap, work, occupied);
                    var resultRect = new PixelRect(result, size);

                    Assert.True(resultRect.X >= work.X && resultRect.Right <= work.Right,
                        $"anchor {anchor}, gap {gap}: {resultRect} left the work area horizontally");
                    Assert.True(resultRect.Y >= work.Y && resultRect.Bottom <= work.Bottom,
                        $"anchor {anchor}, gap {gap}: {resultRect} left the work area vertically");

                    if (SomeTriedLatticePositionIsClear(anchor, size, gap, work, occupied))
                    {
                        Assert.False(Intersects(resultRect, occupied),
                            $"anchor {anchor}, gap {gap}: a clear position existed but Resolve returned an overlapping one");
                    }
                }
            }
        }

        private static PixelRect[] SeedOccupied(System.Random rng, PixelRect work)
        {
            var count = rng.Next(0, 4); // 0-3 pinned rects, per the ticket's spec
            var occupied = new PixelRect[count];

            for (var i = 0; i < count; i++)
            {
                var width = rng.Next(150, 500);
                var height = rng.Next(150, 400);
                // Seeded a bit outside the work area too, the same way a
                // panel dragged near an edge would be — a pinned rect does
                // not have to be fully on screen to be worth avoiding.
                var x = rng.Next(work.X - 200, work.Right + 200 - width);
                var y = rng.Next(work.Y - 200, work.Bottom + 200 - height);
                occupied[i] = new PixelRect(x, y, width, height);
            }

            return occupied;
        }

        // The lattice ChatPanelPlacement.Resolve itself walks: the two
        // vertical candidates, and — for each of them — every position
        // reachable by sliding right or left in `gap`-sized steps (minimum
        // 8px) out to the edge of the work area. Written independently of
        // Resolve's own loop so this is a check on the algorithm's promise
        // ("if anything on the lattice clears, return something that
        // clears") rather than a restatement of its control flow.
        private static bool SomeTriedLatticePositionIsClear(
            PixelPoint anchor, PixelSize size, int gap, PixelRect work, PixelRect[] occupied)
        {
            var below = ClampCandidate(anchor, size, gap, work, above: false);
            var above = ClampCandidate(anchor, size, gap, work, above: true);

            if (!Intersects(below, occupied)) return true;
            if (!Intersects(above, occupied)) return true;

            var step = System.Math.Max(gap, 8);

            foreach (var side in new[] { below, above })
            {
                for (var dx = step; dx <= work.Width; dx += step)
                {
                    var right = ClampRect(new PixelRect(new PixelPoint(side.X + dx, side.Y), size), work);
                    if (!Intersects(right, occupied)) return true;

                    var left = ClampRect(new PixelRect(new PixelPoint(side.X - dx, side.Y), size), work);
                    if (!Intersects(left, occupied)) return true;
                }
            }

            return false;
        }

        private static PixelRect ClampCandidate(PixelPoint anchor, PixelSize size, int gap, PixelRect work, bool above)
        {
            var y = above ? anchor.Y - gap - size.Height : anchor.Y + gap;
            var x = anchor.X - size.Width / 2;
            return ClampRect(new PixelRect(new PixelPoint(x, y), size), work);
        }

        private static PixelRect ClampRect(PixelRect candidate, PixelRect work)
        {
            var x = System.Math.Clamp(candidate.X, work.X, System.Math.Max(work.X, work.Right - candidate.Width));
            var y = System.Math.Clamp(candidate.Y, work.Y, System.Math.Max(work.Y, work.Bottom - candidate.Height));
            return new PixelRect(new PixelPoint(x, y), candidate.Size);
        }

        private static bool Intersects(PixelRect candidate, System.Collections.Generic.IReadOnlyList<PixelRect> occupied)
        {
            foreach (var rect in occupied)
            {
                if (candidate.Intersects(rect)) return true;
            }

            return false;
        }
    }
}
