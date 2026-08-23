using Xunit;

namespace ClaudeBuddy.Tests;

// Covers RemoteControlSessions.Remote — the record the orb scan actually reads,
// and the only part of that class testable without starting a real Claude Code
// session. Everything else there is process lifecycle and a timer, which the
// opt-in live tests in tests/IntegrationTests exercise instead.
//
// Small surface, but both members are load-bearing in a way that fails quietly:
// a wrong Key silently merges two different machines' sessions into one orb, and
// a wrong Working leaves an orb claiming a remote session is idle while it works.
public class RemoteSnapshotTests
{
    private static RemoteControlSessions.Remote Remote(string name, string status = "idle") =>
        new(name, "94f106", status, DateTime.UtcNow);

    // Namespaced like OpenClaw's "openclaw:" keys, so one glance says which
    // source owns an id.
    [Fact]
    public void Key_IsNamespaced()
    {
        Assert.Equal("rc:job-hunter", Remote("job-hunter").Key);
    }

    // The collision that matters: SessionManager keys orbs by session id, and a
    // remote session named the same as a local one is a real possibility — the
    // same person tends to name things the same way on both machines. Without
    // the prefix they would land on one orb.
    [Fact]
    public void Key_DoesNotCollideWithALocalSessionOfTheSameName()
    {
        var remote = Remote("evidence").Key;

        Assert.NotEqual("evidence", remote);
        Assert.StartsWith("rc:", remote);
    }

    // The two values a real relay transcript actually showed for a remote peer:
    // "idle", and "running" while it worked. Both observed across four polls of
    // one live exchange with a session on another machine.
    //
    // "running" gets its own test because the first version of this code did not
    // match it. It was written against "busy", which is what `claude agents
    // --json` prints for a *local* session — a different vocabulary from the
    // peer list's, taken from the wrong source. The visible consequence was an
    // orb that sat perfectly still for the whole time a machine elsewhere was
    // working, which is the single thing an orb exists to show.
    [Theory]
    [InlineData("running", true)]
    [InlineData("idle", false)]
    [InlineData("", false)]
    // Tolerated but never observed on a peer row; kept so a vocabulary change
    // upstream degrades toward "working" rather than toward "asleep".
    [InlineData("busy", true)]
    [InlineData("working", true)]
    public void Working_ReadsTheStatusLabel(string status, bool expected)
    {
        Assert.Equal(expected, Remote("job-hunter", status).Working);
    }

    // The label's casing is upstream's to change, and an orb that stops
    // animating because someone capitalised a word would be a silly way to break.
    [Fact]
    public void Working_IgnoresCase()
    {
        Assert.True(Remote("job-hunter", "Running").Working);
        Assert.True(Remote("job-hunter", "RUNNING").Working);
    }

    // Off means nothing at all is published, so the scan never has to ask why —
    // and with the feature off by default this is the state on every machine
    // that has not opted in.
    [Fact]
    public void Snapshot_IsEmptyWhileTheFeatureIsOff()
    {
        // Not asserting on ClaudeBuddySettings here: this suite has no settings
        // isolation (that lives in IntegrationTests), and the default is off, so
        // the untouched state is the one worth checking.
        Assert.Empty(RemoteControlSessions.Snapshot());
    }
}
