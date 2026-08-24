using System;
using Avalonia;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// TeamLinkGeometry.ArrowOutline: the seven points of one arrow between two orbs.
//
// The shape was previously computed inside TeamLinks' own window class, next to
// the P/Invoke that makes that window click-through, so it could not be asserted
// on without a window server. Nothing about it needs one — it is arithmetic on
// two points and a unit vector — and it is the part of the arrow a user actually
// sees, so it is the part worth testing.
//
// Point order is a single closed figure: the member end's near edge, up to the
// head's base, out to the widest part of the head, the tip, and back down the far
// side. Indices below refer to that walk.
public class ArrowOutlineTests
{
    private const int NearShaftAtMember = 0;
    private const int NearShaftAtHead = 1;
    private const int NearHeadCorner = 2;
    private const int Tip = 3;
    private const int FarHeadCorner = 4;
    private const int FarShaftAtHead = 5;
    private const int FarShaftAtMember = 6;

    private static Point[] Horizontal(double length = 100) =>
        TeamLinkGeometry.ArrowOutline(new Point(0, 0), new Point(length, 0), 1, 0);

    [Fact]
    public void IsAlwaysSevenPoints()
    {
        Assert.Equal(7, Horizontal().Length);
    }

    // The tip is the one point that is exactly where it was asked to be. Every
    // other point is derived from it, so if this drifts the whole arrow does.
    [Fact]
    public void TheTipIsTheEndPointItself()
    {
        var end = new Point(100, 0);
        var outline = TeamLinkGeometry.ArrowOutline(new Point(0, 0), end, 1, 0);

        Assert.Equal(end, outline[Tip]);
    }

    // The shaft tapers: narrower where it leaves the member than where it meets
    // the head. A shaft that did not would read as a stick with a triangle
    // balanced on the end, which is what the taper exists to avoid.
    [Fact]
    public void TheShaftIsNarrowerAtTheMemberThanAtTheHead()
    {
        var outline = Horizontal();

        var atMember = Math.Abs(outline[NearShaftAtMember].Y - outline[FarShaftAtMember].Y);
        var atHead = Math.Abs(outline[NearShaftAtHead].Y - outline[FarShaftAtHead].Y);

        Assert.True(atMember < atHead,
            $"expected the shaft to widen towards the head, got {atMember} then {atHead}");
    }

    // The head has to be wider than the shaft it sits on, or there is no head.
    [Fact]
    public void TheHeadIsWiderThanTheShaft()
    {
        var outline = Horizontal();

        var shaft = Math.Abs(outline[NearShaftAtHead].Y - outline[FarShaftAtHead].Y);
        var head = Math.Abs(outline[NearHeadCorner].Y - outline[FarHeadCorner].Y);

        Assert.True(head > shaft, $"expected head {head} wider than shaft {shaft}");
        Assert.Equal(TeamLinkGeometry.HeadHalfWidth * 2, head, 6);
    }

    // The head's base sits exactly HeadLength back from the tip — the same
    // constant the arrangement uses to decide an arrow has room to be drawn.
    // These used to be two separate declarations of 9.
    [Fact]
    public void TheHeadBaseSitsHeadLengthBackFromTheTip()
    {
        var outline = Horizontal(100);

        Assert.Equal(100 - TeamLinkGeometry.HeadLength, outline[NearHeadCorner].X, 6);
        Assert.Equal(100 - TeamLinkGeometry.HeadLength, outline[FarHeadCorner].X, 6);
    }

    // Symmetry about the centre line, which is what makes the arrow look
    // straight. Asserted on a diagonal because an axis-aligned arrow would pass
    // even if the perpendicular were computed with the wrong sign.
    [Fact]
    public void IsSymmetricAboutTheLineOnADiagonal()
    {
        var start = new Point(10, 10);
        var end = new Point(60, 60);
        var u = 1 / Math.Sqrt(2);
        var outline = TeamLinkGeometry.ArrowOutline(start, end, u, u);

        // Midpoint of each opposing pair should land on the centre line, where
        // x == y for this particular diagonal.
        foreach (var (a, b) in new[]
                 {
                     (NearShaftAtMember, FarShaftAtMember),
                     (NearShaftAtHead, FarShaftAtHead),
                     (NearHeadCorner, FarHeadCorner),
                 })
        {
            var midX = (outline[a].X + outline[b].X) / 2;
            var midY = (outline[a].Y + outline[b].Y) / 2;
            Assert.Equal(midX, midY, 6);
        }
    }

    // Pointing the other way must mirror, not rotate into something else: the
    // near and far edges swap, and nothing changes width.
    [Fact]
    public void ReversingTheDirectionMirrorsTheOutline()
    {
        var forward = TeamLinkGeometry.ArrowOutline(new Point(0, 0), new Point(100, 0), 1, 0);
        var backward = TeamLinkGeometry.ArrowOutline(new Point(100, 0), new Point(0, 0), -1, 0);

        var forwardWidth = Math.Abs(forward[NearHeadCorner].Y - forward[FarHeadCorner].Y);
        var backwardWidth = Math.Abs(backward[NearHeadCorner].Y - backward[FarHeadCorner].Y);

        Assert.Equal(forwardWidth, backwardWidth, 6);
        Assert.Equal(TeamLinkGeometry.HeadLength, backward[NearHeadCorner].X, 6);
    }

    // A vertical arrow is the case that catches a swapped nx/ny: with (0,1) the
    // perpendicular is (-1,0), so the width must appear in x rather than y.
    [Fact]
    public void SpreadsAcrossXWhenPointingStraightDown()
    {
        var outline = TeamLinkGeometry.ArrowOutline(new Point(0, 0), new Point(0, 100), 0, 1);

        Assert.Equal(TeamLinkGeometry.HeadHalfWidth * 2,
            Math.Abs(outline[NearHeadCorner].X - outline[FarHeadCorner].X), 6);
        Assert.Equal(outline[NearHeadCorner].Y, outline[FarHeadCorner].Y, 6);
    }

    // Degenerate but reachable: TeamLinks decides whether an arrow is long
    // enough before calling this, and the guard is on centre distance rather
    // than on this vector, so a zero-length one must still produce a figure
    // rather than throwing.
    [Fact]
    public void DoesNotThrowOnAZeroLengthArrow()
    {
        var outline = TeamLinkGeometry.ArrowOutline(new Point(5, 5), new Point(5, 5), 0, 0);

        Assert.Equal(7, outline.Length);
        Assert.Equal(new Point(5, 5), outline[Tip]);
    }
}
