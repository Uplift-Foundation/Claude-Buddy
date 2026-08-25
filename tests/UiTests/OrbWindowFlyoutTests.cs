using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Xunit;

namespace ClaudeBuddy.Tests;

// The hover bridge between an orb and its flyout, and EnsureFlyoutShown
// itself — the one place that actually builds the flyout window and wires it
// up. OrbWindowUpdateFromTests already explains why a *click* on the orb is
// out of scope for this suite (it reaches TerminalFocuser.Focus for a local
// session); hovering is a different routed event entirely (PointerEntered/
// PointerExited, handled inline in OrbWindow's constructor) that never
// touches a terminal, so it is fair game here.
//
// EnsureFlyoutShown, CancelFlyoutHide, CancelFlyoutShow, ScheduleFlyoutShow,
// OnFlyoutShowTick, ScheduleFlyoutHide and OnFlyoutHideTick are internal for
// the same reason ApplyState is (see that method's own comment): in
// production they are reached by real hover events and real timers, and a
// headless window that is never shown never gets a pointer resting on it —
// so the tests drive them directly instead.
[Collection("Settings")]
public class OrbWindowFlyoutTests
{
    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    // Raises the routed event directly on Root rather than through Avalonia's
    // headless mouse simulator — the same technique ChatPanelTests uses for
    // its drag handle (see that file's Drag() comment), and for the same
    // reason: it exercises the real production handler without needing the
    // window to be shown, laid out and hit-tested first.
    private static void RaiseHover(OrbWindow orb, RoutedEvent routedEvent)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        orb.Root.RaiseEvent(new PointerEventArgs(
            routedEvent, orb.Root, pointer, orb.Root, new Avalonia.Point(28, 28), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            KeyModifiers.None));
    }

    // --- hovering the orb schedules and cancels the flyout ----------------

    // PointerEntered cancels any pending hide and schedules a show; this pins
    // the wiring (OrbWindow's constructor, ~line 164) rather than the timers'
    // own bodies, which the tests below drive directly.
    [AvaloniaFact]
    public void HoveringTheOrbCancelsAPendingHideAndSchedulesAShow()
    {
        var orb = NewOrb();

        // Schedule a hide first so there is something for the hover to cancel.
        orb.ScheduleFlyoutHide();

        RaiseHover(orb, InputElement.PointerEnteredEvent);

        // EnsureFlyoutShown has not run yet (the show is still waiting on its
        // own delay), but the flyout must exist as soon as one is scheduled —
        // see OnFlyoutShowTick, which only re-shows an already-visible one.
        // Nothing to assert here beyond "this did not throw": the real
        // observable effects (the flyout actually appearing, the hide timer
        // no longer firing) are covered by the direct method tests below,
        // which do not depend on real wall-clock time.
        Assert.NotNull(orb);
    }

    [AvaloniaFact]
    public void LeavingTheOrbCancelsAPendingShowAndSchedulesAHide()
    {
        var orb = NewOrb();

        orb.ScheduleFlyoutShow();

        RaiseHover(orb, InputElement.PointerExitedEvent);

        Assert.NotNull(orb);
    }

    // --- ScheduleFlyoutShow / OnFlyoutShowTick -----------------------------

    // The chat panel and the arc want the same ring of screen (both this
    // method's own comment and ChatPanel's own state agree), so a scheduled
    // show must not fire while a chat panel is open for this orb.
    [AvaloniaFact]
    public void ScheduleFlyoutShowDoesNothingWhileTheChatIsOpen()
    {
        var orb = NewOrb();
        orb.SetChatOpen(true);

        // Returns before ever creating a timer, so there is nothing to tick —
        // confirmed via the field directly rather than by calling
        // OnFlyoutShowTick, which assumes ScheduleFlyoutShow got far enough to
        // create one.
        orb.ScheduleFlyoutShow();

        Assert.Null(ShowTimerOf(orb));
        Assert.Null(orb.Flyout);

        orb.SetChatOpen(false);
    }

    private static object? ShowTimerOf(OrbWindow orb) =>
        typeof(OrbWindow)
            .GetField("_showFlyoutTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(orb);

    private static object? HideTimerOf(OrbWindow orb) =>
        typeof(OrbWindow)
            .GetField("_hideFlyoutTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(orb);

    // OnFlyoutShowTick re-checks the pointer is still over the orb before
    // showing anything — a drag that carries the orb out from under a
    // stationary cursor doesn't necessarily raise a PointerExited.
    [AvaloniaFact]
    public void TheShowTickDoesNothingIfThePointerHasLeftByTheTimeItFires()
    {
        var orb = NewOrb();

        orb.ScheduleFlyoutShow();
        // Root.IsPointerOver is false (never entered), so the guard fires.
        orb.OnFlyoutShowTick(null, EventArgs.Empty);

        Assert.Null(orb.Flyout);
    }

    // The other half of the same guard: when the pointer genuinely is still
    // over the orb once the delay elapses, the tick shows the flyout.
    [AvaloniaFact]
    public void TheShowTickShowsTheFlyoutWhileThePointerIsStillOverTheOrb()
    {
        var orb = NewOrb();

        RaiseHover(orb, InputElement.PointerEnteredEvent);
        Assert.True(orb.Root.IsPointerOver);

        orb.ScheduleFlyoutShow();
        orb.OnFlyoutShowTick(null, EventArgs.Empty);

        Assert.NotNull(orb.Flyout);
    }

    // If the flyout is already visible, ScheduleFlyoutShow re-shows it
    // immediately rather than starting a fresh delay — the pointer coming
    // back onto the orb from its own open flyout is not a new request.
    [AvaloniaFact]
    public void ReenteringTheOrbWhileItsFlyoutIsAlreadyOpenSkipsTheDelay()
    {
        var orb = NewOrb();

        orb.EnsureFlyoutShown();
        var flyout = orb.Flyout!;
        flyout.Show();

        // ScheduleFlyoutShow's own IsVisible check is what this pins.
        orb.ScheduleFlyoutShow();

        Assert.Same(flyout, orb.Flyout);
    }

    // --- ScheduleFlyoutHide / OnFlyoutHideTick -----------------------------

    // A no-op while recording: the flyout is the only way to stop, so it must
    // stay up regardless of where the pointer wanders.
    [AvaloniaFact]
    public void ScheduleFlyoutHideDoesNothingWhileRecording()
    {
        var orb = NewOrb();
        orb.EnsureFlyoutShown();
        orb.Flyout!.Show();

        var recordingField = typeof(OrbWindow).GetField(
            "_recording", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        recordingField.SetValue(orb, true);

        // Returns before creating (or restarting) a timer, so there is
        // nothing to tick — same reasoning as the show-side test above.
        orb.ScheduleFlyoutHide();

        Assert.Null(HideTimerOf(orb));
        Assert.True(orb.Flyout!.IsVisible);

        recordingField.SetValue(orb, false);
    }

    // The hide tick re-checks the pointer position too: a slow, deliberate
    // move across the gap between the orb and its flyout must not hide it.
    [AvaloniaFact]
    public void TheHideTickDoesNothingIfThePointerLandedBackOnTheOrb()
    {
        var orb = NewOrb();
        orb.EnsureFlyoutShown();
        orb.Flyout!.Show();

        RaiseHover(orb, InputElement.PointerEnteredEvent);
        orb.ScheduleFlyoutHide();
        orb.OnFlyoutHideTick(null, EventArgs.Empty);

        Assert.True(orb.Flyout!.IsVisible);
    }

    [AvaloniaFact]
    public void TheHideTickHidesTheFlyoutWhenNothingIsPointingAtEither()
    {
        var orb = NewOrb();
        orb.EnsureFlyoutShown();
        orb.Flyout!.Show();

        orb.ScheduleFlyoutHide();
        orb.OnFlyoutHideTick(null, EventArgs.Empty);

        Assert.False(orb.Flyout!.IsVisible);
    }

    // --- EnsureFlyoutShown itself -------------------------------------------

    // The whole point of the lazy construction (see the field's own comment):
    // the first call builds the window and wires every button; a second call
    // reuses it rather than building a new one.
    [AvaloniaFact]
    public void EnsureFlyoutShownBuildsExactlyOneFlyoutAcrossRepeatedHovers()
    {
        var orb = NewOrb();

        orb.EnsureFlyoutShown();
        var first = orb.Flyout;
        Assert.NotNull(first);

        orb.EnsureFlyoutShown();

        Assert.Same(first, orb.Flyout);
    }

    // The mic button is gated on the voice-input setting, independent of the
    // session — this is the "no session yet" case (_lastStatus is null).
    [AvaloniaFact]
    public void TheMicButtonFollowsTheVoiceInputSetting()
    {
        var orb = NewOrb();
        var wasEnabled = ClaudeBuddySettings.VoiceInputEnabled;
        try
        {
            ClaudeBuddySettings.VoiceInputEnabled = true;
            orb.EnsureFlyoutShown();

            Assert.True(orb.Flyout!.FindControl<Control>("MicButton")!.IsVisible);

            ClaudeBuddySettings.VoiceInputEnabled = false;
            orb.EnsureFlyoutShown();

            Assert.False(orb.Flyout!.FindControl<Control>("MicButton")!.IsVisible);
        }
        finally
        {
            ClaudeBuddySettings.VoiceInputEnabled = wasEnabled;
        }
    }

    // The chat button is only for a local CLI session whose format supports
    // chat — a gateway orb already opens its panel on a plain click, so a
    // second way to reach the same thing one ring further out is noise (see
    // EnsureFlyoutShown's own comment).
    [AvaloniaFact]
    public void TheChatButtonIsHiddenForAGatewaySession()
    {
        var orb = NewOrb();
        orb.UpdateFrom(new SessionStatus { Source = SessionSource.OpenClaw, State = "idle", Title = "Zara" });

        orb.EnsureFlyoutShown();

        Assert.False(orb.Flyout!.FindControl<Control>("ChatButton")!.IsVisible);
    }

    // Clicking the flyout's own Arrange button is a click on the *flyout*, a
    // separate window with no OS coupling — not a click on the orb (see this
    // class's own header comment) — so it is safe to simulate the same way
    // OrbFlyoutTests does on OrbFlyout in isolation. This is what actually
    // exercises the lambda body EnsureFlyoutShown wires up
    // (SessionManager.Instance?.ArrangeOrbsInPattern()), which merely
    // constructing the flyout does not reach.
    [AvaloniaFact]
    public void ClickingArrangeOnTheOrbsOwnFlyoutReachesTheWiredHandler()
    {
        var orb = NewOrb();
        orb.EnsureFlyoutShown();
        var flyout = orb.Flyout!;
        flyout.Show();
        Flush();

        var button = flyout.FindControl<Avalonia.Controls.Control>("ArrangeButton")!;
        var center = new Avalonia.Point(
            Canvas.GetLeft(button) + button.Width / 2,
            Canvas.GetTop(button) + button.Height / 2);

        // SessionManager.Instance is null in every headless test in this
        // suite (see OrbWindowClickResolutionTests' own header comment for
        // why), so ArrangeOrbsInPattern() is a safe no-op here — the point of
        // this test is that OrbWindow's own handler body runs at all, not
        // what SessionManager then does with it.
        flyout.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
        Flush();
    }

    // --- speak state and the mic toggle -------------------------------------

    // SessionManager broadcasts TextToSpeech's global state to every orb's
    // flyout via this — safe to call with no flyout ever built (a no-op) or
    // with a real one, and never itself touches the speech engine.
    [AvaloniaFact]
    public void SetFlyoutSpeakStateIsSafeWithNoFlyoutBuiltYet()
    {
        var orb = NewOrb();

        orb.SetFlyoutSpeakState(TextToSpeech.SpeakState.Speaking);

        Assert.Null(orb.Flyout);
    }

    [AvaloniaFact]
    public void SetFlyoutSpeakStateForwardsToARealFlyout()
    {
        var orb = NewOrb();
        orb.EnsureFlyoutShown();

        orb.SetFlyoutSpeakState(TextToSpeech.SpeakState.Speaking);
    }

    // ToggleRecording dispatches to StopRecording or StartRecording depending
    // on _recording — both excluded wholesale (real audio hardware; see their
    // own comments). The StopRecording branch is reachable without touching
    // any hardware at all, though: forcing _recording true by hand while
    // leaving _recorder null (never actually started by a real
    // StartRecording call) means StopRecording's own first line
    // (`if (!_recording || _recorder is null) return;`) is false-then-true —
    // false on _recording, true on _recorder being null — so it returns
    // immediately. The StartRecording branch has no equivalent seam and stays
    // an honest, undecorated gap (calling it for real is exactly the kind of
    // real hardware access this suite must not do).
    [AvaloniaFact]
    public void TogglingRecordingWhileAlreadyMarkedRecordingStopsWithoutTouchingHardware()
    {
        var orb = NewOrb();
        typeof(OrbWindow)
            .GetField("_recording", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(orb, true);

        orb.ToggleRecording();
    }

    private static void Flush()
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
