using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-110: a chat that has been told to stay.
//
// ChatPanelStaticApiTests next door covers the same static entry points as they
// apply to the transient panel, which is the only kind that existed before this
// feature. This file is the other half — everything that is only true once a
// panel has been pinned, and everything about the two of them being on screen
// together.
//
// The two invariants ChatPanel's own class comment names are what most of these
// cases are really about, so they are worth saying again here in the terms a
// test can check. At most one unpinned panel: Transient is never two things, so
// unpinning has to dissolve whatever held that role. At most one panel per
// session: opening a conversation that is already pinned somewhere raises that
// window rather than binding it into the transient as well, because two windows
// subscribed to one session would both answer its permission prompts.
//
// Conventions inherited from ChatPanelStaticApiTests and ChatPanelTests rather
// than reinvented: orbs are never closed (closing one corrupts a process-wide
// font resource shared with every other headless window), and every session
// this class opens is torn down in Dispose. CloseFor rather than HideFor there,
// because HideFor is the one call that deliberately cannot reach a pinned panel
// — a class that pinned something and cleaned up with HideFor would hand the
// next class a live window nothing had told it about.
//
// [Collection("Settings")] because the text-scale case writes
// ClaudeBuddySettings.ChatTextScale, which is process-wide.
[Collection("Settings")]
public class ChatPanelPinTests : IDisposable
{
    private readonly List<string> _toClean = new();

