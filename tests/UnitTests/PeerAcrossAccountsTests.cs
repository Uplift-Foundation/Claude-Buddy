using Xunit;

namespace ClaudeBuddy.UnitTests;

// Serving sessions from more than one Claude account over one socket.
//
// **Every case here was found on real hardware and none of them could have been
// found any other way.** The mini has a session called `job-hunter-mac-mini`
// under both `.claude` and `.claude-board` — the same person's two logins on one
// machine, which is ordinary. Every roster it sent came back with zero entries,
// the asking machine drew no orbs, and nothing in either log said why.
//
// A relay never had this problem, and that is the whole point: it signs into one
// account and can only see that account's sessions, so one roster is the shape
// of the thing. A socket has no account, and the code that came over from the
// relay quietly assumed it did.
public class PeerAcrossAccountsTests
{
    private static AgentRoster.Entry Agent(string name, string sessionId, int pid = 0) =>
        new(name, sessionId, pid);

    private static (string SessionId, SessionStatus Status) Session(
        string sessionId, int pid = 0) =>
        (sessionId, new SessionStatus { SessionPid = pid });

    // --- merging several accounts' rosters -----------------------------------

    [Fact]
    public void EveryAccountsSessionsAreOffered()
    {
        var merged = RemoteMirrorServer.MergeRosters(new[]
        {
            new[] { Agent("one", "s1") },
            new[] { Agent("two", "s2") }
        });

        Assert.Equal(new[] { "s1", "s2" }, merged.Select(e => e.SessionId));
    }

    [Fact]
    public void TheSameSessionSeenTwiceIsListedOnce()
    {
        // Two config dirs can point at overlapping state. Listing a session
        // twice would put two orbs on screen for one terminal.
        var merged = RemoteMirrorServer.MergeRosters(new[]
        {
            new[] { Agent("shared", "s1") },
            new[] { Agent("shared", "s1") }
        });

        Assert.Single(merged);
    }

    [Fact]
    public void TwoAccountsSharingANameKeepBothSessions()
    {
        // Deduplicated by session id, not by name — which is the case this
        // machine actually has. Collapsing by name would silently hide one of
        // two genuinely different terminals.
        var merged = RemoteMirrorServer.MergeRosters(new[]
        {
            new[] { Agent("job-hunter", "s1") },
            new[] { Agent("job-hunter", "s2") }
        });

        Assert.Equal(new[] { "s1", "s2" }, merged.Select(e => e.SessionId));
    }

    [Fact]
    public void TheFirstAccountWinsATie()
    {
        // Order is the tie-break, so the answer is stable from tick to tick
        // rather than flipping between two equally good candidates.
        var merged = RemoteMirrorServer.MergeRosters(new[]
        {
            new[] { Agent("first", "s1") },
            new[] { Agent("second", "s1") }
        });

        Assert.Equal("first", Assert.Single(merged).Name);
    }

    [Fact]
    public void NoAccountsIsNoSessions() =>
        Assert.Empty(RemoteMirrorServer.MergeRosters(Array.Empty<IReadOnlyList<AgentRoster.Entry>>()));

    [Fact]
    public void AnAccountWithNothingRunningContributesNothing()
    {
        var merged = RemoteMirrorServer.MergeRosters(new[]
        {
            Array.Empty<AgentRoster.Entry>(),
            new[] { Agent("only", "s1") }
        });

        Assert.Single(merged);
    }

    // --- picking the session a name refers to --------------------------------

    [Fact]
    public void ANameMatchingOneLiveSessionResolves()
    {
        var picked = RemoteMirrorServer.Pick(
            "jh", new[] { Agent("jh", "s1") }, new[] { Session("s1") });

        Assert.Equal("s1", picked?.SessionId);
    }

    [Fact]
    public void ANameSharedByTwoAccountsResolvesToTheOneBuddyActuallyHas()
    {
        // **The case that shipped broken.** The old rule refused any name two
        // roster entries shared, which is right inside one account and throws
        // away the only answer there is across two: only one of them is a
        // session this machine holds a status file for.
        var picked = RemoteMirrorServer.Pick(
            "job-hunter",
            new[] { Agent("job-hunter", "s1"), Agent("job-hunter", "s2") },
            new[] { Session("s2") });

        Assert.Equal("s2", picked?.SessionId);
    }

    [Fact]
    public void TwoLiveSessionsUnderOneNameAreStillRefused()
    {
        // What the original rule was protecting, and it still is. Guessing here
        // would type into one of two terminals at random, which is worse than
        // declining.
        Assert.Null(RemoteMirrorServer.Pick(
            "job-hunter",
            new[] { Agent("job-hunter", "s1"), Agent("job-hunter", "s2") },
            new[] { Session("s1"), Session("s2") }));
    }

    [Fact]
    public void ASessionMatchedByProcessIdStillCounts()
    {
        // The registry knows it and Buddy has not seen its hook fire yet, so
        // the ids do not line up but the process does.
        var picked = RemoteMirrorServer.Pick(
            "jh", new[] { Agent("jh", "registry-id", pid: 4242) },
            new[] { Session("buddy-id", pid: 4242) });

        Assert.Equal("buddy-id", picked?.SessionId);
    }

    [Fact]
    public void APidOfZeroIsNotAMatch()
    {
        // Every session with no pid recorded would otherwise match every agent
        // with none, which is the sort of match that puts one machine's
        // transcript under another machine's name.
        Assert.Null(RemoteMirrorServer.Pick(
            "jh", new[] { Agent("jh", "s1", pid: 0) }, new[] { Session("s2", pid: 0) }));
    }

    [Fact]
    public void ANameNoAccountKnowsResolvesToNothing() =>
        Assert.Null(RemoteMirrorServer.Pick(
            "stranger", new[] { Agent("jh", "s1") }, new[] { Session("s1") }));

    [Fact]
    public void AKnownNameWithNoLiveSessionResolvesToNothing() =>
        Assert.Null(RemoteMirrorServer.Pick(
            "jh", new[] { Agent("jh", "s1") }, Array.Empty<(string, SessionStatus)>()));

    [Fact]
    public void MatchingIsCaseInsensitiveOnBothNameAndId()
    {
        var picked = RemoteMirrorServer.Pick(
            "JH", new[] { Agent("jh", "S1") }, new[] { Session("s1") });

        Assert.Equal("s1", picked?.SessionId);
    }
}
