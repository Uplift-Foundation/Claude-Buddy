using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers BackgroundJobs: reading `claude agents --json` and deciding whether a
// session id names a background job that is still going. Only IsLiveJob's two
// halves are exercised — fetching the listing shells out to the CLI and is left
// to a real machine.
//
// The listing is a format nobody here controls, and getting it wrong is silent
// in the worst direction: a miss reads as "not a job", which removes an orb for
// a session that is working. The fixtures below are trimmed from real output of
// `claude agents --json`, not written from memory — the bug this file was added
// for is one an invented fixture would have agreed with.
public class BackgroundJobsTests
{
    // Two background jobs and one interactive session, exactly as the CLI
    // prints them. The first job is the case that mattered: `id` 5f6960b2 with
    // its work now in session 53bd5d2c, because the job was resumed and the
    // session id it started with is not the one it is running.
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
        "cwd": "/Users/warrenthompson/Source/Claude-Buddy/.claude/worktrees/merge-all-prs",
        "kind": "background",
        "sessionId": "53bd5d2c-e484-4817-8d4d-469f92874291",
        "state": "working",
        "status": "busy",
        "name": "merge pull requests develop"
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

    // The bug. A job keeps the id it was created with; the session running it
    // does not. Asking about the *session* has to find the job's row, or an orb
    // vanishes from under a session that is mid-turn.
    [Fact]
    public void ResumedJobIsLiveUnderTheSessionItIsActuallyRunning()
    {
        Assert.True(BackgroundJobs.IsLive(Parsed, "53bd5d2c-e484-4817-8d4d-469f92874291"));
    }

    // The other half of the same row: the session the job *started* with is over,
    // and a leftover status file for it must not inherit the job's liveness. This
    // is what stops the fix from being "key on both and hope".
    [Fact]
    public void EarlierSessionOfAResumedJobIsNotLive()
    {
        Assert.False(BackgroundJobs.IsLive(Parsed, "5f6960b2-b8a3-4d46-b507-d6f520c47a81"));
    }

    [Fact]
    public void JobWhoseIdMatchesItsSessionIsLive()
    {
        const string listing = """
        [
          {
            "pid": 88340,
            "id": "d5909c7d",
            "cwd": "/Users/warrenthompson/Source/Claude-Buddy",
            "kind": "background",
            "sessionId": "d5909c7d-d3cc-41c1-ad23-1034369afea8",
            "state": "working",
            "status": "busy",
            "name": "claude buddy orb missing"
          }
        ]
        """;

        Assert.True(BackgroundJobs.IsLive(
            BackgroundJobs.Parse(listing), "d5909c7d-d3cc-41c1-ad23-1034369afea8"));
    }

    // "done" is the whole reason this class exists: the hook writes "idle" for a
    // finished job and for one between turns, and only the listing tells them
    // apart.
    [Fact]
    public void FinishedJobIsNotLive()
    {
        Assert.False(BackgroundJobs.IsLive(Parsed, "5c68059f-546c-4053-8a58-70e8c72f8767"));
    }

    [Fact]
    public void DoneIsMatchedRegardlessOfCase()
    {
        const string listing = """
        [{"id":"aaaaaaaa","sessionId":"aaaaaaaa-0000-0000-0000-000000000000","state":"DONE"}]
        """;

        Assert.False(BackgroundJobs.IsLive(
            BackgroundJobs.Parse(listing), "aaaaaaaa-0000-0000-0000-000000000000"));
    }

    // An interactive session is not a job, so it isn't in the map — and nothing
    // asks about one, because it records a pid. Asserted anyway: if it ever
    // started answering true, a superseded interactive file would stop being
    // pruned.
    [Fact]
    public void InteractiveSessionIsNotAJob()
    {
        Assert.False(BackgroundJobs.IsLive(Parsed, "24dea509-cad2-4d11-95f5-e906132af56b"));
    }

