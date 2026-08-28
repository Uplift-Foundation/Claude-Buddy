using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// Whether a real pointer gesture on a *team-role* orb becomes a click at all.
//
// This is the one place in the UI suite that synthesizes a pointer on an orb
// rather than calling OnClicked with an int, and it is deliberate: the bug it
// was written for is a report that team orbs "do nothing" when double-clicked
// while non-team orbs work, and every existing test drives OnClicked directly —
// which is downstream of exactly the plumbing under suspicion. Asserting on the
// destination cannot say whether the journey started.
//
// Safe to do here, unlike anywhere else in this suite, because the click action
// is pinned to "none" for the duration. RunClickAction's "none" arm returns
// without touching TerminalFocuser, so no tmux, ps or osascript subprocess can
// be launched by a synthesized press — which is the reason
// OrbWindowClickResolutionTests' header rules a local orb's pointer path out. The
// question here is not what the click *does*; it is whether the gesture becomes
// a click, and ResolvedGestures answers that without needing it to do anything.
//
// [Collection("Settings")] because these tests write the three click-action
// settings, and ClaudeBuddySettings is a process-wide static — see
// tests/UiTests/SettingsCollection.cs.
[Collection("Settings")]
public class OrbWindowTeamClickTests
{
    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // A teammate's status, which is what puts an orb into the member scale:
    // SetTeamRole is called from UpdateFrom on a non-empty Lead, and nothing
    // else turns it on.
    private static SessionStatus Teammate(string lead = "lead-session-id") => new()
    {
        State = "idle",
        Cwd = "/Users/warren/project",
        Title = "engineer",
        Lead = lead,
        Tty = "ttys018",
        TermProgram = "tmux",
        TmuxPane = "%53",
        SessionPid = 4321,
    };

    private static SessionStatus Loner() => new()
    {
        State = "idle",
        Cwd = "/Users/warren/project",
        Title = "solo",
        Tty = "ttys007",
        TermProgram = "tmux",
        TmuxPane = "%6",
        SessionPid = 4322,
    };

    // Every gesture bound to nothing, so a resolved click reaches
    // RunClickAction's "none" arm and stops there. Restored by the caller.
    private static IDisposable NoActionsBound()
    {
        var click = ClaudeBuddySettings.ClickAction;
        var dbl = ClaudeBuddySettings.DoubleClickAction;
        var triple = ClaudeBuddySettings.TripleClickAction;

        ClaudeBuddySettings.ClickAction = "none";
        ClaudeBuddySettings.DoubleClickAction = "none";
        ClaudeBuddySettings.TripleClickAction = "none";

        return new Restore(() =>
        {
            ClaudeBuddySettings.ClickAction = click;
            ClaudeBuddySettings.DoubleClickAction = dbl;
            ClaudeBuddySettings.TripleClickAction = triple;
        });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private static OrbWindow Shown(SessionStatus status)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.Show();
        orb.UpdateFrom(status);
        Flush();
        return orb;
    }

    // The centre of the orb's own 56x56 box, which is where a person aims: the
    // circle is drawn around DIP (28,28) at both scales — see OrbWindow.axaml's
    // note on Root being pinned there.
    private static readonly Point Centre = new(28, 28);

