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

    private FakeChatSession NewFake(IEnumerable<ChatTurn>? history = null, string? sessionId = null,
        IReadOnlyList<SlashCommand>? slashCommands = null)
    {
        var id = sessionId ?? "fake-" + Guid.NewGuid();
        _sessionIdsToClean.Add(id);
        return new FakeChatSession(history)
        {
            SessionId = id,
            DisplayName = "Fake Session",
            SlashCommands = slashCommands ?? Array.Empty<SlashCommand>()
        };
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

    // The bubble is the DataTemplate's own root Border (see ChatPanel.axaml's
    // per-turn template), so it is the first Border a depth-first walk from
    // the row's ContentPresenter reaches — before the speaker-avatar/initials
    // chip Borders nested two levels deeper inside it.
    private static Border BubbleBorderOf(Avalonia.Controls.Control row) =>
        row.GetVisualDescendants().OfType<Border>().First();

    // A resize handle's centre, in panel coordinates — used as the position
    // carried by Drag's synthesized pointer events, not as a hit-test
    // target (see Drag's own comment for why hit-testing isn't used here).
    // TranslatePoint rather than OrbFlyoutTests' Canvas.GetLeft/Top: these
    // handles are alignment-positioned, not Canvas-positioned.
    private static Point CenterOf(Avalonia.Controls.Control control, Visual ancestor) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), ancestor)
        ?? throw new InvalidOperationException($"{control} is not inside {ancestor}");

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // MouseDown/MouseMove rely on hit-testing against the *rendered* scene
    // graph, and that went stale after enough resizes of this class's
    // reused ChatPanel singleton — a point inside Root's own correctly
    // laid-out Bounds hit-tested to nothing at all, even after forcing
    // extra render ticks. Same fix TypingAndPressingEnterSendsTheTypedText
    // AndClearsTheBox already needed for a different flakiness on this same
    // reused window: raise the routed event directly on whichever element
    // the real handler is registered on (the handle itself for
    // PointerPressed, the panel for PointerMoved/PointerReleased — see
    // where ChatPanel's constructor wires each one up), which sidesteps
    // hit-testing while still exercising the real production handlers.
    private static void Drag(ChatPanel panel, string handleName, Vector delta)
    {
        var handle = panel.FindControl<Avalonia.Controls.Control>(handleName)!;
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var start = CenterOf(handle, panel);
        var end = start + delta;

        handle.RaiseEvent(new PointerPressedEventArgs(
            handle, pointer, panel, start, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
        FlushRender();

        panel.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, panel, pointer, panel, end, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));
        FlushRender();

        panel.RaiseEvent(new PointerReleasedEventArgs(
            panel, pointer, panel, end, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
        FlushRender();
    }

    // Every resize-related test needs this: ChatPanel is a reused singleton
    // across the whole class (see the class comment), and nothing resets
    // Width/Height/Position between tests the way HideFor resets the
    // session binding — a resize test run after another one otherwise
    // inherits whatever size or position the previous one's drag left
    // behind, which is exactly what made this suite flaky to write (a
    // "grows by 40" assertion fails outright if a prior test already left
    // the panel sitting at MaxWidth, with nowhere left to grow).
    private static void ResetGeometry(ChatPanel panel)
    {
        panel.Width = 340;
        panel.Height = 420;
        panel.Position = new PixelPoint(100, 100);
        FlushRender();
    }

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

    // BeginResizeDrag looked like the obvious way to drive this (see the
    // long comment beside it in ChatPanel.axaml.cs) but is a silent no-op on
    // Avalonia.Native's macOS backend, which is exactly the kind of failure
    // that compiles, runs, and does nothing — worth a real drag-simulated
    // test rather than trusting that the hand-rolled replacement works.
    [AvaloniaFact]
    public void DraggingTheSouthEastCornerGrowsWidthAndHeightWithoutMovingThePanel()
    {
        var orb = NewOrb();
        var fake = NewFake();
        ChatPanel.OpenFor(orb, fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        ResetGeometry(panel);
        var (width0, height0, pos0) = (panel.Width, panel.Height, panel.Position);

        Drag(panel, "ResizeSE", new Vector(40, 30));

        // A tolerance rather than exact equality: the drag's delta is
        // recovered by converting a screen-pixel difference back through
        // RenderScaling, which for a non-1.0 scale would not land on an
        // exact fraction of a DIP.
        Assert.True(Math.Abs(panel.Width - (width0 + 40)) < 1.0);
        Assert.True(Math.Abs(panel.Height - (height0 + 30)) < 1.0);
        Assert.Equal(pos0, panel.Position);
    }

    // The opposite corner has to pull the window's own Position along with
    // it — growing away from a fixed bottom-right corner means the top-left
    // one has to move, the same way any OS window's own resize border does.
    [AvaloniaFact]
    public void DraggingTheNorthWestCornerGrowsThePanelAndMovesItsPosition()
    {
        var orb = NewOrb();
        var fake = NewFake();
        ChatPanel.OpenFor(orb, fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        ResetGeometry(panel);
        var (width0, height0, pos0) = (panel.Width, panel.Height, panel.Position);

        Drag(panel, "ResizeNW", new Vector(-20, -15));

        Assert.True(panel.Width > width0 + 19);
        Assert.True(panel.Height > height0 + 14);
        Assert.True(panel.Position.X < pos0.X);
        Assert.True(panel.Position.Y < pos0.Y);
    }

    [AvaloniaFact]
    public void ResizeClampsToTheConfiguredMaximumWidth()
    {
        var orb = NewOrb();
        var fake = NewFake();
        ChatPanel.OpenFor(orb, fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        ResetGeometry(panel);
        Drag(panel, "ResizeE", new Vector(2000, 0));

        Assert.Equal(panel.MaxWidth, panel.Width);
    }

    [AvaloniaFact]
    public void ResizeClampsToTheConfiguredMinimumWidth()
    {
        var orb = NewOrb();
        var fake = NewFake();
        ChatPanel.OpenFor(orb, fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        ResetGeometry(panel);
        // Dragging the west edge to the right shrinks the panel.
        Drag(panel, "ResizeW", new Vector(2000, 0));

        Assert.Equal(panel.MinWidth, panel.Width);
    }

    [AvaloniaFact]
    public void ResizeClampsToTheConfiguredMaximumHeight()
    {
        var orb = NewOrb();
        var fake = NewFake();
        ChatPanel.OpenFor(orb, fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        ResetGeometry(panel);
        Drag(panel, "ResizeS", new Vector(0, 2000));

        Assert.Equal(panel.MaxHeight, panel.Height);
    }

    // TurnView.MaxBubbleWidth used to be a fixed 244px; it's now a fraction
    // of Scroll's actual width, kept live by ChatPanel's Scroll.SizeChanged
    // hook so an already-rendered message doesn't stay pinned to whatever
    // width the panel happened to open at.
    [AvaloniaFact]
    public void WideningThePanelWidensAnExistingBubble()
    {
        var orb = NewOrb();
        var fake = NewFake(new[] { new ChatTurn { Role = ChatRole.User, Text = "hi" } });
        ChatPanel.OpenFor(orb, fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        ResetGeometry(panel);
        var bubble = BubbleBorderOf(RenderedRows(panel)[0]);
        var maxWidth0 = bubble.MaxWidth;

        Drag(panel, "ResizeE", new Vector(200, 0));

        Assert.True(bubble.MaxWidth > maxWidth0 + 100);
    }

    // The other half of the same wiring: a turn that arrives after the
    // resize has to pick up the current width too, which is what
    // ChatPanel's _turns.CollectionChanged hook is for — TurnView otherwise
    // defaults to a width sized for the panel's original, narrower opening.
    [AvaloniaFact]
    public void ATurnAddedAfterAResizeAlsoGetsTheWiderBubbleWidth()
    {
        var orb = NewOrb();
        var fake = NewFake();
        ChatPanel.OpenFor(orb, fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        ResetGeometry(panel);
        Drag(panel, "ResizeE", new Vector(200, 0));

        fake.RaiseTurnAdded(new ChatTurn { Role = ChatRole.User, Text = "hi" });
        FlushRender();

        var bubble = BubbleBorderOf(RenderedRows(panel)[0]);

        // 244 was the old fixed cap for a non-system bubble; a turn that
        // still landed there would mean the new turn missed the resize.
        Assert.True(bubble.MaxWidth > 244);
    }

    // System lines read as a note about the conversation rather than one
    // side of it, and get almost the full width (0.95x) rather than the
    // 0.8x a user/assistant bubble gets — at the same available width, that
    // has to make the system line visibly wider.
    [AvaloniaFact]
    public void SystemLineBubbleIsWiderThanARegularBubbleAtTheSameAvailableWidth()
    {
        var orb = NewOrb();
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "hi" },
            new ChatTurn { Role = ChatRole.System, Text = "connected" },
        });
        ChatPanel.OpenFor(orb, fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        var rows = RenderedRows(panel);
        var userBubble = BubbleBorderOf(rows[0]);
        var systemBubble = BubbleBorderOf(rows[1]);

        Assert.True(systemBubble.MaxWidth > userBubble.MaxWidth);
    }

    // StandardCursorType has no diagonal resize member Avalonia.Native's
    // macOS backend actually draws (see BuildDiagonalCursor's own comment),
    // so the two diagonals are hand-drawn bitmaps built once and shared by
    // both of their corners — this is the observable half of that wiring a
    // black-box test can reach without touching the private static fields
    // themselves.
    [AvaloniaFact]
    public void DiagonalCornersShareOneCursorPerDiagonalAndTheTwoDiagonalsDiffer()
    {
        var orb = NewOrb();
        var fake = NewFake();
        ChatPanel.OpenFor(orb, fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        var nw = panel.FindControl<Avalonia.Controls.Control>("ResizeNW")!;
        var ne = panel.FindControl<Avalonia.Controls.Control>("ResizeNE")!;
        var sw = panel.FindControl<Avalonia.Controls.Control>("ResizeSW")!;
        var se = panel.FindControl<Avalonia.Controls.Control>("ResizeSE")!;

        Assert.NotNull(nw.Cursor);
        Assert.NotNull(ne.Cursor);
        Assert.Same(nw.Cursor, se.Cursor);
        Assert.Same(ne.Cursor, sw.Cursor);
        Assert.NotSame(nw.Cursor, ne.Cursor);
    }

    // Setting Input.Text directly (rather than TextInputEventArgs character
    // by character) is enough here: ChatPanel wires UpdateSlashSuggestions to
    // TextBox.TextChanged, which a direct property set fires the same as
    // typing does — the same mechanism Send()'s own `Input.Text = ""` and
    // Bind()'s draft restore already rely on in production code.
    [AvaloniaFact]
    public void TypingASlashShowsMatchingSuggestionsAndFiltersAsYouType()
    {
        var orb = NewOrb();
        var fake = NewFake(slashCommands: new[]
        {
            new SlashCommand("/clear", "Clear the conversation"),
            new SlashCommand("/clean-cache", "Shares a prefix with /clear"),
            new SlashCommand("/color", "Set the prompt color"),
        });

        ChatPanel.OpenFor(orb, fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;
        var slashBox = panel.FindControl<Border>("SlashBox")!;

        input.Text = "/cl";
        input.CaretIndex = input.Text.Length;
        Flush();

        Assert.True(slashBox.IsVisible);
        var suggestions = panel.FindControl<ItemsControl>("SlashList")!.ItemsSource!.Cast<object>().ToList();
        Assert.Equal(2, suggestions.Count);

        // Narrowing further to something only /color matches switches the
        // popup's contents rather than just appending to them.
        input.Text = "/co";
        input.CaretIndex = input.Text.Length;
        Flush();

        suggestions = panel.FindControl<ItemsControl>("SlashList")!.ItemsSource!.Cast<object>().ToList();
        Assert.Single(suggestions);
    }

    [AvaloniaFact]
    public void ArrowDownThenEnterAcceptsTheSecondSuggestionRatherThanSending()
    {
        var orb = NewOrb();
        var fake = NewFake(slashCommands: new[]
        {
            new SlashCommand("/clear", "Clear the conversation"),
            new SlashCommand("/clean-cache", "Shares a prefix with /clear"),
        });

        ChatPanel.OpenFor(orb, fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;

        input.Text = "/cl";
        input.CaretIndex = input.Text.Length;
        Flush();

        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
        Flush();
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Flush();

        // Accepted, not sent: the panel fills the box for you to add
        // arguments to, the same as any other editor's autocomplete.
        Assert.Equal("/clean-cache ", input.Text);
        Assert.Empty(fake.SentTexts);
    }

    [AvaloniaFact]
    public void FinishingACommandByHandClosesSuggestionsInsteadOfOfferingItself()
    {
        var orb = NewOrb();
        var fake = NewFake(slashCommands: new[]
        {
            new SlashCommand("/clear", "Clear the conversation"),
            new SlashCommand("/clean-cache", "Shares a prefix with /clear"),
        });

        ChatPanel.OpenFor(orb, fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;
        var slashBox = panel.FindControl<Border>("SlashBox")!;

        input.Text = "/clear";
        input.CaretIndex = input.Text.Length;
        Flush();

        Assert.False(slashBox.IsVisible);
    }

    [AvaloniaFact]
    public void EscapeDismissesSuggestionsWithoutClosingThePanelOrSending()
    {
        var orb = NewOrb();
        var fake = NewFake(slashCommands: new[]
        {
            new SlashCommand("/clear", "Clear the conversation"),
            new SlashCommand("/color", "Set the prompt color"),
        });

        ChatPanel.OpenFor(orb, fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;
        var slashBox = panel.FindControl<Border>("SlashBox")!;

        input.Text = "/c";
        input.CaretIndex = input.Text.Length;
        Flush();
        Assert.True(slashBox.IsVisible);

        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Flush();

        Assert.False(slashBox.IsVisible);
        Assert.Empty(fake.SentTexts);
        Assert.True(panel.IsVisible);
    }

    [AvaloniaFact]
    public void SessionWithNoSlashCommandsNeverShowsSuggestions()
    {
        var orb = NewOrb();
        var fake = NewFake();

        ChatPanel.OpenFor(orb, fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;
        var slashBox = panel.FindControl<Border>("SlashBox")!;

        input.Text = "/anything";
        input.CaretIndex = input.Text.Length;
        Flush();

        Assert.False(slashBox.IsVisible);
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
