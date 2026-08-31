using Xunit;

namespace ClaudeBuddy.UnitTests;

// One orb per session, however many transports can see it.
//
// **Both can see the same session, and this is the normal case rather than an
// odd one.** A session running `claude --remote-control` is listed by any relay
// on that account *and* served over the direct link by the Buddy beside it — so
// a machine that was reachable before the link existed and still is produces two
// rows with the same Key, and the scan draws two orbs for one terminal.
//
// Caught on a live machine: the mini's session runs with `--remote-control`, so
// the moment the link came up it was on both lists.
public class OneOrbPerSessionTests
{
    private static RemoteControlSessions.Remote Row(
        string name, string via, string account = "acct", string status = "idle") =>
        new(name, via, status, DateTime.UnixEpoch, account);

    [Fact]
    public void ASessionBothTransportsSeeIsDrawnOnce()
    {
        var rows = RemoteControlSessions.OnePerSession(
            relayed: new[] { Row("job-hunter", "relay") },
            direct: new[] { Row("job-hunter", "avatar") });

        Assert.Single(rows);
    }

    [Fact]
    public void TheDirectRowIsTheOneKept()
    {
        // Not arbitrary. The two carry the same session and not the same
        // capability: a relay row offers a messaging channel unless the mirror
        // also answers, while a link row comes from a roster that already said
        // it can show the transcript. Keeping the relay row would put a "no live
        // view" panel on a session that has one.
        var rows = RemoteControlSessions.OnePerSession(
            relayed: new[] { Row("job-hunter", "claude-buddy-rc--claude-avatar") },
            direct: new[] { Row("job-hunter", "avatar") });

        Assert.Equal("avatar", Assert.Single(rows).Ref);
    }

    [Fact]
    public void ASessionOnlyTheRelayCanSeeIsStillDrawn()
    {
        // The whole reason the relay is still here: a machine on another
        // network is reachable that way and no other.
        var rows = RemoteControlSessions.OnePerSession(
            relayed: new[] { Row("far-away", "relay") },
            direct: Array.Empty<RemoteControlSessions.Remote>());

        Assert.Equal("far-away", Assert.Single(rows).Name);
    }

    [Fact]
    public void ASessionOnlyTheLinkCanSeeIsDrawn()
    {
        var rows = RemoteControlSessions.OnePerSession(
            relayed: Array.Empty<RemoteControlSessions.Remote>(),
            direct: new[] { Row("job-hunter", "avatar") });

        Assert.Equal("job-hunter", Assert.Single(rows).Name);
    }

    [Fact]
    public void TwoAccountsSharingASessionNameAreStillTwoOrbs()
    {
        // The Key carries the account precisely so two accounts holding
        // identically-named sessions do not collapse onto one orb — the same
        // person naming things the same way twice is the normal case. This must
        // not undo that.
        var rows = RemoteControlSessions.OnePerSession(
            relayed: Array.Empty<RemoteControlSessions.Remote>(),
            direct: new[] { Row("job-hunter", "avatar", "one"), Row("job-hunter", "avatar", "two") });

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void EverythingElseSurvivesAlongside()
    {
        var rows = RemoteControlSessions.OnePerSession(
            relayed: new[] { Row("shared", "relay"), Row("relay-only", "relay") },
            direct: new[] { Row("shared", "avatar"), Row("link-only", "avatar") });

        Assert.Equal(3, rows.Count);
        Assert.Equal("avatar", rows.Single(r => r.Name == "shared").Ref);
    }

    [Fact]
    public void TheDirectRowsComeFirstAndTheOrderIsStable()
    {
        // Rebuilt on every poll, so an order that depended on dictionary
        // iteration would reshuffle the orbs under the cursor.
        var rows = RemoteControlSessions.OnePerSession(
            relayed: new[] { Row("r1", "relay"), Row("r2", "relay") },
            direct: new[] { Row("d1", "avatar"), Row("d2", "avatar") });

        Assert.Equal(new[] { "d1", "d2", "r1", "r2" }, rows.Select(r => r.Name));
    }

    [Fact]
    public void NothingAnywhereIsNoOrbs()
    {
        Assert.Empty(RemoteControlSessions.OnePerSession(
            Array.Empty<RemoteControlSessions.Remote>(),
            Array.Empty<RemoteControlSessions.Remote>()));
    }

    [Fact]
    public void ADuplicateWithinOneTransportIsAlsoCollapsed()
    {
        // Two relays on two accounts can list the same session; before the
        // account went into the Key that was the bug this shape once had.
        var rows = RemoteControlSessions.OnePerSession(
            relayed: new[] { Row("jh", "a"), Row("jh", "b") },
            direct: Array.Empty<RemoteControlSessions.Remote>());

        Assert.Single(rows);
    }
}
