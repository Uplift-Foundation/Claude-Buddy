using System;
using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// Presence on the orb: the third axis, beside identity and state. A parked
// background job or an orphaned teammate is dimmed and held still, and it wears
// the gear badge whether it is parked or working.
//
// The failure this guards against is the one the whole ticket is about, and it
// is a failure of *rendering* rather than of classification — the rules are
// covered per case in tests/UnitTests/SessionPresenceTests.cs, and none of that
// helps if UpdateFrom does not carry the answer onto the window. Fifteen orbs
// breathing at full opacity on an idle machine was exactly that gap: everything
// needed to tell them apart was parsed, and then discarded.
//
// [Collection("Settings")] because constructing an OrbWindow reads a colour
// setting in a field initializer, and ClaudeBuddySettings is a process-wide
// static — see tests/UiTests/SettingsCollection.cs.
[Collection("Settings")]
public class OrbWindowPresenceTests
{
    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static SessionStatus Status(
        OrbPresence presence = OrbPresence.Present,
        SessionKind kind = SessionKind.Background,
        string state = "idle",
        int pid = 4321,
        SessionSource source = SessionSource.ClaudeCode) =>
        new()
        {
            Source = source,
            State = state,
            Kind = kind,
            Presence = presence,
            SessionPid = pid,
            Cwd = "/Users/warren/project",
            Shape = kind == SessionKind.Background
                ? LocalSessionShape.Background
                : LocalSessionShape.Terminal,
        };

    // The shared pulse roster, which is what "held still" actually means — a
    // parked orb has to leave it, or the ticker keeps breathing it 20 times a
    // second. Private static, read the way TrayRemoteItemTests reaches
    // TrayController's own private menu rather than widened for a test.
    private static bool IsOnThePulseRoster(OrbWindow orb)
    {
        var field = typeof(OrbWindow).GetField(
            "Pulsing", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var roster = (IList)field!.GetValue(null)!;
        for (var i = 0; i < roster.Count; i++)
        {
            if (ReferenceEquals(roster[i], orb)) return true;
        }

        return false;
    }

    private static Border Badge(OrbWindow orb, string name) =>
        orb.FindControl<Border>(name)!;

    private static string Glyph(OrbWindow orb, string name) =>
        orb.FindControl<TextBlock>(name)!.Text ?? "";

    private static double Opacity(OrbWindow orb)
    {
        var root = orb.FindControl<Grid>("Root");
        Assert.NotNull(root);
        return root!.Opacity;
    }

    // --- dimming ------------------------------------------------------------

    [AvaloniaFact]
    public void AParkedSessionIsDimmedAndHeldStill()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        // Working first, so this is a transition rather than an initial value —
        // the same order the scan produces when a job's turn ends.
        orb.UpdateFrom(Status(OrbPresence.Present, state: "generating"));
        Assert.Equal(OrbPresence.Present, orb.Presence);

        orb.UpdateFrom(Status(OrbPresence.NeedsInput));

        Assert.NotEqual(OrbPresence.Present, orb.Presence);
        Assert.True(Opacity(orb) < 1.0);
        Assert.False(IsOnThePulseRoster(orb));
    }

