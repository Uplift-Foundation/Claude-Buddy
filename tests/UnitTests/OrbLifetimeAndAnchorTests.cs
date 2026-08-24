using Avalonia;
using Xunit;

namespace ClaudeBuddy.Tests;

// The three places SessionManager reads a setting and turns it into a rule:
// how long an orb outlives its session, whether a CLI is tracked at all, and
// where an arrangement gets drawn.
//
// Every one of these setters writes settings.json, and TestBootstrap points the
// whole assembly at one temp directory — so a value left behind here is the
// value every later test in every other class reads. The save/restore is not
// tidiness; TrayRemoteItemTests records what it cost to learn that, where a
// leaked RemoteControlEnabled made an unrelated test pass or fail on nothing
// but which class the runner reached first.
public class OrbLifetimeAndAnchorTests
{
    // --- StaleAfter ----------------------------------------------------------

    [Fact]
    public void ForeverIsNullRatherThanAVeryLongTimeSpan()
    {
        // Null is what JudgeLiveness checks for, and the distinction is real:
        // "forever" has to skip the comparison entirely rather than compare
        // against some large number, because the state a user reaches for it in
        // is precisely the one where a session has been quiet for days.
        var before = ClaudeBuddySettings.OrbLifetimeMinutes;
        try
        {
            ClaudeBuddySettings.OrbLifetimeMinutes = ClaudeBuddySettings.OrbLifetimeForever;
            Assert.Null(SessionManager.StaleAfter);

            ClaudeBuddySettings.OrbLifetimeMinutes = 30;
            Assert.Equal(30, SessionManager.StaleAfter?.TotalMinutes);
        }
        finally
        {
            ClaudeBuddySettings.OrbLifetimeMinutes = before;
        }
    }

    // --- EnabledFor ----------------------------------------------------------

    [Fact]
    public void ACliSwitchedOffIsIgnoredWhileEverythingWithNoSwitchIsKept()
    {
        // OpenClaw and RemoteControl have no display switch here on purpose:
        // OpenClaw's own toggle means something stronger and is consulted where
        // the gateway is asked, and there is nothing to gate a remote session
        // on beyond the bridge already being running.
        var claude = ClaudeBuddySettings.ClaudeCodeEnabled;
        var codex = ClaudeBuddySettings.CodexEnabled;
        try
        {
            ClaudeBuddySettings.ClaudeCodeEnabled = false;
            ClaudeBuddySettings.CodexEnabled = false;

            Assert.False(SessionManager.EnabledFor(SessionSource.ClaudeCode));
            Assert.False(SessionManager.EnabledFor(SessionSource.Codex));
            Assert.True(SessionManager.EnabledFor(SessionSource.OpenClaw));
            Assert.True(SessionManager.EnabledFor(SessionSource.RemoteControl));

            ClaudeBuddySettings.ClaudeCodeEnabled = true;
            ClaudeBuddySettings.CodexEnabled = true;

            Assert.True(SessionManager.EnabledFor(SessionSource.ClaudeCode));
            Assert.True(SessionManager.EnabledFor(SessionSource.Codex));
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeEnabled = claude;
            ClaudeBuddySettings.CodexEnabled = codex;
        }
    }

    // --- ArrangementAnchor / ShiftArrangementAnchor --------------------------

    [Fact]
    public void TheFirstArrangementEverIsCentredAndThenRemembered()
    {
        // The remembering is the point of it: without a saved anchor, an orb
        // joining or leaving would re-fit the shape around the screen's middle
        // rather than around wherever the user has since dragged it, so the
        // whole arrangement would jump every time a session started.
        var before = ClaudeBuddySettings.ArrangeAnchor;
        try
        {
            ClaudeBuddySettings.ArrangeAnchor = null;

            var work = new PixelRect(0, 0, 1920, 1080);
            var first = SessionManager.ArrangementAnchor(work);

            Assert.Equal(new PixelPoint(960, 540), first);
            Assert.Equal(new ClaudeBuddySettings.OrbPlacement(960, 540), ClaudeBuddySettings.ArrangeAnchor);

            // Asked again about a *different* screen, it still answers with what
            // was saved — the shape stays where it is rather than following the
            // work area around.
            Assert.Equal(
                first,
                SessionManager.ArrangementAnchor(new PixelRect(0, 0, 800, 600)));
        }
        finally
        {
            ClaudeBuddySettings.ArrangeAnchor = before;
        }
    }

    [Fact]
    public void DraggingTheWholeShapeMovesItsSavedCentreByTheSameDelta()
    {
        // A whole-shape drag moves every arranged orb by one delta; without the
        // anchor getting the same nudge, the next session to start or end would
        // snap the shape back to where it was before the drag.
        var before = ClaudeBuddySettings.ArrangeAnchor;
        try
        {
            ClaudeBuddySettings.ArrangeAnchor = new ClaudeBuddySettings.OrbPlacement(500, 400);

            new SessionManager().ShiftArrangementAnchor(-120, 60);

            Assert.Equal(new ClaudeBuddySettings.OrbPlacement(380, 460), ClaudeBuddySettings.ArrangeAnchor);
        }
        finally
        {
            ClaudeBuddySettings.ArrangeAnchor = before;
        }
    }

    [Fact]
    public void AZeroDeltaAndAnUnarrangedShapeBothLeaveTheAnchorAlone()
    {
        var before = ClaudeBuddySettings.ArrangeAnchor;
        try
        {
            var manager = new SessionManager();

            // Nothing arranged yet, so there is no anchor to move — and a save
            // here would invent one, which would then be honoured as "where the
            // shape already is" the first time somebody did arrange.
            ClaudeBuddySettings.ArrangeAnchor = null;
            manager.ShiftArrangementAnchor(10, 10);
            Assert.Null(ClaudeBuddySettings.ArrangeAnchor);

            ClaudeBuddySettings.ArrangeAnchor = new ClaudeBuddySettings.OrbPlacement(500, 400);
            manager.ShiftArrangementAnchor(0, 0);
            Assert.Equal(new ClaudeBuddySettings.OrbPlacement(500, 400), ClaudeBuddySettings.ArrangeAnchor);
        }
        finally
        {
            ClaudeBuddySettings.ArrangeAnchor = before;
        }
    }
}
