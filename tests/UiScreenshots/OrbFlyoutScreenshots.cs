using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace ClaudeBuddy.Tests;

// One capture per scenario in tests/UiTests/OrbFlyoutTests.cs, named to
// match — the point is a screenshot for every behavior that suite already
// proves, not a curated highlight reel. Where a test's whole point is an
// event firing (the five "clicking X raises Y" tests), a screenshot can't
// show the click itself; what it shows instead is the button that test
// clicks, in the state that test put the flyout in before clicking it.
public class OrbFlyoutScreenshots
{
    // Same coordinate math as OrbFlyoutTests.CenterOf — Canvas.Left/Top plus
    // half the button's declared 24x24 size, not Bounds, for the same
    // just-revealed-control reason documented there.
    private static Avalonia.Point CenterOf(Control button) =>
        new(Canvas.GetLeft(button) + button.Width / 2,
            Canvas.GetTop(button) + button.Height / 2);

    private static void Click(OrbFlyout flyout, string buttonName)
    {
        var button = flyout.FindControl<Control>(buttonName)!;
        flyout.MouseDown(CenterOf(button), MouseButton.Left, RawInputModifiers.None);
        ScreenshotHelper.Flush();
    }

    [AvaloniaFact]
    public void ConstructsHeadlessWithNoException()
    {
        var flyout = new OrbFlyout();
        ScreenshotHelper.Capture(flyout, "orb-flyout-constructs-headless-with-no-exception.png");
    }

    [AvaloniaFact]
    public void DefaultConstructionShowsThreeButtonsOnTheArc()
    {
        var flyout = new OrbFlyout();
        ScreenshotHelper.Capture(flyout, "orb-flyout-default-construction-three-buttons.png");
    }

    [AvaloniaFact]
    public void SetMicVisibleAddsAFourthButtonWithoutChangingTheArcsBoundingBox()
    {
        var flyout = new OrbFlyout();
        flyout.SetMicVisible(true);
        ScreenshotHelper.Capture(flyout, "orb-flyout-mic-visible-adds-fourth-button.png");
    }

    [AvaloniaFact]
    public void SetChatVisibleOnTopOfMicShowsAllFiveButtonsOnTheSameBoundingBox()
    {
        var flyout = new OrbFlyout();
        flyout.SetMicVisible(true);
        flyout.SetChatVisible(true);
        ScreenshotHelper.Capture(flyout, "orb-flyout-mic-and-chat-visible-five-buttons.png");
    }

    [AvaloniaFact]
    public void SetMicVisibleFalseAfterTrueReturnsToTheThreeButtonArc()
    {
        var flyout = new OrbFlyout();
        flyout.SetMicVisible(true);
        flyout.SetMicVisible(false);
        ScreenshotHelper.Capture(flyout, "orb-flyout-mic-toggled-back-off.png");
    }

    [AvaloniaFact]
    public void SetArrangedTrueSwapsTheArrangeButtonToItsActiveFill()
    {
        var flyout = new OrbFlyout();
        flyout.SetArranged(true);
        ScreenshotHelper.Capture(flyout, "orb-flyout-arrange-button-active-fill.png");
    }

    [AvaloniaFact]
    public void ClickingArrangeButtonRaisesArrangeClickedExactlyOnce()
    {
        var flyout = new OrbFlyout();
        flyout.Show();
        ScreenshotHelper.Flush();
        Click(flyout, "ArrangeButton");
        ScreenshotHelper.CaptureAlreadyShown(flyout, "orb-flyout-click-arrange-button.png");
    }

    [AvaloniaFact]
    public void ClickingSettingsButtonRaisesSettingsClickedExactlyOnce()
    {
        var flyout = new OrbFlyout();
        flyout.Show();
        ScreenshotHelper.Flush();
        Click(flyout, "SettingsButton");
        ScreenshotHelper.CaptureAlreadyShown(flyout, "orb-flyout-click-settings-button.png");
    }

    [AvaloniaFact]
    public void ClickingSpeakButtonRaisesSpeakClickedExactlyOnce()
    {
        var flyout = new OrbFlyout();
        flyout.Show();
        ScreenshotHelper.Flush();
        Click(flyout, "SpeakButton");
        ScreenshotHelper.CaptureAlreadyShown(flyout, "orb-flyout-click-speak-button.png");
    }

    [AvaloniaFact]
    public void ClickingMicButtonRaisesMicClickedExactlyOnce()
    {
        var flyout = new OrbFlyout();
        flyout.Show();
        ScreenshotHelper.Flush();
        flyout.SetMicVisible(true);
        ScreenshotHelper.Flush();
        Click(flyout, "MicButton");
        ScreenshotHelper.CaptureAlreadyShown(flyout, "orb-flyout-click-mic-button.png");
    }

    [AvaloniaFact]
    public void ClickingChatButtonRaisesChatClickedExactlyOnce()
    {
        var flyout = new OrbFlyout();
        flyout.Show();
        ScreenshotHelper.Flush();
        flyout.SetChatVisible(true);
        ScreenshotHelper.Flush();
        Click(flyout, "ChatButton");
        ScreenshotHelper.CaptureAlreadyShown(flyout, "orb-flyout-click-chat-button.png");
    }
}
