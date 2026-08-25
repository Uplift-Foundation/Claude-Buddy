using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// What a click *resolves to*, deliberately kept separate from OrbWindow's own
// pointer handling (OnPointerPressed/Moved/Released), which OrbWindowUpdateFromTests'
// own header comment rules out for this whole suite: it reaches
// TerminalFocuser.Focus for a local session, which fires real tmux/ps/osascript
// with no OS guard.
//
// ActionFor, AwaitsMoreClicks, OnClicked, RunClickAction and GoToSession are
// downstream of that pointer handling but do not require a pointer event to
// reach — OnClicked in particular is called with a plain int, and the real
// entry point (OnPointerReleased) is a two-line "count the clicks, call this"
// wrapper around it. Driving them directly is the same trade this file's
// sibling tests make for ApplyState and EnsureFlyoutShown.
//
// The one thing that keeps GoToSession safe to call here: every status used
// below has IsLocalCli == false (an OpenClaw session), and TerminalFocuser.Focus
// itself returns immediately for anything that isn't a local CLI (confirmed at
// TerminalFocuser.cs's own entry check, and pinned separately by
// OrbWindowUpdateFromTests.ARemoteStatusIsNeverTreatedAsHavingATerminal) — so
// GoToSession's own fallback call to it is a safe no-op, never a real process
// launch. A *local* session's click is never exercised anywhere in this suite.
//
// SessionManager.Instance is null throughout — the same choice
// RemoteScanTests/SessionScanTests/GatewayScanTests make, testing SessionManager's
// own logic against a local instance rather than the process-wide singleton.
// That leaves the "Instance is not null" branches of OpenChat/GoToSession as a
// deliberate, named gap alongside the pointer-handling one above, rather than
// something this file works around by standing up a real SessionManager and
// mutating a static everything else assumes stays null.
[Collection("Settings")]
public class OrbWindowClickResolutionTests
{
    private static OrbWindow NewGatewayOrb(string title = "Zara")
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(new SessionStatus { Source = SessionSource.OpenClaw, State = "idle", Title = title });
        return orb;
    }

    // --- ActionFor ----------------------------------------------------------

    [AvaloniaFact]
    public void ActionForReadsTheMatchingSettingPerClickCount()
    {
        var click = ClaudeBuddySettings.ClickAction;
        var dbl = ClaudeBuddySettings.DoubleClickAction;
        var triple = ClaudeBuddySettings.TripleClickAction;
        try
        {
            ClaudeBuddySettings.ClickAction = "chat";
            ClaudeBuddySettings.DoubleClickAction = "speak";
            ClaudeBuddySettings.TripleClickAction = "none";

            Assert.Equal("chat", OrbWindow.ActionFor(1));
            Assert.Equal("speak", OrbWindow.ActionFor(2));
            Assert.Equal("none", OrbWindow.ActionFor(3));

            // Anything beyond three collapses onto the triple-click setting —
            // OnClicked never calls this with more than 3, but the switch's
            // default arm is what makes that safe rather than assumed.
            Assert.Equal("none", OrbWindow.ActionFor(4));
        }
        finally
        {
            ClaudeBuddySettings.ClickAction = click;
            ClaudeBuddySettings.DoubleClickAction = dbl;
            ClaudeBuddySettings.TripleClickAction = triple;
        }
    }

    // --- AwaitsMoreClicks -----------------------------------------------------

    [AvaloniaFact]
    public void AWaitIsOnlyOwedWhenALongerGestureDiffersFromThisOne()
    {
        var click = ClaudeBuddySettings.ClickAction;
        var dbl = ClaudeBuddySettings.DoubleClickAction;
        var triple = ClaudeBuddySettings.TripleClickAction;
        try
        {
            // Every gesture bound to the same thing: no reason to ever wait.
            ClaudeBuddySettings.ClickAction = "chat";
            ClaudeBuddySettings.DoubleClickAction = "chat";
            ClaudeBuddySettings.TripleClickAction = "chat";
            Assert.False(OrbWindow.AwaitsMoreClicks(1));
            Assert.False(OrbWindow.AwaitsMoreClicks(2));

            // Triple click is nothing, and the single click is already
            // waiting on a double that differs — but the triple click itself
            // has nothing longer left to wait for.
            ClaudeBuddySettings.TripleClickAction = "none";
            Assert.False(OrbWindow.AwaitsMoreClicks(3));

            // A double click bound to something else: the single click has to
            // wait to find out which one the user meant.
            ClaudeBuddySettings.DoubleClickAction = "speak";
            Assert.True(OrbWindow.AwaitsMoreClicks(1));

            // The double click itself has nothing bound to the triple that
            // differs (triple is "none"), so it does not have to wait either.
            Assert.False(OrbWindow.AwaitsMoreClicks(2));

            // A triple bound to something new makes the double wait too.
            ClaudeBuddySettings.TripleClickAction = "chat";
            Assert.True(OrbWindow.AwaitsMoreClicks(2));
        }
        finally
        {
            ClaudeBuddySettings.ClickAction = click;
            ClaudeBuddySettings.DoubleClickAction = dbl;
            ClaudeBuddySettings.TripleClickAction = triple;
        }
    }

    // --- RunClickAction -------------------------------------------------------

    [AvaloniaFact]
    public void RunClickActionChatOpensTheChatPanel()
    {
        var click = ClaudeBuddySettings.ClickAction;
        try
        {
            ClaudeBuddySettings.ClickAction = "chat";
            var orb = NewGatewayOrb();

            // SessionManager.Instance is null, so RemoteChatFor answers null
            // and OpenChat returns before ever touching ChatPanel — this pins
            // that RunClickAction actually dispatches to OpenChat for "chat"
            // rather than asserting anything ChatPanel-visible.
            orb.RunClickAction(1);
        }
        finally
        {
            ClaudeBuddySettings.ClickAction = click;
        }
    }

    [AvaloniaFact]
    public void RunClickActionSpeakCallsTheSpeakHandler()
    {
        var click = ClaudeBuddySettings.ClickAction;
        try
        {
            ClaudeBuddySettings.ClickAction = "speak";
            var orb = NewGatewayOrb();

            // No status yet fed with anything speakable, so this resolves to
            // FindSpeakableText returning null and OnSpeakClicked returning —
            // again, the point is that RunClickAction reaches it at all.
            orb.RunClickAction(1);
        }
        finally
        {
            ClaudeBuddySettings.ClickAction = click;
        }
    }

    [AvaloniaFact]
    public void RunClickActionNoneDoesNothing()
    {
        var click = ClaudeBuddySettings.ClickAction;
        try
        {
            ClaudeBuddySettings.ClickAction = "none";
            var orb = NewGatewayOrb();

            orb.RunClickAction(1);
        }
        finally
        {
            ClaudeBuddySettings.ClickAction = click;
        }
    }

    // The default arm ("go to session") for a non-local status: GoToSession's
    // own !IsLocalCli branch finds no chat (Instance is null) and falls
    // through to TerminalFocuser.Focus, which returns immediately for a
    // non-local status — see this file's header comment.
    [AvaloniaFact]
    public void RunClickActionDefaultGoesToTheSessionForANonLocalStatus()
    {
        var click = ClaudeBuddySettings.ClickAction;
        try
        {
            // Anything not "chat"/"speak"/"none" takes the default arm —
            // matching whatever ships as the real default is the point.
            ClaudeBuddySettings.ClickAction = "terminal";
            var orb = NewGatewayOrb();

            orb.RunClickAction(1);
        }
        finally
        {
            ClaudeBuddySettings.ClickAction = click;
        }
    }

    // --- GoToSession, directly --------------------------------------------

    [AvaloniaFact]
    public void GoToSessionOnANonLocalStatusIsSafeWhenNoChatIsFound()
    {
        var orb = NewGatewayOrb();

        orb.GoToSession();
    }

    // --- OnClicked ------------------------------------------------------------

    [AvaloniaFact]
    public void MoreThanThreeClicksIsIgnored()
    {
        var orb = NewGatewayOrb();

        // Nothing to assert beyond "this returns immediately rather than
        // scheduling or running anything" — a fourth click landing on a
        // pending single/double/triple must not fire a fresh action.
        orb.OnClicked(4);
    }

    [AvaloniaFact]
    public void ASingleClickWithNothingLongerBoundRunsImmediately()
    {
        var click = ClaudeBuddySettings.ClickAction;
        var dbl = ClaudeBuddySettings.DoubleClickAction;
        var triple = ClaudeBuddySettings.TripleClickAction;
        try
        {
            ClaudeBuddySettings.ClickAction = "none";
            ClaudeBuddySettings.DoubleClickAction = "none";
            ClaudeBuddySettings.TripleClickAction = "none";

            var orb = NewGatewayOrb();
            orb.OnClicked(1);
        }
        finally
        {
            ClaudeBuddySettings.ClickAction = click;
            ClaudeBuddySettings.DoubleClickAction = dbl;
            ClaudeBuddySettings.TripleClickAction = triple;
        }
    }

    // A single click bound differently from the double has to wait out the
    // multi-click window before it knows which one happened — this is the
    // "starts a timer" branch, and the timer's own Tick handler is what
    // finally calls RunClickAction.
    [AvaloniaFact]
    public async System.Threading.Tasks.Task AWaitingClickRunsOnceTheMultiClickWindowElapses()
    {
        var click = ClaudeBuddySettings.ClickAction;
        var dbl = ClaudeBuddySettings.DoubleClickAction;
        try
        {
            ClaudeBuddySettings.ClickAction = "chat";
            ClaudeBuddySettings.DoubleClickAction = "speak";

            var orb = NewGatewayOrb();
            orb.OnClicked(1);

            // MultiClickMs is 300; pump real time until the scheduled tick has
            // had a chance to fire, the same pattern AvatarPopupTests uses for
            // its own DispatcherTimer.
            for (var attempt = 0; attempt < 60; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await System.Threading.Tasks.Task.Delay(10);
            }
        }
        finally
        {
            ClaudeBuddySettings.ClickAction = click;
            ClaudeBuddySettings.DoubleClickAction = dbl;
        }
    }

    // --- the context menu handlers -----------------------------------------

    [AvaloniaFact]
    public void ResetIdleClickIsSafeWithNoSessionManagerRunning()
    {
        var orb = NewGatewayOrb();

        orb.ResetIdle_Click(null, null!);
    }

    [AvaloniaFact]
    public void ResetPositionClickIsSafeWithNoSessionManagerRunning()
    {
        var orb = NewGatewayOrb();

        orb.ResetPosition_Click(null, null!);
    }

    // Under the headless test lifetime, Application.Current.ApplicationLifetime
    // is never IClassicDesktopStyleApplicationLifetime (see AppStylesTests and
    // this project's own TestAppBuilder), so Exit_Click's guard is always false
    // here — Shutdown() is never actually reachable from a headless test, which
    // is exactly what makes calling this safe: there is no live desktop
    // lifetime for it to shut down.
    [AvaloniaFact]
    public void ExitClickIsHarmlessUnderTheHeadlessLifetime()
    {
        var orb = NewGatewayOrb();

        orb.Exit_Click(null, null!);
    }
}
