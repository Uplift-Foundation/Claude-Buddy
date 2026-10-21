using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Xunit;

namespace Orbweaver.Tests;

// SettingsWindow.HelpText, and the Avalonia defect it exists to route around.
//
// Under this suite's headless platform, Avalonia 12.1.1 cannot lay out wrapped
// text that contains an empty line: TextFormatter returns a zero-length line
// at the second "\n" and every one after it, so TextLayout.CreateTextLines
// never reaches the end of the paragraph and grows its line list until the
// process is out of memory. The Grok auto-refresh help had such a blank line,
// and the first Settings window shown with Grok usage on hung the suite there
// (Window.Show -> ExecuteInitialLayoutPass -> TextBlock.MeasureOverride ->
// TextLayout.CreateTextLines -> List.AddWithResize, one thread, 5 GB and
// climbing). The same text under real Skia terminates, which is why no
// installed build ever froze on it.
//
// Every layout here is bounded with MaxLines, so a regression fails instead of
// hanging the run the way the original did.
[Collection("Settings")]
public class SettingsHelpTextTests : IDisposable
{
    private const int LineCap = 400;

    private readonly ScopedSettingsDir _settings = new("help-text");

    public void Dispose() => _settings.Dispose();

    private static TextLayout Bounded(TextBlock block, string text, double width) =>
        new(text, new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
            block.FontSize, null, TextAlignment.Left, block.TextWrapping, null, null,
            FlowDirection.LeftToRight, width, double.PositiveInfinity, double.NaN, 0, LineCap);

    private static int Covered(TextLayout layout) => layout.TextLines.Sum(l => l.Length);

    [AvaloniaFact]
    public void HeadlessAvaloniaStillCannotWrapAnEmptyLine()
    {
        // The defect itself, pinned. If this starts failing, an Avalonia
        // upgrade has fixed it and HelpText's paragraph split is no longer
        // load-bearing — it can stay, but its comment should say so.
        var block = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };

        var wrapped = Bounded(block, "a\n\nb", 300);
        Assert.Equal(LineCap, wrapped.TextLines.Count);
        Assert.True(Covered(wrapped) < 4);

        // The inputs HelpText does hand it are fine: one paragraph, or a
        // single line break.
        Assert.Equal(3, Covered(Bounded(block, "a\nb", 300)));
        Assert.Equal(1, Bounded(block, "a b", 300).TextLines.Count);
    }

    [AvaloniaFact]
    public void HelpWithoutAParagraphBreakIsTheSingleTextBlockItAlwaysWas()
    {
        var hint = Assert.IsType<TextBlock>(SettingsWindow.HelpText("One paragraph."));

        Assert.Equal("One paragraph.", hint.Text);
        Assert.Equal(TextWrapping.Wrap, hint.TextWrapping);
        Assert.Equal(11, hint.FontSize);
        Assert.Equal(0.55, hint.Opacity);
        Assert.Equal(new Avalonia.Thickness(0, 6, 0, 0), hint.Margin);
    }

    [AvaloniaFact]
    public void HelpWithAParagraphBreakIsOneTextBlockPerParagraph()
    {
        var panel = Assert.IsType<StackPanel>(SettingsWindow.HelpText("First.\n\nSecond.\n\nThird."));

        var blocks = panel.Children.Cast<TextBlock>().ToList();
        Assert.Equal(new[] { "First.", "Second.", "Third." }, blocks.Select(b => b.Text));
        Assert.All(blocks, b =>
        {
            Assert.Equal(TextWrapping.Wrap, b.TextWrapping);
            Assert.Equal(11, b.FontSize);
            Assert.Equal(default, b.Margin);
        });
        Assert.Equal(new Avalonia.Thickness(0, 6, 0, 0), panel.Margin);
        Assert.Equal(6, panel.Spacing);
    }

    // The guard: every section of the window switched on, and every wrapped
    // TextBlock in it laid out — bounded — to the end of its text. Laid out
    // rather than searched for "\n\n", so it also catches whatever other input
    // this Avalonia cannot get to the end of.
    [AvaloniaFact]
    public void EveryWrappedTextInAFullyPopulatedSettingsWindowLaysOutToItsEnd()
    {
        foreach (var flag in typeof(OrbweaverSettings)
                     .GetProperties(BindingFlags.Public | BindingFlags.Static)
                     .Where(p => p.PropertyType == typeof(bool) && p.CanWrite))
        {
            flag.SetValue(null, true);
        }

        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!;
        var window = (SettingsWindow)ctor.Invoke(null);

        var wrapped = window.GetLogicalDescendants().OfType<TextBlock>()
            .Where(b => b.TextWrapping == TextWrapping.Wrap && !string.IsNullOrEmpty(b.Text))
            .ToList();

        // The Grok auto-refresh help is the text that hung the suite; if it is
        // not in the window, this test is not testing what it says it is.
        Assert.Contains(wrapped, b => b.Text!.StartsWith("Grok only reports its own usage once"));

        foreach (var block in wrapped)
        {
            var layout = Bounded(block, block.Text!, 300);
            Assert.True(Covered(layout) == block.Text!.Length,
                $"headless Avalonia cannot lay out this wrapped text to its end: {block.Text}");
        }

        // And the help's second paragraph is its own block, not dropped.
        Assert.Contains(wrapped, b => b.Text!.StartsWith("This buys freshness, not movement."));

        window.Content = null;
    }
}
