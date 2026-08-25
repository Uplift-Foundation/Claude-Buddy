using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.UiTests;

// RemoteControlSessions' three remaining decisions: which sessions the orb scan
// sees, when a session going busy is worth telling anyone about, and when an
// unused relay has sat long enough to be shut down.
//
// Here rather than in tests/UnitTests because RaiseWorkingTransitions delivers
// through Dispatcher.UIThread.Post — it runs on the poll thread and the panel it
// notifies is a control.
//
// The relay table and the working-transition memory are process-wide statics, so
// this runs on the settings lane and clears both around every case.
[Collection("Settings")]
public class RemoteControlTransitionTests : IDisposable
{
    public RemoteControlTransitionTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        RemoteControlSessions.ClearRelaysForTests();
        RemoteControlSessions.ClearWorkingMemoryForTests();
        RemoteControlSessions.Republish();
    }

    private static RemoteControlSessions.Remote Remote(
        string name, string status, string account = "work@example.com") =>
        new(Name: name, Ref: "bridge:session_01", Status: status,
            Seen: DateTime.UtcNow, Account: account);

    private static List<string> Transitions(
        params RemoteControlSessions.Remote[] remotes)
    {
        var seen = new List<string>();
        void Watch(string key, bool working) => seen.Add(key + "=" + working);

        RemoteControlSessions.WorkingChanged += Watch;
        try
        {
            RemoteControlSessions.RaiseWorkingTransitions(remotes);

            // Posted from what is normally the poll thread, so draining the
            // dispatcher is what delivers it.
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            RemoteControlSessions.WorkingChanged -= Watch;
        }

        return seen;
    }

    // ---- what counts as working ------------------------------------------

    // "running" is the word the peer list actually uses. The other two are
    // tolerance rather than observation — the file says so, and says it is
    // exactly the mistake this repo's fixture rule exists to prevent: taking a
    // vocabulary from the wrong source instead of from the output being parsed.
    [Fact]
    public void TheStatusWordsThatMeanBusy()
    {
        Assert.True(Remote("zara", "running").Working);
        Assert.True(Remote("zara", "busy").Working);
        Assert.True(Remote("zara", "working").Working);
        Assert.False(Remote("zara", "idle").Working);
        Assert.False(Remote("zara", "").Working);
    }

    [Fact]
    public void TheStatusMatchIsCaseInsensitive()
    {
        Assert.True(Remote("zara", "RUNNING").Working);
        Assert.True(Remote("zara", "Working on it").Working);
    }

    // ---- transitions -----------------------------------------------------

    // First sight of a busy session is a transition, because the memory starts
    // empty and absent means not-working.
    [AvaloniaFact]
    public void ASessionSeenBusyForTheFirstTimeIsATransition()
    {
        var seen = Transitions(Remote("zara", "running"));

        Assert.Equal(new[] { "rc:work@example.com:zara=True" }, seen);
    }

    // First sight of an idle session is NOT a transition. Otherwise every relay
    // coming up would announce every session it found as having just stopped.
    [AvaloniaFact]
    public void ASessionSeenIdleForTheFirstTimeIsNotATransition()
    {
        Assert.Empty(Transitions(Remote("zara", "idle")));
    }

    // The whole point: the poll asks every few seconds, and a session that is
    // still busy must not be reported again each time.
    [AvaloniaFact]
    public void ASessionThatIsStillBusyIsNotReportedAgain()
    {
        Transitions(Remote("zara", "running"));

        Assert.Empty(Transitions(Remote("zara", "running")));
    }

    [AvaloniaFact]
    public void ASessionThatStopsIsReportedOnce()
    {
        Transitions(Remote("zara", "running"));

        Assert.Equal(new[] { "rc:work@example.com:zara=False" },
            Transitions(Remote("zara", "idle")));

        Assert.Empty(Transitions(Remote("zara", "idle")));
    }

    // Two accounts can hold identically-named sessions — the same person naming
    // things the same way twice is the normal case, not a corner one — so the
    // memory has to be keyed by account as well as name, or one session's
    // transition silently swallows the other's.
    [AvaloniaFact]
    public void TwoAccountsWithTheSameSessionNameAreTrackedSeparately()
    {
        var seen = Transitions(
            Remote("zara", "running", "work@example.com"),
            Remote("zara", "running", "home@example.com"));

        Assert.Equal(2, seen.Count);
        Assert.Contains("rc:work@example.com:zara=True", seen);
        Assert.Contains("rc:home@example.com:zara=True", seen);
    }

    [AvaloniaFact]
    public void SeveralSessionsChangingAtOnceAreAllReported()
    {
        Transitions(Remote("zara", "running"), Remote("kai", "running"));

        var seen = Transitions(Remote("zara", "idle"), Remote("kai", "idle"));

        Assert.Equal(2, seen.Count);
    }

    [AvaloniaFact]
    public void NothingToLookAtRaisesNothing()
    {
        Assert.Empty(Transitions());
    }

    // ---- Republish -------------------------------------------------------

    // Every relay's sessions flattened into the one list the orb scan reads. The
    // flattening is the point: an orb per remote session regardless of which
    // account it came through.
    [Fact]
    public void RepublishFlattensEveryRelaysSessions()
    {
        RemoteControlSessions.SetRelayForTests("work@example.com", "1 session",
            sessions: new[] { Remote("zara", "idle", "work@example.com") });
        RemoteControlSessions.SetRelayForTests("home@example.com", "1 session",
            sessions: new[] { Remote("kai", "idle", "home@example.com") });

        RemoteControlSessions.Republish();

        var names = RemoteControlSessions.SnapshotForTests.Select(r => r.Name).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains("zara", names);
        Assert.Contains("kai", names);
    }

    [Fact]
    public void RepublishWithNoRelaysLeavesAnEmptyList()
    {
        RemoteControlSessions.Republish();

        Assert.Empty(RemoteControlSessions.SnapshotForTests);
    }

    // ---- the idle shutdown ----------------------------------------------

    // The bridge is not free — it is a live Claude Code session on the other
    // machine — so it is shut down after a stretch of nobody looking. "Never" has
    // to mean never, or the setting is a lie.
    [Fact]
    public void IdleNeverMeansNeverHoweverLongItHasBeen()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.RemoteControlIdleMinutes = ClaudeBuddySettings.RemoteControlIdleNever;
        RemoteControlSessions.SetLastUseForTests(DateTime.UtcNow - TimeSpan.FromDays(30));

        Assert.False(RemoteControlSessions.IdleExpired());
    }

    [Fact]
    public void ARelayUsedJustNowHasNotExpired()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.RemoteControlIdleMinutes = 30;
        RemoteControlSessions.SetLastUseForTests(DateTime.UtcNow);

        Assert.False(RemoteControlSessions.IdleExpired());
    }

    [Fact]
    public void ARelayUntouchedForLongerThanTheSettingHasExpired()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.RemoteControlIdleMinutes = 30;
        RemoteControlSessions.SetLastUseForTests(DateTime.UtcNow - TimeSpan.FromMinutes(31));

        Assert.True(RemoteControlSessions.IdleExpired());
    }

    // Exactly at the boundary is not expired: the comparison is strictly
    // greater, so a relay used precisely the cutoff ago survives one more tick.
    [Fact]
    public void ARelayAtExactlyTheCutoffSurvives()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.RemoteControlIdleMinutes = 30;
        RemoteControlSessions.SetLastUseForTests(
            DateTime.UtcNow - TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(2));

        Assert.False(RemoteControlSessions.IdleExpired());
    }

    // A negative setting is not a shorter timeout — it is at or below "never",
    // so it must not shut a relay down instantly.
    [Fact]
    public void ANegativeIdleSettingIsTreatedAsNever()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.RemoteControlIdleMinutes = -5;
        RemoteControlSessions.SetLastUseForTests(DateTime.UtcNow - TimeSpan.FromDays(1));

        Assert.False(RemoteControlSessions.IdleExpired());
    }

    // ---- a colour learned after the fact -----------------------------------

    // A session answers the colour question on its own schedule, which is often
    // after its orb is already on screen. Re-stamping the published snapshot is
    // what gets that colour onto the orb at the next scan rather than at the next
    // poll — the difference between a couple of seconds and up to a minute.
    [Fact]
    public void AColourLearnedLaterReachesAnAlreadyPublishedSession()
    {
        RemoteControlSessions.ForgetAnswersForTests();
        RemoteControlSessions.SetRelayForTests("work@example.com", "1 session",
            sessions: new[] { Remote("zara", "idle") });
        RemoteControlSessions.Republish();

        Assert.Null(RemoteControlSessions.SnapshotForTests.Single().Color);

        RemoteControlSessions.OnMessage("work@example.com",
            new BridgeProtocol.InboundMessage(
                FromName: "zara", From: "bridge:session_01", Mode: "prompting",
                Body: BridgeProtocol.InfoMarker + " color=#ff0000"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("#ff0000", RemoteControlSessions.SnapshotForTests.Single().Color);
    }

    // A session with no answer keeps the colour it already had rather than being
    // blanked by the re-stamp.
    [Fact]
    public void ReStampingLeavesAnUnansweredSessionAlone()
    {
        RemoteControlSessions.ForgetAnswersForTests();
        RemoteControlSessions.SetRelayForTests("work@example.com", "1 session",
            sessions: new[] { Remote("kai", "idle") });
        RemoteControlSessions.Republish();

        RemoteControlSessions.RepublishWithColors();

        Assert.Null(RemoteControlSessions.SnapshotForTests.Single().Color);
    }

    // ---- keeping a relay alive ---------------------------------------------

    // Touch is called on every send, so a relay someone is actively using is not
    // idled out from under them mid-conversation.
    [Fact]
    public void TouchingARelayKeepsItFromExpiring()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.RemoteControlIdleMinutes = 30;
        RemoteControlSessions.SetLastUseForTests(DateTime.UtcNow - TimeSpan.FromHours(1));

        Assert.True(RemoteControlSessions.IdleExpired());

        RemoteControlSessions.Touch();

        Assert.False(RemoteControlSessions.IdleExpired());
    }
}