    private FakeChatSession NewFake(string? displayName = null)
    {
        var id = "pin-" + Guid.NewGuid();
        _toClean.Add(id);

        return new FakeChatSession(null)
        {
            SessionId = id,
            DisplayName = displayName ?? "Fake Session",
        };
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.CloseFor(id);

        // A directly-constructed panel — the no-owner case below builds one —
        // has no session id to close by, and a pinned one left behind would be
        // handed to whichever class runs next as a window it never opened.
        foreach (var panel in ChatPanel.All.Where(p => p.IsPinned).ToList()) Dissolve(panel);

        Flush();
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static ChatPanel Open(OrbWindow orb, FakeChatSession fake)
    {
        ChatPanel.OpenFor(orb, fake);
        Flush();

        return ChatPanel.PanelFor(fake.SessionId)!;
    }

    // Open, then pin — the ordinary way a panel gets pinned, and the arrangement
    // nearly every case here starts from.
    private static ChatPanel OpenPinned(OrbWindow orb, FakeChatSession fake)
    {
        var panel = Open(orb, fake);
        panel.TogglePin();
        Flush();

        return panel;
    }

    // The close button's own gesture, which is what a pinned panel has to be
    // taken down with once it is no longer bound to a session.
    private static void Dissolve(ChatPanel panel)
    {
        Press(panel.CloseButton, panel);
        Flush();
    }

    // Raises a single PointerPressed directly on the control, sidestepping
    // hit-testing exactly as ChatPanelInteractionTests' own Click does — the
    // point is to exercise the real production handler, not the layout that
    // would otherwise have to be forced first. Returns the args so a caller can
    // read back whether the handler claimed the press.
    private static PointerPressedEventArgs Press(Control control, Control root)
    {
        var pointer = new Avalonia.Input.Pointer(
            Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

        var at = control.Bounds.Width > 0
            ? new Point(control.Bounds.Width / 2, control.Bounds.Height / 2)
            : new Point(1, 1);

        var args = new PointerPressedEventArgs(
            control, pointer, root, at, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1);

        control.RaiseEvent(args);
        return args;
    }

    // Losing focus, which is the gesture pinning is defined against. The popup
    // is closed first because ChatPanel's Deactivated handler declines to hide
    // while the enlarged portrait is up, and that popup is process-wide — a
    // capture left open by another class would make this look like the pinned
    // behaviour whatever this panel's flag says.
    private static void Deactivate(ChatPanel panel)
    {
        AvatarPopup.Close();
        Flush();

        ChatPanelTestAccess.Deactivate(panel);
        Flush();
    }

    // Whether the orb thinks its chat is open, which is what suppresses the
    // hover arc. Read by reflection the same way ChatPanelInteractionTests reads
    // it — there is no accessor, and adding one to ship code for a test's sake
    // is the trade this suite has already declined once.
    private static bool ChatOpenField(OrbWindow orb)
    {
        var field = typeof(OrbWindow).GetField("_chatOpen", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (bool)field.GetValue(orb)!;
    }

    // Brushes are compared by colour, not by instance, for the reason
    // ChatPanelStaticApiTests states: two SolidColorBrushes of one colour are
    // unequal, which produces "Expected #e0202024, Actual #e0202024".
    private static Color ColourOf(IBrush? brush) => ((ISolidColorBrush)brush!).Color;

    private static PixelRect RectOf(ChatPanel panel) =>
        new(panel.Position, new PixelSize((int)panel.Width, (int)panel.Height));

    // --- the pin itself ---

    [AvaloniaFact]
    public void TogglingThePinMarksThePanelPinnedAndFillsItsCircle()
    {
        var panel = Open(NewOrb(), NewFake());
        var idle = ColourOf(panel.PinFill.Fill);

        Assert.False(panel.IsPinned);

        panel.TogglePin();
        Flush();

        Assert.True(panel.IsPinned);
        var engaged = ColourOf(panel.PinFill.Fill);
        Assert.NotEqual(idle, engaged);

        // The same blue the speak button wears while it is doing something: a
        // filled circle in this header means "this control is currently on", and
        // a pin that lit up in a colour of its own would be saying something
        // else. Asserted against the speak button rather than against a literal,
        // so the two cannot drift apart without this failing.
        ChatPanel.SetSpeakState(TextToSpeech.SpeakState.Speaking);
        Flush();
        Assert.Equal(ColourOf(panel.SpeakFill.Fill), engaged);

        ChatPanel.SetSpeakState(TextToSpeech.SpeakState.Idle);
        Flush();

        panel.TogglePin();
        Flush();

        Assert.False(panel.IsPinned);
        Assert.Equal(idle, ColourOf(panel.PinFill.Fill));
    }

    // The button is the way a person does it, and it goes through the same
    // TogglePin the tests above drive directly — one path, not a test-only one
    // beside the real one.
    [AvaloniaFact]
    public void ClickingThePinButtonPinsThePanel()
    {
        var panel = Open(NewOrb(), NewFake());

        var press = Press(panel.PinButton, panel);
        Flush();

        Assert.True(panel.IsPinned);

        // Claimed, so the press does not also travel on to the header behind it
        // and start a window drag out of the click that pinned it.
        Assert.True(press.Handled);

        Press(panel.PinButton, panel);
        Flush();

        Assert.False(panel.IsPinned);
    }

    // The tooltip is the only thing on the control that says which way it will
    // go, since the glyph does not change.
    [AvaloniaFact]
    public void TheTooltipSaysWhatTheNextClickWillDo()
    {
        var panel = Open(NewOrb(), NewFake());

        Assert.Equal("Pin — keep this chat open", ToolTip.GetTip(panel.PinButton));

        panel.TogglePin();
        Flush();
        Assert.Equal("Unpin", ToolTip.GetTip(panel.PinButton));

        panel.TogglePin();
        Flush();
        Assert.Equal("Pin — keep this chat open", ToolTip.GetTip(panel.PinButton));
    }

    // --- dismiss-on-deactivate, which is the whole of what pinning turns off ---

    [AvaloniaFact]
    public void AnUnpinnedPanelStillHidesWhenItLosesFocus()
    {
        var panel = Open(NewOrb(), NewFake());
        Assert.True(panel.IsVisible);

        Deactivate(panel);

        Assert.False(panel.IsVisible);
    }

    [AvaloniaFact]
    public void APinnedPanelStaysOnScreenWhenItLosesFocus()
    {
        var fake = NewFake();
        var panel = OpenPinned(NewOrb(), fake);

        Deactivate(panel);

        Assert.True(panel.IsVisible);

        // Still bound, not merely still drawn: a panel that survived the hide
        // but lost its session would be a transcript nothing could add to.
        Assert.True(ChatPanel.IsOpenFor(fake.SessionId));
    }

    // --- where it sits, and what it does when its orb moves ---

    [AvaloniaFact]
    public void PinningDoesNotMoveThePanel()
    {
        var panel = Open(NewOrb(), NewFake());
        panel.Position = new PixelPoint(700, 400);
        Flush();

        panel.TogglePin();
        Flush();

        // "Pin" means leave this exactly where it is. A panel that relocated
        // itself the moment it was told to stay would be answering a question
        // nobody asked.
        Assert.Equal(new PixelPoint(700, 400), panel.Position);
    }

    [AvaloniaFact]
    public void APinnedPanelDoesNotFollowItsOrb()
    {
        var orb = NewOrb();
        var panel = OpenPinned(orb, NewFake());

        panel.Position = new PixelPoint(700, 400);
        Flush();

        // The arrangement animation asks every orb's panel to move. A pinned one
        // is somewhere the user put it, and an orb sliding across the screen has
        // no business taking it along.
        ChatPanel.RepositionFor(orb);
        Flush();

        Assert.Equal(new PixelPoint(700, 400), panel.Position);
    }

    // --- the orb's hover arc ---

    [AvaloniaFact]
    public void PinningGivesTheOrbItsArcBack()
    {
        var orb = NewOrb();

        // The orb sets this itself, before calling OpenFor — see OrbWindow's
        // OpenChat. Opening a panel through the static API alone leaves it
        // false, so the arrangement has to say it, or the assertion below would
        // be true whether or not Pin did anything.
        orb.SetChatOpen(true);

        var panel = Open(orb, NewFake());

        Assert.True(ChatOpenField(orb));

        panel.TogglePin();
        Flush();

        // Reads backwards until the rule is said out loud: the arc is suppressed
        // because the panel is drawn a Gap away from the orb's centre, over
        // exactly the radius the arc wants. A pinned panel does not have that
        // space — it can be dragged anywhere — so the arc is free again.
        Assert.False(ChatOpenField(orb));

        panel.TogglePin();
        Flush();

        Assert.True(ChatOpenField(orb));
    }

    // --- a second panel ---

    [AvaloniaFact]
    public void ClickingAnotherOrbOpensASecondPanelClearOfThePinnedOne()
    {
        var pinned = OpenPinned(NewOrb(), NewFake("Pinned"));
        var transient = Open(NewOrb(), NewFake("Transient"));

        Assert.NotSame(pinned, transient);
        Assert.Same(transient, ChatPanel.Transient);
        Assert.Contains(pinned, ChatPanel.All);

        // The point of ChatPanelPlacement: the new panel is not drawn on top of
        // the one somebody pinned there. Both orbs anchor at the same point
        // under a headless platform — an OrbWindow that was never shown answers
        // PointToScreen with its own client origin — so this is the collision
        // case rather than two panels that were never going to meet.
        Assert.False(RectOf(pinned).Intersects(RectOf(transient)));
    }

    // The second invariant: one session, one panel. Clicking the orb of a chat
    // that is already pinned raises that window rather than binding the same
    // conversation into the transient as well.
    [AvaloniaFact]
    public void ClickingAPinnedChatsOrbActivatesItRatherThanDuplicatingIt()
    {
        var fake = NewFake();
        var pinned = OpenPinned(NewOrb(), fake);

        var before = ChatPanel.All.Count;

        var again = NewOrb();

        // As OrbWindow.OpenChat does it: the flag goes up first, and the panel
        // is asked second. The pinned branch is what puts it back down.
        again.SetChatOpen(true);
        ChatPanel.OpenFor(again, fake);
        Flush();

        Assert.Equal(before, ChatPanel.All.Count);
        Assert.Same(pinned, ChatPanel.PanelFor(fake.SessionId));

        // No transient was built to hold it, which is the failure this guards
        // against — two windows on one transcript, each subscribed to the
        // session, the one you were not looking at answering a permission prompt
        // out from under the one you were.
        Assert.Null(ChatPanel.Transient);

        // And the orb keeps its arc, because the panel it just raised is not
        // sitting in the space the arc wants.
        Assert.False(ChatOpenField(again));
    }

    // The same rule with a transient already open: raising the pinned panel must
    // not rebind the transient onto the pinned conversation either.
    [AvaloniaFact]
    public void RaisingAPinnedChatLeavesTheTransientShowingItsOwn()
    {
        var pinnedSession = NewFake("Pinned");
        var pinned = OpenPinned(NewOrb(), pinnedSession);

        var otherSession = NewFake("Other");
        var transient = Open(NewOrb(), otherSession);

        ChatPanel.OpenFor(NewOrb(), pinnedSession);
        Flush();

        Assert.Same(pinned, ChatPanel.PanelFor(pinnedSession.SessionId));
        Assert.Same(transient, ChatPanel.PanelFor(otherSession.SessionId));
        Assert.NotSame(pinned, transient);
    }

    // --- unpinning ---

    [AvaloniaFact]
    public void UnpinningMakesThisTheTransientPanelWhereItStands()
    {
        var orb = NewOrb();
        orb.SetChatOpen(true);
        var panel = OpenPinned(orb, NewFake());

        // Pinning handed the arc back; unpinning has to take it again, because
        // the panel is about to start being recentred on the orb once more.
        Assert.False(ChatOpenField(orb));

        panel.Position = new PixelPoint(640, 320);
        Flush();

        panel.TogglePin();
        Flush();

        Assert.Same(panel, ChatPanel.Transient);

        // In place, not repositioned. Unpinning says "this one can behave
        // normally again", not "put it back".
        Assert.Equal(new PixelPoint(640, 320), panel.Position);
        Assert.True(ChatOpenField(orb));
    }

    [AvaloniaFact]
    public void UnpinningDissolvesWhateverTransientWasAlreadyOpen()
    {
        var pinnedSession = NewFake("Pinned");
        var pinned = OpenPinned(NewOrb(), pinnedSession);

        var otherSession = NewFake("Other");
        var transient = Open(NewOrb(), otherSession);

        pinned.TogglePin();
        Flush();

        // Dissolved rather than hidden. A hidden unpinned panel is still
        // unpinned, so Transient would go on handing *it* out — every orb click,
        // every arrangement hide — while this window sat in front of the user
        // believing it was the transient.
        Assert.Same(pinned, ChatPanel.Transient);
        Assert.DoesNotContain(transient, ChatPanel.All);
        Assert.False(transient.IsVisible);
        Assert.False(ChatPanel.IsOpenFor(otherSession.SessionId));
    }

    // The other arm: nothing else holds the transient role, so there is nothing
    // to give up.
    [AvaloniaFact]
    public void UnpinningWithNoOtherTransientDissolvesNothing()
    {
        var fake = NewFake();
        var panel = OpenPinned(NewOrb(), fake);

        Assert.Null(ChatPanel.Transient);

        panel.TogglePin();
        Flush();

        Assert.Same(panel, ChatPanel.Transient);
        Assert.Contains(panel, ChatPanel.All);
        Assert.True(ChatPanel.IsOpenFor(fake.SessionId));
    }

    // --- HideFor and CloseFor, which differ only on a pinned panel ---

    [AvaloniaFact]
    public void HideForLeavesAPinnedPanelAlone()
    {
        var fake = NewFake();
        var panel = OpenPinned(NewOrb(), fake);

        ChatPanel.HideFor(fake.SessionId);
        Flush();

        Assert.True(panel.IsVisible);
        Assert.True(ChatPanel.IsOpenFor(fake.SessionId));
    }

    // An orb sliding across the screen asks HideFor; an orb whose session has
    // ended asks CloseFor, and that one reaches a pinned panel. Pinning is "keep
    // this chat open", not "keep this chat open after the conversation ends".
    [AvaloniaFact]
    public void CloseForClosesAPinnedPanelForGood()
    {
        var fake = NewFake();
        var panel = OpenPinned(NewOrb(), fake);

        ChatPanel.CloseFor(fake.SessionId);
        Flush();

        Assert.DoesNotContain(panel, ChatPanel.All);
        Assert.False(panel.IsVisible);
        Assert.False(ChatPanel.IsOpenFor(fake.SessionId));
    }

    // IsOpenFor is asked by dictation looking for somewhere to land, and a
    // pinned panel is as much on screen as the transient is.
    [AvaloniaFact]
    public void IsOpenForFindsAPinnedPanelToo()
    {
        var fake = NewFake();
        OpenPinned(NewOrb(), fake);

        Assert.Null(ChatPanel.Transient);
        Assert.True(ChatPanel.IsOpenFor(fake.SessionId));
    }

    // --- what reaches every panel, and what reaches one ---

    // Speech is one voice and one global state. Two panels drawing it
    // differently is a disagreement you can only see by putting them side by
    // side, which pinning now makes easy.
    [AvaloniaFact]
    public void TheSpeakStateReachesEveryPanel()
    {
        var pinned = OpenPinned(NewOrb(), NewFake("Pinned"));
        var transient = Open(NewOrb(), NewFake("Transient"));

        ChatPanel.SetSpeakState(TextToSpeech.SpeakState.Idle);
        Flush();
        var idle = ColourOf(pinned.SpeakFill.Fill);

        ChatPanel.SetSpeakState(TextToSpeech.SpeakState.Speaking);
        Flush();

        Assert.NotEqual(idle, ColourOf(pinned.SpeakFill.Fill));
        Assert.Equal(ColourOf(pinned.SpeakFill.Fill), ColourOf(transient.SpeakFill.Fill));

        ChatPanel.SetSpeakState(TextToSpeech.SpeakState.Idle);
        Flush();

        Assert.Equal(idle, ColourOf(pinned.SpeakFill.Fill));
        Assert.Equal(idle, ColourOf(transient.SpeakFill.Fill));
    }

    // The same argument for the text-scale slider: one global number, and two
    // panels drawing it at different sizes would be a bug visible only side by
    // side.
    [AvaloniaFact]
    public void TheTextScaleReachesEveryPanel()
    {
        var was = ClaudeBuddySettings.ChatTextScale;

        try
        {
            var pinned = OpenPinned(NewOrb(), NewFake("Pinned"));
            var transient = Open(NewOrb(), NewFake("Transient"));

            ClaudeBuddySettings.ChatTextScale = 1.5;
            ChatPanel.ReapplyTextScale();
            Flush();

            Assert.Equal(11.5 * 1.5, pinned.Input.FontSize, 3);
            Assert.Equal(11.5 * 1.5, transient.Input.FontSize, 3);
        }
        finally
        {
            ClaudeBuddySettings.ChatTextScale = was;
            ChatPanel.ReapplyTextScale();
            Flush();
        }
    }

    // Addressed to an orb, not to "the panel". The mic light belongs to the
    // conversation whose flyout the mic is on.
    [AvaloniaFact]
    public void RecordingOnlyLightsTheAddressedOrbsPanel()
    {
        var pinnedOrb = NewOrb();
        var pinned = OpenPinned(pinnedOrb, NewFake("Pinned"));

        var transientOrb = NewOrb();
        var transient = Open(transientOrb, NewFake("Transient"));

        var idle = ColourOf(transient.MicFill.Fill);

        ChatPanel.SetRecording(pinnedOrb, true);
        Flush();

        Assert.NotEqual(idle, ColourOf(pinned.MicFill.Fill));
        Assert.Equal(idle, ColourOf(transient.MicFill.Fill));

        ChatPanel.SetRecording(pinnedOrb, false);
        Flush();

        Assert.Equal(idle, ColourOf(pinned.MicFill.Fill));
    }

    // The reason AppendToInput took an orb in the first place: words spoken at
    // one orb landing in whichever panel happened to be transient is the worst
    // kind of wrong — silent, and in someone else's message box.
    [AvaloniaFact]
    public void DictationOnlyReachesTheAddressedOrbsPanel()
    {
        var pinnedOrb = NewOrb();
        var pinned = OpenPinned(pinnedOrb, NewFake("Pinned"));

        var transientOrb = NewOrb();
        var transient = Open(transientOrb, NewFake("Transient"));

        pinned.Input.Text = "";
        transient.Input.Text = "";

        ChatPanel.AppendToInput(pinnedOrb, "spoken at the pinned one");
        Flush();

        Assert.Equal("spoken at the pinned one", pinned.Input.Text);
        Assert.Equal("", transient.Input.Text);

        ChatPanel.AppendToInput(transientOrb, "and at the other");
        Flush();

        Assert.Equal("spoken at the pinned one", pinned.Input.Text);
        Assert.Equal("and at the other", transient.Input.Text);
    }

    // Same gate, third caller. The roster usually answers after a panel is
    // already open, so this fires for whichever orb the names arrived for and
    // must not redraw the header of one it did not.
    [AvaloniaFact]
    public void RefreshingIdentityOnlyTouchesTheAddressedOrbsPanel()
    {
        var pinnedOrb = NewOrb();
        var pinned = OpenPinned(pinnedOrb, NewFake("Pinned"));

        var transientOrb = NewOrb();
        var transient = Open(transientOrb, NewFake("Transient"));

        ChatPanel.RefreshIdentityFor(pinnedOrb);
        ChatPanel.RefreshIdentityFor(transientOrb);
        ChatPanel.RefreshIdentityFor(NewOrb());
        Flush();

        Assert.Equal("Pinned", pinned.TitleText.Text);
        Assert.Equal("Transient", transient.TitleText.Text);
    }

    // --- the close button, and Escape ---

    [AvaloniaFact]
    public void TheCloseButtonRemovesAPinnedPanelForGood()
    {
        var fake = NewFake();
        var panel = OpenPinned(NewOrb(), fake);

        Press(panel.CloseButton, panel);
        Flush();

        // Gone rather than hidden: it was a window the user deliberately
        // created, and hiding it would leave something in the registry that
        // nothing could ever show again.
        Assert.DoesNotContain(panel, ChatPanel.All);
        Assert.False(panel.IsVisible);
    }

    [AvaloniaFact]
    public void EscapeRemovesAPinnedPanelForGood()
    {
        var fake = NewFake();
        var panel = OpenPinned(NewOrb(), fake);

        panel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Flush();

        Assert.DoesNotContain(panel, ChatPanel.All);
        Assert.False(panel.IsVisible);
    }

    // The other arm of Dismiss, kept here beside the pinned one rather than
    // trusted to ChatPanelInteractionTests: the two behaviours are only worth
    // anything as a pair, and a change that made the transient close for good
    // would pass over there.
    [AvaloniaFact]
    public void TheCloseButtonOnlyHidesTheTransientPanel()
    {
        var panel = Open(NewOrb(), NewFake());

        Press(panel.CloseButton, panel);
        Flush();

        Assert.Contains(panel, ChatPanel.All);
        Assert.False(panel.IsVisible);
    }

    // --- the header as a drag handle ---

    // Offered only while pinned, because an unpinned panel is recentred on its
    // orb by RepositionFor every time the arrangement moves and a drag would be
    // silently undone.
    //
    // What can be asserted here is the branch and its safety, not the drag:
    // BeginMoveDrag reaches HeadlessWindowImpl, whose implementation does
    // nothing and reports nothing — the press comes back unhandled, with no
    // pointer captured and no window moved, whichever way the flag is set.
    // Confirmed by probe rather than assumed. A real move drag is the window
    // manager's, and there is not one here.
    [AvaloniaFact]
    public void TheHeaderIsADragHandleOnlyWhenPinned()
    {
        var panel = Open(NewOrb(), NewFake());
        panel.Position = new PixelPoint(300, 200);
        Flush();

        Press(panel.HeaderRow, panel);
        Flush();

        Assert.Equal(new PixelPoint(300, 200), panel.Position);
        Assert.True(panel.IsVisible);

        panel.TogglePin();
        Flush();

        Press(panel.HeaderRow, panel);
        Flush();

        Assert.Equal(new PixelPoint(300, 200), panel.Position);
        Assert.True(panel.IsVisible);
    }

    // The portrait's press is claimed before the null check, not after, so one
    // gesture cannot mean "enlarge this" or "move the window" depending on
    // whether a download had finished. An unbound panel has no avatar, which is
    // exactly the case that used to fall through to the header.
    [AvaloniaFact]
    public void AnEmptyPortraitStillSwallowsItsPress()
    {
        var panel = Open(NewOrb(), NewFake());

        var press = Press(panel.AvatarBox, panel);
        Flush();

        Assert.True(press.Handled);
    }

    // --- which pinned rectangles a new panel is placed clear of ---

    // A rectangle on another display can never overlap this one, and dodging it
    // would push a panel sideways to miss something the user cannot see beside
    // it.
    [AvaloniaFact]
    public void APinnedPanelOnAnotherScreenIsNotAvoided()
    {
        var pinned = OpenPinned(NewOrb(), NewFake("Pinned"));

        // Where the transient lands while the pinned one is in the way.
        var dodging = Open(NewOrb(), NewFake("Dodging")).Position;

        // Off the one headless screen entirely, which is what a second display
        // looks like from here.
        pinned.Position = new PixelPoint(4000, 4000);
        Flush();

        // The same transient window, rebound and so repositioned from scratch.
        var undodged = Open(NewOrb(), NewFake("Undodged")).Position;

        Assert.NotEqual(dodging, undodged);
    }

    // A pinned panel that is not visible occupies nothing, the same as one on
    // another screen.
    [AvaloniaFact]
    public void AHiddenPinnedPanelIsNotAvoided()
    {
        var pinned = OpenPinned(NewOrb(), NewFake("Pinned"));

        var dodging = Open(NewOrb(), NewFake("Dodging")).Position;

        pinned.Hide();
        Flush();

        var undodged = Open(NewOrb(), NewFake("Undodged")).Position;

        Assert.NotEqual(dodging, undodged);
    }

    // --- a panel in the registry with nothing in it ---

    // Every query over the registry reads `_session` through a null-conditional,
    // and this is the state that makes the null arm real: a panel that has been
    // built but never bound to a conversation.
    //
    // Only construction produces one. Unbind — what hiding a panel runs — takes
    // the subscriptions off but leaves `_session` pointing at the conversation
    // that was last in the window, so a hidden transient is not this state and
    // the assertion below says so rather than pretending otherwise. That
    // leftover is older than CB-110 and harmless where it is read: IsOpenFor
    // gates on visibility first, and OpenFor only cares whether the panel it
    // found is pinned, which a hidden transient never is.
    //
    // Arranged inside WithNoPanel so the new panel is the only one in the
    // registry — HideFor asks Transient, which would otherwise be the window
    // every other class in this assembly shares.
    [AvaloniaFact]
    public void TheRegistryQueriesSurviveAPanelBoundToNothing()
    {
        var fake = NewFake();
        var panel = Open(NewOrb(), fake);

        ChatPanel.HideFor(fake.SessionId);
        Flush();

        // Hidden and unsubscribed, but still holding the session it showed.
        Assert.Contains(panel, ChatPanel.All);
        Assert.False(ChatPanel.IsOpenFor(fake.SessionId));
        Assert.Same(panel, ChatPanel.PanelFor(fake.SessionId));

        using var _ = ChatPanelTestAccess.WithNoPanel();

        var unbound = new ChatPanel();

        try
        {
            Assert.Null(ChatPanel.PanelFor(fake.SessionId));

            // Visible as well, so IsOpenFor's visibility test does not
            // short-circuit past the session it is really asking about.
            unbound.Show();
            Flush();

            Assert.True(unbound.IsVisible);
            Assert.False(ChatPanel.IsOpenFor(fake.SessionId));

            // And HideFor, which asks the transient rather than searching:
            // this panel is the only unpinned one in the registry, so it is
            // the transient, and it has nothing to compare.
            Assert.Same(unbound, ChatPanel.Transient);
            ChatPanel.HideFor(fake.SessionId);
            Flush();

            Assert.True(unbound.IsVisible);
        }
        finally
        {
            // Through the close button, since a panel with no session cannot be
            // reached by Dispose's CloseFor loop.
            unbound.TogglePin();
            Dissolve(unbound);
        }
    }

    // --- no panels at all ---

    // The settings window can be open, and the gateway's roster can arrive,
    // before any orb has ever been clicked. Every static hook has to survive
    // that rather than throw its way out of a slider drag.
    [AvaloniaFact]
    public void EveryStaticHookSurvivesWithNoPanelAtAll()
    {
        using var _ = ChatPanelTestAccess.WithNoPanel();

        var orb = NewOrb();
        var fake = NewFake();

        Assert.False(ChatPanel.IsOpenFor(fake.SessionId));

        ChatPanel.HideFor(fake.SessionId);
        ChatPanel.CloseFor(fake.SessionId);
        ChatPanel.RepositionFor(orb);
        ChatPanel.SetSpeakState(TextToSpeech.SpeakState.Speaking);
        ChatPanel.SetSpeakState(TextToSpeech.SpeakState.Idle);
        ChatPanel.SetRecording(orb, true);
        ChatPanel.AppendToInput(orb, "nowhere to put this");
        ChatPanel.RefreshIdentityFor(orb);
        ChatPanel.ReapplyTextScale();

        Assert.Empty(ChatPanel.All);
    }

    // A panel with no orb behind it, which is what every `_owner?.` in the pin
    // path is written for. Not reachable through the app — OpenFor always binds
    // one — but the null-conditional is there, and a change that made either Pin
    // or Unpin dereference the owner would be a crash on a path nothing else
    // covers.
    //
    // Taken down through the close button rather than left in the registry: a
    // directly-constructed panel has no session id, so Dispose's CloseFor loop
    // cannot reach it.
    [AvaloniaFact]
    public void PinningAPanelWithNoOrbBehindItIsHarmless()
    {
        var panel = new ChatPanel();

        try
        {
            panel.TogglePin();
            Assert.True(panel.IsPinned);

            panel.TogglePin();
            Assert.False(panel.IsPinned);
        }
        finally
        {
            panel.TogglePin();
            Dissolve(panel);
        }

        Assert.DoesNotContain(panel, ChatPanel.All);
    }
}
