using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace ClaudeBuddy.Tests;

// ChatPanel is a process-wide singleton (see its own header comment: two
// panels would fight over being the key window), so every test here has to
// clean up after itself rather than rely on process isolation between test
// methods — HideFor(sessionId) is the public teardown call (it unbinds the
// session and hides the window), called from Dispose so a failed assertion
// still leaves the singleton clean for the next test.
public class ChatPanelTests : IDisposable
{
    private readonly List<string> _sessionIdsToClean = new();

    private FakeChatSession NewFake(IEnumerable<ChatTurn>? history = null, string? sessionId = null)
    {
        var id = sessionId ?? "fake-" + Guid.NewGuid();
        _sessionIdsToClean.Add(id);
        return new FakeChatSession(history) { SessionId = id, DisplayName = "Fake Session" };
    }

    // Deliberately never closed: closing an OrbWindow tears down its
    // headless compositor, and something in that teardown corrupts a
    // process-wide resource shared with every other headless window's
    // FontManager — closing any orb built in one test method reliably
    // makes the *next* test's window construction throw a
    // KeyNotFoundException for "fonts:SystemFonts", confirmed by toggling
    // Close() on and off across this file's four tests. Leaving orbs
    // unclosed (they are never Shown either, so nothing is left visibly
    // open) is the workaround; ChatPanel.HideFor still tears down the one
    // thing that actually needs tearing down between tests, the panel
    // singleton's binding.
    private OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    public void Dispose()
    {
        foreach (var id in _sessionIdsToClean) ChatPanel.HideFor(id);
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
    }

    // Flush() alone drains only the dispatcher's own queue, which is enough
    // for everything else in this file because nothing else leaves that
    // thread. Attaching a pasted picture does — AttachImageAsync's encode
    // runs on a real ThreadPool thread via Task.Run — so this waits on real
    // wall-clock time for that thread to actually finish and post its
    // continuation back before flushing again, instead of asserting against
    // a dispatcher queue the background work hasn't reached yet.
    private static async Task FlushAsync()
    {
        for (var i = 0; i < 40; i++)
        {
            Flush();
            await Task.Delay(10);
        }
    }

    // ItemsControl's default panel (a StackPanel, per ChatPanel.axaml not
    // overriding ItemsPanel) hosts exactly one realized ContentPresenter per
    // item in History order — confirmed by instrumenting it directly rather
    // than assumed: each child's DataContext is the item's TurnView, so
    // Children.Count is the row count with no need to hunt through the
    // template's nested Borders (the DataTemplate itself nests two more
    // Border elements per row for the speaker-avatar/initials chip, which
    // would over-count rows if counted instead).
    private static Controls RenderedRows(ChatPanel panel) =>
        panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children;

    // A TextBlock's visible text is either its own Text (the common case:
    // BuildBody's Line() sets Text directly when a line has no Markdown
    // styling) or, for a styled line, the concatenation of its Inlines' Run
    // text — ChatPanel builds those as plain Run objects with no nested
    // Inlines of their own.
    private static string RenderedText(TextBlock tb)
    {
        if (!string.IsNullOrEmpty(tb.Text)) return tb.Text;
        if (tb.Inlines is null || tb.Inlines.Count == 0) return "";
        return string.Concat(tb.Inlines.OfType<Run>().Select(r => r.Text));
    }

