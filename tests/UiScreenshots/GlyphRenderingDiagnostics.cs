using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-171. Windows captures of this suite come back with some text rendered as
// solid black blobs — legible on the macOS rid, unreadable on win-x64, with
// every test green on both. Two features have merged without a Windows visual
// review because of it.
//
// The measurable signature, read off the published PNGs rather than guessed
// at: on win-x64 the affected text has exactly **two** alpha values, 0 and
// 255. Not "fewer" levels — two. Every Group heading (SettingsWindow's
// SemiBold 13pt title) is like this on Windows and antialiased on macOS, and
// two whole cards (the speech card and the peer-link card) are like it
// throughout. Antialiased text at 11-13px carries 100+ alpha levels; with
// two, stems merge and counters fill, which is exactly what the blobs are.
//
// So this is not a font-fallback gap, a layout difference or a corrupted
// cache: the advances are right to the pixel against the macOS run, and the
// glyphs are the right glyphs. The mask is being generated without
// antialiasing.
//
// What this file does NOT do is assume why. Avalonia's Skia backend only
// reaches SKFontEdging.Alias through TextRenderingMode.Alias or
// RenderOptions.EdgeMode.Aliased, and neither this app nor either theme it
// loads (Fluent on Windows, the Devolutions macOS theme on macOS — see
// App.axaml.cs) sets either one. Skia's DirectWrite scaler likewise only
// chooses DWRITE_RENDERING_MODE_ALIASED when Skia already asked for a
// bi-level mask; it never downgrades on its own. Those are three readings of
// source that each rule a mechanism *out* and none that rules one *in*, and
// the defect cannot be reproduced on macOS at all.
//
// Hence a measurement rather than a theory. This runs on the runner where the
// defect lives, walks a matrix of weights, sizes and explicit rendering modes,
// and writes what each one actually produced next to the screenshots. The
// report names the resolved typeface for each sample, because "which face did
// SemiBold land on" is the question every remaining hypothesis turns on.
//
// It is deliberately report-only for now: the point of this round is
// evidence, and a red step on Windows would cost three retries of the whole
// suite before the artifact uploads. The assertion that turns corruption into
// a failing test belongs in the same file once the report says what the floor
// should be.
public class GlyphRenderingDiagnostics
{
    // Alpha levels and coverage for one rendered control, read back through
    // Skia rather than through Avalonia's own bitmap API: the suite already
    // decodes PNGs this way in IconTintTests, and going through the encoder is
    // the only way to measure exactly the bytes a reviewer will look at rather
    // than an intermediate the encoder might still change.
    private static (int Levels, int Ink, double Antialiased) Coverage(Control control)
    {
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(control.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(control.Bounds.Height)));

        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(control);

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;

        using var decoded = SKBitmap.Decode(stream);

        var counts = new int[256];

        for (var y = 0; y < decoded.Height; y++)
        {
            for (var x = 0; x < decoded.Width; x++)
            {
                counts[decoded.GetPixel(x, y).Alpha]++;
            }
        }

        var levels = counts.Count(c => c > 0);
        var ink = counts.Skip(1).Sum();
        var partial = counts.Skip(1).Take(254).Sum();

