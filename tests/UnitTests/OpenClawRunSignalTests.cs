using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests
{
    // CB-169: one case per arm of the rule that decides whether an OpenClaw
    // event is evidence of a session generating. Pure — no statics, no clock —
    // so every arm is a JSON literal and an expected answer. The shapes are the
    // ones captured off a live gateway (docs/openclaw-findings.md, "A run
    // outside a cron session"); the replays of whole captured sequences are in
    // OpenClawEventStateTests.
    public class OpenClawRunSignalTests
    {
        private const string Run = "00000000-0000-4000-8000-000000000003";
        private const string Other = "00000000-0000-4000-8000-000000000009";

        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

        private static RunEvent Classify(string name, string payload) =>
            OpenClawRunSignal.Classify(name, Json(payload));

        // --- sessions.changed: the only lifecycle a non-cron run sends ---

        [Fact]
        public void ARosterRowWithPhaseStartAndARunIdOpensThatRun()
        {
            var ev = Classify("sessions.changed",
                $$"""{"sessionKey":"agent:main:main","phase":"start","runId":"{{Run}}"}""");

            Assert.Equal(new RunEvent(RunSignal.Open, Run), ev);
        }

        [Fact]
        public void ARosterRowWithPhaseEndAndARunIdEndsThatRun()
        {
            var ev = Classify("sessions.changed",
                $$"""{"sessionKey":"agent:main:main","phase":"end","runId":"{{Run}}"}""");

            Assert.Equal(new RunEvent(RunSignal.End, Run), ev);
        }

        // CB-170, measured: an aborted run's roster row arrives with phase "end"
        // and then again, about a second later, with phase "error". The second
        // is as terminal as the first.
        [Fact]
        public void ARosterRowWithPhaseErrorAndARunIdEndsThatRun()
        {
            var ev = Classify("sessions.changed",
                $$"""{"sessionKey":"agent:main:main","phase":"error","runId":"{{Run}}","status":"killed"}""");

            Assert.Equal(new RunEvent(RunSignal.End, Run), ev);
        }

        // The CB-152 burst: a reason, no phase, no runId — for the whole roster.
        [Theory]
        [InlineData("""{"sessionKey":"agent:main:discord:direct:1","reason":"cron-binding"}""")]
        [InlineData("""{"sessionKey":"agent:main:main","reason":"placement"}""")]
        public void ARosterRowWithNoPhaseIsNothing(string payload)
        {
            Assert.Equal(RunSignal.None, Classify("sessions.changed", payload).Signal);
        }

        // A phase with nothing to tie it to a run can't be ended by the run's own
        // end, so it must not open one.
        [Fact]
        public void APhaseStartWithNoRunIdIsNothing()
        {
            Assert.Equal(RunSignal.None,
                Classify("sessions.changed", """{"sessionKey":"agent:main:main","phase":"start"}""").Signal);
        }

        // A transcript row being appended is a delivery, not generation — it
        // arrives immediately before the start *and* immediately before the end.
        [Fact]
        public void APhaseMessageIsNothing()
        {
            Assert.Equal(RunSignal.None,
                Classify("sessions.changed", $$"""{"sessionKey":"agent:main:main","phase":"message","runId":"{{Run}}"}""").Signal);
        }

        // --- agent ---

        [Theory]
        [InlineData("thinking")]
        [InlineData("assistant")]
        public void StreamingAgentOutputIsWork(string stream)
        {
            var ev = Classify("agent", $$"""{"sessionKey":"k","runId":"{{Run}}","stream":"{{stream}}"}""");

            Assert.Equal(new RunEvent(RunSignal.Work, Run), ev);
        }

        // Work, not Open: it has only been seen under sessions.messages.subscribe,
        // which the app does not call, so it may hold an orb for RunIdle at most.
        [Fact]
        public void ALifecycleStartIsWork()
        {
            Assert.Equal(RunSignal.Work,
                Classify("agent", """{"sessionKey":"k","stream":"lifecycle","data":{"phase":"start"}}""").Signal);
        }

        [Theory]
        [InlineData("end")]
        [InlineData("error")]
        public void ALifecycleEndOrErrorEndsTheRun(string phase)
        {
            var ev = Classify("agent",
                $$$"""{"sessionKey":"k","runId":"{{{Run}}}","stream":"lifecycle","data":{"phase":"{{{phase}}}"}}""");

            Assert.Equal(new RunEvent(RunSignal.End, Run), ev);
        }

        [Theory]
        [InlineData("""{"sessionKey":"k","stream":"lifecycle","data":{"phase":"paused"}}""")]
        [InlineData("""{"sessionKey":"k","stream":"lifecycle"}""")]
        [InlineData("""{"sessionKey":"k","stream":"lifecycle","data":"end"}""")]
        [InlineData("""{"sessionKey":"k","stream":"compaction"}""")]
        [InlineData("""{"sessionKey":"k"}""")]
        public void AnyOtherAgentShapeIsNothing(string payload)
        {
            Assert.Equal(RunSignal.None, Classify("agent", payload).Signal);
        }

        // --- session.tool ---

        [Theory]
        [InlineData("start")]
        [InlineData("result")]
        public void AToolStartingOrReturningIsWork(string phase)
        {
            Assert.Equal(RunSignal.Work,
                Classify("session.tool", $$$"""{"sessionKey":"k","stream":"tool","data":{"phase":"{{{phase}}}"}}""").Signal);
        }

        [Theory]
        [InlineData("""{"sessionKey":"k","data":{"phase":"update"}}""")]
        [InlineData("""{"sessionKey":"k"}""")]
        public void AnyOtherToolShapeIsNothing(string payload)
        {
            Assert.Equal(RunSignal.None, Classify("session.tool", payload).Signal);
        }

        // --- chat ---

        [Fact]
        public void AChatDeltaIsWork()
        {
            Assert.Equal(RunSignal.Work, Classify("chat", """{"sessionKey":"k","state":"delta"}""").Signal);
        }

        [Theory]
        [InlineData("final")]
        [InlineData("error")]
        [InlineData("aborted")]
        public void ATerminalChatStateEndsTheRun(string state)
        {
            Assert.Equal(RunSignal.End,
                Classify("chat", $$"""{"sessionKey":"k","state":"{{state}}"}""").Signal);
        }

        [Theory]
        [InlineData("""{"sessionKey":"k","state":"queued"}""")]
        [InlineData("""{"sessionKey":"k","state":7}""")]
        public void AnyOtherChatStateIsNothing(string payload)
        {
            Assert.Equal(RunSignal.None, Classify("chat", payload).Signal);
        }

        // --- cron ---

        // Measured: the finish has no runId field, only the run-scoped key.
        [Fact]
        public void ACronFinishEndsTheRunNamedByItsKey()
        {
            var ev = Classify("cron", $$"""{"sessionKey":"agent:ops:cron:j:run:{{Run}}","action":"finished"}""");

            Assert.Equal(new RunEvent(RunSignal.End, Run), ev);
        }

        [Theory]
        [InlineData("started")]
        [InlineData("scheduled")]
        [InlineData("updated")]
        public void AnyOtherCronActionIsNothing(string action)
        {
            Assert.Equal(RunSignal.None,
                Classify("cron", $$"""{"sessionKey":"agent:ops:cron:j","action":"{{action}}"}""").Signal);
        }

        // Only the exact measured casing counts. A gateway that ever varies its
        // casing produces None rather than a wrong Work/Open/End — the same
        // "unmeasured shape is None" rule as an unknown event name, applied
        // inside a known one.
        [Theory]
        [InlineData("Start")]
        [InlineData("START")]
        public void ARosterPhaseInTheWrongCaseIsNothing(string phase)
        {
            Assert.Equal(RunSignal.None,
                Classify("sessions.changed", $$"""{"sessionKey":"k","phase":"{{phase}}","runId":"{{Run}}"}""").Signal);
        }

        [Fact]
        public void AStreamInTheWrongCaseIsNothing()
        {
            Assert.Equal(RunSignal.None,
                Classify("agent", """{"sessionKey":"k","stream":"Thinking"}""").Signal);
        }

        [Fact]
        public void AChatStateInTheWrongCaseIsNothing()
        {
            Assert.Equal(RunSignal.None,
                Classify("chat", """{"sessionKey":"k","state":"DELTA"}""").Signal);
        }

        // A non-string phase can't be compared to "start"/"end" at all — Str
        // returns null for it, same as a missing field, rather than throwing or
        // stringifying the number.
        [Fact]
        public void ARosterNonStringPhaseIsNothing()
        {
            Assert.Equal(RunSignal.None,
                Classify("sessions.changed", $$"""{"sessionKey":"k","phase":1,"runId":"{{Run}}"}""").Signal);
        }

        // --- everything else ---

        // CB-149: the housekeeping re-upsert, in the shape it actually arrives in
        // (no top-level sessionKey) and in the shape the old tests assumed.
        [Theory]
        [InlineData("""{"action":"upserted","task":{"status":"completed","childSessionKey":"agent:ops:cron:j:run:r"}}""")]
        [InlineData("""{"sessionKey":"agent:main:main","action":"upserted","task":{"status":"completed"}}""")]
        [InlineData("""{"sessionKey":"agent:main:main","action":"created"}""")]
        public void ATaskEventIsNothing(string payload)
        {
            Assert.Equal(RunSignal.None, Classify("task", payload).Signal);
        }

        // The actual fix to the pendulum: an event name nobody has measured does
        // not light an orb, so a new gateway event can't pin one.
        [Theory]
        [InlineData("session.message")]
        [InlineData("message")]
        [InlineData("session.operation")]
        [InlineData("some.future.event")]
        public void AnUnknownEventIsNothing(string name)
        {
            Assert.Equal(RunSignal.None,
                Classify(name, $$"""{"sessionKey":"agent:main:main","runId":"{{Run}}","stream":"thinking"}""").Signal);
        }

        [Theory]
        [InlineData("7")]
        [InlineData("null")]
        [InlineData("[]")]
        public void AMalformedPayloadIsNothing(string payload)
        {
            Assert.Equal(default, Classify("agent", payload));
        }

        // --- which run an event names ---

        [Fact]
        public void TheRunIdFieldWinsOverTheKeySuffix()
        {
            var ev = Classify("chat", $$"""{"sessionKey":"k:run:{{Other}}","runId":"{{Run}}","state":"delta"}""");

            Assert.Equal(Run, ev.RunId);
        }

        [Theory]
        [InlineData("""{"sessionKey":"agent:main:main","runId":"","state":"delta"}""")]
        [InlineData("""{"sessionKey":"agent:main:main:run:","state":"delta"}""")]
        [InlineData("""{"state":"delta"}""")]
        public void NoRunIdAnywhereIsNull(string payload)
        {
            Assert.Null(Classify("chat", payload).RunId);
        }

        // --- which session an event belongs to ---

        [Theory]
        [InlineData("""{"sessionKey":"agent:main:main"}""", "agent:main:main")]
        [InlineData("""{"sessionKey":"agent:ops:cron:j:run:r"}""", "agent:ops:cron:j")]
        [InlineData("""{"sessionKey":":run:r"}""", ":run:r")]
        [InlineData("""{"sessionKey":""}""", null)]
        [InlineData("""{"sessionKey":5}""", null)]
        [InlineData("""{"task":{"childSessionKey":"agent:ops:cron:j"}}""", null)]
        [InlineData("7", null)]
        public void TheSessionIsTheKeyWithItsRunTrimmed(string payload, string? expected)
        {
            Assert.Equal(expected, OpenClawRunSignal.SessionOf(Json(payload)));
        }

        // --- what an event does to the record ---

        private static readonly DateTime T0 = new(2026, 9, 25, 16, 37, 7, DateTimeKind.Utc);

        [Fact]
        public void NothingLeavesTheRecordAlone()
        {
            var track = new RunTrack(T0, Run, true);

            Assert.Equal(track, OpenClawRunSignal.Apply(track, default, T0.AddSeconds(5)));
            Assert.Null(OpenClawRunSignal.Apply(null, default, T0));
        }

        [Fact]
        public void WorkOnAQuietSessionStartsARecord()
        {
            Assert.Equal(new RunTrack(T0, Run, false),
                OpenClawRunSignal.Apply(null, new RunEvent(RunSignal.Work, Run), T0));
        }

        // Work refreshes the clock without demoting an opened run to the short
        // window, and without swapping which run it is waiting on.
        [Fact]
        public void WorkOnARunningSessionRefreshesItAndKeepsItsRun()
        {
            var open = new RunTrack(T0, Run, true);

            Assert.Equal(new RunTrack(T0.AddSeconds(3), Run, true),
                OpenClawRunSignal.Apply(open, new RunEvent(RunSignal.Work, Other), T0.AddSeconds(3)));
        }

        [Fact]
        public void WorkAdoptsARunIdTheRecordDidNotHave()
        {
            var anonymous = new RunTrack(T0, null, false);

            Assert.Equal(Run,
                OpenClawRunSignal.Apply(anonymous, new RunEvent(RunSignal.Work, Run), T0)!.Value.RunId);
        }

        [Fact]
        public void OpenReplacesWhateverWasThere()
        {
            var old = new RunTrack(T0, Other, false);

            Assert.Equal(new RunTrack(T0.AddSeconds(1), Run, true),
                OpenClawRunSignal.Apply(old, new RunEvent(RunSignal.Open, Run), T0.AddSeconds(1)));
        }

        // Two runs open on one session key — the record holds exactly one, so a
        // second Open forgets the first rather than merging with it (per
        // OpenReplacesWhateverWasThere). Ending the *first* run by name then
        // leaves the second lit, because the record no longer claims to be the
        // first run at all: an End that names a run the record isn't tracking
        // is a no-op (AnEndForADifferentRunLeavesItRunning), and that is exactly
        // what happens here.
        [Fact]
        public void EndingTheFirstOfTwoOverlappingRunsLeavesTheSecondLit()
        {
            var afterFirstOpen = OpenClawRunSignal.Apply(null, new RunEvent(RunSignal.Open, Run), T0);
            var afterSecondOpen = OpenClawRunSignal.Apply(afterFirstOpen, new RunEvent(RunSignal.Open, Other), T0.AddSeconds(1));

            var afterFirstEnds = OpenClawRunSignal.Apply(afterSecondOpen, new RunEvent(RunSignal.End, Run), T0.AddSeconds(2));

            Assert.Equal(afterSecondOpen, afterFirstEnds);
            Assert.True(OpenClawRunSignal.IsGenerating(afterFirstEnds!.Value, T0.AddSeconds(2)));
        }

        // The other order: the record only ever claims to be tracking the
        // *second* run once it has opened, so the second run's own End clears
        // the whole record — even though nothing has said the first run ended.
        // A session can only ever glow for the run this file currently believes
        // is the live one; two genuinely concurrent runs on one session key are
        // not disambiguated on the wire, and this is the documented cost of
        // that (see OpenClawRunSignal's own header comment).
        [Fact]
        public void EndingTheSecondOfTwoOverlappingRunsClearsTrackingOfBoth()
        {
            var afterFirstOpen = OpenClawRunSignal.Apply(null, new RunEvent(RunSignal.Open, Run), T0);
            var afterSecondOpen = OpenClawRunSignal.Apply(afterFirstOpen, new RunEvent(RunSignal.Open, Other), T0.AddSeconds(1));

            var afterSecondEnds = OpenClawRunSignal.Apply(afterSecondOpen, new RunEvent(RunSignal.End, Other), T0.AddSeconds(2));

            Assert.Null(afterSecondEnds);
        }

        [Fact]
        public void TheRunsOwnEndClearsIt()
        {
            Assert.Null(OpenClawRunSignal.Apply(new RunTrack(T0, Run, true), new RunEvent(RunSignal.End, Run), T0));
        }

        [Fact]
        public void AnEndForADifferentRunLeavesItRunning()
        {
            var track = new RunTrack(T0, Run, true);

            Assert.Equal(track, OpenClawRunSignal.Apply(track, new RunEvent(RunSignal.End, Other), T0.AddSeconds(1)));
        }

        // Nothing to compare on one side or the other: the end is taken at its word,
        // which is what a run-less cron "finished" has always done.
        [Fact]
        public void AnEndWithNothingToCompareClears()
        {
            Assert.Null(OpenClawRunSignal.Apply(new RunTrack(T0, Run, true), new RunEvent(RunSignal.End, null), T0));
            Assert.Null(OpenClawRunSignal.Apply(new RunTrack(T0, null, false), new RunEvent(RunSignal.End, Run), T0));
            Assert.Null(OpenClawRunSignal.Apply(null, new RunEvent(RunSignal.End, Run), T0));
        }

        // --- how long silence is allowed ---

        [Fact]
        public void StreamingWorkGoesQuietAfterRunIdle()
        {
            var track = new RunTrack(T0, Run, false);

            Assert.True(OpenClawRunSignal.IsGenerating(track, T0 + OpenClawRunSignal.RunIdle));
            Assert.False(OpenClawRunSignal.IsGenerating(track, T0 + OpenClawRunSignal.RunIdle + TimeSpan.FromMilliseconds(1)));
        }

        [Fact]
        public void AnOpenedRunOutlastsRunIdleUntilTheCeiling()
        {
            var track = new RunTrack(T0, Run, true);

            Assert.True(OpenClawRunSignal.IsGenerating(track, T0 + OpenClawRunSignal.RunIdle + TimeSpan.FromSeconds(1)));
            Assert.True(OpenClawRunSignal.IsGenerating(track, T0 + OpenClawRunSignal.RunCeiling));
            Assert.False(OpenClawRunSignal.IsGenerating(track, T0 + OpenClawRunSignal.RunCeiling + TimeSpan.FromMilliseconds(1)));
        }

        // The ceiling was chosen to cover the longest run measured on the live
        // gateway (1812 s). If someone shortens it, this says what it will cut.
        [Fact]
        public void TheCeilingCoversTheLongestMeasuredRun()
        {
            Assert.True(OpenClawRunSignal.RunCeiling > TimeSpan.FromSeconds(1812));
        }
    }
}
