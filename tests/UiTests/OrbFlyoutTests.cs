using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// OrbFlyout has no OS coupling (confirmed by reading OrbFlyout.axaml.cs in
// full) and its five buttons are plain named Grids wired to PointerPressed,
// not Button/Command — so "click" here means simulating a real pointer
// press at the button's on-screen centre and letting Avalonia's headless hit
// tester find it, the same way a real mouse click would.
public class OrbFlyoutTests
{
    [AvaloniaFact]
    public void ConstructsHeadlessWithNoException()
    {
        var flyout = new OrbFlyout();
        Assert.NotNull(flyout);
    }

    // Reproduces LayoutArc()'s own arithmetic (OrbFlyout.axaml.cs lines
    // ~153-220) rather than asserting a hardcoded number pulled from a single
    // run: FromAngle=140, ToAngle=40, ArcRadius=56, ButtonHalf=12 (24x24
    // buttons, per OrbFlyout.axaml) are read directly off the source and
    // comments, and the loop below is the same spread-evenly-then-bound
    // computation LayoutArc performs. If that method's arithmetic ever
    // changes, this reproduction has to change with it — which is the point:
    // a change to the arc's shape should have to touch a test that says so.
    private static (double OriginX, double OriginY) ExpectedArcOrigin(int visibleButtonCount)
    {
        const double ArcRadius = 56;
        const double ButtonHalf = 12;
        const double FromAngle = 140.0;
        const double ToAngle = 40.0;

        var angles = new double[visibleButtonCount];
        for (var i = 0; i < visibleButtonCount; i++)
        {
            angles[i] = visibleButtonCount == 1
                ? (FromAngle + ToAngle) / 2
                : FromAngle + (ToAngle - FromAngle) * i / (visibleButtonCount - 1);
        }

        double left = double.MaxValue, top = double.MaxValue;
        foreach (var angle in angles)
        {
            var rad = angle * Math.PI / 180.0;
            var cx = ArcRadius * Math.Cos(rad);
            var cy = ArcRadius * Math.Sin(rad);
            left = Math.Min(left, cx - ButtonHalf);
            top = Math.Min(top, cy - ButtonHalf);
        }

        return (-left, -top);
    }

    [AvaloniaFact]
    public void DefaultConstructionShowsThreeButtonsOnTheArc()
    {
        // Arrange, Settings and Speak start visible; Mic and Chat start
        // hidden (OrbFlyout.axaml IsVisible="False" on both) — three buttons
        // is what LayoutArc ran against in the constructor.
        var flyout = new OrbFlyout();

        var expected = ExpectedArcOrigin(3);
        Assert.Equal(expected.OriginX, flyout.ArcOriginX, precision: 6);
        Assert.Equal(expected.OriginY, flyout.ArcOriginY, precision: 6);
    }

    // LayoutArc always spreads its buttons between the same two fixed end
    // angles (FromAngle=140, ToAngle=40) no matter how many are visible —
    // only n=1 is special-cased to land dead centre (90°) instead. Angle 140
    // is always the first entry and 40 always the last for n>=2, and cosine
    // (which decides the left edge) is monotonic across [40,140], so the
    // leftmost/topmost point is the same for 2, 3, 4 or 5 buttons: the arc's
    // bounding box does not grow as buttons are added, only the spacing
    // *between* neighbours shrinks. That is the opposite of what "spreads
    // wider" suggests in isolation — it reads correctly next to the old,
    // literally-hardcoded-per-count layout the comment above LayoutArc
    // compares itself to, where adding a button meant genuinely new endpoint
    // angles rather than the same two endpoints with one more point
    // in between.
    [AvaloniaFact]
    public void SetMicVisibleAddsAFourthButtonWithoutChangingTheArcsBoundingBox()
    {
        var flyout = new OrbFlyout();
        var threeButtonOrigin = (flyout.ArcOriginX, flyout.ArcOriginY);

        flyout.SetMicVisible(true);

        var expected = ExpectedArcOrigin(4);
        Assert.Equal(expected.OriginX, flyout.ArcOriginX, precision: 6);
        Assert.Equal(expected.OriginY, flyout.ArcOriginY, precision: 6);

        Assert.Equal(threeButtonOrigin.Item1, flyout.ArcOriginX, precision: 6);
        Assert.Equal(threeButtonOrigin.Item2, flyout.ArcOriginY, precision: 6);
    }

    [AvaloniaFact]
    public void SetChatVisibleOnTopOfMicShowsAllFiveButtonsOnTheSameBoundingBox()
    {
        var flyout = new OrbFlyout();

        flyout.SetMicVisible(true);
        var fourButtonOrigin = (flyout.ArcOriginX, flyout.ArcOriginY);

        flyout.SetChatVisible(true);

        var expected = ExpectedArcOrigin(5);
        Assert.Equal(expected.OriginX, flyout.ArcOriginX, precision: 6);
        Assert.Equal(expected.OriginY, flyout.ArcOriginY, precision: 6);
        Assert.Equal(fourButtonOrigin.Item1, flyout.ArcOriginX, precision: 6);
        Assert.Equal(fourButtonOrigin.Item2, flyout.ArcOriginY, precision: 6);
    }

    [AvaloniaFact]
    public void SetMicVisibleFalseAfterTrueReturnsToTheThreeButtonArc()
    {
        var flyout = new OrbFlyout();
        var threeButtonOrigin = (flyout.ArcOriginX, flyout.ArcOriginY);

        flyout.SetMicVisible(true);
        flyout.SetMicVisible(false);

        Assert.Equal(threeButtonOrigin.Item1, flyout.ArcOriginX, precision: 6);
        Assert.Equal(threeButtonOrigin.Item2, flyout.ArcOriginY, precision: 6);
    }