    private static void Press(OrbWindow orb, int clickCount)
    {
        // ClickCount is carried on the press and nowhere else (see
        // OnPointerPressed), and the headless platform's MouseDown does not
        // synthesize a double click for two presses in a row — so the count is
        // raised explicitly. Everything else about the gesture is real: the
        // event is raised on the orb's own Root, which is where the production
        // handler's own hit test lands it, and OnPointerPressed/Released are
        // reached as the Window's class handlers exactly as they are in the app.
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var root = orb.FindControl<Grid>("Root")!;

        root.RaiseEvent(new PointerPressedEventArgs(
            root, pointer, orb, Centre, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, clickCount));

        root.RaiseEvent(new PointerReleasedEventArgs(
            root, pointer, orb, Centre, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
    }

    // The control case, and the one the bug report says works: an orb with no
    // team role at all. If this ever fails, the two below say nothing about
    // teams.
    [AvaloniaFact]
    public void ADoubleClickOnAnOrdinaryOrbResolves()
    {
        using var _ = NoActionsBound();
        var orb = Shown(Loner());

        Press(orb, 1);
        Press(orb, 2);
        Flush();

        Assert.Equal(2, orb.ResolvedGestures);
        Assert.Equal(2, orb.LastResolvedClicks);
    }

    // The reported failure: a member-scaled teammate. Same gesture, same point,
    // and the only difference in the status is the Lead field that shrinks the
    // drawn orb.
    [AvaloniaFact]
    public void ADoubleClickOnAMemberScaledTeammateResolves()
    {
        using var _ = NoActionsBound();
        var orb = Shown(Teammate());

        Press(orb, 1);
        Press(orb, 2);
        Flush();

        Assert.Equal(2, orb.ResolvedGestures);
        Assert.Equal(2, orb.LastResolvedClicks);
    }

    // A single click on the same orb, because the two gestures take different
    // arms of OnClicked and a member orb that ate only one of them would still
    // read as "team orbs do nothing".
    [AvaloniaFact]
    public void ASingleClickOnAMemberScaledTeammateResolves()
    {
        using var _ = NoActionsBound();
        var orb = Shown(Teammate());

        Press(orb, 1);
        Flush();

        Assert.Equal(1, orb.ResolvedGestures);
        Assert.Equal(1, orb.LastResolvedClicks);
    }

    // The gesture chain from the machine the bug was reported on, which is not
    // the shipped default and turns out to be the whole difference: clickAction
    // "chat", doubleClickAction "terminal", tripleClickAction "speak".
    //
    // Every gesture in that chain has a longer one bound to something else, so
    // *both* the single and the double click take OnClicked's waiting arm — the
    // double click has to sit out the multi-click window in case a third one is
    // coming. Nothing about that is team-specific, and pinning it here is what
    // rules the dispatch out: if a team orb resolved this chain and an ordinary
    // one did not, the plumbing would be the answer.
    [AvaloniaFact]
    public async System.Threading.Tasks.Task ADoubleClickResolvesOnTheReportersOwnGestureChain()
    {
        var click = ClaudeBuddySettings.ClickAction;
        var dbl = ClaudeBuddySettings.DoubleClickAction;
        var triple = ClaudeBuddySettings.TripleClickAction;
        try
        {
            ClaudeBuddySettings.ClickAction = "chat";
            ClaudeBuddySettings.DoubleClickAction = "terminal";
            ClaudeBuddySettings.TripleClickAction = "speak";

            // A gateway status, so the "terminal" arm this chain resolves to
            // reaches TerminalFocuser.Focus and returns from its !IsLocalCli
            // guard without launching anything — the same trade
            // OrbWindowClickResolutionTests' header documents. Lead is still set,
            // so the orb is drawn at the member scale.
            var orb = new OrbWindow(Guid.NewGuid().ToString());
            orb.Show();
            orb.UpdateFrom(new SessionStatus
            {
                Source = SessionSource.OpenClaw,
                State = "idle",
                Title = "engineer",
                Lead = "lead-session-id",
            });
            Flush();

            Press(orb, 1);
            Press(orb, 2);

            // Both gestures wait, so the second click's action only runs when the
            // multi-click window elapses — MultiClickMs is 300, and the pattern
            // for pumping a real DispatcherTimer is
            // OrbWindowClickResolutionTests'.
            for (var attempt = 0; attempt < 60 && orb.LastResolvedClicks != 2; attempt++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                await System.Threading.Tasks.Task.Delay(10);
            }

            Assert.Equal(2, orb.ResolvedGestures);
            Assert.Equal(2, orb.LastResolvedClicks);
        }
        finally
        {
            ClaudeBuddySettings.ClickAction = click;
            ClaudeBuddySettings.DoubleClickAction = dbl;
            ClaudeBuddySettings.TripleClickAction = triple;
        }
    }
}
