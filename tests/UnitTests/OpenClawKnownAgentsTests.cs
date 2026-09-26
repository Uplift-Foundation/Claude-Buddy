using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// OpenClawSessions.KnownAgents(): the list CB-168's new-chat dialog reads to
// build its OpenClaw agent picker. Sourced from the same table LoadAgentNamesAsync
// fills from agents.list (a live call, excluded from coverage), so this is
// tested through the SetIdentitiesForTests seam the rest of this file's
// siblings already use rather than a real connection.
//
// [Collection("Settings")] because AgentNames/Identities are process-wide
// statics — see OpenClawPeerIdentityTests, which serializes against the same
// tables for the same reason.
[Collection("Settings")]
public class OpenClawKnownAgentsTests : System.IDisposable
{
    // Cleared after every test in this file, not just at the start of each —
    // AgentNames/Identities are process-wide statics, and a test in another
    // file that never calls SetIdentitiesForTests at all should not see
    // whichever fixture ran here last.
    public void Dispose() =>
        OpenClawSessions.SetIdentitiesForTests(
            new Dictionary<string, OpenClawSessions.AgentIdentity>(),
            new Dictionary<string, string>());

    [Fact]
    public void WithNothingLoadedTheListIsEmpty()
    {
        OpenClawSessions.SetIdentitiesForTests(
            new Dictionary<string, OpenClawSessions.AgentIdentity>(),
            new Dictionary<string, string>());

        Assert.Empty(OpenClawSessions.KnownAgents());
    }

    // Names sort ahead of ids: a picker ordered by "ea-hope, kubernetes, main"
    // reads as arbitrary next to "Alexis, Amber, Hope, Lilibeth" sorted by
    // what the agents are actually called.
    [Fact]
    public void AgentsAreSortedByNameNotById()
    {
        OpenClawSessions.SetIdentitiesForTests(
            new Dictionary<string, OpenClawSessions.AgentIdentity>
            {
                ["kubernetes"] = new("Amber", null, null),
                ["main"] = new("Lilibeth", null, null),
                ["ea-hope"] = new("Hope", null, null),
            },
            new Dictionary<string, string>
            {
                ["kubernetes"] = "Amber",
                ["main"] = "Lilibeth",
                ["ea-hope"] = "Hope",
            });

        var agents = OpenClawSessions.KnownAgents();

        Assert.Collection(agents,
            a => Assert.Equal(("kubernetes", "Amber"), a),
            a => Assert.Equal(("ea-hope", "Hope"), a),
            a => Assert.Equal(("main", "Lilibeth"), a));
    }

    [Fact]
    public void SortIsCaseInsensitive()
    {
        OpenClawSessions.SetIdentitiesForTests(
            new Dictionary<string, OpenClawSessions.AgentIdentity>
            {
                ["a"] = new("bravo", null, null),
                ["b"] = new("Alpha", null, null),
            },
            new Dictionary<string, string>
            {
                ["a"] = "bravo",
                ["b"] = "Alpha",
            });

        var agents = OpenClawSessions.KnownAgents();

        Assert.Collection(agents,
            a => Assert.Equal("Alpha", a.Name),
            a => Assert.Equal("bravo", a.Name));
    }
}