    [AvaloniaFact]
    public void SetArrangedTrueSwapsTheArrangeButtonToItsActiveFill()
    {
        var flyout = new OrbFlyout();
        var arrangeFill = flyout.FindControl<Ellipse>("ArrangeFill");
        Assert.NotNull(arrangeFill);

        // The XAML-parsed Fill="#F01..." starts life as an
        // ImmutableSolidColorBrush; SetArranged replaces it with a plain
        // SolidColorBrush (see OrbFlyout's ArrangeNormalFill/ArrangeActiveFill
        // fields). Both implement ISolidColorBrush, which is the common
        // surface that actually matters here: the colour.
        var normalColor = ((ISolidColorBrush)arrangeFill!.Fill!).Color;

        flyout.SetArranged(true);
        var activeColor = ((ISolidColorBrush)arrangeFill.Fill!).Color;

        Assert.NotEqual(normalColor, activeColor);
        // The amber "active" fill named in OrbFlyout.axaml.cs (ArrangeActiveFill).
        Assert.Equal(Color.Parse("#E0B8860B"), activeColor);

        flyout.SetArranged(false);
        var restoredColor = ((ISolidColorBrush)arrangeFill.Fill!).Color;
        // Back to whatever SetArranged(false) applies (ArrangeNormalFill),
        // not necessarily bit-identical to the XAML-parsed brush it replaced —
        // only the colour is asserted, which is the whole of what the button
        // shows.
        Assert.Equal(Color.Parse("#E0202024"), restoredColor);
    }

    // --- the five click events -------------------------------------------
    //
    // Each button is a plain Grid wired to PointerPressed (no Command, no
    // Button.Click), so the click is simulated as a real pointer press at
    // the button's centre in the flyout's own coordinate space — the same
    // space LayoutArc positions buttons in via Canvas.SetLeft/SetTop — and
    // Avalonia's headless hit tester is what finds the Grid underneath it.
    // That requires the window to actually be shown and laid out first, or
    // there is nothing on screen yet for a point to hit.

    // Canvas.Left/Top plus half of the button's own declared Width/Height
    // (24x24 for every arc button, per OrbFlyout.axaml) — not Bounds, which
    // is only populated once a measure/arrange pass has actually run for
    // that specific control, and a button that was just switched from
    // IsVisible="False" to true has not necessarily had one yet by the time
    // a click needs to land on it.
    private static Point CenterOf(Control button) =>
        new(Canvas.GetLeft(button) + button.Width / 2,
            Canvas.GetTop(button) + button.Height / 2);

    private static OrbFlyout ShownFlyout()
    {
        var flyout = new OrbFlyout();
        flyout.Show();
        Flush();
        return flyout;
    }

    // Dispatcher.UIThread.RunJobs() alone flushes measure/arrange (Bounds on
    // a just-revealed button is correct immediately after it), but headless
    // hit-testing follows the *rendered* scene graph, which a dispatcher-jobs
    // flush does not touch — a click on a button that was invisible a moment
    // ago landed on whatever used to be drawn at that point instead, until a
    // render tick is forced. Confirmed by instrumenting InputHitTest directly:
    // after only RunJobs(), a point inside MicButton's freshly-correct Bounds
    // hit-tested to SpeakButton's fill (the previous frame's contents at that
    // point); adding ForceRenderTimerTick() made it resolve to MicButton's own
    // fill. This is the one place this suite's assumptions about the headless
    // API needed correcting by experiment rather than by reading it.
    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ClickingArrangeButtonRaisesArrangeClickedExactlyOnce()
    {
        var flyout = ShownFlyout();
        var fired = 0;
        flyout.ArrangeClicked += () => fired++;

        var button = flyout.FindControl<Control>("ArrangeButton")!;
        flyout.MouseDown(CenterOf(button), MouseButton.Left, RawInputModifiers.None);
        Flush();

        Assert.Equal(1, fired);
    }

    [AvaloniaFact]
    public void ClickingSettingsButtonRaisesSettingsClickedExactlyOnce()
    {
        var flyout = ShownFlyout();
        var fired = 0;
        flyout.SettingsClicked += () => fired++;

        var button = flyout.FindControl<Control>("SettingsButton")!;
        flyout.MouseDown(CenterOf(button), MouseButton.Left, RawInputModifiers.None);
        Flush();

        Assert.Equal(1, fired);
    }

    [AvaloniaFact]
    public void ClickingSpeakButtonRaisesSpeakClickedExactlyOnce()
    {
        var flyout = ShownFlyout();
        var fired = 0;
        flyout.SpeakClicked += () => fired++;

        var button = flyout.FindControl<Control>("SpeakButton")!;
        flyout.MouseDown(CenterOf(button), MouseButton.Left, RawInputModifiers.None);
        Flush();

        Assert.Equal(1, fired);
    }

    [AvaloniaFact]
    public void ClickingMicButtonRaisesMicClickedExactlyOnce()
    {
        var flyout = ShownFlyout();
        flyout.SetMicVisible(true);
        Flush();

        var fired = 0;
        flyout.MicClicked += () => fired++;

        var button = flyout.FindControl<Control>("MicButton")!;
        flyout.MouseDown(CenterOf(button), MouseButton.Left, RawInputModifiers.None);
        Flush();

        Assert.Equal(1, fired);
    }

    [AvaloniaFact]
    public void ClickingChatButtonRaisesChatClickedExactlyOnce()
    {
        var flyout = ShownFlyout();
        flyout.SetChatVisible(true);
        Flush();

        var fired = 0;
        flyout.ChatClicked += () => fired++;

        var button = flyout.FindControl<Control>("ChatButton")!;
        flyout.MouseDown(CenterOf(button), MouseButton.Left, RawInputModifiers.None);
        Flush();

        Assert.Equal(1, fired);
    }
}
