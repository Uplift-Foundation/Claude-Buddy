using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace ClaudeBuddy.Tests;

// How a turn's text becomes the row's actual content: every Markdown block
// kind BuildBlock knows how to draw, every inline style Line() knows how to
// draw, the one case where there is nothing to draw at all, and a picture
// arriving by ImageUrl rather than already-decoded ImageBytes (which
// ATurnWithImageBytesRendersAsAThumbnail in ChatPanelTests already covers).
//
// The ImageUrl path normally ends in a real HTTP GET — OpenClawSessions.
// FetchMediaAsync is itself excluded from coverage for exactly that reason —
// but it checks its own cache before ever reaching the network, so seeding
// that cache by reflection (the same kind of test-only seam
// ChatPanelTestAccess uses for ChatPanel's own singleton field) lets the
// decode path be driven for real without a socket ever opening.
[Collection("Settings")]
public class ChatPanelMarkdownTests : IDisposable
{
    private readonly List<string> _toClean = new();

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private FakeChatSession NewFake(IEnumerable<ChatTurn> history)
    {
        var id = "markdown-" + Guid.NewGuid();
        _toClean.Add(id);
        return new FakeChatSession(history) { SessionId = id, DisplayName = "Markdown Session" };
    }

    private static IEnumerable<TextBlock> TextBlocksIn(Avalonia.Controls.Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>();

    private static string RenderedText(TextBlock tb)
    {
        if (!string.IsNullOrEmpty(tb.Text)) return tb.Text;
        if (tb.Inlines is null || tb.Inlines.Count == 0) return "";
        return string.Concat(tb.Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
    }

    private static Avalonia.Controls.Control RowOf(ChatPanel panel, int index) =>
        (Avalonia.Controls.Control)panel.FindControl<Avalonia.Controls.ItemsControl>("Turns")!
            .ItemsPanelRoot!.Children[index];

    // --- every block kind BuildBlock knows how to draw ---

    [AvaloniaFact]
    public void EveryMarkdownBlockKindRendersItsOwnText()
    {
        var text = string.Join("\n\n", new[]
        {
            "# Top heading",
            "#### Deep heading",
            "> a quoted line",
            "- a bullet point",
            "1. a numbered item",
            "```\nfenced code line\n```",
            "a trailing paragraph",
        });

        var fake = NewFake(new[] { new ChatTurn { Role = ChatRole.Assistant, Text = text } });
        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        var row = RowOf(panel, 0);
        var rendered = TextBlocksIn(row).Select(RenderedText).ToList();

        Assert.Contains("Top heading", rendered);
        Assert.Contains("Deep heading", rendered);
        Assert.Contains("a quoted line", rendered);
        Assert.Contains("a bullet point", rendered);
        Assert.Contains("a numbered item", rendered);
        Assert.Contains("fenced code line", rendered);
        Assert.Contains("a trailing paragraph", rendered);

        // A top-level heading (depth <= 2) is drawn a size larger than a deep
        // one — the one behavioural difference BuildBlock's Heading case
        // actually makes between the two.
        var top = TextBlocksIn(row).First(tb => RenderedText(tb) == "Top heading");
        var deep = TextBlocksIn(row).First(tb => RenderedText(tb) == "Deep heading");
        Assert.True(top.FontSize > deep.FontSize);

        // The quote is drawn at reduced opacity — BuildBlock's own reason:
        // it reads as an aside rather than as the reply itself.
        var quoteText = TextBlocksIn(row).First(tb => RenderedText(tb) == "a quoted line");
        Assert.Equal(0.75, quoteText.Opacity);

        // The bullet and ordered markers are drawn as their own runs beside
        // the text, not folded into it.
        Assert.Contains("•", rendered);
        Assert.Contains("1.", rendered);
    }

    // --- every inline style Line() knows how to draw ---

    // Bold and inline code are already pinned by ChatPanelTests'
    // MarkdownTurnRendersAsStyledRunsNotLiteralMarkup; this is the other
    // three switch arms in the same method.
    [AvaloniaFact]
    public void ItalicBoldItalicAndLinkSpansRenderAsStyledRunsNotLiteralMarkup()
    {
        var text = "*italic* and ***bold italic*** and [a link](https://example.invalid)";
        var fake = NewFake(new[] { new ChatTurn { Role = ChatRole.Assistant, Text = text } });
        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        var row = RowOf(panel, 0);
        var styled = TextBlocksIn(row).First(tb => tb.Inlines is { Count: > 1 });
        var runs = styled.Inlines!.OfType<Avalonia.Controls.Documents.Run>().ToList();

        var italic = Assert.Single(runs, r => r.Text == "italic");
        Assert.Equal(FontStyle.Italic, italic.FontStyle);

        var boldItalic = Assert.Single(runs, r => r.Text == "bold italic");
        Assert.Equal(FontWeight.SemiBold, boldItalic.FontWeight);
        Assert.Equal(FontStyle.Italic, boldItalic.FontStyle);

        var link = Assert.Single(runs, r => r.Text == "a link");
        Assert.Equal(TextDecorations.Underline, link.TextDecorations);

        var rendered = RenderedText(styled);
        Assert.DoesNotContain("*", rendered);
        Assert.DoesNotContain("[", rendered);
        Assert.DoesNotContain("https://example.invalid", rendered);
    }

    // --- nothing to parse at all ---

    // A turn whose text is only whitespace still needs a control to hand
    // back — HasText is what keeps it from actually being shown, not Body
    // returning null.
    [AvaloniaFact]
    public void AWhitespaceOnlyTurnStillBuildsABodyWithNoText()
    {
        var fake = NewFake(new[] { new ChatTurn { Role = ChatRole.Assistant, Text = "   " } });
        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        var row = RowOf(panel, 0);

        // No exception building or rendering it is the whole assertion here;
        // HasText's own binding is what actually hides the row's text area.
        Assert.NotEmpty(TextBlocksIn(row));
    }

    // --- a picture arriving by ImageUrl ---

    private static void SeedMediaCache(string url, byte[]? bytes)
    {
        var field = typeof(OpenClawSessions).GetField("Media", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dict = (Dictionary<string, byte[]?>)field.GetValue(null)!;
        dict[url] = bytes;
    }

    [AvaloniaFact]
    public async Task ATurnWithAnImageUrlFetchesAndDecodesItFromTheCache()
    {
        // The same one-pixel PNG ChatPanelTests' own ImageBytes test uses.
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

        var url = "https://gateway.invalid/media/" + Guid.NewGuid();
        SeedMediaCache(url, bytes);

        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "a picture", ImageUrl = url, IsComplete = true },
        });
        ChatPanel.OpenFor(NewOrb(), fake);

        var panel = ChatPanelTestAccess.Instance!;
        Avalonia.Controls.Image? picture = null;

        for (var i = 0; i < 40; i++)
        {
            FlushRender();
            picture = panel.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
                .FirstOrDefault(im => im.Width == 228);
            if (picture?.Source is not null) break;
            await Task.Delay(10);
        }

        Assert.NotNull(picture);
        Assert.NotNull(picture!.Source);
    }

