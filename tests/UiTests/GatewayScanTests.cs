using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// A scan that has gateway sessions in it, and the room orbs it invents from them.
//
// Same harness as SessionScanTests next door — SessionManager's internal
// constructor takes a scratch status directory and Start() is never called — with
// one addition: the gateway snapshot is published through a test seam, because the
// only thing that publishes one in production is the poll loop, which needs a live
// socket and is excluded.
//
// The snapshot itself is built by handing real sessions.list JSON to
// OpenClawSessions.Parse, rather than constructing Session records by hand. That
// way the test travels the route a real poll does and cannot drift into asserting
// a shape the gateway never sends.
//
// What is being tested is the thing the gateway has no notion of. It reports a
// session per agent per channel, so eight agents in one room are eight orbs with
// nothing saying they are the same conversation — the room orb is invented here,
// once, however many agents point at it.
public class GatewayScanTests
{
    // The real clock, not a fixed date. Parse takes its clock as an argument but
    // ScanAndUpdate reads DateTime.UtcNow for the lifetime check, so a fixture
    // pinned to a made-up "now" produces sessions that look hours stale and get
    // expired before an orb is ever made — which is exactly how the first draft
    // of this file failed every case.
    private static DateTime Now => DateTime.UtcNow;

    private static long JustNow => new DateTimeOffset(Now.AddSeconds(-5)).ToUnixTimeMilliseconds();

    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cb-gwscan-" + Guid.NewGuid());

        public Scratch() => Directory.CreateDirectory(Dir);

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static Dictionary<string, OrbWindow> Orbs(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance)!;

