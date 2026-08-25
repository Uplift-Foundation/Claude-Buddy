using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ClaudeBuddy.Tests;

// Shared by every screenshot class in this project: build a control, show
// it, force a real render, save the frame. Pulled out once every one of the
// 28 tests/UiTests scenarios got its own capture — 28 copies of the same
// four lines would have been the actual maintenance burden here, not the
// screenshots themselves.
internal static class ScreenshotHelper
{
    // dotnet test's working directory is the test assembly's own output
    // folder (tests/UiScreenshots/bin/...), not wherever the command was
    // invoked from, so Directory.GetCurrentDirectory() lands nowhere near
    // where ci.yml's artifact-upload step expects TestResults/ to be. Walk
    // up from the running assembly to the repo root instead, the same way
    // tests/IntegrationTests's hook-script tests locate ClaudeBuddyHook.sh.
    public static readonly string OutputDir =
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

    static ScreenshotHelper()
    {
        Directory.CreateDirectory(OutputDir);
    }

    // RunJobs() alone flushes measure/arrange, but a compositor-driven render
    // (headless hit-testing has the same caveat, per tests/UiTests's
    // OrbFlyoutTests) still needs a timer tick to actually run a paint pass
    // over whatever just got invalidated.
    public static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // One control rather than a whole window, for a surface that sits below
    // the fold of a scrolling page. Capture(window) renders at the window's own
    // height, so anything scrolled out of view is simply absent from the image
    // — which is not a useful screenshot of a settings row near the bottom.
    //
    // The control must already be arranged: show its window and Flush() first,
    // or Bounds is empty and this saves a 1x1 pixel.
    public static void CaptureControl(Control control, string fileName)
    {
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(control.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(control.Bounds.Height)));

        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(control);
        bitmap.Save(Path.Combine(OutputDir, fileName));
    }

    public static void Capture(Window window, string fileName)
    {
        window.Show();
        Flush();
        CaptureAlreadyShown(window, fileName);
    }

    // For a window already Shown and flushed by its own test (OrbFlyoutTests'
    // click tests need to click before capturing, so Capture's own Show+Flush
    // would be redundant work, not wrong, just worth avoiding).
    //
    // Renders straight into a fresh RenderTargetBitmap rather than reading
    // back window.CaptureRenderedFrame() (the compositor's own last-composed
    // frame): ChatPanel is a process-wide singleton that gets rebound and
    // re-shown rather than recreated per test (see ChatPanel.Bind's own
    // `if (!IsVisible) Show()`), and CaptureRenderedFrame() came back
    // byte-identical across every test after the first real one against that
    // reused window — the compositor's cached frame never advanced past the
    // window's original Show(), no matter how many ForceRenderTimerTick()
    // calls or InvalidateVisual() calls ran first. Confirmed the underlying
    // data was correct throughout (right item counts, right IsVisible) — only
    // the frame the compositor handed back was stale. RenderTargetBitmap
    // walks the current visual tree directly instead of asking the
    // compositor for its last frame, so it has no such history to be stale
    // against. Windows built fresh per test (OrbFlyout, OrbWindow) never
    // exercised the reused-singleton path, so it never showed up there.
    public static void CaptureAlreadyShown(Window window, string fileName)
    {
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Height)));

        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(window);
        bitmap.Save(Path.Combine(OutputDir, fileName));
    }
}
