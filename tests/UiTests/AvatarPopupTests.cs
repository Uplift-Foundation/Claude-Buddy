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
public class AvatarPopupTests
{
    private static readonly FieldInfo InstanceField =
        typeof(AvatarPopup).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException("AvatarPopup", "_instance");

    private static AvatarPopup? Instance => (AvatarPopup?)InstanceField.GetValue(null);

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
        Flush();

        Assert.Same(avatar.Frames[0], PortraitSource());

        // Second frame, not merely "some other frame": the tick advances by one
        // and wraps, so landing on frames[1] first is what says the order is
        // right rather than that something changed.
        for (var i = 0; i < 200 && !ReferenceEquals(PortraitSource(), avatar.Frames[1]); i++)
        {
            Flush();
            await Task.Delay(5);
        }

        Assert.Same(avatar.Frames[1], PortraitSource());

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
}
