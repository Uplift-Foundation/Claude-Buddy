using Avalonia.Headless.XUnit;

namespace ClaudeBuddy.Tests;

// The original two hand-picked captures from when this project was a small
// proof of concept: does capturing an actual rendered frame headless, and
// getting it somewhere a human can look at it, work end to end for this app
// at all? It did, and the four other files in this project are the answer
// to the question that came after — scaled to a screenshot for every
// scenario tests/UiTests already covers, not just these two.
public class ScreenshotTests
{
    [AvaloniaFact]
    public void OrbFlyoutWithMicAndChatVisible()
    {
        var flyout = new OrbFlyout();
        flyout.SetMicVisible(true);
        flyout.SetChatVisible(true);

        ScreenshotHelper.Capture(flyout, "orb-flyout.png");
    }

    [AvaloniaFact]
    public void OrbWindowWithAGreenAccentAndAWaitingState()
    {
        var orb = new OrbWindow("screenshot-poc");
        orb.UpdateFrom(new SessionStatus
        {
            State = "waiting",
            Cwd = "/Users/example/project",
            Title = "Example session",
            Color = "green"
        });

        ScreenshotHelper.Capture(orb, "orb-window.png");
    }
}
