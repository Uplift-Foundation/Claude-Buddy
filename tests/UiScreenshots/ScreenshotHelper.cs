using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Xunit;

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
        Save(control, fileName);
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
        Save(window, fileName);
    }

    // One render, written to disk and checked, rather than two paths that
    // could drift apart.
    private static void Save(Visual visual, string fileName)
    {
        AskForGrayscaleText(visual);

        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(visual.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(visual.Bounds.Height)));

        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(visual);

        var path = Path.Combine(OutputDir, fileName);
        bitmap.Save(path);

        AssertTextIsLegible(visual, path, fileName);
    }

    // CB-171. A screenshot that nobody can read is worse than no screenshot,
    // because it still looks like evidence: every test passes, the PR comment
    // carries a picture, and a reviewer who cannot make the Windows one out
    // assumes the shot is just small. Two features merged that way before
    // anyone worked out the captures themselves were wrong.
    //
    // What goes wrong is specific and measurable. On win-x64 the affected text
    // comes back as a bi-level mask — two alpha values, 0 and 255 — where the
    // same text on osx-arm64 carries a hundred or more. At 11-13px that is not
    // a quality difference; stems merge, counters fill, and a sentence becomes
    // a row of black lumps. So the check is not "does this look right", which
    // no assertion can ask, but "was this text antialiased at all", which is a
    // yes or no with an enormous margin either side of it.
    //
    // Asked of each TextBlock's own rectangle rather than of the image as a
    // whole, deliberately. A whole-image statistic has to guess which pixels
    // were text, and every version of that guess this was prototyped against
    // put a real capture within one or two levels of a corrupt one — a margin
    // far too thin to gate a build on. The visual tree already knows exactly
    // where the text is, and asking it turns a statistical rule into a direct
    // one.
    private static void AssertTextIsLegible(Visual root, string path, string fileName)
    {
        using var image = SKBitmap.Decode(path);

        if (image is null) return;

        // Read the whole surface once. SKBitmap.GetPixel is a per-call
        // colour-type conversion, and a chat panel capture is 340x420 with
        // three dozen TextBlocks over it — going through it pixel by pixel
        // turned a suite that runs in seconds into one that does not finish.
        var luma = LumaPlane(image);

        foreach (var block in root.GetSelfAndVisualDescendants().OfType<TextBlock>())
        {
            if (!block.IsVisible || string.IsNullOrWhiteSpace(block.Text)) continue;
            if (block.Bounds.Width < 8 || block.Bounds.Height < 6) continue;

            var origin = block.TranslatePoint(default, root);

            if (origin is null) continue;

            var left = (int)Math.Floor(origin.Value.X);
            var top = (int)Math.Floor(origin.Value.Y);
            var right = Math.Min(image.Width, left + (int)Math.Ceiling(block.Bounds.Width));
            var bottom = Math.Min(image.Height, top + (int)Math.Ceiling(block.Bounds.Height));

            left = Math.Max(0, left);
            top = Math.Max(0, top);

            if (right - left < 8 || bottom - top < 6) continue;

            var counts = new int[256];

            for (var y = top; y < bottom; y++)
            {
                var row = y * image.Width;

                for (var x = left; x < right; x++)
                {
                    counts[luma[row + x]]++;
                }
            }

            var distinct = counts.Count(c => c > 0);

            // Text whose ink never reaches the capture — scrolled out, clipped,
            // or drawn in the background colour — has nothing to say about
            // antialiasing, and a rule that failed on it would be asserting
            // something else entirely.
            var total = counts.Sum();
            var modal = counts.Max();

            if (total - modal < 40) continue;

            Assert.True(
                distinct >= 6,
                $"{fileName}: the text \"{Trim(block.Text!)}\" rendered with {distinct} "
                + $"distinct luminance levels in its own {right - left}x{bottom - top} "
                + "rectangle. Antialiased text at this size carries dozens; two means a "
                + "bi-level glyph mask, which at 11-13px is the unreadable-blob defect "
                + "CB-171 exists for. The capture is corrupt, not merely ugly — do not "
                + "review it, and do not raise this floor to make the run green.");
        }
    }

    private static string Trim(string text) =>
        text.Length <= 40 ? text : text[..37] + "...";

    // Luminance rather than alpha: some captures sit on an opaque background
    // and carry a single alpha value across the whole image, which would make
    // an alpha-based reading report "one level" about a perfectly good
    // screenshot.
    private static byte[] LumaPlane(SKBitmap image)
    {
        var pixels = image.Pixels;
        var luma = new byte[pixels.Length];

        for (var i = 0; i < pixels.Length; i++)
        {
            var pixel = pixels[i];

            luma[i] = (byte)Math.Round(
                (0.299 * pixel.Red) + (0.587 * pixel.Green) + (0.114 * pixel.Blue));
        }

        return luma;
    }

    // CB-171, the fix. Ask for grayscale antialiasing rather than taking the
    // default, which on the Windows runner produces text nobody can read.
    //
    // Measured on the runner itself rather than reasoned about, because three
    // readings of source had each ruled a mechanism out and none had ruled one
    // in. Rendering the same string at four sizes and three weights under each
    // of the four TextRenderingMode values, on win-x64:
    //
    //     Unspecified         2 luminance levels   (bi-level: the defect)
    //     SubpixelAntialias   2 luminance levels   (byte-identical ink count)
    //     Alias               2 luminance levels
    //     Antialias           9-17 levels          (correct)
    //
    // Every size, every weight, no exceptions. Unspecified and
    // SubpixelAntialias agreeing exactly is the confirmation that
    // Unspecified *is* SubpixelAntialias — which is what Avalonia's
    // GlyphRunImpl.GetTextBlob says it resolves to. On osx-arm64 the same
    // matrix gives 148-169 levels for all three of those and 2 only for Alias,
    // which is why this has always looked like a Windows-only font problem
    // rather than what it is.
    //
    // So: an LCD (subpixel) glyph mask is three per-channel coverages, and
    // there is nowhere for them to go in a RenderTargetBitmap's alpha channel
    // — coverage collapses to opaque-or-nothing on the way in. Avalonia knows
    // this and guards its own offscreen targets against it: SurfaceRenderTarget
    // is created with DisableTextLcdRendering hard-coded true for anything that
    // is not a compositor layer (Skia's DrawingContextImpl.CreateRenderTarget).
    // A RenderTargetBitmap the test constructs directly never passes through
    // that, so it is the one text-drawing surface in the process with LCD text
    // still switched on. Asking for grayscale here is not a workaround — it is
    // the setting Avalonia would have applied itself one layer down.
    //
    // Scoped to the capture path on purpose. The app draws through the
    // compositor onto a real window surface, where LCD is ordinary ClearType
    // and correct; its only two RenderTargetBitmap uses (a chat-panel arrow
    // and a menu swatch) draw lines and ellipses, no text. Nothing a Windows
    // user sees is changed by this, and a screenshot suite that quietly
    // changed it would be showing something the app does not do.
    //
    // Set on the visual being captured rather than on the application: the
    // ImmediateRenderer that RenderTargetBitmap.Render walks pushes
    // visual.TextOptions around that visual's whole subtree, so one call
    // covers everything in frame and nothing outside it.
    private static void AskForGrayscaleText(Visual visual)
    {
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Antialias);
    }
}
