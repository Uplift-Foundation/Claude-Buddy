using Xunit;

namespace ClaudeBuddy.Tests
{
    // Whether a background job is still going — the rule that decides whether an
    // orb disappears.
    //
    // Worth testing carefully because the failure is invisible in one direction
    // and merely untidy in the other. Hiding an orb that should be there loses a
    // session the user was working with and gives no sign it happened; leaving
    // one that should be gone gives them a click that opens a window and closes
    // it again. The source picks the untidy direction on purpose, and these
    // cases are what keeps that choice from being reversed by accident.
    //
    // None of this needs the `claude` CLI. The subprocess that fetches the
    // listing is a separate method and is excluded from coverage; what is left is
    // the decision and the parse, which is where the rules actually live.
    public class BackgroundJobsTests
    {
        private static Dictionary<string, string> States(params (string Id, string State)[] rows)
            => rows.ToDictionary(r => r.Id, r => r.State, StringComparer.Ordinal);

        // --- IsLiveJobGiven: the rules ---

        // A listing that could not be read answers true. This is the important
        // one: no orb should vanish because the CLI was briefly unavailable, and
        // the failure the user cannot see is the one worth being careful about.
        [Fact]
        public void AnUnreadableListingHidesNothing()
        {
            Assert.True(BackgroundJobs.IsLiveJobGiven("2f6c1e88-aaaa", null));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void ASessionWithNoIdHidesNothing(string? sessionId)
        {
            Assert.True(BackgroundJobs.IsLiveJobGiven(sessionId!, States()));
        }

        // Absent from a listing that *was* read means not a job at all — a
        // subagent, or a session that has ended. Nothing left to show either
        // way.
        [Fact]
        public void ASessionMissingFromAGoodListingIsNotLive()
        {
            Assert.False(
                BackgroundJobs.IsLiveJobGiven("2f6c1e88-aaaa", States(("99999999", "running"))));
        }

        [Fact]
        public void AJobStillRunningIsLive()
        {
            Assert.True(
                BackgroundJobs.IsLiveJobGiven("2f6c1e88-aaaa", States(("2f6c1e88", "running"))));
        }

        [Fact]
        public void AJobThatIsDoneIsNotLive()
        {
            Assert.False(
                BackgroundJobs.IsLiveJobGiven("2f6c1e88-aaaa", States(("2f6c1e88", "done"))));
        }

        [Theory]
        [InlineData("DONE")]
        [InlineData("Done")]
        public void DoneIsRecognisedWhateverItsCase(string state)
        {
            Assert.False(
                BackgroundJobs.IsLiveJobGiven("2f6c1e88-aaaa", States(("2f6c1e88", state))));
        }

        // Any state that is not "done" counts as live, including one this app has
        // never seen. A daemon that grows a new state should not make orbs
        // disappear.
        [Theory]
        [InlineData("")]
        [InlineData("queued")]
        [InlineData("paused")]
        [InlineData("some-state-from-a-later-cli")]
        public void AnyStateThatIsNotDoneIsLive(string state)
        {
            Assert.True(
                BackgroundJobs.IsLiveJobGiven("2f6c1e88-aaaa", States(("2f6c1e88", state))));
        }

        // --- JobIdOf: the short form the daemon uses ---

        [Fact]
        public void TheJobIdIsTheFirstSegmentOfTheSessionUuid()
        {
            Assert.Equal(
                "2f6c1e88", BackgroundJobs.JobIdOf("2f6c1e88-9d30-4de6-baf3-943457317be5"));
        }

        // Split rather than a fixed width, so an id that isn't a uuid degrades
        // to itself instead of being truncated into something that matches the
        // wrong job.
        [Theory]
        [InlineData("nodashes", "nodashes")]
        [InlineData("", "")]
        [InlineData("-leading", "-leading")]
        public void AnIdThatIsNotAUuidDegradesToItself(string given, string want)
        {
            Assert.Equal(want, BackgroundJobs.JobIdOf(given));
        }

        // --- ParseAgents: the listing, as a shape another program defines ---

        [Fact]
        public void AListingBecomesIdsAndStates()
        {
            var states = BackgroundJobs.ParseAgents(
                """[{"id":"2f6c1e88","state":"running"},{"id":"95eddb0e","state":"done"}]""");

            Assert.NotNull(states);
            Assert.Equal("running", states!["2f6c1e88"]);
            Assert.Equal("done", states["95eddb0e"]);
        }

        [Fact]
        public void AnEmptyListingIsAnEmptyMapRatherThanNull()
        {
            var states = BackgroundJobs.ParseAgents("[]");

            Assert.NotNull(states);
            Assert.Empty(states!);
        }

        // Interactive sessions carry no id at all — only background ones are
        // jobs — so a row without one is skipped rather than stored under a
        // blank key, which would match every session whose id also went missing.
        [Theory]
        [InlineData("""[{"state":"running"}]""")]
        [InlineData("""[{"id":null,"state":"running"}]""")]
        [InlineData("""[{"id":"","state":"running"}]""")]
        [InlineData("""[{"id":7,"state":"running"}]""")]
        public void ARowWithNoUsableIdIsSkipped(string json)
        {
            var states = BackgroundJobs.ParseAgents(json);

            Assert.NotNull(states);
            Assert.Empty(states!);
        }

        // A row with an id but no state is kept with an empty state, which
        // IsLiveJobGiven reads as live. Keeping it is the safe direction: the job
        // exists, and only "done" is grounds for taking its orb away.
        [Theory]
        [InlineData("""[{"id":"2f6c1e88"}]""")]
        [InlineData("""[{"id":"2f6c1e88","state":null}]""")]
        [InlineData("""[{"id":"2f6c1e88","state":3}]""")]
        public void ARowWithNoUsableStateIsKeptAsLive(string json)
        {
            var states = BackgroundJobs.ParseAgents(json);

            Assert.NotNull(states);
            Assert.Equal("", states!["2f6c1e88"]);
            Assert.True(BackgroundJobs.IsLiveJobGiven("2f6c1e88-aaaa", states));
        }

        // Null, not an empty map, for anything that is not a listing. The
        // difference matters: an empty map means "read it, nothing there", which
        // hides orbs, and null means "could not read it", which hides none.
        [Theory]
        [InlineData("")]
        [InlineData("not json at all")]
        [InlineData("{\"id\":\"2f6c1e88\"}")]
        [InlineData("null")]
        [InlineData("7")]
        [InlineData("[{\"id\":\"a\"},")]
        public void AnythingThatIsNotAListingIsUnreadableRatherThanEmpty(string json)
        {
            Assert.Null(BackgroundJobs.ParseAgents(json));
        }

        // A non-object inside the array is stepped over rather than ending the
        // parse — one malformed row should not cost every orb in the listing.
        [Fact]
        public void ANonObjectRowIsSteppedOver()
        {
            var states = BackgroundJobs.ParseAgents(
                """[7, "text", null, {"id":"2f6c1e88","state":"running"}]""");

            Assert.NotNull(states);
            Assert.Equal("running", states!["2f6c1e88"]);
            Assert.Single(states);
        }

        // Ids are compared ordinally, and this case is worth stating plainly
        // because the two halves of the rule combine in an unobvious direction.
        // A case difference means the lookup misses, a miss means "absent from a
        // listing that was read", and absent means *not live* — so an id that
        // differed only in case would take the orb away rather than leave it.
        //
        // That is the opposite of the safe direction the rest of this file picks
        // (an unreadable listing hides nothing), so it is worth knowing it is
        // here. It does not bite today: the daemon writes lowercase uuids and
        // the session id is the same string from the same source, so the two
        // never disagree in practice. Asserted as it behaves rather than as it
        // arguably should, because a test claiming otherwise would be describing
        // a comparer nobody chose.
        [Fact]
        public void AnIdDifferingOnlyInCaseReadsAsAbsent()
        {
            var states = BackgroundJobs.ParseAgents("""[{"id":"2F6C1E88","state":"running"}]""");

            Assert.NotNull(states);
            Assert.False(BackgroundJobs.IsLiveJobGiven("2f6c1e88-aaaa", states));
        }
    }
}
