using Xunit;

namespace ClaudeBuddy.UnitTests;

// OrbArrangement.Resolves: does following this orb's lead, and its lead's lead,
// eventually get out of the team — or does it go round in circles?
//
// It decides which orbs are anchors, and an anchor is what a shape is laid out
// around. Say no when the answer is yes and an orb is drawn as part of a team it
// is not in; say yes when the answer is no and the layout walks a cycle forever.
//
// The 20736-case sweep in tests/ArrangementTests covers this through real
// arrangements, but a sweep can only produce lead tables that a sweep produces.
// These are the shapes it does not: they are asked for directly.
public class OrbArrangementResolvesTests
{
    // The ordinary case. Orb 0 leads to 1, and 1 leads nowhere — so 0 resolves
    // out of the team, and 1 is the anchor it resolves to.
    [Fact]
    public void AnOrbWhoseChainLeavesTheTeamResolves()
    {
        var leadOf = new[] { 1, -1 };

        Assert.True(OrbArrangement.Resolves(0, leadOf, 2));
    }

    // The end of a chain does not resolve — it IS the anchor, and an anchor that
    // claimed to resolve would leave the arrangement with nothing to lay out
    // around.
    [Fact]
    public void TheEndOfAChainDoesNotResolve()
    {
        var leadOf = new[] { 1, -1 };

        Assert.False(OrbArrangement.Resolves(1, leadOf, 2));
    }

    // An orb leading itself is its own anchor rather than an infinite walk.
    [Fact]
    public void AnOrbLeadingItselfDoesNotResolve()
    {
        Assert.False(OrbArrangement.Resolves(0, new[] { 0 }, 1));
    }

    // A two-orb cycle: each leads the other, and neither gets out.
    [Fact]
    public void ACycleDoesNotResolve()
    {
        var leadOf = new[] { 1, 0 };

        Assert.False(OrbArrangement.Resolves(0, leadOf, 2));
        Assert.False(OrbArrangement.Resolves(1, leadOf, 2));
    }

    // The shape the sweep never makes, and the only one that reaches the hop
    // budget: a tail running into a cycle it is not part of. Starting at 0 the
    // walk goes 0 → 1 → 2 → 3 → 2 → 3 … It never comes back to where it began,
    // so the "cycled back to where we began" check never fires, and it never
    // leaves the range either. Only running out of hops ends it.
    //
    // Without that budget this would spin forever on the UI thread while
    // arranging — which is the failure it exists to prevent.
    [Fact]
    public void AChainRunningIntoACycleItIsNotPartOfDoesNotResolve()
    {
        var leadOf = new[] { 1, 2, 3, 2 };

        Assert.False(OrbArrangement.Resolves(0, leadOf, 4));
    }

    // A lead pointing past the end of the list is out of range, which counts as
    // leaving the team — so an orb whose lead has since gone away still lays out
    // rather than disappearing.
    [Fact]
    public void ALeadPointingPastTheEndCountsAsLeavingTheTeam()
    {
        Assert.True(OrbArrangement.Resolves(0, new[] { 1, 99 }, 2));
    }

    // A lead table shorter than the orb count is treated as "no lead" for the
    // orbs it does not cover, rather than reading off the end of the array.
    [Fact]
    public void AnIndexBeyondTheLeadTableIsTreatedAsNoLead()
    {
        Assert.False(OrbArrangement.Resolves(3, new[] { -1 }, 4));
    }

    [Fact]
    public void AnEmptyLeadTableResolvesNothing()
    {
        Assert.False(OrbArrangement.Resolves(0, System.Array.Empty<int>(), 1));
    }
}
