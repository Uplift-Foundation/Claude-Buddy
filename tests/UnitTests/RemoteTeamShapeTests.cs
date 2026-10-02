using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-223: agent-team shape over the direct link, one rule at a time — which
// snapshot statuses are members and whose, which members a roster offers,
// what each entry says, how it reads on the wire to an older Buddy, and what
// the near side makes of it. The exchange itself, through a real server and
// client, is in tests/IntegrationTests/MirrorRoundTripTests; the orbs and
// their links are in tests/UiTests/RemoteAgentToOrbTests.
public class RemoteTeamShapeTests
{
    private static SessionStatus Claude(int pid, string lead = "") =>
        new() { Source = SessionSource.ClaudeCode, SessionPid = pid, Lead = lead, Title = "backlog" };

    private static string Route(string id) => RemoteMirrorServer.RouteFor(id);

    // --- the snapshot's team read (ApplyTeams) ---------------------------------

    [Fact]
    public void AMemberGetsItsLeadNameAndColour()
    {
        var member = Claude(11);
        SessionManager.ApplyTeams(new[] { ("m", member) },
            new Dictionary<int, AgentTeam.Membership> { [11] = new("lead-id", "blue", "wren") });

        Assert.Equal("lead-id", member.Lead);
        Assert.Equal("wren", member.Agent);
        Assert.Equal("blue", member.AgentColor);
    }

    // A process whose lead is itself is the lead, exactly as the live scan reads it.
    [Fact]
    public void ALeadIsNotItsOwnMember()
    {
        var lead = Claude(10);
        SessionManager.ApplyTeams(new[] { ("lead-id", lead) },
            new Dictionary<int, AgentTeam.Membership> { [10] = new("lead-id", "red", "boss") });

        Assert.Equal("", lead.Lead);
        Assert.Equal("", lead.Agent);
        Assert.Equal("", lead.AgentColor);
    }

    [Fact]
    public void ASessionTheTeamReadDoesNotKnowIsInNoTeam()
    {
        var solo = Claude(12, lead: "stale");
        SessionManager.ApplyTeams(new[] { ("s", solo) }, new Dictionary<int, AgentTeam.Membership>());

        Assert.Equal("", solo.Lead);
        Assert.Equal("", solo.Agent);
    }

    // A gateway session uses Lead for its room; the team read must not erase it.
    [Fact]
    public void ANonClaudeSessionKeepsWhateverLeadItHad()
    {
        var room = new SessionStatus { Source = SessionSource.OpenClaw, SessionPid = 13, Lead = "room:abc" };
        SessionManager.ApplyTeams(new[] { ("o", room) },
            new Dictionary<int, AgentTeam.Membership> { [13] = new("lead-id", "blue", "x") });

        Assert.Equal("room:abc", room.Lead);
    }

    // --- which members a roster offers (TeamMembersToOffer) ---------------------

    private static readonly Func<SessionStatus, bool> Everyone = _ => true;

    [Fact]
    public void MembersOfAnOfferedLeadAreOffered()
    {
        var sessions = new List<(string, SessionStatus)>
        {
            ("lead", Claude(1)), ("a", Claude(2, "lead")), ("b", Claude(3, "lead")),
        };

        var added = RemoteMirrorServer.TeamMembersToOffer(new[] { Route("lead") }, sessions, Everyone);

        Assert.Equal(new[] { "a", "b" }, added.Select(s => s.SessionId));
    }

    [Fact]
    public void MembersOfALeadThatIsNotOfferedAreNot()
    {
        var sessions = new List<(string, SessionStatus)> { ("lead", Claude(1)), ("a", Claude(2, "lead")) };

        Assert.Empty(RemoteMirrorServer.TeamMembersToOffer(Array.Empty<string>(), sessions, Everyone));
    }

    // A member that leads a team of its own makes its members eligible — in a
    // later round, whatever order the sessions are listed in.
    [Fact]
    public void ANestedTeamIsOfferedToItsDepthWhateverTheOrder()
    {
        var sessions = new List<(string, SessionStatus)>
        {
            ("grandchild", Claude(3, "child")), ("child", Claude(2, "lead")), ("lead", Claude(1)),
        };

        var added = RemoteMirrorServer.TeamMembersToOffer(new[] { Route("lead") }, sessions, Everyone);

        Assert.Equal(new[] { "child", "grandchild" }, added.Select(s => s.SessionId));
    }