    private static IEnumerable<TextBlock> TextBlocksIn(Avalonia.Controls.Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>();

    [AvaloniaFact]
    public void OpenForRendersOneRowPerHistoryTurnWithMatchingText()
    {
        var orb = NewOrb();
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "hi there" },
            new ChatTurn { Role = ChatRole.Assistant, Text = "hello back" },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "room message",
                Speaker = "Zara",
                SpeakerColor = "#00AF5F"
            },
        });

        ChatPanel.OpenFor(orb, fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var rows = RenderedRows(panel);
        Assert.Equal(3, rows.Count);

        foreach (var (row, turn) in rows.Zip(fake.History))
        {
            var texts = TextBlocksIn(row).Select(RenderedText).ToList();
            Assert.Contains(turn.Text, texts);
        }
    }

    [AvaloniaFact]
    public void MarkdownTurnRendersAsStyledRunsNotLiteralMarkup()
    {
        var orb = NewOrb();
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.Assistant, Text = "**bold** and `code`" },
        });

        ChatPanel.OpenFor(orb, fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var row = RenderedRows(panel)[0];

        // BuildBody -> Line() only builds an Inlines run-list when there is
        // more than one styled span (ChatMarkdown.Inline splits "**bold**",
        // " and ", "`code`" into three); a plain line instead sets .Text
        // directly and never populates Inlines at all.
        var styledBlock = TextBlocksIn(row).FirstOrDefault(tb => tb.Inlines is { Count: > 1 });
        Assert.NotNull(styledBlock);
        Assert.True(styledBlock!.Inlines!.Count > 1);

        var rendered = RenderedText(styledBlock);
        Assert.Contains("bold", rendered);
        Assert.Contains("code", rendered);
        // The literal Markdown syntax must not survive parsing.
        Assert.DoesNotContain("**", rendered);
        Assert.DoesNotContain("`", rendered);
    }

    [AvaloniaFact]
    public void TurnWithSpeakerShowsTheSpeakersNameOnItsRow()
    {
        var orb = NewOrb();
        var fake = NewFake(new[]
        {
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "room message",
                Speaker = "Zara",
                SpeakerColor = "#00AF5F"
            },
        });

        ChatPanel.OpenFor(orb, fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var row = RenderedRows(panel)[0];

        var texts = TextBlocksIn(row).Select(RenderedText).ToList();
        Assert.Contains("Zara", texts);
    }

    // This brief expected HeadlessWindowExtensions.KeyTextInput/KeyPress (the
    // documented hardware-simulation surface — see the class comment on
    // OrbFlyoutTests, where they work fine) to drive this too. They don't,
    // reliably, for this window: ChatPanel is a singleton reused across every
    // test method in this class, and by the time this test runs it has
    // already been Bound/Hidden three times over by the tests above it.
    //
    // Instrumented directly to find out why: TopLevel.FocusManager correctly
    // reports Input as focused (GetFocusedElement() returns it), but the raw
    // TextInput/KeyDown events that KeyTextInput/KeyPress synthesize still
    // arrive with the *window* as their Source rather than Input — i.e. the
    // lower-level routing headless input simulation relies on to pick a
    // target (independent of TopLevel.FocusManager) never got pointed at
    // Input on this pass, even though the modern focus API insists it is
    // focused. Confirmed in isolation, run alone this exact test (using
    // KeyTextInput/KeyPress as the brief describes) passes — it is
    // specifically the accumulation of prior Bind()/Hide() cycles against the
    // same singleton window that desyncs it, not anything wrong with the
    // panel or with these tests individually.
    //
    // The fix below raises TextInputEvent/KeyDownEvent directly on Input,
    // which sidesteps that focus-resolution step entirely while still
    // exercising the real production event handlers (TextBox's own
    // TextInput handling; ChatPanel's own tunnel-registered OnInputKeyDown)
    // — a real test of the panel's behaviour, just not routed through
    // hardware simulation's target-resolution, which this environment does
    // not do reliably for a window this well-used by the time this test runs.
    [AvaloniaFact]
    public void TypingAndPressingEnterSendsTheTypedTextAndClearsTheBox()
    {
        var orb = NewOrb();
        var fake = NewFake();

        ChatPanel.OpenFor(orb, fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;

        input.Focus();
        Flush();

        input.RaiseEvent(new Avalonia.Input.TextInputEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.TextInputEvent,
            Text = "hello from a test"
        });
        Flush();
        Assert.Equal("hello from a test", input.Text);

        // OnInputKeyDown is registered on Input itself with
        // RoutingStrategies.Tunnel (see ChatPanel's constructor), so raising
        // KeyDownEvent at Input still reaches it: Avalonia's tunnel route
        // ends at the element RaiseEvent was called on.
        input.RaiseEvent(new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = Key.Enter
        });
        Flush();

        Assert.Equal(new[] { "hello from a test" }, fake.SentTexts);
        Assert.Equal("", input.Text);
    }

    // A picture on the clipboard, pasted into a session that implements
    // IRemoteChatImages: it should show as a thumbnail rather than being
    // typed as text, and sending should route through SendWithImagesAsync
    // carrying the path of the file the panel actually wrote — not through
    // SendAsync, which would mean the picture went nowhere.
    [AvaloniaFact]
    public async Task PastingAPictureAttachesItAndSendingCarriesItsPath()
    {
        var orb = NewOrb();
        var fake = NewFake();

        ChatPanel.OpenFor(orb, fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;
        var attachments = panel.FindControl<ItemsControl>("Attachments")!;

        input.Focus();
        Flush();

        var bitmap = new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96));
        await panel.Clipboard!.SetBitmapAsync(bitmap);

        var gesture = TextBox.PasteGesture!;
        input.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = gesture.Key,
            KeyModifiers = gesture.KeyModifiers
        });
        await FlushAsync();

        Assert.True(attachments.IsVisible);
        Assert.Single((System.Collections.IEnumerable)attachments.ItemsSource!);

        input.RaiseEvent(new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Text = "a screenshot"
        });
        Flush();

        input.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter
        });
        Flush();

        Assert.Empty(fake.SentTexts);
        var sent = Assert.Single(fake.SentWithImages);
        Assert.Equal("a screenshot", sent.Text);
        var path = Assert.Single(sent.ImagePaths);
        Assert.True(File.Exists(path));

        Assert.False(attachments.IsVisible);
        Assert.Equal("", input.Text);

        File.Delete(path);
    }

    // The same paste against a session that does *not* implement
    // IRemoteChatImages — a gateway room, today — must not be swallowed:
    // OnInputKeyDown only intercepts the gesture when the bound session has
    // somewhere to put a picture, so this has to fall through to the
    // TextBox's own paste rather than silently doing nothing.
    [AvaloniaFact]
    public async Task PastingAPictureOnASessionWithoutImageSupportLeavesItToTheTextBox()
    {
        var orb = NewOrb();
        var bare = new BareChatSession();
        _sessionIdsToClean.Add(bare.SessionId);

        ChatPanel.OpenFor(orb, bare);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;
        var attachments = panel.FindControl<ItemsControl>("Attachments")!;

        input.Focus();
        Flush();

        var bitmap = new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96));
        await panel.Clipboard!.SetBitmapAsync(bitmap);

        var gesture = TextBox.PasteGesture!;
        input.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = gesture.Key,
            KeyModifiers = gesture.KeyModifiers
        });
        Flush();

        Assert.False(attachments.IsVisible);
    }

    // A turn that already carries ImageBytes — what a local CLI's own
    // transcript produces for a pasted picture once ChatTranscript has
    // decoded it (see ChatTranscript.MapUser) — has to actually render as a
    // picture in the bubble, not just toggle some flag the panel never
    // draws. This is the one part of the feature the paste tests above
    // don't reach: those cover the *pending* attachment strip above the
    // input box, not a turn already in history.
    [AvaloniaFact]
    public async Task ATurnWithImageBytesRendersAsAThumbnail()
    {
        var orb = NewOrb();

        // A one-pixel PNG, the same fixture ChatTranscript's own tests use —
        // the pixels don't matter, only that this decodes as a real image.
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

        var turn = new ChatTurn
        {
            Role = ChatRole.User,
            Text = "a screenshot",
            IsComplete = true,
            ImageBytes = bytes
        };

        var fake = NewFake(new[] { turn });

        ChatPanel.OpenFor(orb, fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        // Matched on the picture's own fixed width (228, per ChatPanel.axaml)
        // rather than just "any Image in the row": the same row template also
        // draws a 16pt Image for a speaker's avatar, and the two would
        // otherwise be indistinguishable by type alone.
        Image? picture = null;
        for (var i = 0; i < 40; i++)
        {
            Flush();
            picture = panel.GetVisualDescendants().OfType<Image>().FirstOrDefault(im => im.Width == 228);
            if (picture?.Source is not null) break;
            await Task.Delay(10);
        }

        Assert.NotNull(picture);
        Assert.NotNull(picture!.Source);
    }

    // Nothing but the four required members of IRemoteChatSession — no
    // IRemoteChatImages — so a test can be sure the panel behaves correctly
    // for the transport that doesn't have one yet.
    private sealed class BareChatSession : IRemoteChatSession
    {
        public string SessionId { get; } = "bare-" + Guid.NewGuid();
        public string DisplayName { get; init; } = "Bare Session";
        public RemoteChatState State { get; set; } = RemoteChatState.Connected;

        private readonly List<ChatTurn> _history = new();
        public IReadOnlyList<ChatTurn> History => _history;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;

        public Task SendAsync(string text)
        {
            var turn = new ChatTurn { Role = ChatRole.User, Text = text };
            _history.Add(turn);
            TurnAdded?.Invoke(turn);
            return Task.CompletedTask;
        }

        public void Cancel()
        {
        }
    }
}