        return (levels, ink, ink == 0 ? 0 : (double)partial / ink);
    }

    // A standalone block in a window of its own, so each matrix cell is
    // measured without whatever the settings page's own tree contributes.
    // Never closed, for the reason every other class here says: closing a
    // headless window has corrupted the process-wide FontManager cache in this
    // suite before.
    private static TextBlock Block(string text, double size, FontWeight weight, TextRenderingMode mode)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = Brushes.Black
        };

        if (mode != TextRenderingMode.Unspecified)
        {
            TextOptions.SetTextRenderingMode(block, mode);
        }

        var window = new Window
        {
            Width = 420,
            Height = 60,
            Background = Brushes.White,
            Content = block
        };

        window.Show();
        ScreenshotHelper.Flush();

        return block;
    }

    private static string Face(double size, FontWeight weight)
    {
        var typeface = new Typeface(FontManager.Current.DefaultFontFamily, FontStyle.Normal, weight);

        return FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface)
            ? $"{glyphTypeface.FamilyName} w={glyphTypeface.Weight} s={glyphTypeface.Style} st={glyphTypeface.Stretch}"
            : "<unresolved>";
    }

    [AvaloniaFact]
    public void ReportsHowGlyphMasksAreGeneratedOnThisRunner()
    {
        var report = new StringBuilder();

        report.AppendLine("CB-171 glyph rendering diagnostics");
        report.AppendLine("os        : " + RuntimeInformation.OSDescription);
        report.AppendLine("arch      : " + RuntimeInformation.ProcessArchitecture);
        report.AppendLine("rid       : " + RuntimeInformation.RuntimeIdentifier);
        report.AppendLine("avalonia  : " + typeof(Application).Assembly.GetName().Version);
        report.AppendLine("skiasharp : " + typeof(SKBitmap).Assembly.GetName().Version);
        report.AppendLine("default   : " + FontManager.Current.DefaultFontFamily.Name);
        report.AppendLine();

        var systemFonts = FontManager.Current.SystemFonts.Select(f => f.Name).OrderBy(n => n).ToList();
        report.AppendLine($"system fonts ({systemFonts.Count}):");
        report.AppendLine("  " + string.Join(", ", systemFonts.Take(80)));
        report.AppendLine();

        // The faces the settings page actually asks for. SemiBold 13 is the
        // Group heading — the one sample that is corrupt on Windows in every
        // capture that contains one — and 11 Normal is the help text, which is
        // clean in some captures and corrupt in others.
        report.AppendLine("resolved faces:");
        foreach (var weight in new[] { FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold })
        {
            report.AppendLine($"  {weight,-10} -> {Face(13, weight)}");
        }
        report.AppendLine();

        report.AppendLine("mask coverage by size / weight / requested mode");
        report.AppendLine("  levels=2 means a bi-level mask: the corruption signature.");
        report.AppendLine();
        report.AppendLine($"  {"size",4} {"weight",-10} {"mode",-18} {"levels",6} {"ink",7} {"aa",6}");

        foreach (var size in new double[] { 11, 12, 13, 16 })
        {
            foreach (var weight in new[] { FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold })
            {
                foreach (var mode in new[]
                         {
                             TextRenderingMode.Unspecified,
                             TextRenderingMode.Antialias,
                             TextRenderingMode.SubpixelAntialias,
                             TextRenderingMode.Alias
                         })
                {
                    var block = Block("Connect directly to other machines", size, weight, mode);
                    var (levels, ink, aa) = Coverage(block);

                    report.AppendLine(
                        $"  {size,4} {weight,-10} {mode,-18} {levels,6} {ink,7} {aa,6:F2}");

                    // The one row that is a contract rather than an
                    // observation. Grayscale is what ScreenshotHelper now asks
                    // for on every capture, and it is the only mode that
                    // produced a real mask on the Windows runner — so if it
                    // ever stops doing so, every screenshot this suite
                    // publishes becomes unreadable again and nothing else here
                    // would say a word about it.
                    if (mode == TextRenderingMode.Antialias)
                    {
                        Assert.True(
                            levels > 6,
                            $"Grayscale antialiasing produced {levels} luminance levels at "
                            + $"{size}px {weight} on this runner. Two means a bi-level mask, "
                            + "which is CB-171's defect arriving through the mode chosen to "
                            + "avoid it — the capture path needs a different answer, not a "
                            + "lower floor here.");
                    }
                }
            }
        }

        report.AppendLine();
        report.AppendLine(CloudBadgeCoverage());
        report.AppendLine();
        report.AppendLine(LiveSettingsHeadingReport());

        var path = Path.Combine(ScreenshotHelper.OutputDir, "font-diagnostics.txt");
        File.WriteAllText(path, report.ToString());

        // Nothing to assert yet — see the file header. The report is the
        // deliverable of this round.
        Assert.True(File.Exists(path));
    }

    // The second half of CB-171: whether U+2601 is a glyph Windows actually
    // has, or whether the orb's cloud badge is a fallback box. Asked of the
    // font manager directly rather than inferred from a picture, because a
    // capture that is already suspect cannot answer it.
    private static string CloudBadgeCoverage()
    {
        var report = new StringBuilder();
        report.AppendLine("badge glyph coverage:");

        foreach (var (codepoint, name) in new[]
                 {
                     (0x2601, "U+2601 cloud"),
                     (0x23F1, "U+23F1 stopwatch"),
                     (0x21C4, "U+21C4 arrows"),
                     (0x2699, "U+2699 gear"),
                     (0x0040, "U+0040 at"),
                     (0x0023, "U+0023 hash")
                 })
        {
            var inDefault = FontManager.Current.TryGetGlyphTypeface(
                               new Typeface(FontManager.Current.DefaultFontFamily), out var face)
                           && face.CharacterToGlyphMap.ContainsGlyph(codepoint);

            var matched = FontManager.Current.TryMatchCharacter(
                codepoint,
                FontStyle.Normal,
                FontWeight.Normal,
                FontStretch.Normal,
                FontManager.Current.DefaultFontFamily,
                null,
                out var fallback)
                ? fallback.FontFamily.Name
                : "<none>";

            report.AppendLine($"  {name,-20} in default font: {inDefault,-5} fallback: {matched}");
        }

        return report.ToString();
    }

    // The real thing, not a stand-in: the actual Group heading off a real
    // SettingsWindow, plus every RenderOptions/TextOptions value inherited
    // down to it. If something in the tree is asking for aliased text, this is
    // where it shows up; if nothing is, that rules the whole family of
    // explanations out on the runner rather than by reading source.
    private static string LiveSettingsHeadingReport()
    {
        var report = new StringBuilder();
        report.AppendLine("live SettingsWindow heading:");

        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes);

        if (ctor is null)
        {
            report.AppendLine("  <no private constructor>");
            return report.ToString();
        }

        var window = (Window)ctor.Invoke(null);
        window.Show();
        ScreenshotHelper.Flush();

        foreach (var wanted in new[] { "Claude Code in the cloud", "Speaks", "Other machines" })
        {
            var block = window.GetLogicalDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(b => b.Text == wanted);

            if (block is null)
            {
                report.AppendLine($"  {wanted,-26} <not found>");
                continue;
            }

            var (levels, ink, aa) = Coverage(block);

            report.AppendLine(
                $"  {wanted,-26} size={block.FontSize} weight={block.FontWeight} " +
                $"levels={levels} ink={ink} aa={aa:F2}");

            foreach (var ancestor in block.GetSelfAndLogicalAncestors().OfType<Visual>())
            {
                var edge = RenderOptions.GetEdgeMode(ancestor);
                var text = TextOptions.GetTextRenderingMode(ancestor);
                var hint = TextOptions.GetTextHintingMode(ancestor);

                if (edge == EdgeMode.Unspecified
                    && text == TextRenderingMode.Unspecified
                    && hint == TextHintingMode.Unspecified)
                {
                    continue;
                }

                report.AppendLine(
                    $"      {ancestor.GetType().Name}: edge={edge} text={text} hint={hint}");
            }
        }

        // The same headings again, measured the way the capture path measures
        // them: the option set on an ancestor and that ancestor rendered, not
        // the block. ImmediateRenderer pushes TextOptions from the render root
        // downwards, so a setting above the root is not in scope at all — which
        // is exactly why ScreenshotHelper sets it on the visual it is about to
        // render rather than once on the application. Written this way round
        // because the first attempt set it on the window, rendered the block,
        // and would have reported the fix as not working when what was broken
        // was the measurement.
        report.AppendLine("  ...with grayscale asked for on the rendered ancestor:");

        foreach (var wanted in new[] { "Claude Code in the cloud", "Speaks", "Other machines" })
        {
            var block = window.GetLogicalDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(b => b.Text == wanted);

            var host = block?.GetVisualParent();

            if (block is null || host is null) continue;

            TextOptions.SetTextRenderingMode(host, TextRenderingMode.Antialias);
            ScreenshotHelper.Flush();

            var (levels, ink, aa) = Coverage((Control)host);

            report.AppendLine(
                $"      {wanted,-26} host={host.GetType().Name} levels={levels} ink={ink} aa={aa:F2}");
        }

        return report.ToString();
    }
}
