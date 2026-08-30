using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// The agent's portrait at four times the size the chat panel shows it.
//
// No OS coupling of its own, confirmed by reading the file: the only thing it
// asks the platform for is the screen the click landed on, which the headless
// platform answers with a single 1920x1280 display at scale 1. So everything
// here — where it opens, what dismisses it, and whether an animated portrait
// actually animates — is reachable headless, and none of it was reached before.
//
// One instance, reused, held in a private static field. That is the same shape
// ChatPanel has and it is read the same way, for the reason
// ChatPanelTestAccess records: reflection on a private static field has no
// runtime effect on the app, where adding a public accessor for one test
// project's convenience would change its surface.
//
// **In the Settings collection for the singleton, not for the settings.**
// AvatarPopup holds one instance in a private static field and three other
// classes reach for it — ChatPanelAvatarTests calls Close() and asserts IsOpen,
// OrbWindowClickResolutionTests and OrbWindowShownTests open it. All three were
// already in this collection and this one was not, so it ran in parallel with
// them against shared state. Added as hardening rather than as a fix for any
// one failure; the Release failure below had its own cause, named there.
[Collection("Settings")]
public class AvatarPopupTests
{
    private static readonly FieldInfo InstanceField =
        typeof(AvatarPopup).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException("AvatarPopup", "_instance");

    private static AvatarPopup? Instance => (AvatarPopup?)InstanceField.GetValue(null);

    private static readonly FieldInfo TimerField =
        typeof(AvatarPopup).GetField("_timer", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException("AvatarPopup", "_timer");

    // Takes the real timer out of a test that drives the animation itself.
    //
    // Show starts a DispatcherTimer, and Advance was extracted so a test would
    // not have to wait on it — but the timer was left running alongside, which
    // is a race the extraction did not close. If ten milliseconds pass between
    // Show returning and the next dispatcher drain, a tick is already queued and
    // that drain delivers it, so the portrait is on its *second* frame before
    // the test has advanced anything and every assertion after that is off by
    // one. It only happens when something else is loading the machine, which is
    // why it is a Release failure and a Debug pass.
    //
    // Stopped rather than slowed: an interval long enough "not to fire" is a
    // tolerance, and this suite has already had two flakes fixed by removing
    // one. With the timer stopped, every frame in the test moves because the
    // test moved it.
    //
    // Reflection rather than a seam on AvatarPopup, the same call this file
    // already makes for _instance and for the same reason: stopping a timer from
    // outside is a test's convenience, not something the app has any use for.
    private static void StopTheRealTimer() =>
        (TimerField.GetValue(Instance) as DispatcherTimer)?.Stop();

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // A distinct bitmap per frame, so "the portrait changed" can be asserted by
    // identity rather than by pixels — which is the only option under this
    // suite's null renderer, where every bitmap draws to nothing anyway.
    private static OpenClawAvatars.Avatar Avatar(int frames, int delayMs = 10)
    {
        var bitmaps = new List<Bitmap>();
        for (var i = 0; i < frames; i++) bitmaps.Add(new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96)));

