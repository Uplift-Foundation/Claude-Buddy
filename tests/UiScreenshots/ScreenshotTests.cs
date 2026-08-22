using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// A small proof of concept, not a permanent suite yet: does capturing an
// actual rendered frame of a real control, headless, and getting it into
// somewhere a human can look at it (see .github/workflows/ci.yml's job
// summary step) actually work end to end for this app? Two representative
// windows, not the full 28-test surface tests/UiTests covers — expanding
// coverage is a separate decision once this is seen working.
//
// Every capture follows the same three steps: build the control, Show() it
// (headless rendering needs a "shown" TopLevel to produce a frame at all,
// unlike tests/UiTests's null-renderer suite, which never draws), then
// CaptureRenderedFrame() and save it as a PNG under TestResults/screenshots.
public class ScreenshotTests
{
    // dotnet test's working directory is the test assembly's own output
    // folder (tests/UiScreenshots/bin/...), not wherever the command was
    // invoked from, so Directory.GetCurrentDirectory() lands nowhere near
    // where ci.yml's artifact-upload step expects TestResults/ to be. Walk
    // up from the running assembly to the repo root instead, the same way
    // tests/IntegrationTests's hook-script tests locate ClaudeBuddyHook.sh.
    private static readonly string OutputDir =
        Path.Combine(FindRepoRoot(), "TestResults", "screenshots");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ClaudeBuddy.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find ClaudeBuddy.csproj by walking up from " + AppContext.BaseDirectory);
    }

    static ScreenshotTests()
    {
        Directory.CreateDirectory(OutputDir);
    }

    [AvaloniaFact]
    public void OrbFlyoutWithMicAndChatVisible()
    {
        var flyout = new OrbFlyout();
        flyout.SetMicVisible(true);
        flyout.SetChatVisible(true);
        flyout.Show();
        Dispatcher.UIThread.RunJobs();

        Save(flyout.CaptureRenderedFrame(), "orb-flyout.png");
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
        orb.Show();
        Dispatcher.UIThread.RunJobs();

        Save(orb.CaptureRenderedFrame(), "orb-window.png");
    }

    private static void Save(Avalonia.Media.Imaging.WriteableBitmap? frame, string fileName)
    {
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(OutputDir, fileName));
    }
}
