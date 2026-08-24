using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
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

    // --- the speak button's three looks ----------------------------------
    //
    // Three, because the button means three different things, and the source's
    // own reasoning is that the glyph and the words have to agree or the words
    // are worse than none: pressing it cancels in two of the three states, so
    // both of those have to read as "press again to stop". Amber-and-hourglass
    // rather than the speaking blue for Preparing is the part most likely to be
    // "simplified" away by someone who has not watched the neural engine take
    // several seconds to reach its first sound — a stop button sitting over
    // silence reads as a hang.

    private static string SpeakGlyphOf(OrbFlyout flyout) =>
        flyout.FindControl<Avalonia.Controls.TextBlock>("SpeakGlyph")!.Text ?? "";

    private static Color SpeakColorOf(OrbFlyout flyout) =>
        ((ISolidColorBrush)flyout.FindControl<Ellipse>("SpeakFill")!.Fill!).Color;

    // The tooltip is a ThoughtBubble control, not a string — App.axaml strips
    // the ToolTip template to a bare ContentPresenter so an orb's bubble can
    // *be* its tooltip, which leaves a plain string as unstyled text floating
    // on the desktop. Reading the caption back out means walking into that
    // control rather than casting the tip to a string.
    private static string SpeakTipTextOf(OrbFlyout flyout)
    {
        var tip = ToolTip.GetTip(flyout.FindControl<Control>("SpeakButton")!);
        var text = (tip as Control)?.GetVisualDescendants()
            .OfType<Avalonia.Controls.TextBlock>()
            .Select(block => block.Text)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return text ?? "";
    }

    [AvaloniaFact]
    public void SpeakingIsBlueWithAStopSquareAndSaysStop()
    {
        var flyout = new OrbFlyout();

        flyout.SetSpeakState(TextToSpeech.SpeakState.Speaking);

        Assert.Equal(Color.Parse("#E04A90D9"), SpeakColorOf(flyout));
        Assert.Equal("⏹", SpeakGlyphOf(flyout));
        Assert.Equal("Stop", SpeakTipTextOf(flyout));
    }

    [AvaloniaFact]
    public void PreparingIsAmberWithAnHourglassAndSaysSo()
    {
        var flyout = new OrbFlyout();

        flyout.SetSpeakState(TextToSpeech.SpeakState.Preparing);

        // Amber, and specifically *not* the speaking blue: "working on it" and
        // "playing" have to be told apart at a glance, not only by the glyph.
        Assert.Equal(Color.Parse("#E0B8860B"), SpeakColorOf(flyout));
        Assert.NotEqual(Color.Parse("#E04A90D9"), SpeakColorOf(flyout));
        Assert.Equal("⏳", SpeakGlyphOf(flyout));
        Assert.Equal("Preparing…", SpeakTipTextOf(flyout));
    }

    [AvaloniaFact]
    public void GoingIdleRestoresThePlainSpeakerAndItsOriginalCaption()
    {
        var flyout = new OrbFlyout();

        flyout.SetSpeakState(TextToSpeech.SpeakState.Speaking);
        flyout.SetSpeakState(TextToSpeech.SpeakState.Idle);

        Assert.Equal(Color.Parse("#E0202024"), SpeakColorOf(flyout));
        Assert.Equal("\U0001F508", SpeakGlyphOf(flyout));
        Assert.Equal("Read aloud", SpeakTipTextOf(flyout));
    }

    // Every arc button gets a caption, placed *below* it — above would cover
    // the orb the pointer is on — and every caption is a word or two rather
    // than a help topic, which is what the first version of these was: the
    // bubble came out wider than the arc it was labelling.
    [AvaloniaFact]
    public void EveryArcButtonCarriesAShortCaptionPlacedBelowIt()
    {
        var flyout = new OrbFlyout();

        foreach (var (name, caption) in new[]
                 {
                     ("ArrangeButton", "Arrange"),
                     ("SettingsButton", "Settings"),
                     ("SpeakButton", "Read aloud"),
                     ("MicButton", "Dictate"),
                     ("ChatButton", "Chat")
                 })
        {
            var button = flyout.FindControl<Control>(name)!;

            Assert.NotNull(ToolTip.GetTip(button));
            Assert.Equal(PlacementMode.Bottom, ToolTip.GetPlacement(button));
            Assert.Equal(250, ToolTip.GetShowDelay(button));

            var text = (ToolTip.GetTip(button) as Control)?.GetVisualDescendants()
                .OfType<Avalonia.Controls.TextBlock>()
                .Select(block => block.Text)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            Assert.Equal(caption, text);
            Assert.True(caption.Split(' ').Length <= 2, $"{name}'s caption is a sentence");
        }
    }

    // --- showing, moving and hiding ---------------------------------------

    // The dispatcher has to be given real time here, unlike everywhere else in
    // this file: the fly-out is a DispatcherTimer at 60 Hz over 160 ms, and
    // ForceRenderTimerTick does not advance dispatcher timers — only the render
    // clock. So this pumps jobs against the wall clock until the predicate
    // holds, and fails loudly rather than hanging if it never does.
    private static async Task PumpUntil(Func<bool> done, string what)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) return;

            // await, not Thread.Sleep: the dispatcher timer driving the
            // animation only gets to run when this test yields the dispatcher
            // back, and a sleeping test holds it. Same reason ChatPanelTests'
            // FlushAsync is written this way.
            await Task.Delay(5);
        }

        Assert.Fail($"timed out waiting for {what}");
    }

    [AvaloniaFact]
    public async Task ShowNearAnimatesFromTheOrbAndLandsExactlyOnTheTarget()
    {
        var owner = new Window();
        var flyout = new OrbFlyout();

        var from = new PixelPoint(400, 300);
        var to = new PixelPoint(600, 500);

        flyout.ShowNear(from, to, owner);

        // Starts *at the orb*, fully transparent: the whole point of the
        // animation is that the buttons appear to slide out from under it, and
        // starting anywhere else is the "mic drew on top of the orb" bug the
        // window-order comment describes from the other end.
        Assert.Equal(from, flyout.Position);
        Assert.Equal(0, flyout.Opacity);

        await PumpUntil(() => flyout.Position == to && flyout.Opacity >= 1,
            "the fly-out to reach its target");

        // Exactly the target, not merely close to it: the eased interpolation
        // rounds, so the final tick assigns _flyTo outright rather than
        // trusting the arithmetic to land on it.
        Assert.Equal(to, flyout.Position);
        Assert.Equal(1, flyout.Opacity);
    }

    // A second ShowNear while it is already up is a move, not a re-entry: the
    // orb has been dragged, or the panel has moved it. Re-running the animation
    // would make the flyout swim back to the orb and out again on every drag
    // tick.
    [AvaloniaFact]
    public async Task ShowNearOnAnAlreadyVisibleFlyoutJumpsStraightToTheTarget()
    {
        var owner = new Window();
        var flyout = new OrbFlyout();

        flyout.ShowNear(new PixelPoint(100, 100), new PixelPoint(200, 200), owner);
        await PumpUntil(() => flyout.Position == new PixelPoint(200, 200) && flyout.Opacity >= 1,
            "the first fly-out");

        flyout.ShowNear(new PixelPoint(0, 0), new PixelPoint(900, 700), owner);

        Assert.Equal(new PixelPoint(900, 700), flyout.Position);
        Assert.Equal(1, flyout.Opacity);
    }

    // Hide stops the timer as well as hiding the window. Without that, a
    // flyout dismissed mid-flight keeps ticking against a hidden window and
    // reassigns its position and opacity for the rest of the 160 ms — and the
    // last thing that tick does is call PlaceInFront, which would pull a
    // hidden window back to the top of the topmost band.
    [AvaloniaFact]
    public async Task HidingMidFlightStopsTheAnimationRatherThanLettingItFinish()
    {
        var owner = new Window();
        var flyout = new OrbFlyout();

        var to = new PixelPoint(800, 600);
        flyout.ShowNear(new PixelPoint(0, 0), to, owner);
        flyout.Hide();

        var frozen = flyout.Position;

        // Well past the 160 ms the animation would have taken.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.False(flyout.IsVisible);
        Assert.Equal(frozen, flyout.Position);
        Assert.NotEqual(to, flyout.Position);
    }

    // What OrbWindow reads to decide whether the pointer has really left the
    // orb-plus-flyout region, or has only crossed the gap between them. It has
    // to answer for the Canvas the buttons live on rather than for the window,
    // which is bigger than the arc's bounding box on no platform but is the
    // thing Avalonia would report about by default.
    [AvaloniaFact]
    public void IsPointerOverFlyoutIsFalseWhileNothingIsPointingAtIt()
    {
        var flyout = ShownFlyout();

        Assert.False(flyout.IsPointerOverFlyout);
    }
}
