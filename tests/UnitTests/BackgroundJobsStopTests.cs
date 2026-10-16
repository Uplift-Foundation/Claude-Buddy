using System.Collections.Generic;
using Xunit;

namespace Orbweaver.Tests;

// CB-264: the two decisions behind ending a background job through its daemon
// rather than by signalling its worker — whether to ask the daemon at all, and
// which job id to hand `claude stop`. Running the CLI is left to a real machine;
// BackgroundJobs.Stop is the process launch and nothing else.
//
// The first row is trimmed from the live job this ticket was filed for: a
// parked job resumed into a session whose id shares nothing with the job's own.
// Deriving the id from the session would have stopped nothing — or, worse,
// whichever job happened to own that prefix, which the third row is here to be.
public class BackgroundJobsStopTests
{
    private const string Listing = """
    [
      {
        "pid": 84576,
        "id": "80b19c48",
        "cwd": "/Users/user/Documents/Evidence",
        "kind": "background",
        "sessionId": "f5e13eda-523e-4deb-9b62-ab71fb779c56",
        "name": "parked job",
        "status": "idle",
        "state": "blocked"
      },
      {
        "pid": 97442,
        "cwd": "/Users/user/Documents/Evidence",
        "kind": "interactive",
        "sessionId": "b1bb90dd-1b30-411d-bdf6-c37c2bba188a",
        "name": "terminal",
        "status": "idle"
      },
      {
        "pid": 51022,
        "id": "f5e13eda",
        "cwd": "/Users/user/Source/elsewhere",
        "kind": "background",
        "sessionId": "0c9d7a31-8f2e-4b6a-9d1c-2e7f5a4b3c21",
        "status": "busy",
        "state": "working"
      },
      {
        "id": "7a1c0e55",
        "kind": "background",
        "state": "working"
      }
    ]
    """;

    // The bug's own shape: the job is found by the session it is running now.
    [Fact]
    public void AResumedJobIsFoundByTheSessionItIsRunning() =>
        Assert.Equal("80b19c48",
            BackgroundJobs.JobIdFor(Listing, "f5e13eda-523e-4deb-9b62-ab71fb779c56"));

    // A session whose prefix is some other job's id, where that job's row names
    // a different session, must not be matched to it: that stop would end a job
    // the user never pointed at.
    [Fact]
    public void ASessionIsNeverMatchedToAnotherJobByItsPrefix() =>
        Assert.Null(BackgroundJobs.JobIdFor(Listing, "f5e13eda-0000-0000-0000-000000000000"));

    // A row that names no session falls back to the short id, the same fallback
    // Parse keeps for an older CLI or a job whose session does not exist yet.
    [Fact]
    public void ARowWithNoSessionIsMatchedByTheShortId() =>
        Assert.Equal("7a1c0e55",
            BackgroundJobs.JobIdFor(Listing, "7a1c0e55-1111-2222-3333-444444444444"));

    // An exact session match wins over a fallback that came earlier in the
    // listing.
    [Fact]
    public void AnExactMatchBeatsAnEarlierFallback()
    {
        const string listing = """
        [
          { "id": "aaaa1111", "kind": "background", "state": "working" },
          { "id": "bbbb2222", "kind": "background", "sessionId": "aaaa1111-0000-0000-0000-000000000000", "state": "blocked" }
        ]
        """;

        Assert.Equal("bbbb2222",
            BackgroundJobs.JobIdFor(listing, "aaaa1111-0000-0000-0000-000000000000"));
    }

    // Interactive sessions carry no job id, and there is nothing to stop.
    [Fact]
    public void AnInteractiveSessionHasNoJob() =>
        Assert.Null(BackgroundJobs.JobIdFor(Listing, "b1bb90dd-1b30-411d-bdf6-c37c2bba188a"));

    [Theory]
    [InlineData(Listing, "")]
    [InlineData("not json", "f5e13eda-523e-4deb-9b62-ab71fb779c56")]
    [InlineData("""{ "id": "80b19c48" }""", "f5e13eda-523e-4deb-9b62-ab71fb779c56")]
    [InlineData("""[ 1, "two", null ]""", "f5e13eda-523e-4deb-9b62-ab71fb779c56")]
    public void AnythingElseNamesNoJob(string listing, string sessionId) =>
        Assert.Null(BackgroundJobs.JobIdFor(listing, sessionId));

    // --- WorthAskingTheDaemon -----------------------------------------------

    private static Dictionary<string, string> States(string sessionId, string state) =>
        new() { [sessionId] = state };

    // Every state the daemon lists is worth a stop. Parked is the case that
    // mattered; done is harmless to stop and costs one subprocess.
    [Theory]
    [InlineData("blocked")]
    [InlineData("working")]
    [InlineData("done")]
    public void AListedClaudeCodeJobIsAskedAbout(string state) =>
        Assert.True(BackgroundJobs.WorthAskingTheDaemon(
            SessionSource.ClaudeCode, States("s-1", state), "s-1"));

    // A listing that was read and does not name the session is a terminal
    // session: straight to the signal, no subprocess.
    [Fact]
    public void AClaudeCodeSessionTheListingDoesNotNameIsNot() =>
        Assert.False(BackgroundJobs.WorthAskingTheDaemon(
            SessionSource.ClaudeCode, States("other", "blocked"), "s-1"));

    // No listing at all says nothing about the session, and Stop reads its own
    // before acting, so the daemon is asked.
    [Fact]
    public void AnUnreadableListingStillAsks() =>
        Assert.True(BackgroundJobs.WorthAskingTheDaemon(SessionSource.ClaudeCode, null, "s-1"));

    // Only Claude Code has a daemon. A Codex session the listing happened to
    // name — it cannot, but the keys are only strings — still goes to the
    // signal.
    [Theory]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.Grok)]
    public void OtherClisNeverAsk(SessionSource source) =>
        Assert.False(BackgroundJobs.WorthAskingTheDaemon(source, States("s-1", "blocked"), "s-1"));
}
