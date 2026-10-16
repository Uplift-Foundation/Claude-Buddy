using Xunit;

namespace Orbweaver.UnitTests;

// One orb per session key.
//
// This used to merge two transports — a relay's rows and the direct link's — and
// keep the link's when both listed one session, because two orbs for one
// terminal was the "two Claude Buddys" complaint in a smaller form. The relay is
// gone (937de9ec, and CB-238 for its leftovers), so there is one list; but a
// roster can still name one key twice in a moment of churn, and one orb per key
// is still the rule.
public class OneOrbPerSessionTests
{
    private static RemoteControlSessions.Remote Row(
        string name, string via, string account = "acct", string status = "idle") =>
        new(name, via, status, DateTime.UnixEpoch, account);

    [Fact]
    public void ADuplicateKeyIsCollapsedAndTheFirstRowKept()
    {
        var first = Row("job-hunter", "avatar", status: "running");
        var rows = RemoteControlSessions.OnePerSession(new[] { first, Row("job-hunter", "avatar") });

        Assert.Same(first, Assert.Single(rows));
    }

    // Keys compare without case, the way the scan's dictionary does.
    [Fact]
    public void KeysDifferingOnlyInCaseAreOneSession()
    {
        var rows = RemoteControlSessions.OnePerSession(new[] { Row("Job-Hunter", "a"), Row("job-hunter", "b") });

        Assert.Single(rows);
    }

    [Fact]
    public void TwoAccountsSharingASessionNameAreStillTwoOrbs()
    {
        var rows = RemoteControlSessions.OnePerSession(new[]
        {
            Row("job-hunter", "avatar", "work@example.com"),
            Row("job-hunter", "avatar", "home@example.com")
        });

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void TheOrderIsTheRostersOrder()
    {
        var rows = RemoteControlSessions.OnePerSession(new[] { Row("b", "m"), Row("a", "m"), Row("b", "m"), Row("c", "m") });

        Assert.Equal(new[] { "b", "a", "c" }, rows.Select(r => r.Name));
    }

    [Fact]
    public void NothingIsNoOrbs()
    {
        Assert.Empty(RemoteControlSessions.OnePerSession(Array.Empty<RemoteControlSessions.Remote>()));
    }
}
