using ClaudeBuddy;
using Xunit;

namespace ClaudeBuddy.Tests;

public class PlaceholderTests
{
    [Fact]
    public void PublicAppTypeIsReachable()
    {
        // Proves the ProjectReference alone exposes the app's public types.
        var hex = AgentPalette.HexFor("spike-b-placeholder");
        Assert.Matches("^#[0-9A-F]{6}$", hex);
    }

    [Fact]
    public void InternalAppTypeIsReachableViaInternalsVisibleTo()
    {
        // OrbGlyph is `internal static class` (OrbGlyph.cs). Reaching it here
        // proves the InternalsVisibleTo grant in ClaudeBuddy.csproj actually
        // works for this assembly — the mechanism every internal-pure-logic
        // test in the real suites (SessionManager's scan rules, AgentTeam's
        // sanitizers) depends on.
        Assert.Equal("Cb", OrbGlyph.For("claude-buddy", twoLetter: true));
    }
}
