using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests
{
    // How a gateway orb learns that its session is working.
    //
    // The gateway's session list carries no running state — that was measured
    // against a live gateway and is why this mechanism exists at all. An orb
    // pulses because an *event* named its session, and OnEvent is what records
    // that. The effect is observable through Parse, which reads back what it
    // recorded, so these tests drive the real pair rather than inspecting a
    // dictionary.
    //
    // Session keys are unique per case on purpose: the record of what is running
    // is process-wide and deliberately outlives any one poll, so two cases
    // sharing a key would answer each other's question.
    [Collection("Settings")]
    public class OpenClawEventStateTests
    {
        private static string Key() => $"agent:a{Guid.NewGuid():N}:discord:channel:1";

        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

        private static void Fire(string name, string sessionKey, string? action = null)
        {
            var actionPart = action is null
                ? ""
                : $",\"action\":{JsonSerializer.Serialize(action)}";

            OpenClawSessions.OnEvent(
                name,
                Json($"{{\"sessionKey\":{JsonSerializer.Serialize(sessionKey)}{actionPart}}}"));
        }

        // A listing that reports the session as ancient. Anything that comes back
        // "generating" or survives the recency filter did so because of an event,
        // not because of what the gateway said.
        private static OpenClawSessions.Session? Listed(string key, int minutesAgo = 1)
        {
            ClaudeBuddySettings.OpenClawEnabled = true;
            ClaudeBuddySettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
            ClaudeBuddySettings.OpenClawActiveWithinMinutes =
                ClaudeBuddySettings.OpenClawActiveWithinAll;

            var at = new DateTimeOffset(DateTime.UtcNow.AddMinutes(-minutesAgo)).ToUnixTimeMilliseconds();
            var json = "{\"sessions\":[{\"key\":" + JsonSerializer.Serialize(key)
                       + ",\"chatType\":\"channel\",\"lastActivityAt\":" + at + "}]}";

            var (sessions, _) = OpenClawSessions.Parse(Json(json), DateTime.UtcNow);

            return sessions.FirstOrDefault(s => s.Key == key);
        }

        // --- an event means working ---

        [Fact]
        public void AnEventNamingASessionMarksItGenerating()
        {
            var key = Key();

            Fire("agent", key);

            Assert.Equal("generating", Listed(key)!.State);
        }

        [Fact]
        public void ASessionNoEventHasNamedIsIdle()
        {
            Assert.Equal("idle", Listed(Key())!.State);
        }

        // The key on an event is run-scoped — the session's own key with
        // ":run:<runId>" appended — so it has to be trimmed back before it means
        // anything to the list. Without that every event would record a key no
        // session has and no orb would ever pulse.
        [Fact]
        public void ARunScopedKeyIsTrimmedBackToItsSession()
        {
            var key = Key();

            Fire("agent", key + ":run:0199aa11-2b3c");

            Assert.Equal("generating", Listed(key)!.State);
        }

        // The gateway saying a run finished stops it counting immediately rather
        // than waiting out the idle window — the gateway said so, which beats
        // inferring it from silence.
        [Fact]
        public void AFinishedCronRunStopsCountingAtOnce()
        {
            var key = Key();

            Fire("agent", key);
            Assert.Equal("generating", Listed(key)!.State);

            Fire("cron", key, action: "finished");

            Assert.Equal("idle", Listed(key)!.State);
        }

        // Any other cron action is not a finish, so a job merely being scheduled
        // does not stop an orb pulsing mid-run.
        [Fact]
        public void AnotherCronActionDoesNotStopTheRun()
        {
            var key = Key();

            Fire("agent", key);
            Fire("cron", key, action: "scheduled");

            Assert.Equal("generating", Listed(key)!.State);
        }

        // CB-149: a background task's result landing in the conversation is not
        // a session starting to generate a reply — OpenClawChatSession treats
        // the identical event as Complete(), not as new streaming text. Without
        // this, a task the gateway keeps touching (its own housekeeping
        // re-upserting an old, permanently blocked record among them) pins the
        // orb "generating" forever: that state is the only thing that survives
        // both the recency filter and the stale-orb sweep.
        [Fact]
        public void ATaskUpsertDoesNotMarkASessionGenerating()
        {
            var key = Key();

            Fire("task", key, action: "upserted");

            Assert.Equal("idle", Listed(key)!.State);
        }

        [Fact]
        public void ATaskUpsertStopsAnAlreadyRunningSession()
        {
            var key = Key();

            Fire("agent", key);
            Assert.Equal("generating", Listed(key)!.State);

            Fire("task", key, action: "upserted");

            Assert.Equal("idle", Listed(key)!.State);
        }

        // Any other task action is not the completion delivery, so it still
        // counts as the session doing something.
        [Fact]
        public void AnotherTaskActionStillCountsAsWork()
        {
            var key = Key();

            Fire("task", key, action: "created");

            Assert.Equal("generating", Listed(key)!.State);
        }

        // The gateway's own housekeeping is not evidence of work. A heartbeat tick
        // arrives for every session on a timer, so counting it would leave every
        // orb pulsing forever.
        //
        // "sessions.changed" (CB-152) joined this list live, not by inspection:
        // a single reconnect fired it for a cron job's own internal session,
        // the agent's main DM, and — the reproduction case — a channel with no
        // real activity in three days, all in the same burst. It is the
        // gateway telling every client "the roster changed, go re-fetch", not
        // "this particular session just did something" — the opposite of what
        // its shape (a plain sessionKey, same as a real turn event) suggests.
        [Theory]
        [InlineData("tick")]
        [InlineData("health")]
        [InlineData("presence")]
        [InlineData("connect.challenge")]
        [InlineData("sessions.changed")]
        public void HousekeepingEventsAreNotEvidenceOfWork(string name)
        {
            var key = Key();

            Fire(name, key);

            Assert.Equal("idle", Listed(key)!.State);
        }

        // The property that actually matters: this can't be used to keep a
        // truly stale session artificially "recent" either, since it is what
        // let the reproduction case in CB-152 evade the 15-minute active
        // window for three days without ever showing "generating" — a
        // "sessions.changed" burst on every reconnect kept refreshing
        // LastSeen while Running stayed untouched, so the orb sat on screen
        // dark rather than pulsing, which is what made it look unrelated to
        // CB-149's fix at first.
        [Fact]
        public void ASessionsChangedEventDoesNotKeepAStaleSessionRecent()
        {
            ClaudeBuddySettings.OpenClawEnabled = true;
            ClaudeBuddySettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
            ClaudeBuddySettings.OpenClawActiveWithinMinutes = 5;

            var key = Key();
            Fire("sessions.changed", key);

            var stale = new DateTimeOffset(DateTime.UtcNow.AddHours(-1)).ToUnixTimeMilliseconds();
            var json = "{\"sessions\":[{\"key\":" + JsonSerializer.Serialize(key)
                       + ",\"chatType\":\"channel\",\"lastActivityAt\":" + stale + "}]}";

            var (sessions, _) = OpenClawSessions.Parse(Json(json), DateTime.UtcNow);

            Assert.DoesNotContain(sessions, s => s.Key == key);
        }

        [Fact]
        public void AnEventNamingNoSessionIsIgnored()
        {
            var key = Key();

            OpenClawSessions.OnEvent("agent", Json("""{"other":"fields"}"""));
            OpenClawSessions.OnEvent("agent", Json("""{"sessionKey":""}"""));
            OpenClawSessions.OnEvent("agent", Json("7"));
            OpenClawSessions.OnEvent("agent", Json("null"));

            Assert.Equal("idle", Listed(key)!.State);
        }

        // An event for one session says nothing about another, which is what keeps
        // one busy agent from lighting up the whole screen.
        [Fact]
        public void AnEventForOneSessionLeavesTheOthersAlone()
        {
            var busy = Key();
            var quiet = Key();

            Fire("agent", busy);

            Assert.Equal("generating", Listed(busy)!.State);
            Assert.Equal("idle", Listed(quiet)!.State);
        }

        // --- an event also means recent ---

        // The later of what the gateway claims and what we watched happen, and
        // ours wins: it came from an event the session actually emitted. This is
        // what stops a session that is mid-run from being filtered out for looking
        // stale in a listing that has not caught up.
        [Fact]
        public void AWatchedEventKeepsASessionOffTheStalePile()
        {
            var key = Key();

            Fire("agent", key);

            ClaudeBuddySettings.OpenClawEnabled = true;
            ClaudeBuddySettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
            ClaudeBuddySettings.OpenClawActiveWithinMinutes = 5;

            // The listing says an hour ago. The event says a moment ago.
            var stale = new DateTimeOffset(DateTime.UtcNow.AddHours(-1)).ToUnixTimeMilliseconds();
            var json = "{\"sessions\":[{\"key\":" + JsonSerializer.Serialize(key)
                       + ",\"chatType\":\"channel\",\"lastActivityAt\":" + stale + "}]}";

            var (sessions, _) = OpenClawSessions.Parse(Json(json), DateTime.UtcNow);

            Assert.Contains(sessions, s => s.Key == key);
        }

        // ...and a session nothing has been seen from really is filtered, so the
        // rule above is doing work rather than disabling the filter.
        [Fact]
        public void ASessionWithNoWatchedActivityIsStillFiltered()
        {
            ClaudeBuddySettings.OpenClawEnabled = true;
            ClaudeBuddySettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
            ClaudeBuddySettings.OpenClawActiveWithinMinutes = 5;

            var key = Key();
            var stale = new DateTimeOffset(DateTime.UtcNow.AddHours(-1)).ToUnixTimeMilliseconds();
            var json = "{\"sessions\":[{\"key\":" + JsonSerializer.Serialize(key)
                       + ",\"chatType\":\"channel\",\"lastActivityAt\":" + stale + "}]}";

            var (sessions, _) = OpenClawSessions.Parse(Json(json), DateTime.UtcNow);

            Assert.DoesNotContain(sessions, s => s.Key == key);
        }

        // The event that *ends* a run still counts as activity, deliberately: a
        // conversation that has just finished replying is exactly the one worth
        // keeping on screen.
        [Fact]
        public void TheEventThatEndsARunStillCountsAsActivity()
        {
            var key = Key();

            Fire("cron", key, action: "finished");

            ClaudeBuddySettings.OpenClawEnabled = true;
            ClaudeBuddySettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
            ClaudeBuddySettings.OpenClawActiveWithinMinutes = 5;

            var stale = new DateTimeOffset(DateTime.UtcNow.AddHours(-1)).ToUnixTimeMilliseconds();
            var json = "{\"sessions\":[{\"key\":" + JsonSerializer.Serialize(key)
                       + ",\"chatType\":\"channel\",\"lastActivityAt\":" + stale + "}]}";

            var (sessions, _) = OpenClawSessions.Parse(Json(json), DateTime.UtcNow);

            Assert.Contains(sessions, s => s.Key == key);
            Assert.Equal("idle", sessions.First(s => s.Key == key).State);
        }

        // Same as the cron case above: the event that delivers a task's result
        // still counts as activity, so a completion that lands seconds before a
        // stale listing catches up does not vanish before anyone sees it.
        [Fact]
        public void TheEventThatDeliversATaskStillCountsAsActivity()
        {
            var key = Key();

            Fire("task", key, action: "upserted");

            ClaudeBuddySettings.OpenClawEnabled = true;
            ClaudeBuddySettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
            ClaudeBuddySettings.OpenClawActiveWithinMinutes = 5;

            var stale = new DateTimeOffset(DateTime.UtcNow.AddHours(-1)).ToUnixTimeMilliseconds();
            var json = "{\"sessions\":[{\"key\":" + JsonSerializer.Serialize(key)
                       + ",\"chatType\":\"channel\",\"lastActivityAt\":" + stale + "}]}";

            var (sessions, _) = OpenClawSessions.Parse(Json(json), DateTime.UtcNow);

            Assert.Contains(sessions, s => s.Key == key);
            Assert.Equal("idle", sessions.First(s => s.Key == key).State);
        }
    }
}