    // A live "agent" reply is drawn with no picture at all — TurnsFromHistory
    // is the only parser that ever sees a structured image block, per
    // OpenClawChatSession.OnAgentText's own comment — and only gains one once
    // OpenClawChatSession.TryResolveLiveImage resolves a "[media attached:
    // ...]" marker against the gateway's own history, well after this row
    // already exists. This is the half of that fix TurnView owns: reacting to
    // ImageUrl arriving on a turn it already drew, rather than only reading it
    // once at construction.
    [AvaloniaFact]
    public async Task ATurnThatGainsAnImageUrlAfterItIsAlreadyOnScreenLoadsIt()
    {
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

        var url = "https://gateway.invalid/media/" + Guid.NewGuid();
        SeedMediaCache(url, bytes);

        var turn = new ChatTurn { Role = ChatRole.Assistant, Text = "working on it", IsComplete = true };
        var fake = NewFake(new[] { turn });
        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.Null(panel.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
            .FirstOrDefault(im => im.Width == 228)?.Source);

        // The resolution itself — asking the gateway, matching the nearest
        // picture — is OpenClawLiveImageResolutionTests' job (tests/UnitTests);
        // this only has to prove the row notices once ImageUrl lands, the same
        // way it already notices Text changing under a streaming reply.
        turn.ImageUrl = url;

        Avalonia.Controls.Image? picture = null;
        for (var i = 0; i < 40; i++)
        {
            FlushRender();
            picture = panel.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
                .FirstOrDefault(im => im.Width == 228);
            if (picture?.Source is not null) break;
            await Task.Delay(10);
        }

        Assert.NotNull(picture);
        Assert.NotNull(picture!.Source);
    }

    // Same shape as the ImageUrl case above, for the ImageBytes path: a
    // picture that arrives as bytes rather than as something to fetch from a
    // url — either decoded from an inline chat.history block (CB-91) or read
    // through the gateway's read-scoped media route (CB-88/CB-90). The
    // resolution itself is the unit suites' job;
    // this only has to prove the row notices once ImageBytes lands after
    // construction, the same way it already notices Text and ImageUrl.
    [AvaloniaFact]
    public async Task ATurnThatGainsImageBytesAfterItIsAlreadyOnScreenLoadsIt()
    {
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

        var turn = new ChatTurn { Role = ChatRole.Assistant, Text = "here's the drop", IsComplete = true };
        var fake = NewFake(new[] { turn });
        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.Null(panel.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
            .FirstOrDefault(im => im.Width == 228)?.Source);

        turn.ImageBytes = bytes;

        Avalonia.Controls.Image? picture = null;
        for (var i = 0; i < 40; i++)
        {
            FlushRender();
            picture = panel.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
                .FirstOrDefault(im => im.Width == 228);
            if (picture?.Source is not null) break;
            await Task.Delay(10);
        }

        Assert.NotNull(picture);
        Assert.NotNull(picture!.Source);

        // A turn that already resolved a picture gaining ImageBytes again
        // (the guard's !HasImage arm being false) must not reload — the
        // point of the guard is exactly this: to fire once, not once per
        // property change forever.
        var reloadCountBefore = picture.Source;
        turn.ImageBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        FlushRender();

        Assert.Same(reloadCountBefore, panel.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
            .FirstOrDefault(im => im.Width == 228)?.Source);
    }