    [AvaloniaFact]
    public void AnUnparkedSessionIsDrawnAtFullOpacity()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.Present));

        Assert.Equal(OrbPresence.Present, orb.Presence);
        Assert.Equal(1.0, Opacity(orb));
    }

    [AvaloniaFact]
    public void ResumingWorkRestoresTheOpacity()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.NeedsInput));
        Assert.True(Opacity(orb) < 1.0);

        orb.UpdateFrom(Status(OrbPresence.Present, state: "generating"));

        Assert.Equal(OrbPresence.Present, orb.Presence);
        Assert.Equal(1.0, Opacity(orb));
    }

    // The un-park arm's other half, which only runs on a window that is
    // genuinely Loaded — a job resuming has to start breathing again, and every
    // branch of ApplyState ends in a StartPulse, so being back on the roster is
    // what proves the state was re-applied rather than merely un-dimmed.
    //
    // Shown, never closed, per every sibling in this suite: closing a headless
    // window corrupts a process-wide font cache.
    [AvaloniaFact]
    public void AResumedJobIsPutBackOnThePulseRosterOnceItIsLoaded()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.Show();
        Flush();
        Assert.True(orb.IsLoaded);

        orb.UpdateFrom(Status(OrbPresence.NeedsInput));
        Assert.False(IsOnThePulseRoster(orb));

        orb.UpdateFrom(Status(OrbPresence.Present, state: "generating"));

        Assert.True(IsOnThePulseRoster(orb));
        Assert.Equal(1.0, Opacity(orb));
    }

    // Applied last in UpdateFrom, after the state block — which is the thing
    // that starts motion. In the other order an orb that parked and changed
    // state in one update would be left dim and breathing, which is the one
    // combination that reads as a bug rather than as a state.
    [AvaloniaFact]
    public void ParkingAndAStateChangeInOneUpdateStillEndsStill()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.Show();
        Flush();

        orb.UpdateFrom(Status(OrbPresence.Present, state: "generating"));
        orb.UpdateFrom(Status(OrbPresence.NeedsInput, state: "idle"));

        Assert.NotEqual(OrbPresence.Present, orb.Presence);
        Assert.False(IsOnThePulseRoster(orb));
        Assert.True(Opacity(orb) < 1.0);
    }

    // The force arm, whose only caller in the app is StopRecording — handing the
    // orb's motion back after dictation, which puts it on the pulse roster
    // whether or not it is parked. Driven directly here because StopRecording
    // itself needs a live VoiceRecorder and a Whisper model load, and is
    // excluded from coverage for exactly that reason.
    [AvaloniaFact]
    public void PresenceCanBeReAssertedAfterSomethingElseTookOverTheMotion()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.Show();
        Flush();

        orb.UpdateFrom(Status(OrbPresence.NeedsInput));
        Assert.False(IsOnThePulseRoster(orb));

        // What the mic does: motion, on a parked orb, from outside the presence
        // axis. The un-forced call then declines, since nothing about the
        // presence changed — which is the whole reason force exists.
        orb.ApplyState("generating");
        Assert.True(IsOnThePulseRoster(orb));

        orb.ApplyPresence(OrbPresence.NeedsInput);
        Assert.True(IsOnThePulseRoster(orb));

        orb.ApplyPresence(OrbPresence.NeedsInput, force: true);

        Assert.NotEqual(OrbPresence.Present, orb.Presence);
        Assert.False(IsOnThePulseRoster(orb));
        Assert.True(Opacity(orb) < 1.0);
    }

    // A session that has never reported a state at all — an orb built from a
    // status file whose state field was empty. Un-parking has to fall back to
    // "idle" rather than handing ApplyState an empty string, which is the same
    // fallback the Loaded handler and ReapplyStateColors already make.
    [AvaloniaFact]
    public void UnparkingASessionThatNeverReportedAStateFallsBackToIdle()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.Show();
        Flush();

        orb.UpdateFrom(Status(OrbPresence.NeedsInput, state: ""));
        Assert.NotEqual(OrbPresence.Present, orb.Presence);

        orb.UpdateFrom(Status(OrbPresence.Present, state: ""));

        Assert.Equal(OrbPresence.Present, orb.Presence);
        Assert.Equal(1.0, Opacity(orb));

        // Idle's slow breath, which is what ApplyState's default arm starts.
        Assert.True(IsOnThePulseRoster(orb));
    }

    // --- the two presence marks ---------------------------------------------

    // The daemon's own taxonomy calls a blocked job "Needs input", and several of
    // the ones this was written for are literally holding a question — so plain
    // dimming undersold them. Dim *and* marked: dim because it is not competing
    // with live work, marked because "there is something here for you" is the
    // opposite of what dimming alone says.
    [AvaloniaFact]
    public void ASessionNeedingInputIsDimmedAndMarked()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.NeedsInput));

        Assert.True(Opacity(orb) < 1.0);
        Assert.Equal("needs input", orb.PresenceLabel);

        var badge = Badge(orb, "PresenceBadge");
        Assert.True(badge.IsVisible);
        Assert.Equal("?", Glyph(orb, "PresenceGlyph"));
    }

    // A finished job: dimmed the same, marked differently. The two are opposite
    // instructions — one wants you, one wants nothing ever again — and being
    // unmistakable from across a screen is the whole reason they are two states
    // rather than one dim one.
    [AvaloniaFact]
    public void AFinishedSessionIsDimmedAndMarkedDifferently()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.Finished));

        Assert.True(Opacity(orb) < 1.0);
        Assert.Equal("finished", orb.PresenceLabel);
        Assert.True(Badge(orb, "PresenceBadge").IsVisible);
        Assert.Equal("✓", Glyph(orb, "PresenceGlyph"));
    }

    // An orphaned team member: dimmed, and deliberately unmarked. Nothing is
    // waiting on the user and nothing has finished, so there is nothing to say
    // beyond the dimming — and both marks are the daemon's vocabulary, which has
    // never heard of this session.
    [AvaloniaFact]
    public void AnOrphanedTeammateIsDimmedWithNoMark()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.Parked, kind: SessionKind.Unknown));

        Assert.True(Opacity(orb) < 1.0);
        Assert.Null(orb.PresenceLabel);
        Assert.False(Badge(orb, "PresenceBadge").IsVisible);
    }

    // A present session carries no mark at all, which is most orbs most of the
    // time — the same argument the kind badges are held to.
    [AvaloniaFact]
    public void APresentSessionCarriesNoPresenceMark()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        Assert.Null(orb.PresenceLabel);
        Assert.False(Badge(orb, "PresenceBadge").IsVisible);
    }

    // The mark goes away again when the session comes back, which is the case a
    // one-way "apply the badge" would quietly get wrong: an attach un-dims a
    // parked job, and a stale "?" on a session somebody is sitting in says the
    // opposite of what is true.
    [AvaloniaFact]
    public void ComingBackToLifeTakesTheMarkAwayWithTheDimming()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.NeedsInput));
        Assert.True(Badge(orb, "PresenceBadge").IsVisible);

        orb.UpdateFrom(Status(OrbPresence.Present, state: "generating"));

        Assert.False(Badge(orb, "PresenceBadge").IsVisible);
        Assert.Equal(1.0, Opacity(orb));
    }

    // Both marks live in their own corner. The kind badge is bottom-right and the
    // heart is top-right, and a session can want two of the three said at once —
    // a background job (gear) holding a question (mark) — so a collision would be
    // one mark hidden under another rather than a layout nitpick.
    [AvaloniaFact]
    public void ThePresenceMarkAndTheKindBadgeOccupyDifferentCorners()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.NeedsInput));

        var presence = Badge(orb, "PresenceBadge");
        var kind = Badge(orb, "KindBadge");

        Assert.True(presence.IsVisible);
        Assert.True(kind.IsVisible);

        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Left, presence.HorizontalAlignment);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Top, presence.VerticalAlignment);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, kind.HorizontalAlignment);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Bottom, kind.VerticalAlignment);
    }

    // A team member's orb is drawn smaller, and every badge has to move with the
    // circle or it floats off the rim. The presence mark joins the same sum the
    // other two use rather than carrying its own copy of it.
    [AvaloniaFact]
    public void ThePresenceMarkShrinksOntoATeamMembersSmallerRim()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.NeedsInput));

        var full = Badge(orb, "PresenceBadge").Margin;

        var status = Status(OrbPresence.NeedsInput);
        status.Lead = "lead-session-id";
        orb.UpdateFrom(status);

        var member = Badge(orb, "PresenceBadge").Margin;

        Assert.True(member.Left > full.Left);
        Assert.Equal(member.Left, member.Top);
        Assert.Equal(Badge(orb, "KindBadge").Margin.Right, member.Left);
    }

    // --- the gear badge -----------------------------------------------------

    // The badge says what a session *is*, which does not change while it runs.
    // Whether anything is happening in it rides the opacity instead — so a
    // working job and a parked one wear the same mark, and only one of them is
    // dim. Badging only the parked ones would smuggle a state into the kind
    // channel.
    [AvaloniaTheory]
    [InlineData(OrbPresence.NeedsInput)]
    [InlineData(OrbPresence.Present)]
    [InlineData(OrbPresence.Finished)]
    public void ABackgroundJobWearsTheGearWhateverItsPresence(OrbPresence presence)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(presence));

        Assert.Equal("background job", orb.KindLabel);
        Assert.Equal("⚙", orb.KindGlyphText);

        var badge = orb.FindControl<Border>("KindBadge");
        Assert.NotNull(badge);
        Assert.True(badge!.IsVisible);
    }

    [AvaloniaFact]
    public void AnOrdinaryTerminalSessionStillWearsNoBadgeAtAll()
    {
        // The regression this ticket could most easily have caused: a mark on
        // almost every orb distinguishes nothing, and every local session was
        // unbadged before the gear existed.
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(kind: SessionKind.Unknown));

        Assert.Null(orb.KindLabel);
        Assert.False(orb.FindControl<Border>("KindBadge")!.IsVisible);
    }

    // --- the two menu items -------------------------------------------------

    [AvaloniaFact]
    public void BothLifecycleItemsAreOfferedForALocalSessionWithAPid()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        Assert.True(orb.FindControl<MenuItem>("DismissItem")!.IsVisible);
        Assert.True(orb.FindControl<MenuItem>("EndSessionItem")!.IsVisible);
    }

    // A hook older than the session_pid field. There is nothing to signal, so
    // End is not offered — but the file is still there to be dismissed.
    [AvaloniaFact]
    public void ASessionWithNoRecordedPidIsOfferedDismissButNotEnd()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(pid: 0));

        Assert.True(orb.FindControl<MenuItem>("DismissItem")!.IsVisible);
        Assert.False(orb.FindControl<MenuItem>("EndSessionItem")!.IsVisible);
    }

    // A gateway or bridged session's orb comes from a socket: there is no file
    // to delete and no local process to end, so neither item appears. Hidden
    // rather than greyed out — the answer to "why not" is "this conversation
    // lives on another machine", which is not something a menu can say.
    [AvaloniaTheory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void NeitherItemIsOfferedForASessionThatLivesSomewhereElse(SessionSource source)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(kind: SessionKind.Remote, source: source));

        Assert.False(orb.FindControl<MenuItem>("DismissItem")!.IsVisible);
        Assert.False(orb.FindControl<MenuItem>("EndSessionItem")!.IsVisible);
    }

    // Both handlers with no SessionManager current, which is this suite's
    // situation: making one current would start the status-directory watcher,
    // the scan timer and a tray icon. The null-conditional is the whole body, so
    // what is asserted is that clicking either does nothing at all rather than
    // throwing — and, more to the point, that neither can reach a real session's
    // file or pid from a test process.
    [AvaloniaFact]
    public void NeitherHandlerDoesAnythingWithNoSessionManagerCurrent()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        Assert.Null(SessionManager.Instance);

        orb.Dismiss_Click(null, new Avalonia.Interactivity.RoutedEventArgs());
        orb.EndSession_Click(null, new Avalonia.Interactivity.RoutedEventArgs());
    }
}
