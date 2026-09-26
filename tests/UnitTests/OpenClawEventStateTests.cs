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

        // An "agent" event with no stream is given "thinking", the shape a real
        // streaming turn sends: since CB-169 only measured shapes count as work,
        // and a bare {"sessionKey"} no longer does (see the classifier cases in
        // OpenClawRunSignalTests).
        private static void Fire(string name, string sessionKey, string? action = null)
        {
            var actionPart = action is null
                ? ""
                : $",\"action\":{JsonSerializer.Serialize(action)}";
            var streamPart = name == "agent" ? ",\"stream\":\"thinking\"" : "";

            OpenClawSessions.OnEvent(
                name,
                Json($"{{\"sessionKey\":{JsonSerializer.Serialize(sessionKey)}{actionPart}{streamPart}}}"));
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
        public void AStreamingEventMarksItGenerating()
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

        // This used to assert the opposite — that an upsert *stopped* a running
        // session — on the strength of one cron capture where it sat beside the
        // run's end. CB-169 turned that round: an upsert that arrives mid-reply
        // (CB-149's housekeeping re-touches do, continuously) put a live orb out,
        // and a real task event names no session of its own anyway. It is None
        // now, both ways: it neither starts nor stops a run.
        [Fact]
        public void ATaskUpsertDoesNotStopARunningSession()
        {
            var key = Key();

            Fire("agent", key);
            Assert.Equal("generating", Listed(key)!.State);

            Fire("task", key, action: "upserted");

            Assert.Equal("generating", Listed(key)!.State);
        }

        // Also flipped by CB-169: no task action was ever measured as a session
        // generating, and an unmeasured shape no longer counts as work.
        [Fact]
        public void AnotherTaskActionIsNotWorkEither()
        {
            var key = Key();

            Fire("task", key, action: "created");

            Assert.Equal("idle", Listed(key)!.State);
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

        // --- CB-169: real captured sequences, replayed ---
        //
        // Each row goes through the real OnEvent at base + its captured offset,
        // and state is read back through StateFor at a chosen instant, so the
        // sequences keep their real spacing without the test sleeping. Every
        // agent id in a fixture is salted per case, since the run record is
        // process-wide and two cases replaying one capture would otherwise
        // answer each other's question.

        private readonly record struct Row(double T, string Name, JsonElement Payload);

        private static List<Row> Rows(string fixture, string salt) =>
            fixture.Replace("\"agent:", "\"agent:" + salt)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => Json(line))
                .Select(r => new Row(r.GetProperty("t").GetDouble(), r.GetProperty("name").GetString()!,
                    r.GetProperty("payload")))
                .ToList();

        private static string Salt() => $"s{Guid.NewGuid():N}";

        private static void Deliver(Row row, DateTime at) => OpenClawSessions.OnEvent(row.Name, row.Payload, at);

        private static bool IsStart(Row r) =>
            r.Name == "sessions.changed" && r.Payload.TryGetProperty("phase", out var p) && p.GetString() == "start";

        private static bool IsEnd(Row r) =>
            r.Name == "sessions.changed" && r.Payload.TryGetProperty("phase", out var p) && p.GetString() == "end";

        // The case the ticket is about. A run on a non-cron session sends its
        // start and then nothing until its end, so the glow has to survive a
        // silence far longer than RunIdle. The real run was 4.5 s long; here
        // the same rows are delivered with the end pushed thirty minutes out —
        // the order and the payloads are the capture's, the gap is stretched —
        // and the session reads generating throughout, then idle at the end.
        [Fact]
        public void AMainSessionRunStaysGeneratingAcrossALongSilenceAndStopsAtItsEnd()
        {
            var salt = Salt();
            var key = $"agent:{salt}main:main";
            var rows = Rows(OpenClawEventFixtures.MainSessionRun, salt);
            var start = rows.FindIndex(IsStart);
            var at = DateTime.UtcNow;

            foreach (var row in rows.Take(start + 1)) Deliver(row, at.AddSeconds(row.T));
            var opened = at.AddSeconds(rows[start].T);

            // Through Parse, on the real clock: the orb an owner would see.
            Assert.Equal("generating", Listed(key)!.State);

            foreach (var silence in new[] { 21.0, 60, 600, 1800 })
            {
                Assert.Equal("generating", OpenClawSessions.StateFor(key, opened.AddSeconds(silence)));
            }

            var later = opened.AddMinutes(30);
            foreach (var row in rows.Skip(start + 1)) Deliver(row, later.AddSeconds(row.T));

            Assert.Equal("idle", OpenClawSessions.StateFor(key, later.AddSeconds(5)));
        }

        // The same run at its real spacing: generating from the start, idle from
        // the end, and the transcript-row "message" phase in between changes
        // nothing either way.
        [Fact]
        public void AMainSessionRunAtItsCapturedSpacingGlowsForExactlyItsLength()
        {
            var salt = Salt();
            var key = $"agent:{salt}main:main";
            var at = DateTime.UtcNow;
            var open = false;

            foreach (var row in Rows(OpenClawEventFixtures.MainSessionRun, salt))
            {
                var when = at.AddSeconds(row.T);
                Deliver(row, when);
                if (IsStart(row)) open = true;
                if (IsEnd(row)) open = false;

                Assert.Equal(open ? "generating" : "idle", OpenClawSessions.StateFor(key, when));
            }
        }

        // An end is only for its own run. The captured end, with its runId
        // swapped for another run's, leaves the session generating; the real
        // one then ends it.
        [Fact]
        public void AnEndForADifferentRunDoesNotClearTheSession()
        {
            var salt = Salt();
            var key = $"agent:{salt}main:main";
            var rows = Rows(OpenClawEventFixtures.MainSessionRun, salt);
            var at = DateTime.UtcNow;

            Deliver(rows.First(IsStart), at);

            var end = rows.First(IsEnd).Payload.GetRawText();
            var foreign = Json(end.Replace("00000000-0000-4000-8000-000000000003", "00000000-0000-4000-8000-0000000000ff"));
            OpenClawSessions.OnEvent("sessions.changed", foreign, at.AddSeconds(1));

            Assert.Equal("generating", OpenClawSessions.StateFor(key, at.AddSeconds(2)));

            Deliver(rows.First(IsEnd), at.AddSeconds(3));

            Assert.Equal("idle", OpenClawSessions.StateFor(key, at.AddSeconds(4)));
        }

        // The safety net: a start whose end never comes stops at RunCeiling
        // rather than pinning the orb until the app restarts.
        [Fact]
        public void AStartWithNoEndGoesIdleAtTheCeiling()
        {
            var salt = Salt();
            var key = $"agent:{salt}main:main";
            var at = DateTime.UtcNow;

            Deliver(Rows(OpenClawEventFixtures.MainSessionRun, salt).First(IsStart), at);

            Assert.Equal("generating", OpenClawSessions.StateFor(key, at + OpenClawRunSignal.RunCeiling));
            Assert.Equal("idle", OpenClawSessions.StateFor(key, at + OpenClawRunSignal.RunCeiling + TimeSpan.FromSeconds(1)));
        }

        // Candidate (a) from the plan, as a regression case: a task upsert for
        // the session landing mid-reply — CB-149's housekeeping does this
        // continuously — used to put the glow out. It no longer touches it.
        [Fact]
        public void AHousekeepingUpsertMidReplyLeavesTheReplyGlowing()
        {
            var salt = Salt();
            var key = $"agent:{salt}main:main";
            var at = DateTime.UtcNow;

            Deliver(Rows(OpenClawEventFixtures.MainSessionRun, salt).First(IsStart), at);
            OpenClawSessions.OnEvent("task",
                Json($$$"""{"sessionKey":"{{{key}}}","action":"upserted","task":{"status":"blocked"}}"""), at.AddSeconds(30));

            Assert.Equal("generating", OpenClawSessions.StateFor(key, at.AddSeconds(31)));
        }

        // The control: the cron run the old design was built from still glows
        // from its first work event to its lifecycle end, and not before or
        // after. Its spacing is real throughout — no gap in it exceeds RunIdle.
        [Fact]
        public void ACronSessionRunGlowsFromItsFirstWorkToItsEnd()
        {
            var salt = Salt();
            var key = $"agent:{salt}agent1:cron:00000000-0000-4000-8000-000000000001";
            var at = DateTime.UtcNow;
            var seen = new List<(string Name, string State)>();

            foreach (var row in Rows(OpenClawEventFixtures.CronSessionRun, salt))
            {
                var when = at.AddSeconds(row.T);
                Deliver(row, when);
                seen.Add((row.Name, OpenClawSessions.StateFor(key, when)));
            }

            var first = seen.FindIndex(s => s.Name == "session.tool");
            var end = seen.FindIndex(s => s.Name == "agent" && s.State == "idle");

            Assert.True(first > 0 && end > first, string.Join(", ", seen));
            Assert.All(seen.Take(first), s => Assert.Equal("idle", s.State));
            Assert.All(seen.Skip(first).Take(end - first), s => Assert.Equal("generating", s.State));
            Assert.All(seen.Skip(end), s => Assert.Equal("idle", s.State));
        }

        // CB-152, from the capture: a roster-wide burst lights nothing, and does
        // not make a stale session look recent either.
        [Fact]
        public void ARosterBurstNeverGeneratesAndKeepsNothingRecent()
        {
            var salt = Salt();
            var at = DateTime.UtcNow;
            var rows = Rows(OpenClawEventFixtures.RosterBurst, salt);
            var keys = rows.Select(r => OpenClawRunSignal.SessionOf(r.Payload)).OfType<string>().Distinct().ToList();

            Assert.Equal(8, keys.Count);

            foreach (var row in rows)
            {
                Deliver(row, at.AddSeconds(row.T));
                Assert.All(keys, k => Assert.Equal("idle", OpenClawSessions.StateFor(k, at.AddSeconds(row.T))));
            }

            ClaudeBuddySettings.OpenClawEnabled = true;
            ClaudeBuddySettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
            ClaudeBuddySettings.OpenClawActiveWithinMinutes = 5;

            var stale = new DateTimeOffset(DateTime.UtcNow.AddHours(-1)).ToUnixTimeMilliseconds();
            var json = "{\"sessions\":[" + string.Join(",", keys.Select(k =>
                "{\"key\":" + JsonSerializer.Serialize(k) + ",\"chatType\":\"channel\",\"lastActivityAt\":" + stale + "}")) + "]}";

            var (sessions, _) = OpenClawSessions.Parse(Json(json), DateTime.UtcNow);

            Assert.DoesNotContain(sessions, s => keys.Contains(s.Key));
        }

        // Out-of-order delivery: an End arriving with nothing open yet is a
        // no-op (there is no record to clear), and it leaves no trace that
        // would stop the real Start from opening the run normally afterward.
        [Fact]
        public void AnEndThatArrivesBeforeItsStartIsIgnoredAndTheLateStartOpensNormally()
        {
            var salt = Salt();
            var key = $"agent:{salt}main:main";
            var rows = Rows(OpenClawEventFixtures.MainSessionRun, salt);
            var at = DateTime.UtcNow;

            Deliver(rows.First(IsEnd), at);
            Assert.Equal("idle", OpenClawSessions.StateFor(key, at.AddSeconds(1)));

            Deliver(rows.First(IsStart), at.AddSeconds(2));
            Assert.Equal("generating", OpenClawSessions.StateFor(key, at.AddSeconds(3)));
        }

        // A stray Work event after the run's End relights the orb rather than
        // being dropped — Apply starts a fresh anonymous record for it. Bounded
        // by RunIdle, not the ceiling, since nothing reopened the run: a late
        // straggler gets at most twenty seconds, not forty-five minutes.
        [Fact]
        public void AWorkEventAfterEndRelightsTheOrbForRunIdleNotTheCeiling()
        {
            var salt = Salt();
            var key = $"agent:{salt}main:main";
            var rows = Rows(OpenClawEventFixtures.MainSessionRun, salt);
            var at = DateTime.UtcNow;

            Deliver(rows.First(IsStart), at);
            Deliver(rows.First(IsEnd), at.AddSeconds(1));
            Assert.Equal("idle", OpenClawSessions.StateFor(key, at.AddSeconds(2)));

            OpenClawSessions.OnEvent("agent",
                Json($$"""{"sessionKey":"{{key}}","stream":"thinking"}"""), at.AddSeconds(3));

            Assert.Equal("generating", OpenClawSessions.StateFor(key, at.AddSeconds(3) + OpenClawRunSignal.RunIdle));
            Assert.Equal("idle",
                OpenClawSessions.StateFor(key, at.AddSeconds(3) + OpenClawRunSignal.RunIdle + TimeSpan.FromMilliseconds(1)));
        }

        // Reconnect clearing: a session mid-run when the socket drops must not
        // stay pinned "generating" for up to 45 minutes just because nothing
        // told this client the run ended. Restart() is safe to call directly
        // with the feature off and no host set — it clears Running before it
        // ever looks at either setting (see its own comment) — so this reaches
        // the same line RunAsync's connection-drop path calls, without opening
        // a socket.
        [Fact]
        public void RestartClearsAMidRunSessionSoItDoesNotStayPinnedAcrossAReconnect()
        {
            var was = ClaudeBuddySettings.OpenClawEnabled;
            var host = ClaudeBuddySettings.OpenClawHost;
            try
            {
                var key = Key();
                Fire("agent", key);
                Assert.Equal("generating", Listed(key)!.State);

                ClaudeBuddySettings.OpenClawEnabled = false;
                ClaudeBuddySettings.OpenClawHost = "";
                OpenClawSessions.Restart();

                Assert.Equal("idle", Listed(key)!.State);
            }
            finally
            {
                ClaudeBuddySettings.OpenClawEnabled = was;
                ClaudeBuddySettings.OpenClawHost = host;
            }
        }

        // CB-149, from the capture: housekeeping with no run in it lights
        // nothing — including the session a task names as its child.
        [Fact]
        public void HousekeepingNeverGenerates()
        {
            var salt = Salt();
            var at = DateTime.UtcNow;
            var rows = Rows(OpenClawEventFixtures.Housekeeping, salt)
                .Concat(Rows(OpenClawEventFixtures.CronSessionRun, salt).Where(r => r.Name == "task"))
                .ToList();
            var child = $"agent:{salt}agent1:cron:00000000-0000-4000-8000-000000000001";

            foreach (var row in rows)
            {
                Deliver(row, at.AddSeconds(row.T));
                Assert.Equal("idle", OpenClawSessions.StateFor(child, at.AddSeconds(row.T)));
            }
        }
    }
}