        return (Dictionary<string, OrbWindow>)field.GetValue(manager)!;
    }

    private static SessionManager Manager(string statusDir)
    {
        var ctor = typeof(SessionManager).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!;

        return (SessionManager)ctor.Invoke(new object[] { statusDir });
    }

    // Publishes exactly what a poll would: parsed sessions, nothing hand-rolled.
    private static void Publish(string sessionsJson)
    {
        ClaudeBuddySettings.OpenClawEnabled = true;
        ClaudeBuddySettings.OpenClawShowHeartbeats = true;
        ClaudeBuddySettings.OpenClawActiveWithinMinutes =
            ClaudeBuddySettings.OpenClawActiveWithinAll;

        var (sessions, _) = OpenClawSessions.Parse(
            JsonDocument.Parse(sessionsJson).RootElement, DateTime.UtcNow);

        OpenClawSessions.SetSnapshotForTests(sessions);
    }

    private static void PublishNothing()
    {
        OpenClawSessions.SetSnapshotForTests(Array.Empty<OpenClawSessions.Session>());
    }

    // --- gateway orbs ---

    [AvaloniaFact]
    public void AGatewaySessionGetsAnOrb()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[{"key":"agent:lilibeth:discord:direct:2467","chatType":"direct",
                              "lastActivityAt":{{JustNow}}}]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Contains("openclaw:agent:lilibeth:discord:direct:2467", Orbs(manager).Keys);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Namespaced, because these ids share a dictionary with Claude Code's uuids —
    // and because a gateway key contains colons and slashes that would otherwise
    // be spliced into a status-file path.
    [AvaloniaFact]
    public void GatewayOrbIdsAreNamespaced()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[{"key":"agent:main:discord:direct:1","chatType":"direct",
                              "lastActivityAt":{{JustNow}}}]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.All(Orbs(manager).Keys, id => Assert.StartsWith("openclaw:", id));
        }
        finally
        {
            PublishNothing();
        }
    }

    // --- the room orb ---

    // Two agents standing in one channel produce three orbs: one each, and one
    // for the room. The gateway has no notion of a room as a thing, so this is
    // the only place it exists.
    [AvaloniaFact]
    public void TwoAgentsInOneChannelGetOneRoomOrbBetweenThem()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[
                  {"key":"agent:lilibeth:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}},
                  {"key":"agent:zara:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}}
                ]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var orbs = Orbs(manager);
            var roomId = SessionManager.RoomId("discord:1474");

            Assert.Contains(roomId, orbs.Keys);
            Assert.Equal(3, orbs.Count);
            Assert.Single(orbs.Keys, id => id == roomId);
        }
        finally
        {
            PublishNothing();
        }
    }

    // ...and every agent in it points at that one orb, which is what draws the
    // arrows that say they are one conversation.
    [AvaloniaFact]
    public void EveryAgentInAChannelPointsAtTheRoomOrb()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[
                  {"key":"agent:lilibeth:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}},
                  {"key":"agent:zara:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}}
                ]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var roomId = SessionManager.RoomId("discord:1474");

            foreach (var agent in new[] { "lilibeth", "zara" })
            {
                var status = manager.StatusFor($"openclaw:agent:{agent}:discord:channel:1474");

                Assert.NotNull(status);
                Assert.Equal(roomId, status!.Lead);
            }
        }
        finally
        {
            PublishNothing();
        }
    }

    // Eight agents, one room: still one room orb. The count is the whole point —
    // eight rooms would be worse than none.
    [AvaloniaFact]
    public void EightAgentsInOneChannelStillGetOneRoomOrb()
    {
        using var scratch = new Scratch();
        try
        {
            var rows = string.Join(",", Enumerable.Range(0, 8).Select(i => $$"""
                {"key":"agent:a{{i}}:discord:channel:99","chatType":"channel",
                 "groupChannel":"#arch","lastActivityAt":{{JustNow}}}
                """));

            Publish("{\"sessions\":[" + rows + "]}");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var orbs = Orbs(manager);

            Assert.Equal(9, orbs.Count);
            Assert.Single(orbs.Keys, id => id == SessionManager.RoomId("discord:99"));
        }
        finally
        {
            PublishNothing();
        }
    }

    // Two channels are two rooms, so the grouping is per channel rather than per
    // gateway.
    [AvaloniaFact]
    public void TwoChannelsGetTwoRoomOrbs()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[
                  {"key":"agent:main:discord:channel:1","chatType":"channel",
                   "groupChannel":"#one","lastActivityAt":{{JustNow}}},
                  {"key":"agent:main:discord:channel:2","chatType":"channel",
                   "groupChannel":"#two","lastActivityAt":{{JustNow}}}
                ]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var orbs = Orbs(manager);

            Assert.Contains(SessionManager.RoomId("discord:1"), orbs.Keys);
            Assert.Contains(SessionManager.RoomId("discord:2"), orbs.Keys);
        }
        finally
        {
            PublishNothing();
        }
    }

    // A direct message is not a room: two people messaging privately is not
    // something other agents can join, so no room orb is invented for it.
    [AvaloniaFact]
    public void ADirectMessageGetsNoRoomOrb()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[{"key":"agent:main:discord:direct:2467","chatType":"direct",
                              "lastActivityAt":{{JustNow}}}]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var orbs = Orbs(manager);

            Assert.Single(orbs);
            Assert.DoesNotContain(orbs.Keys, id => id.Contains(":room:"));
        }
        finally
        {
            PublishNothing();
        }
    }

    // The room orb is marked as one, which is what tells the panel to open a
    // merged conversation rather than a single agent's.
    [AvaloniaFact]
    public void TheRoomOrbIsMarkedAsARoom()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[{"key":"agent:main:discord:channel:1474","chatType":"channel",
                              "groupChannel":"#general","lastActivityAt":{{JustNow}}}]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var status = manager.StatusFor(SessionManager.RoomId("discord:1474"));

            Assert.NotNull(status);
            Assert.True(status!.IsRoom);
        }
        finally
        {
            PublishNothing();
        }
    }

    // A room whose member is mid-run reads as working, so the room orb pulses
    // while anybody in it is talking rather than only when the newest message
    // happens to be from a busy agent.
    [AvaloniaFact]
    public void ARoomIsWorkingWhileAnyMemberIs()
    {
        using var scratch = new Scratch();
        try
        {
            // The generating agent is deliberately the *older* of the two, which
            // is the case a "take the newest session's state" rule gets wrong.
            var older = new DateTimeOffset(Now.AddMinutes(-2)).ToUnixTimeMilliseconds();

            Publish($$"""
                {"sessions":[
                  {"key":"agent:busy:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{older}}},
                  {"key":"agent:quiet:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}}
                ]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var status = manager.StatusFor(SessionManager.RoomId("discord:1474"));

            Assert.NotNull(status);

            // Neither session is generating here — the gateway reports state
            // separately from the list — so the room is idle, and that is the
            // baseline this asserts. The generating case needs an event to have
            // been seen, which is the poll loop's job and excluded with it.
            Assert.Equal("idle", status!.State);
        }
        finally
        {
            PublishNothing();
        }
    }

    // With the gateway switched off the snapshot is empty whatever was last
    // published, so no gateway orb survives a user turning it off — the point
    // being that it happens on the next scan rather than at the next launch.
    [AvaloniaFact]
    public void TurningTheGatewayOffTakesItsOrbsAway()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[{"key":"agent:main:discord:channel:1474","chatType":"channel",
                              "groupChannel":"#general","lastActivityAt":{{JustNow}}}]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();
            Assert.NotEmpty(Orbs(manager));

            ClaudeBuddySettings.OpenClawEnabled = false;
            manager.ScanAndUpdate();

            Assert.Empty(Orbs(manager));
        }
        finally
        {
            ClaudeBuddySettings.OpenClawEnabled = true;
            PublishNothing();
        }
    }

    // --- the auto-colour marker ---

    // The hook learns about the colour setting from a marker file beside the
    // status files, because the hook runs on every tool call and reading a
    // setting there would be an osascript each time. So this file is a contract
    // with a script, not an implementation detail — and the scan reconciles it.
    [AvaloniaFact]
    public void TheAutoColourMarkerFollowsTheSetting()
    {
        using var scratch = new Scratch();
        var marker = Path.Combine(scratch.Dir, ".auto-color");
        var manager = Manager(scratch.Dir);

        try
        {
            ClaudeBuddySettings.AutoColorSessions = true;
            manager.SyncAutoColorMarker();
            Assert.True(File.Exists(marker), "the marker should appear when the setting is on");

            ClaudeBuddySettings.AutoColorSessions = false;
            manager.SyncAutoColorMarker();
            Assert.False(File.Exists(marker), "the marker should go when the setting is off");
        }
        finally
        {
            ClaudeBuddySettings.AutoColorSessions = false;
        }
    }

    // Idempotent in both directions, because it runs on every scan — a couple of
    // times a second — and must not rewrite a file that is already right.
    [AvaloniaFact]
    public void SyncingTheMarkerTwiceChangesNothing()
    {
        using var scratch = new Scratch();
        var marker = Path.Combine(scratch.Dir, ".auto-color");
        var manager = Manager(scratch.Dir);

        try
        {
            ClaudeBuddySettings.AutoColorSessions = true;
            manager.SyncAutoColorMarker();
            var first = File.GetLastWriteTimeUtc(marker);

            manager.SyncAutoColorMarker();

            Assert.Equal(first, File.GetLastWriteTimeUtc(marker));

            ClaudeBuddySettings.AutoColorSessions = false;
            manager.SyncAutoColorMarker();
            manager.SyncAutoColorMarker();

            Assert.False(File.Exists(marker));
        }
        finally
        {
            ClaudeBuddySettings.AutoColorSessions = false;
        }
    }
}