        return new OpenClawAvatars.Avatar(bitmaps, Enumerable.Repeat(delayMs, frames).ToList());
    }

    private static Bitmap? PortraitSource() =>
        (Instance!.FindControl<Ellipse>("Portrait")!.Fill as ImageBrush)?.Source as Bitmap;

    // Every test leaves it closed. The instance is process-wide and the
    // Deactivated handler posts a Dismiss at Background priority, so a popup
    // left open is a Dismiss queued into whichever test runs next.
    private static void Reset()
    {
        AvatarPopup.Close();
        Flush();
    }

    [AvaloniaFact]
    public void NothingIsOpenUntilSomethingIsShown()
    {
        Reset();

        Assert.False(AvatarPopup.IsOpen);

        // Closing when nothing is open is the ordinary case — the chat panel
        // calls it whenever the header changes — and has to be a no-op rather
        // than a null dereference.
        AvatarPopup.Close();
        Assert.False(AvatarPopup.IsOpen);
    }

    [AvaloniaFact]
    public void ShowingCentresThePortraitOnTheClickAndReportsItOpen()
    {
        Reset();

        // The popup is 292 square at scale 1, so a click in the middle of the
        // headless display puts its top-left exactly 146 up and left of the
        // click — it grows out of the thing you clicked rather than appearing
        // somewhere unrelated.
        AvatarPopup.Show(Avatar(1), new PixelPoint(960, 640));
        Flush();

        Assert.True(AvatarPopup.IsOpen);
        Assert.Equal(new PixelPoint(960 - 146, 640 - 146), Instance!.Position);

        Reset();
    }

    // Pulled back inside the screen it landed on, at both ends. A portrait
    // opened from an orb parked against an edge would otherwise hang off it,
    // and an orb parked against an edge is the normal case — that is where
    // people put them.
    [AvaloniaFact]
    public void APortraitOpenedAtAnEdgeIsPulledBackOntoTheScreen()
    {
        Reset();

        var screen = Instance?.Screens.Primary;

        AvatarPopup.Show(Avatar(1), new PixelPoint(0, 0));
        Flush();

        var work = (screen ?? Instance!.Screens.Primary)!.WorkingArea;

        Assert.Equal(new PixelPoint(work.X, work.Y), Instance!.Position);

        AvatarPopup.Show(Avatar(1), new PixelPoint(work.Right + 500, work.Bottom + 500));
        Flush();

        // Fully on screen: right and bottom edges of a 292-square window inside
        // the work area, not merely its origin.
        Assert.Equal(new PixelPoint(work.Right - 292, work.Bottom - 292), Instance.Position);

        Reset();
    }

    [AvaloniaFact]
    public void AClickAnywhereOnItCloses()
    {
        Reset();

        AvatarPopup.Show(Avatar(1), new PixelPoint(960, 640));
        Flush();
        Assert.True(AvatarPopup.IsOpen);

        // There is nothing in here to interact with, so a click can only mean
        // "done looking" — which is why the handler is on the window and takes
        // no account of where in it the press landed.
        Instance!.MouseDown(new Point(10, 10), MouseButton.Left, RawInputModifiers.None);
        Flush();

        Assert.False(AvatarPopup.IsOpen);
    }

    [AvaloniaTheory]
    [InlineData(Key.Escape)]
    [InlineData(Key.Space)]
    [InlineData(Key.Enter)]
    public void EscapeSpaceAndEnterAllClose(Key key)
    {
        Reset();

        AvatarPopup.Show(Avatar(1), new PixelPoint(960, 640));
        Flush();

        Instance!.KeyPressQwerty(KeyToPhysical(key), RawInputModifiers.None);
        Flush();

        Assert.False(AvatarPopup.IsOpen);

        Reset();
    }

    // Anything else is left alone. A portrait that closed on any keystroke
    // would vanish the moment you started typing in the panel behind it.
    [AvaloniaFact]
    public void AnUnrelatedKeystrokeLeavesItOpen()
    {
        Reset();

        AvatarPopup.Show(Avatar(1), new PixelPoint(960, 640));
        Flush();

        Instance!.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        Flush();

        Assert.True(AvatarPopup.IsOpen);

        Reset();
    }

    // A still portrait starts no timer at all: a DispatcherTimer per opened
    // portrait, never stopped because there was nothing to stop, is a wakeup
    // every few milliseconds for as long as the app runs.
    [AvaloniaFact]
    public async Task AStillPortraitNeverChangesFrame()
    {
        Reset();

        var avatar = Avatar(1);
        AvatarPopup.Show(avatar, new PixelPoint(960, 640));
        Flush();

        var shown = PortraitSource();
        Assert.Same(avatar.Frames[0], shown);

        for (var i = 0; i < 10; i++)
        {
            Flush();
            await Task.Delay(10);
        }

        Assert.Same(avatar.Frames[0], PortraitSource());

        Reset();
    }

    [AvaloniaFact]
    public async Task AnAnimatedPortraitAdvancesThroughItsFrames()
    {
        Reset();

        var avatar = Avatar(3);
        AvatarPopup.Show(avatar, new PixelPoint(960, 640));

        // Before the first drain, because a drain is what delivers a tick that
        // is already due. See StopTheRealTimer.
        StopTheRealTimer();
        Flush();

        Assert.Same(avatar.Frames[0], PortraitSource());

        // Advanced directly rather than by waiting on the real timer.
        //
        // This used to poll for frames[1] while the timer ran, and it was flaky
        // for a reason worth keeping written down: with three frames, two ticks
        // delivered in one dispatcher drain step go straight from frames[0] to
        // frames[2], so the poll never matches and the wait runs out. It only
        // happened when the rest of the suite was loading the machine enough for
        // ticks to bunch up, which is the worst kind of flake — it looks like the
        // code and it is the schedule.
        //
        // Second frame, not merely "some other frame": the tick advances by one
        // and wraps, so landing on frames[1] first is what says the order is
        // right rather than that something changed.
        Instance!.Advance(avatar);
        Assert.Same(avatar.Frames[1], PortraitSource());

        // And on round the wrap, which is the arm the modulo is there for.
        Instance!.Advance(avatar);
        Assert.Same(avatar.Frames[2], PortraitSource());
        Instance!.Advance(avatar);
        Assert.Same(avatar.Frames[0], PortraitSource());

        Reset();
        await Task.CompletedTask;
    }

    // The guard the tick keeps: a tick queued against a portrait that has since
    // been replaced must do nothing rather than write a stale frame over the new
    // one. Reachable now that a tick can be delivered on demand — previously
    // this could only have been hit by winning a race.
    [AvaloniaFact]
    public void ATickForAReplacedPortraitDoesNothing()
    {
        Reset();

        var first = Avatar(3);
        var second = Avatar(2);

        AvatarPopup.Show(first, new PixelPoint(960, 640));
        Flush();
        AvatarPopup.Show(second, new PixelPoint(960, 640));
        Flush();

        var showing = PortraitSource();

        // A late tick belonging to the portrait that is gone.
        Instance!.Advance(first);

        Assert.Same(showing, PortraitSource());
        Assert.DoesNotContain(PortraitSource(), first.Frames);

        Reset();
    }

    // The reason the tick closes over its own avatar rather than reading the
    // field: a tick already queued when a second portrait is presented must not
    // repaint the new one with the old one's frames. Showing a second avatar
    // and then letting time pass is exactly that race, run deliberately.
    [AvaloniaFact]
    public async Task ATickLeftOverFromThePreviousPortraitDoesNotRepaintTheNewOne()
    {
        Reset();

        var first = Avatar(3);
        var second = Avatar(1);

        AvatarPopup.Show(first, new PixelPoint(960, 640));
        Flush();
        AvatarPopup.Show(second, new PixelPoint(960, 640));
        Flush();

        for (var i = 0; i < 20; i++)
        {
            Flush();
            await Task.Delay(5);
        }

        // Still the still portrait: the replaced avatar's frames never appear.
        Assert.Same(second.Frames[0], PortraitSource());
        Assert.DoesNotContain(PortraitSource(), first.Frames);

        Reset();
    }

    // Reopening reuses the one instance rather than leaking a window per look,
    // which is the whole reason it is a singleton: two of these open at once
    // has no meaning.
    [AvaloniaFact]
    public void ReopeningReusesTheSameWindow()
    {
        Reset();

        AvatarPopup.Show(Avatar(1), new PixelPoint(400, 400));
        Flush();
        var window = Instance;

        AvatarPopup.Close();
        Flush();
        Assert.False(AvatarPopup.IsOpen);

        AvatarPopup.Show(Avatar(2), new PixelPoint(800, 800));
        Flush();

        Assert.Same(window, Instance);
        Assert.True(AvatarPopup.IsOpen);

        Reset();
    }

    private static PhysicalKey KeyToPhysical(Key key) => key switch
    {
        Key.Escape => PhysicalKey.Escape,
        Key.Space => PhysicalKey.Space,
        Key.Enter => PhysicalKey.Enter,
        _ => PhysicalKey.A
    };

    // ---- the tick that drives an animated portrait -------------------------

    // Advance is what the popup's timer calls, and it carries that timer so each
    // frame can set the interval for the NEXT one. A GIF whose frames have
    // different delays is drawn at the wrong speed otherwise — one interval for
    // the whole animation, taken from whichever frame happened to be first.
    [AvaloniaFact]
    public void EachFrameSetsTheIntervalForTheOneAfterIt()
    {
        Reset();

        var frames = new List<Bitmap>
        {
            new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96)),
            new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96))
        };
        var avatar = new OpenClawAvatars.Avatar(frames, new List<int> { 40, 250 });

        AvatarPopup.Show(avatar, new PixelPoint(960, 640));
        Flush();

        // **The line this test was missing, and the reason it failed in Release
        // and passed in Debug.** Show starts a real DispatcherTimer on a 40ms
        // first frame; the test then drives Advance by hand. If a tick lands
        // between Show and the first Advance, the portrait is already on frame 1
        // before the test moves it and every assertion below is off by one.
        //
        // StopTheRealTimer exists for exactly this — its own comment above
        // describes this failure — and one of the two tests that drives frames
        // by hand simply did not call it. Fixed by calling it rather than by
        // slowing the timer down, which would be the tolerance this suite has
        // already removed twice.
        StopTheRealTimer();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };

        Instance!.Advance(avatar, timer);

        // Advanced onto frame 1, so the interval is frame 1's delay — the wait
        // before frame 0 comes back round.
        Assert.Equal(TimeSpan.FromMilliseconds(250), timer.Interval);
        Assert.Same(frames[1], PortraitSource());

        Instance!.Advance(avatar, timer);

        Assert.Equal(TimeSpan.FromMilliseconds(40), timer.Interval);
        Assert.Same(frames[0], PortraitSource());

        Reset();
    }

    // Called without one, nothing tries to set an interval on nothing. The
    // timer is optional because a still portrait has no timer at all, and a
    // queued tick can outlive the timer that queued it.
    [AvaloniaFact]
    public void AdvancingWithNoTimerStillMovesTheFrame()
    {
        Reset();

        var avatar = Avatar(2);
        AvatarPopup.Show(avatar, new PixelPoint(960, 640));
        Flush();

        var first = PortraitSource();

        Instance!.Advance(avatar);

        Assert.NotSame(first, PortraitSource());

        Reset();
    }

    // A tick that arrives after the portrait has been replaced does nothing to
    // the new one. Queued ticks outliving their portrait is the ordinary case —
    // the popup is reused rather than recreated — and without this guard a
    // second agent's portrait would flicker with the first agent's frames.
    [AvaloniaFact]
    public void ATickForAPortraitThatHasBeenReplacedIsIgnored()
    {
        Reset();

        var stale = Avatar(2);
        var current = Avatar(2);

        AvatarPopup.Show(stale, new PixelPoint(960, 640));
        Flush();
        AvatarPopup.Show(current, new PixelPoint(960, 640));
        Flush();

        var before = PortraitSource();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(99) };

        Instance!.Advance(stale, timer);

        Assert.Same(before, PortraitSource());
        Assert.Equal(TimeSpan.FromMilliseconds(99), timer.Interval);

        Reset();
    }
}
