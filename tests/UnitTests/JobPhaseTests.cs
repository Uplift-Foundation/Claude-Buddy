using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.Tests;

// BackgroundJobs.Phase: what the daemon says a background session is *doing*,
// as opposed to IsLive's "is it still worth an orb".
//
// Beside BackgroundJobsTests rather than inside it because it is a second
// question over the same listing, and the two have to keep agreeing about the
// one word they share ("done" hides an orb and is also a phase). The fixtures
// are trimmed from real `claude agents --json` output for the reason that file
// records: this format is nobody's here, and the bug it was written for is one
// an invented fixture would have agreed with.
//
// What makes the distinction worth its own function is that the mistake is
// silent and directional. Reading "blocked" as work in progress is the bug this
// ticket exists for — fifteen orbs breathing on a machine with nothing running.
// Reading "working" as parked would be worse: an agent visibly at work, drawn
// as though nothing were happening.
public class JobPhaseTests
{
    // One job mid-turn, one parked between turns, one finished, and an
    // interactive session that is not a job at all. The middle row is the one
    // the whole ticket is about: state "blocked" with status "idle", which on
    // disk is indistinguishable from the first.
    private const string Listing = """
    [
      {
        "pid": 24612,
        "cwd": "/Users/warrenthompson/Documents/GTD/Evidence",
        "kind": "interactive",
        "sessionId": "24dea509-cad2-4d11-95f5-e906132af56b",
        "name": "evidence",
        "status": "busy"
      },
      {
        "pid": 88341,
        "id": "5f6960b2",
        "cwd": "/Users/warrenthompson/Source/Claude-Buddy",
        "kind": "background",
        "sessionId": "53bd5d2c-e484-4817-8d4d-469f92874291",
        "state": "working",
        "status": "busy",
        "name": "job lawyer"
      },
      {
        "pid": 88342,
        "id": "e2f87abf",
        "cwd": "/Users/warrenthompson/Documents/GTD/Evidence",
        "kind": "background",
        "sessionId": "e2f87abf-5980-45af-acb6-75a321e7bee9",
        "state": "blocked",
        "status": "idle",
        "name": "evidence"
      },
      {
        "pid": 27933,
        "id": "5c68059f",
        "cwd": "/Users/warrenthompson/Source/Claude-Buddy",
        "kind": "background",
        "sessionId": "5c68059f-546c-4053-8a58-70e8c72f8767",
        "state": "done",
        "status": "idle",
        "name": "claude-buddy"
      }
    ]
    """;

    private static Dictionary<string, string>? Parsed => BackgroundJobs.Parse(Listing);

    [Fact]
    public void AJobMidTurnIsWorking()
    {
        Assert.Equal(
            JobPhase.Working,
            BackgroundJobs.Phase(Parsed, "53bd5d2c-e484-4817-8d4d-469f92874291"));
    }

    // The bug, in one assertion. "blocked" is a pooled worker sitting between
    // turns; the file next to it says "idle", which is also what a job mid-turn
    // writes, and the orb drew them the same.
    [Fact]
    public void AJobBetweenTurnsIsParked()
    {
        Assert.Equal(
            JobPhase.Parked,
            BackgroundJobs.Phase(Parsed, "e2f87abf-5980-45af-acb6-75a321e7bee9"));
    }

    [Fact]
    public void AFinishedJobIsDone()
    {
        Assert.Equal(
            JobPhase.Done,
            BackgroundJobs.Phase(Parsed, "5c68059f-546c-4053-8a58-70e8c72f8767"));
    }

    // An interactive session carries no `id`, so it is not in the map at all —
    // and neither is a subagent or a status file that outlived its session.
    // Absent from a listing that *was* read is a fact about the session, which
    // is why it is not the same answer as Unknown below.
    [Theory]
    [InlineData("24dea509-cad2-4d11-95f5-e906132af56b")]
    [InlineData("aaaaaaaa-1111-2222-3333-444444444444")]
    public void ASessionTheListingDoesNotNameIsNotAJob(string sessionId)
    {
        Assert.Equal(JobPhase.NotAJob, BackgroundJobs.Phase(Parsed, sessionId));
    }

    // Fail open, the same way IsLive does and for the same reason: the CLI being
    // briefly unavailable is not evidence about anything. Everything downstream
    // treats Unknown as "change nothing", so this is the answer that keeps a
    // momentary failure from dimming every orb on screen at once.
    [Fact]
    public void AListingThatCouldNotBeReadIsUnknown()
    {
        Assert.Equal(JobPhase.Unknown, BackgroundJobs.Phase(null, "53bd5d2c-e484-4817-8d4d-469f92874291"));
    }

    [Fact]
    public void AnEmptySessionIdIsUnknownRatherThanNotAJob()
    {
        // Nothing was asked, so nothing was answered. NotAJob would be a claim
        // about a session that was never named.
        Assert.Equal(JobPhase.Unknown, BackgroundJobs.Phase(Parsed, ""));
    }