    // The other arm of the same guard: ImageBytes changing to something that
    // still isn't a picture while the row has no image yet — distinct from
    // the "already has one" arm above, since here HasImage is still false
    // when the property changes. Two changes (empty, then null) rather than
    // one: QA (CB-88) found the single-empty-value version left one IL-level
    // arc of the pattern-match still unexercised, and this closes it.
    [AvaloniaFact]
    public void ATurnWhoseImageBytesChangeToEmptyOrNullNeverLoadsAnything()
    {
        var turn = new ChatTurn
        {
            Role = ChatRole.Assistant, Text = "no picture yet", IsComplete = true,
            ImageBytes = Array.Empty<byte>()
        };
        var fake = NewFake(new[] { turn });
        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        turn.ImageBytes = null;
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.Null(panel.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
            .FirstOrDefault(im => im.Width == 228)?.Source);
    }

    // A cached "no bytes" answer (a gateway that answered with nothing) must
    // not throw trying to decode zero bytes as a picture — it just leaves
    // the turn as the text it already has.
    [AvaloniaFact]
    public async Task ATurnWhoseImageUrlResolvesToNoBytesKeepsItsTextOnly()
    {
        var url = "https://gateway.invalid/media/" + Guid.NewGuid();
        SeedMediaCache(url, Array.Empty<byte>());

        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "no picture after all", ImageUrl = url, IsComplete = true },
        });
        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();
        await Task.Delay(20);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        var row = RowOf(panel, 0);
        Assert.Contains("no picture after all", TextBlocksIn(row).Select(RenderedText));
    }

    // A finding, asserted as it behaves rather than as DecodeAndShowAsync's
    // own comment promises.
    //
    // The comment above its catch says a picture that "will not decode" is
    // caught rather than left to fault the row, and OpenClawAvatars' decoder
    // does genuinely return null for bytes SKCodec cannot read (see
    // OpenClawAvatarsTests' BytesThatAreNotAnImageDecodeToNothing and
    // ATruncatedImageDecodesToNothing). Avalonia.Media.Imaging.Bitmap's own
    // DecodeToWidth does not behave the same way: five garbage bytes, and a
    // genuinely truncated real PNG's first twelve bytes, both come back as a
    // real 456x456 Bitmap with no exception at all, rather than throwing or
    // returning null — confirmed directly against DecodeToWidth outside the
    // panel before writing this. So the catch this comment describes has no
    // known way to be reached through this call at all; what a corrupt fetch
    // actually produces is a picture-shaped image, not the text-only
    // fallback the comment promises.
    //
    // Left as it is rather than fixed here, for the same reason
    // RemoteScanTests' colour-fallback finding was: it is cosmetic — a wrong
    // thumbnail rather than a wrong message — the intent is written down, and
    // changing what a broken fetch renders is its own ticket rather than
    // something that should ride along with a coverage pass. This test is
    // the record, and it will start failing the day DecodeToWidth (or this
    // method) actually rejects unreadable bytes, which is the right moment
    // to notice and simplify it.
    [AvaloniaFact]
    public async Task GarbageBytesRenderAsAPictureRatherThanFallingBackToTextOnly()
    {
        var url = "https://gateway.invalid/media/" + Guid.NewGuid();
        SeedMediaCache(url, new byte[] { 1, 2, 3, 4, 5 });

        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "a broken picture", ImageUrl = url, IsComplete = true },
        });
        ChatPanel.OpenFor(NewOrb(), fake);

        var panel = ChatPanelTestAccess.Instance!;
        Avalonia.Controls.Image? picture = null;

        for (var i = 0; i < 40; i++)
        {
            FlushRender();
            picture = panel.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
                .FirstOrDefault(im => im.Width == 228);
            if (picture?.Source is not null) break;
            await Task.Delay(10);
        }

        // The text is still there either way — HasText does not depend on
        // HasImage — but the picture rendering at all, from bytes that are
        // not a picture, is the finding.
        var row = RowOf(panel, 0);
        Assert.Contains("a broken picture", TextBlocksIn(row).Select(RenderedText));
        Assert.NotNull(picture);
        Assert.NotNull(picture!.Source);
    }
}
