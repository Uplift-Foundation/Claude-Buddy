using Xunit;

namespace ClaudeBuddy.UnitTests;

// Where the list of local sessions comes from when another machine asks.
//
// **This used to prefer the orb list and fall back to a scan, and the
// preference was the bug.** The orb list is what is on screen, and what is on
// screen has had the user's orb-lifetime preference applied to it — so an idle
// session that had stopped being drawn was reported to every other machine as
// not existing. On a headless Mac it was worse: that list is filled by a scan on
// an Avalonia dispatcher that never pumps while the screen stays locked, so it
// was not merely filtered but permanently empty.
//
// Watched happen on real hardware. The mini answered one roster with a session
// and, an hour later, the same roster with none — from two live status files on
// its disk the whole time. Nothing errored. The far machine showed a session it
// could see, could not open, and was told did not exist.
//
// Serving is a question of fact, so the disk is asked.
[Collection("Settings")]
public class HeadlessSessionsTests : IDisposable
{
    private readonly Func<IReadOnlyList<(string SessionId, SessionStatus Status)>> _realScan
        = RemoteControlSessions.HeadlessFallback;

    public void Dispose()
    {
        RemoteControlSessions.HeadlessFallback = _realScan;
        RemoteControlSessions.ResetForTests();
    }

    private static (string SessionId, SessionStatus Status) Session(string id) =>
        (id, new SessionStatus());

    private static IReadOnlyList<(string SessionId, SessionStatus Status)> One(string id) =>
        new[] { Session(id) };

    [Fact]
    public void TheDiskIsWhatAnswers()
    {
        RemoteControlSessions.ResetForTests();
        RemoteControlSessions.HeadlessFallback = () => One("from-disk");

        Assert.Equal("from-disk", Assert.Single(RemoteControlSessions.LocalSessions()).SessionId);
    }

    [Fact]
    public void AnEmptyDiskIsAnAnswerRatherThanAProblem()
    {
        RemoteControlSessions.ResetForTests();
        RemoteControlSessions.HeadlessFallback = () => Array.Empty<(string, SessionStatus)>();

        Assert.Empty(RemoteControlSessions.LocalSessions());
    }

    [Fact]
    public void AskedTwiceInAMomentItScansOnce()
    {
        // A peer asks every ten seconds and the scan reads a directory and a job
        // listing. Several peers asking together should cost one scan, not one
        // each.
        RemoteControlSessions.ResetForTests();

        var scans = 0;
        RemoteControlSessions.HeadlessFallback = () => { scans++; return One("s1"); };

        RemoteControlSessions.LocalSessions();
        RemoteControlSessions.LocalSessions();
        RemoteControlSessions.LocalSessions();

        Assert.Equal(1, scans);
    }

    [Fact]
    public void AMomentLaterItScansAgain()
    {
        // Short enough that a session starting is noticed at once. Driven by the
        // injectable clock rather than by sleeping, which is how this repository
        // has fixed four flakes and would rather not fix a fifth.
        RemoteControlSessions.ResetForTests();

        var start = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        var now = start;
        RemoteControlSessions.Now = () => now;

        var scans = 0;
        RemoteControlSessions.HeadlessFallback = () => { scans++; return One("s1"); };

        RemoteControlSessions.LocalSessions();
        now = start.AddSeconds(5);
        RemoteControlSessions.LocalSessions();

        Assert.Equal(2, scans);
    }

    [Fact]
    public void EverySessionSurvivesTheAnswer()
    {
        // Whichever list comes back is passed through whole. What to *show* is
        // decided further up, and a roster that quietly dropped one would be the
        // same class of bug as the one this file exists for.
        RemoteControlSessions.ResetForTests();
        RemoteControlSessions.HeadlessFallback =
            () => new[] { Session("a"), Session("b"), Session("c") };

        Assert.Equal(3, RemoteControlSessions.LocalSessions().Count);
    }
}