    // A subagent, or a status file whose session ended without clearing it.
    // Neither is in the listing, and an orb for either is a dead click.
    [Fact]
    public void SessionAbsentFromTheListingIsNotLive()
    {
        Assert.False(BackgroundJobs.IsLive(Parsed, "e5fb1fd3-6aac-4249-9f17-602010512235"));
    }

    // A listing that couldn't be read hides nothing. This decides whether to
    // remove an orb, and the CLI being briefly unavailable is not evidence that
    // anything finished.
    [Fact]
    public void UnreadableListingKeepsEveryOrb()
    {
        Assert.True(BackgroundJobs.IsLive(null, "53bd5d2c-e484-4817-8d4d-469f92874291"));
    }

    [Fact]
    public void EmptySessionIdKeepsItsOrb()
    {
        Assert.True(BackgroundJobs.IsLive(Parsed, ""));
    }

    // A row naming a job but no session at all — an older CLI, or a job the
    // daemon knows about before its session exists. The short id is the only
    // handle it has, so the derived lookup still has to work.
    [Fact]
    public void RowWithNoSessionIdIsFoundByTheDerivedJobId()
    {
        const string listing = """
        [{"id":"e2f87abf","kind":"background","state":"working"}]
        """;

        var states = BackgroundJobs.Parse(listing);

        Assert.True(BackgroundJobs.IsLive(states, "e2f87abf-5980-45af-acb6-75a321e7bee9"));
        Assert.False(BackgroundJobs.IsLive(states, "11111111-5980-45af-acb6-75a321e7bee9"));
    }

    // A session id that isn't a uuid degrades to itself rather than to the empty
    // string, so it can still match a row keyed by a short id.
    [Fact]
    public void SessionIdWithNoDashMatchesAJobIdOfTheSameName()
    {
        const string listing = """
        [{"id":"solo","kind":"background","state":"working"}]
        """;

        Assert.True(BackgroundJobs.IsLive(BackgroundJobs.Parse(listing), "solo"));
    }

    // A row with no `state` is a job that hasn't said, which is not "done".
    [Fact]
    public void RowWithNoStateIsLive()
    {
        const string listing = """
        [{"id":"bbbbbbbb","sessionId":"bbbbbbbb-0000-0000-0000-000000000000"}]
        """;

        Assert.True(BackgroundJobs.IsLive(
            BackgroundJobs.Parse(listing), "bbbbbbbb-0000-0000-0000-000000000000"));
    }

    // Not an array: a shape this doesn't recognise reads as "couldn't be read",
    // which keeps orbs rather than dropping them.
    [Fact]
    public void NonArrayListingReadsAsUnreadable()
    {
        Assert.Null(BackgroundJobs.Parse("""{"error":"not logged in"}"""));
    }

    [Fact]
    public void EmptyListingIsReadButNamesNothing()
    {
        var states = BackgroundJobs.Parse("[]");

        Assert.NotNull(states);
        Assert.False(BackgroundJobs.IsLive(states, "53bd5d2c-e484-4817-8d4d-469f92874291"));
    }

    // Rows that aren't objects, and ids of the wrong type, are skipped rather
    // than thrown on — the listing is somebody else's format.
    [Fact]
    public void UnexpectedRowsAreSkipped()
    {
        const string listing = """
        [
          "unexpected",
          {"id":42,"sessionId":"cccccccc-0000-0000-0000-000000000000","state":"working"},
          {"id":"dddddddd","sessionId":null,"state":"working"},
          {"id":"eeeeeeee","sessionId":"eeeeeeee-0000-0000-0000-000000000000","state":"working"}
        ]
        """;

        var states = BackgroundJobs.Parse(listing);

        Assert.NotNull(states);
        Assert.False(BackgroundJobs.IsLive(states, "cccccccc-0000-0000-0000-000000000000"));
        // A null sessionId leaves the short id as the only handle, like an older
        // CLI's row.
        Assert.True(BackgroundJobs.IsLive(states, "dddddddd-0000-0000-0000-000000000000"));
        Assert.True(BackgroundJobs.IsLive(states, "eeeeeeee-0000-0000-0000-000000000000"));
    }
}