    // A [Theory] would be the natural shape for this and the loops below, and
    // cannot be: JobPhase is internal (it belongs beside BackgroundJobs, which
    // is), and a public xUnit test method may not take an internal parameter
    // type. Widening the enum to public to suit the test harness would be the
    // tail wagging the dog — ScanVerdictTests has the same constraint and
    // resolves it the same way.
    [Fact]
    public void StatesAreMatchedRegardlessOfCase()
    {
        var cases = new (string State, JobPhase Expected)[]
        {
            ("WORKING", JobPhase.Working),
            ("Blocked", JobPhase.Parked),
            ("DONE", JobPhase.Done),
        };

        foreach (var (state, expected) in cases)
        {
            var listing = """
                [{"id":"aaaaaaaa","sessionId":"aaaaaaaa-0000-0000-0000-000000000000","state":"STATE"}]
                """.Replace("STATE", state);

            Assert.Equal(
                expected,
                BackgroundJobs.Phase(BackgroundJobs.Parse(listing), "aaaaaaaa-0000-0000-0000-000000000000"));
        }
    }

    // A state this build has never heard of reads as work in progress, which is
    // the same thing IsLive already does with it. The point is which way this
    // fails when the daemon grows a sixth word: such a job keeps rendering as it
    // always did, rather than going quietly still because a switch was not
    // revisited.
    [Theory]
    [InlineData("queued")]
    [InlineData("paused")]
    [InlineData("some-state-from-a-later-cli")]
    [InlineData("")]
    public void AStateThisBuildHasNeverHeardOfReadsAsWorking(string state)
    {
        var listing = """
            [{"id":"bbbbbbbb","sessionId":"bbbbbbbb-0000-0000-0000-000000000000","state":"STATE"}]
            """.Replace("STATE", state);

        Assert.Equal(
            JobPhase.Working,
            BackgroundJobs.Phase(BackgroundJobs.Parse(listing), "bbbbbbbb-0000-0000-0000-000000000000"));
    }

    [Fact]
    public void ARowWithNoStateAtAllReadsAsWorking()
    {
        const string listing = """
        [{"id":"cccccccc","sessionId":"cccccccc-0000-0000-0000-000000000000"}]
        """;

        Assert.Equal(
            JobPhase.Working,
            BackgroundJobs.Phase(BackgroundJobs.Parse(listing), "cccccccc-0000-0000-0000-000000000000"));
    }

    // The same key fallback IsLive has: a row naming a job but no session at all
    // — an older CLI, or a job the daemon knows about before its session exists
    // — is still found through the short job id.
    [Fact]
    public void ARowWithNoSessionIdIsFoundByTheDerivedJobId()
    {
        const string listing = """
        [{"id":"e2f87abf","kind":"background","state":"blocked"}]
        """;

        var states = BackgroundJobs.Parse(listing);

        Assert.Equal(JobPhase.Parked, BackgroundJobs.Phase(states, "e2f87abf-5980-45af-acb6-75a321e7bee9"));
        Assert.Equal(JobPhase.NotAJob, BackgroundJobs.Phase(states, "11111111-5980-45af-acb6-75a321e7bee9"));
    }

    // The two functions read the same map and share exactly one word, so they
    // have to agree on it. IsLive is what removes an orb and Phase is what dims
    // one: a session both hid and dimmed, or one that Phase called Done while
    // IsLive kept its orb, would be two rules disagreeing about the same row.
    [Theory]
    [InlineData("53bd5d2c-e484-4817-8d4d-469f92874291")]   // working
    [InlineData("e2f87abf-5980-45af-acb6-75a321e7bee9")]   // blocked
    [InlineData("5c68059f-546c-4053-8a58-70e8c72f8767")]   // done
    [InlineData("24dea509-cad2-4d11-95f5-e906132af56b")]   // interactive
    [InlineData("aaaaaaaa-1111-2222-3333-444444444444")]   // absent
    public void PhaseAndIsLiveAgreeAboutWhichRowsAreOver(string sessionId)
    {
        var phase = BackgroundJobs.Phase(Parsed, sessionId);
        var live = BackgroundJobs.IsLive(Parsed, sessionId);

        // The only two phases IsLive calls live are Working and Parked. Done is
        // over, and NotAJob has nothing to be live about.
        Assert.Equal(phase is JobPhase.Working or JobPhase.Parked, live);
    }

    // ...and on the fail-open case too, which is the half that matters most:
    // both answer "assume nothing" for a listing that could not be read.
    [Fact]
    public void PhaseAndIsLiveBothFailOpenOnAnUnreadableListing()
    {
        Assert.True(BackgroundJobs.IsLive(null, "53bd5d2c-e484-4817-8d4d-469f92874291"));
        Assert.Equal(JobPhase.Unknown, BackgroundJobs.Phase(null, "53bd5d2c-e484-4817-8d4d-469f92874291"));
    }
}
