using System.Linq;
using Xunit;

namespace ClaudeBuddy.Tests;

// What AgentPalette does when the circle genuinely runs out.
//
// The rest of it is covered by the palette cases in tests/TranscriptTests (and
// their mirror in TranscriptSuiteTests) — the pinned hashes, the collision that
// made two agents draw identical orbs, the separation that has to hold across
// the wrap at hue 0. What none of those reach is the last line of Assign, the
// floor its own comment calls "only reachable with hundreds of agents": more
// distinct ids than there are degrees in a circle, where even one degree apart
// is impossible and every id falls back to the hue its own hash asked for.
//
// It matters because the fallback is the only path where two agents may share a
// colour, and it must still hand every one of them *a* colour. Returning a
// partial map — or throwing — would leave orbs with no ring at all, and the
// list this runs over is a gateway's, which nothing here controls the length of.
public class AgentPaletteCrowdTests
{
    [Fact]
    public void MoreAgentsThanThereAreHuesStillGetsEveryOneOfThemAColour()
    {
        // 361: one more than the wheel can hold at one degree apart, which is
        // the smallest input that cannot be spread at any gap.
        var crowd = Enumerable.Range(0, 361).Select(i => $"agent-{i}").ToArray();

        var assigned = AgentPalette.Assign(crowd);

        Assert.Equal(crowd.Length, assigned.Count);
        Assert.All(crowd, id => Assert.Equal(AgentPalette.HexFor(id), assigned[id]));

        // Every value is a real colour rather than an empty string or a null.
        Assert.All(assigned.Values, hex => Assert.Matches("^#[0-9A-F]{6}$", hex));

        // And it is the *derived* colour, not a spread one — which is the whole
        // difference between this path and the one above it. Distinctness is
        // explicitly not promised here; if it happened to hold, the earlier
        // gaps would have succeeded and this line would never have run.
        Assert.True(assigned.Values.Distinct().Count() < crowd.Length);
    }

    [Fact]
    public void ExactlyAsManyAgentsAsThereAreHuesStillGetsOneEach()
    {
        // The boundary on the other side, so the fallback above is known to be
        // the last resort and not something reached an agent early. At 360 the
        // one-degree floor still fits everybody, and every ring is a different
        // colour — cramped and distinct, which is the honest best on offer at
        // that count.
        var crowd = Enumerable.Range(0, 360).Select(i => $"agent-{i}").ToArray();

        var assigned = AgentPalette.Assign(crowd);

        Assert.Equal(crowd.Length, assigned.Count);
        Assert.Equal(crowd.Length, assigned.Values.Distinct().Count());
    }
}