    // A lead cycle offers nothing neither half already had, and terminates.
    [Fact]
    public void ALeadCycleNobodyOfferedAddsNothing()
    {
        var sessions = new List<(string, SessionStatus)> { ("x", Claude(1, "y")), ("y", Claude(2, "x")) };

        Assert.Empty(RemoteMirrorServer.TeamMembersToOffer(Array.Empty<string>(), sessions, Everyone));
    }

    [Fact]
    public void ALeadCycleWithOneHalfOfferedAddsTheOtherOnce()
    {
        var sessions = new List<(string, SessionStatus)> { ("x", Claude(1, "y")), ("y", Claude(2, "x")) };

        var added = RemoteMirrorServer.TeamMembersToOffer(new[] { Route("x") }, sessions, Everyone);

        Assert.Equal(new[] { "y" }, added.Select(s => s.SessionId));
    }

    // An ineligible member (abandoned) is not offered, and cannot sponsor
    // members of its own.
    [Fact]
    public void AnIneligibleMemberSponsorsNobody()
    {
        var sessions = new List<(string, SessionStatus)>
        {
            ("lead", Claude(1)), ("stale", Claude(2, "lead")), ("under", Claude(3, "stale")),
        };

        var added = RemoteMirrorServer.TeamMembersToOffer(new[] { Route("lead") }, sessions,
            s => s.SessionPid != 2);

        Assert.Empty(added);
    }

    [Fact]
    public void AnAlreadyOfferedMemberIsNotAddedTwice()
    {
        var sessions = new List<(string, SessionStatus)> { ("lead", Claude(1)), ("a", Claude(2, "lead")) };

        Assert.Empty(RemoteMirrorServer.TeamMembersToOffer(new[] { Route("lead"), Route("a") }, sessions, Everyone));
    }

    // Agent teams are Claude Code's; a Codex session with a Lead is not one.
    [Fact]
    public void OnlyClaudeCodeSessionsAreTeamMembers()
    {
        var codex = new SessionStatus { Source = SessionSource.Codex, SessionPid = 2, Lead = "lead" };
        var sessions = new List<(string, SessionStatus)> { ("lead", Claude(1)), ("c", codex) };

        Assert.Empty(RemoteMirrorServer.TeamMembersToOffer(new[] { Route("lead") }, sessions, Everyone));
    }

    // --- what each entry says (WithTeamFields) -----------------------------------

    private static MirrorProtocol.MirrorRosterEntry Entry(string id) =>
        new("backlog", MirrorProtocol.CliClaudeCode, true, true, Route: Route(id));

    [Fact]
    public void AMemberEntryCarriesItsLeadsRouteNameAndColour()
    {
        var member = Claude(2, "lead");
        member.Agent = "wren";
        member.AgentColor = "blue";
        var sessions = new List<(string, SessionStatus)> { ("lead", Claude(1)), ("a", member) };

        var entries = RemoteMirrorServer.WithTeamFields(new List<MirrorProtocol.MirrorRosterEntry>
            { Entry("lead"), Entry("a") }, sessions);

        Assert.Null(entries[0].Lead);
        Assert.Equal(Route("lead"), entries[1].Lead);
        Assert.Equal("wren", entries[1].Agent);
        Assert.Equal("blue", entries[1].AgentColor);
    }

    // Never point a near machine at an orb it was not offered.
    [Fact]
    public void AMemberWhoseLeadIsNotInTheRosterSaysNothingAboutATeam()
    {
        var sessions = new List<(string, SessionStatus)> { ("a", Claude(2, "lead")) };
        var original = Entry("a");

        var entries = RemoteMirrorServer.WithTeamFields(new List<MirrorProtocol.MirrorRosterEntry> { original }, sessions);

        Assert.Same(original, entries[0]);
    }

    [Fact]
    public void BlankNameAndColourTravelAsAbsent()
    {
        var sessions = new List<(string, SessionStatus)> { ("lead", Claude(1)), ("a", Claude(2, "lead")) };

        var member = RemoteMirrorServer.WithTeamFields(new List<MirrorProtocol.MirrorRosterEntry>
            { Entry("lead"), Entry("a") }, sessions)[1];

        Assert.Equal(Route("lead"), member.Lead);
        Assert.Null(member.Agent);
        Assert.Null(member.AgentColor);
    }

