using Xunit;

namespace ClaudeBuddy.Tests;

// Covers AgentRoster — turning `claude agents --json` into the name → session id
// join a mirror request depends on.
//
// The fixture is real captured output, per this repo's fixture rule: it is the
// literal first rows of `claude agents --json` run on this machine on
// 23 Aug 2026, with paths left as they came. Written from a capture rather than
// from memory for exactly the reason the rule exists — the shape here decides
// which session's private conversation gets shown on which panel, and a field
// name guessed wrong would either break the join outright or, far worse, match
// the wrong thing.
public class AgentRosterTests
{
    private const string RealAgentsJson = """
        [
          {
            "pid": 77492,
            "cwd": "/Users/warrenthompson/Documents/GTD/Evidence",
            "kind": "interactive",
            "startedAt": 1787191603916,
            "sessionId": "bd79c1fb-a5a9-4691-90e3-45b927c44c4e",
            "name": "job-lawyer",
            "status": "idle"
          },
          {
            "pid": 24612,
            "cwd": "/Users/warrenthompson/Documents/GTD/Evidence",
            "kind": "interactive",
            "startedAt": 1787240502135,
            "sessionId": "24dea509-cad2-4d11-95f5-e906132af56b",
            "name": "evidence",
            "status": "idle"
          },
          {
            "pid": 44875,
            "cwd": "/Users/warrenthompson/Source/Placement",
            "kind": "interactive",
            "startedAt": 1787243557951,
            "sessionId": "e9e9bc74-f7b4-4a79-9c9d-189cc5aa0898",
            "name": "placement-41",
            "status": "idle"
          }
        ]
        """;

    [Fact]
    public void ReadsEveryRegistrationInTheCapture()
    {
        var entries = AgentRoster.ParseAgentsJson(RealAgentsJson);

        Assert.Equal(3, entries.Count);

        Assert.Equal("job-lawyer", entries[0].Name);
        Assert.Equal("bd79c1fb-a5a9-4691-90e3-45b927c44c4e", entries[0].SessionId);
        Assert.Equal(77492, entries[0].Pid);

        Assert.Equal("placement-41", entries[2].Name);
        Assert.Equal(44875, entries[2].Pid);
    }

    // The names Claude Code registers are not the titles Buddy shows — these are
    // cwd-derived — which is the entire reason this parser exists rather than
    // matching a peer name against a status file's Title.
    [Fact]
    public void TheRegisteredNameIsTheOneAPeerRowWouldCarry()
    {
        var entries = AgentRoster.ParseAgentsJson(RealAgentsJson);

        Assert.Equal(
            new[] { "job-lawyer", "evidence", "placement-41" },
            entries.Select(e => e.Name));
    }

    [Fact]
    public void ARowMissingWhatTheJoinNeedsIsDropped()
    {
        const string json = """
            [
              {"pid": 1, "name": "no-session-id"},
              {"pid": 2, "sessionId": "abc"},
              {"pid": 3, "name": "", "sessionId": "def"},
              {"pid": 4, "name": "good", "sessionId": "ghi"}
            ]
            """;

        var entry = Assert.Single(AgentRoster.ParseAgentsJson(json));

        Assert.Equal("good", entry.Name);
        Assert.Equal("ghi", entry.SessionId);
    }

    [Fact]
    public void ARowWithNoPidStillJoinsBySessionId()
    {
        var entry = Assert.Single(AgentRoster.ParseAgentsJson(
            """[{"name": "quiet", "sessionId": "abc"}]"""));

        Assert.Equal(0, entry.Pid);
        Assert.Equal("abc", entry.SessionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"not\":\"an array\"}")]
    [InlineData("[1, 2, 3]")]
    [InlineData("[")]
    public void AnythingUnreadableIsNoRegistrationsRatherThanAThrow(string json) =>
        Assert.Empty(AgentRoster.ParseAgentsJson(json));

    [Fact]
    public void NothingAtAllIsNoRegistrations() => Assert.Empty(AgentRoster.ParseAgentsJson(null));

    // --- resolving -------------------------------------------------------------

    [Fact]
    public void ANameResolvesToItsOwnRegistration()
    {
        var entries = AgentRoster.ParseAgentsJson(RealAgentsJson);

        var found = AgentRoster.Resolve(entries, "evidence");

        Assert.NotNull(found);
        Assert.Equal("24dea509-cad2-4d11-95f5-e906132af56b", found!.Value.SessionId);
    }

    // The peer list's casing is upstream's to change, and losing a mirror over a
    // capital letter would be a poor trade — the same tolerance the inbound
    // message matcher already has.
    [Fact]
    public void ResolvingIgnoresCase()
    {
        var entries = AgentRoster.ParseAgentsJson(RealAgentsJson);

        Assert.NotNull(AgentRoster.Resolve(entries, "EVIDENCE"));
    }

    [Fact]
    public void ANameNobodyRegisteredResolvesToNothing()
    {
        var entries = AgentRoster.ParseAgentsJson(RealAgentsJson);

        Assert.Null(AgentRoster.Resolve(entries, "job-hunter"));
    }

    // The case worth being strict about, and the reason Resolve is a method
    // rather than a dictionary lookup.
    //
    // Two sessions in one account can absolutely share a name — the same person
    // working in two checkouts of one repository gets it without trying — and a
    // peer row carries nothing that tells them apart. Picking either would be
    // right half the time, and the failure is silent and private: somebody's
    // other conversation mirrored onto the wrong panel. Refusing turns into "no
    // live view", which is a thing the person can see and understand.
    [Fact]
    public void AnAmbiguousNameRefusesRatherThanGuessing()
    {
        const string json = """
            [
              {"pid": 1, "name": "buddy", "sessionId": "one"},
              {"pid": 2, "name": "buddy", "sessionId": "two"}
            ]
            """;

        Assert.Null(AgentRoster.Resolve(AgentRoster.ParseAgentsJson(json), "buddy"));
    }

    [Fact]
    public void AnAmbiguousNameRefusesEvenWhenTheCasingDiffers()
    {
        const string json = """
            [
              {"pid": 1, "name": "buddy", "sessionId": "one"},
              {"pid": 2, "name": "BUDDY", "sessionId": "two"}
            ]
            """;

        Assert.Null(AgentRoster.Resolve(AgentRoster.ParseAgentsJson(json), "buddy"));
    }

    [Fact]
    public void ResolvingAgainstNothingIsNothing() =>
        Assert.Null(AgentRoster.Resolve(Array.Empty<AgentRoster.Entry>(), "anything"));
}
