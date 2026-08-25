using Avalonia.Headless.XUnit;
using Avalonia.Media;
using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.Tests;

// Tinting a profile's dock icon.
//
// In UiTests because it goes through Avalonia's Bitmap and WriteableBitmap, which
// want the headless platform initialised — everything else about it is arithmetic
// over two files on disk.
//
// The mapping is a luma ramp rather than a hue shift: dark pixels scale the tint
// toward black and light pixels blend it toward white, so a monochrome icon comes
// out as a shaded version of one colour. Worth testing because the failure is a
// dock icon that looks slightly wrong and nobody can say why — and because the
// premultiplication step in the middle is exactly the sort of thing that gets
// "simplified" away, with muddy icon edges as the only symptom.
public class IconTintTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-tint-" + Guid.NewGuid().ToString("N"));

    public IconTintTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // A source PNG built pixel by pixel, so what comes back can be compared
    // against a known input rather than against an image nobody chose.
    private string Png(params SKColor[] pixels)
    {
        var info = new SKImageInfo(pixels.Length, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);

        for (var x = 0; x < pixels.Length; x++) bitmap.SetPixel(x, 0, pixels[x]);

        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".png");
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);

        return path;
    }

    private SKColor[] Read(string path)
    {
        using var bitmap = SKBitmap.Decode(path);
        var row = new SKColor[bitmap.Width];

        for (var x = 0; x < bitmap.Width; x++) row[x] = bitmap.GetPixel(x, 0);

        return row;
    }

    private SKColor[] Tint(Color tint, params SKColor[] source)
    {
        var from = Png(source);
        var to = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".png");

        ClaudeDesktopBundles.WriteTinted(from, to, tint);

        return Read(to);
    }

    private static readonly Color Green = Color.FromRgb(0x5A, 0xF7, 0x8E);

    // Black is the darkest end of the ramp, so it stays black whatever the tint —
    // an icon's outline should not become the accent colour.
    [AvaloniaFact]
    public void BlackStaysBlack()
    {
        var result = Tint(Green, new SKColor(0, 0, 0, 255));

        Assert.InRange(result[0].Red, 0, 4);
        Assert.InRange(result[0].Green, 0, 4);
        Assert.InRange(result[0].Blue, 0, 4);
    }

    // White is the lightest end, so it stays white — the icon keeps its
    // highlights instead of flooding with colour.
    [AvaloniaFact]
    public void WhiteStaysWhite()
    {
        var result = Tint(Green, new SKColor(255, 255, 255, 255));

        Assert.InRange(result[0].Red, 251, 255);
        Assert.InRange(result[0].Green, 251, 255);
        Assert.InRange(result[0].Blue, 251, 255);
    }

    // Mid grey lands on the tint itself, which is the whole point of the ramp:
    // the body of a monochrome icon becomes the chosen colour.
    [AvaloniaFact]
    public void MidGreyBecomesTheTint()
    {
        var result = Tint(Green, new SKColor(128, 128, 128, 255));

        Assert.InRange(result[0].Red, Green.R - 6, Green.R + 6);
        Assert.InRange(result[0].Green, Green.G - 6, Green.G + 6);
        Assert.InRange(result[0].Blue, Green.B - 6, Green.B + 6);
    }

    // Monotonic: a lighter source pixel is never darker after tinting. This is
    // what makes the result read as the same icon rather than as a posterised one,
    // and it is the property a rewritten ramp would most easily break.
    [AvaloniaFact]
    public void LighterPixelsStayLighter()
    {
        var greys = Enumerable.Range(0, 16)
            .Select(i => new SKColor((byte)(i * 17), (byte)(i * 17), (byte)(i * 17), 255))
            .ToArray();

        var result = Tint(Green, greys);

        for (var i = 1; i < result.Length; i++)
        {
            var before = result[i - 1].Red + result[i - 1].Green + result[i - 1].Blue;
            var after = result[i].Red + result[i].Green + result[i].Blue;

            Assert.True(after >= before,
                $"pixel {i} came out darker than {i - 1}: {after} against {before}");
        }
    }

    // Alpha is carried through untouched. An icon whose transparency changed would
    // acquire a square background in the dock.
    [AvaloniaFact]
    public void AlphaIsPreserved()
    {
        var result = Tint(Green,
            new SKColor(128, 128, 128, 255),
            new SKColor(128, 128, 128, 128),
            new SKColor(128, 128, 128, 1));

        Assert.Equal(255, result[0].Alpha);
        Assert.InRange(result[1].Alpha, 126, 130);
        Assert.InRange(result[2].Alpha, 0, 3);
    }

    // A fully transparent pixel is skipped entirely, which is both faster and
    // safer: dividing by its zero alpha to undo premultiplication is the one input
    // that has no answer.
    [AvaloniaFact]
    public void FullyTransparentPixelsAreLeftAlone()
    {
        var result = Tint(Green, new SKColor(0, 0, 0, 0));

        Assert.Equal(0, result[0].Alpha);
    }

    // The premultiplication round trip, which is the step the source comments
    // warn about. A half-transparent mid grey has to land on the same colour as an
    // opaque mid grey — if the undo were skipped, its luma would read as half of
    // what it is and it would come out much darker, which is exactly the "muddy
    // edges" the comment describes, since an icon's antialiased edge is made
    // entirely of partly transparent pixels.
    [AvaloniaFact]
    public void APartlyTransparentPixelGetsTheSameColourAsAnOpaqueOne()
    {
        var result = Tint(Green,
            new SKColor(128, 128, 128, 255),
            new SKColor(128, 128, 128, 96));

        // Compared as decoded, with no un-premultiplying of my own. A PNG stores
        // straight alpha by definition, so the round trip through the file has
        // already undone the premultiplication WriteTinted re-applies on the way
        // out — an earlier version of this test divided by alpha again and read
        // the faded pixel as 233 against the opaque pixel's 90, which is the
        // arithmetic being done twice rather than a real difference.
        var opaque = result[0];
        var faded = result[1];

        Assert.InRange(faded.Red, opaque.Red - 10, opaque.Red + 10);
        Assert.InRange(faded.Green, opaque.Green - 10, opaque.Green + 10);
        Assert.InRange(faded.Blue, opaque.Blue - 10, opaque.Blue + 10);
    }

    // Two different tints really do produce two different icons — a test that
    // passed for a function ignoring its tint argument would be worthless.
    [AvaloniaFact]
    public void TheTintArgumentDecidesTheColour()
    {
        var grey = new SKColor(128, 128, 128, 255);

        var green = Tint(Green, grey)[0];
        var red = Tint(Color.FromRgb(0xF7, 0x5A, 0x5A), grey)[0];

        Assert.NotEqual(green, red);
        Assert.True(green.Green > green.Red, "the green tint should be greenest");
        Assert.True(red.Red > red.Green, "the red tint should be reddest");
    }

    // The written file is a real PNG of the same size, which is what the icon
    // pipeline hands to iconutil next.
    [AvaloniaFact]
    public void TheOutputIsAPngOfTheSameSize()
    {
        var from = Png(Enumerable.Repeat(new SKColor(128, 128, 128, 255), 8).ToArray());
        var to = Path.Combine(_root, "out.png");

        ClaudeDesktopBundles.WriteTinted(from, to, Green);

        Assert.True(File.Exists(to));
        using var written = SKBitmap.Decode(to);
        Assert.Equal(8, written.Width);
        Assert.Equal(1, written.Height);
    }
}