    // Entries with no route, a route no session answers to, a non-Claude
    // source or a self-lead are all returned as they came.
    [Fact]
    public void EntriesThatAreNotTeamMembersAreReturnedUnchanged()
    {
        var self = Claude(5, "self");
        var codex = new SessionStatus { Source = SessionSource.Codex, Lead = "lead" };
        var sessions = new List<(string, SessionStatus)> { ("lead", Claude(1)), ("self", self), ("c", codex) };
        var noRoute = new MirrorProtocol.MirrorRosterEntry("legacy", MirrorProtocol.CliClaudeCode, true, true);
        var unknown = Entry("nobody");
        var selfEntry = Entry("self");
        var codexEntry = Entry("c");

        var entries = RemoteMirrorServer.WithTeamFields(new List<MirrorProtocol.MirrorRosterEntry>
            { Entry("lead"), noRoute, unknown, selfEntry, codexEntry }, sessions);

        Assert.Same(noRoute, entries[1]);
        Assert.Same(unknown, entries[2]);
        Assert.Same(selfEntry, entries[3]);
        Assert.Same(codexEntry, entries[4]);
    }

    // --- the wire, to an older Buddy and from one --------------------------------

    // An older Buddy's roster has none of the fields and still parses: no team.
    [Fact]
    public void AnOlderBuddysEntryReadsAsNoTeam()
    {
        var entry = JsonSerializer.Deserialize<MirrorProtocol.MirrorRosterEntry>(
            """{"name":"backlog","cli":"claude","transcript":true,"pane":true,"route":"sid:a"}""")!;

        Assert.Null(entry.Lead);
        Assert.Null(entry.Agent);
        Assert.Null(entry.AgentColor);
    }

    // An entry with no team serialises exactly as it did before the fields
    // existed, so its CB-216 hash does not move; an entry with one does.
    [Fact]
    public void TheHashMovesOnlyWhenATeamDoes()
    {
        var plain = new List<MirrorProtocol.MirrorRosterEntry> { Entry("a") };
        var json = System.Text.Encoding.UTF8.GetString(MirrorProtocol.RosterBytes(plain));
        Assert.DoesNotContain("lead", json);

        var teamed = new List<MirrorProtocol.MirrorRosterEntry> { Entry("a") with { Lead = Route("lead"), Agent = "w" } };
        Assert.NotEqual(MirrorProtocol.Hash(MirrorProtocol.RosterBytes(plain)),
            MirrorProtocol.Hash(MirrorProtocol.RosterBytes(teamed)));

        var round = JsonSerializer.Deserialize<List<MirrorProtocol.MirrorRosterEntry>>(MirrorProtocol.RosterBytes(teamed))!;
        Assert.Equal(Route("lead"), round[0].Lead);
        Assert.Equal("w", round[0].Agent);
    }

    // --- the near side (RemotesFromRoster, LeadKey) ---------------------------------

    [Fact]
    public void ANearSideMembersLeadKeyIsTheLeadOrbsOwnKey()
    {
        var known = new List<(string, MirrorProtocol.MirrorRosterEntry)>
        {
            ("far", Entry("lead")),
            ("far", Entry("a") with { Lead = Route("lead"), Agent = "wren", AgentColor = "blue" }),
        };

        var remotes = RemoteControlSessions.RemotesFromRoster("acct", known, DateTime.UtcNow);

        Assert.Null(remotes[0].LeadKey);
        Assert.Equal(remotes[0].Key, remotes[1].LeadKey);
        Assert.Equal("wren", remotes[1].Agent);
        Assert.Equal("blue", remotes[1].AgentColor);
    }

    [Fact]
    public void BlankTeamFieldsArriveAsNoTeam()
    {
        var known = new List<(string, MirrorProtocol.MirrorRosterEntry)>
        {
            ("far", Entry("a") with { Lead = " ", Agent = "", AgentColor = " " }),
        };

        var remote = Assert.Single(RemoteControlSessions.RemotesFromRoster("acct", known, DateTime.UtcNow));

        Assert.Null(remote.LeadRoute);
        Assert.Null(remote.LeadKey);
        Assert.Null(remote.Agent);
        Assert.Null(remote.AgentColor);
    }
}
