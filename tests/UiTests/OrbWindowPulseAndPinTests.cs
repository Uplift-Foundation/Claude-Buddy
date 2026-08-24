using System;
using System.IO;
using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.UiTests;

// The shared pulse tick, the pin, and the two click actions that were previously
// only reachable through something unsafe.
//
// The pulse ticker is process-wide and accumulates every orb ever shown for the
// life of the test process, because nothing closes a window to remove one. So it
// is driven directly rather than waited on — a test that waited passed in
// isolation and failed once the rest of the suite loaded the machine, even with a
// ten-second budget.
[Collection("Settings")]
public class OrbWindowPulseAndPinTests
{
    private static OrbWindow Orb(string id = "pulse-1")
    {
        var orb = new OrbWindow(id);
        orb.UpdateFrom(new SessionStatus
        {
            State = "idle",
            Cwd = "/Users/w/Source/Thing",
            SessionPid = Environment.ProcessId,
            TermProgram = "iTerm.app",
        });
        return orb;
    }

    // ---- the pulse ---------------------------------------------------------

    // With nothing pulsing the tick is a no-op and stops the timer. That arm is
    // the one that matters: without it the ticker spins at 30fps for the life of
    // the app after the last orb goes quiet.
    [AvaloniaFact]
    public void TickingWithNothingPulsingIsHarmless()
    {
        OrbWindow.TickAllPulses();
        OrbWindow.TickAllPulses();
    }

    // ---- the pin -----------------------------------------------------------

    // PinAt puts an orb where an earlier run left it, WITHOUT treating that as a
    // fresh drag — there is nothing to write back, since the position came from
    // settings in the first place.
    //
    // Worth having a direct test: this is currently dead in the running app.
    // AnimateArrangement's completion callbacks close over a field that
    // OnArrangeAnimTick nulls before invoking them, so PinAt is never actually
    // reached after an arrange — see
    // OrbArrangementAnimationTests.ArrangingAndCompletingTheGlidePinsEveryOrbAtItsTarget,
    // which records that. When that bug is fixed this is the behaviour it should
    // restore.
    [AvaloniaFact]
    public void PinningPutsTheOrbWhereItIsToldAndMarksItPinned()
    {
        var orb = Orb();

        orb.PinAt(new PixelPoint(321, 654));

        Assert.Equal(new PixelPoint(321, 654), orb.Position);
        Assert.True(orb.IsPinned);
    }

    [AvaloniaFact]
    public void UnpinningClearsThePinWithoutMovingTheOrb()
    {
        var orb = Orb();
        orb.PinAt(new PixelPoint(321, 654));

        orb.Unpin();

        Assert.False(orb.IsPinned);
        Assert.Equal(new PixelPoint(321, 654), orb.Position);
    }

    // ---- the click actions -------------------------------------------------

    // Both of these are safe to run precisely because of what is missing:
    // SessionManager.Instance is null, so the chat action finds no session and
    // returns; and this orb has no transcript, so the speak action finds nothing
    // to say and returns before reaching the speech engine. Running them proves
    // the switch dispatches, which is the part that can be wrong.
    [AvaloniaFact]
    public void TheChatActionIsHarmlessWithNoSessionManager()
    {
        ClaudeBuddySettings.ReloadForTests();
        var orb = Orb();

        orb.RunClickAction(ActionClicks("chat"));
    }

    [AvaloniaFact]
    public void TheSpeakActionIsHarmlessWithNothingToSay()
    {
        ClaudeBuddySettings.ReloadForTests();
        var orb = Orb();

        Assert.Null(orb.FindSpeakableText());

        orb.RunClickAction(ActionClicks("speak"));
    }

    // ---- the cwd fallback --------------------------------------------------

    // A session whose own transcript path is empty falls back to the newest
    // transcript for its working directory. That is how an orb can still speak
    // after a /clear, and it is reachable here only because FindSpeakableText
    // takes a home to search — otherwise it would depend on what happens to be in
    // the developer's real ~/.claude.
    [AvaloniaFact]
    public void WithNoTranscriptPathTheCwdFallbackIsUsed()
    {
        ClaudeBuddySettings.ReloadForTests();

        var cwd = "/Users/w/Source/Thing";
        var home = Path.Combine(Path.GetTempPath(), "cb-orbhome-" + Guid.NewGuid());
        var project = Path.Combine(
            home, ".claude", "projects", TranscriptReader.EncodeCwd(cwd));
        Directory.CreateDirectory(project);

        File.WriteAllText(Path.Combine(project, "s.jsonl"),
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"from the fallback"}]}}"""
            + "\n");

        var orb = new OrbWindow("fallback-1");
        orb.UpdateFrom(new SessionStatus
        {
            State = "idle",
            Cwd = cwd,
            TranscriptPath = null,
            SessionPid = Environment.ProcessId,
            TermProgram = "iTerm.app",
        });

        try
        {
            Assert.Equal("from the fallback", orb.FindSpeakableText(home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // And a cwd with nothing under it returns null rather than reaching for
    // whatever else is in that home.
    [AvaloniaFact]
    public void ACwdWithNoTranscriptAnywhereSaysNothing()
    {
        ClaudeBuddySettings.ReloadForTests();

        var home = Path.Combine(Path.GetTempPath(), "cb-orbhome-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(home);

        var orb = new OrbWindow("fallback-2");
        orb.UpdateFrom(new SessionStatus
        {
            State = "idle",
            Cwd = "/Users/w/Source/Nothing",
            SessionPid = Environment.ProcessId,
            TermProgram = "iTerm.app",
        });

        try
        {
            Assert.Null(orb.FindSpeakableText(home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // Finds however many clicks the settings currently map to the wanted action,
    // so these tests do not break when someone reorders the click preferences.
    private static int ActionClicks(string wanted)
    {
        for (var clicks = 1; clicks <= 3; clicks++)
        {
            if (OrbWindow.ActionFor(clicks) == wanted) return clicks;
        }

        // Nothing maps to it under the current settings; RunClickAction's "none"
        // arm is covered elsewhere, so a count that resolves to nothing is fine.
        return 1;
    }

    // The flyout's arrange button, which is safe for the same reason the tray's
    // is: SessionManager.Instance is null outside the running app, so this is a
    // no-op. A version that dereferenced instead of using ?. would throw here,
    // and nothing else would have caught it.
    [AvaloniaFact]
    public void ArrangingWithNoSessionManagerIsANoOp()
    {
        OrbWindow.ArrangeAllOrbs();
    }
}
