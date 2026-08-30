using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace ClaudeBuddy.Tests;

// Selecting text in the transcript and getting it onto the clipboard.
//
// Two things here are worth more than the rest, because both fail silently and
// both were checked against the real Avalonia before the code was written
// rather than assumed:
//
//  * A styled line — anything with bold, code or a link in it — is built from
//    Inlines and leaves TextBlock.Text null. Avalonia reads selections through
//    `HasComplexContent ? Inlines?.Text : Text`, so it works; had it read Text
//    alone, every plain paragraph would have been selectable and every
//    formatted one silently not. AStyledLineStillYieldsItsSelectedText is the
//    guard against that ever regressing.
//
//  * TextBox marks the copy gesture handled whether or not it had anything to
//    copy, and the composer is the only focusable control in this window — so
//    the panel has to claim the keystroke ahead of it, on the tunnel route.
//    TheCopyGestureCopiesTheSelectedMessageText would pass just as happily with
//    a bubbling handler that never ran, which is why the composer-wins case
//    next to it asserts the other direction too.
[Collection("Settings")]
public class ChatPanelSelectionTests : IDisposable
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
        var id = "selection-" + Guid.NewGuid();
        _toClean.Add(id);
        return new FakeChatSession(history) { SessionId = id, DisplayName = "Selection Session" };
    }

    private FakeChatSession Open(params ChatTurn[] turns)
    {
        var fake = NewFake(turns);
        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();
        return fake;
    }

    // Every run of text a message is drawn from, in the order it appears.
    private static List<SelectableTextBlock> Bubbles(ChatPanel panel) =>
        panel.FindControl<ItemsControl>("Turns")!
            .GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .ToList();

    private static SelectableTextBlock BubbleSaying(ChatPanel panel, string text) =>
        Bubbles(panel).First(b => Rendered(b).Contains(text, StringComparison.Ordinal));

    // A styled line leaves Text null and carries its words in Inlines, so
    // reading one means asking for whichever it actually used.
    private static string Rendered(TextBlock block)
    {
        if (!string.IsNullOrEmpty(block.Text)) return block.Text!;
        if (block.Inlines is null || block.Inlines.Count == 0) return "";

        return string.Concat(
            block.Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
    }

    private static void Select(SelectableTextBlock block, int start, int end)
    {
        block.SelectionStart = start;
        block.SelectionEnd = end;
    }

    private static void PressCopy(ChatPanel panel)
    {
        var gesture = TextBox.CopyGesture!;
        panel.FindControl<TextBox>("Input")!.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = gesture.Key,
            KeyModifiers = gesture.KeyModifiers,
        });
    }

    private static async Task<string?> ClipboardSettlesOn(ChatPanel panel, string expected)
    {
        // The clipboard write is asynchronous on both paths — ours and the
        // TextBox's own — so the assertion polls rather than assuming the
        // keystroke has finished being handled by the time RaiseEvent returns.
        string? text = null;
        for (var i = 0; i < 40; i++)
        {
            Flush();
            text = await panel.Clipboard!.TryGetTextAsync();
            if (text == expected) return text;
            await Task.Delay(10);
        }

        return text;
    }

    // --- the text is selectable at all ---

    // The change this whole feature is: a message body used to be a plain
    // TextBlock, which in Avalonia cannot be selected by any means at all.
    [AvaloniaFact]
    public void EveryPartOfAMessageIsDrawnAsSelectableText()
    {
        var text = string.Join("\n\n", new[]
        {
            "a plain paragraph",
            "# a heading",
            "- a bullet point",
            "> a quoted line",
            "```\nfenced code\n```",
        });

        Open(new ChatTurn { Role = ChatRole.Assistant, Text = text });
        var panel = ChatPanelTestAccess.Instance!;

        var rendered = Bubbles(panel).Select(Rendered).ToList();

        Assert.Contains(rendered, r => r.Contains("a plain paragraph", StringComparison.Ordinal));
        Assert.Contains(rendered, r => r.Contains("a heading", StringComparison.Ordinal));
        Assert.Contains(rendered, r => r.Contains("a bullet point", StringComparison.Ordinal));
        Assert.Contains(rendered, r => r.Contains("a quoted line", StringComparison.Ordinal));
        Assert.Contains(rendered, r => r.Contains("fenced code", StringComparison.Ordinal));
    }

    // The rule ChatPanel.axaml states — the composer is the only focusable
    // thing in the window, so nothing can pull focus out of it mid-reply.
    // SelectableTextBlock ships Focusable=true, so this is the one property
    // that had to be overridden and the one most likely to be quietly dropped.
    [AvaloniaFact]
    public void SelectableTextNeverTakesFocusFromTheComposer()
    {
        Open(new ChatTurn { Role = ChatRole.Assistant, Text = "a reply worth copying" });
        var panel = ChatPanelTestAccess.Instance!;

        Assert.NotEmpty(Bubbles(panel));
        Assert.All(Bubbles(panel), b => Assert.False(b.Focusable));
    }

    // Bold, code and links are drawn as Inlines, which leaves Text null. This
    // is the case that would fail silently if Avalonia read Text alone.
    [AvaloniaFact]
    public void AStyledLineStillYieldsItsSelectedText()
    {
        Open(new ChatTurn { Role = ChatRole.Assistant, Text = "run **make** in `src` now" });
        var panel = ChatPanelTestAccess.Instance!;

        var block = BubbleSaying(panel, "make");

        // The premise: this line genuinely is inline-built, not a plain string.
        Assert.True(string.IsNullOrEmpty(block.Text));
        Assert.NotNull(block.Inlines);
        Assert.True(block.Inlines!.Count > 1);

        Select(block, 0, 8);

        Assert.Equal("run make", block.SelectedText);
        Assert.True(block.CanCopy);
    }

    [AvaloniaFact]
    public void ACodeBlockCanBeSelected()
    {
        Open(new ChatTurn { Role = ChatRole.Assistant, Text = "```\ndotnet build\n```" });
        var panel = ChatPanelTestAccess.Instance!;

        var code = BubbleSaying(panel, "dotnet build");
        Select(code, 0, "dotnet build".Length);

        Assert.Equal("dotnet build", code.SelectedText);
    }

    // --- the copy gesture ---

    [AvaloniaFact]
    public async Task TheCopyGestureCopiesTheSelectedMessageText()
    {
        Open(new ChatTurn { Role = ChatRole.Assistant, Text = "the important path is /etc/hosts" });
        var panel = ChatPanelTestAccess.Instance!;

        await panel.Clipboard!.SetTextAsync("something else entirely");

        var block = BubbleSaying(panel, "/etc/hosts");
        Select(block, "the important path is ".Length, Rendered(block).Length);

        PressCopy(panel);

        Assert.Equal("/etc/hosts", await ClipboardSettlesOn(panel, "/etc/hosts"));
    }

    // The other direction, and the reason the rule is written down: the
    // composer is always the focused control, so its own selection has to keep
    // the gesture even while a bubble is still showing one.
    [AvaloniaFact]
    public async Task TheComposerKeepsTheCopyGestureWhenItHasItsOwnSelection()
    {
        Open(new ChatTurn { Role = ChatRole.Assistant, Text = "a reply nobody asked to copy" });
        var panel = ChatPanelTestAccess.Instance!;

        var block = BubbleSaying(panel, "a reply");
        Select(block, 0, 7);

        var input = panel.FindControl<TextBox>("Input")!;
        input.Text = "my own words";
        input.SelectionStart = 0;
        input.SelectionEnd = "my own".Length;
        input.Focus();
        Flush();

        PressCopy(panel);

        Assert.Equal("my own", await ClipboardSettlesOn(panel, "my own"));
    }

    // Nothing selected anywhere: the panel does not claim the keystroke, and
    // whatever was on the clipboard stays there.
    [AvaloniaFact]
    public async Task TheCopyGestureTakesNothingWhenNothingIsSelected()
    {
        Open(new ChatTurn { Role = ChatRole.Assistant, Text = "a reply nobody selected" });
        var panel = ChatPanelTestAccess.Instance!;

        var input = panel.FindControl<TextBox>("Input")!;
        input.Text = "";
        input.Focus();
        Flush();

        await panel.Clipboard!.SetTextAsync("untouched");

        PressCopy(panel);

        for (var i = 0; i < 10; i++)
        {
            Flush();
            await Task.Delay(10);
        }

        Assert.Equal("untouched", await panel.Clipboard!.TryGetTextAsync());
    }

    // --- one selection at a time ---

    // Avalonia selections are per-control and know nothing about each other,
    // so without the panel clearing the others, two passages would stay lit at
    // once and the copy gesture would have to guess between them.
    [AvaloniaFact]
    public void SelectingInOneMessageClearsTheSelectionInAnother()
    {
        Open(
            new ChatTurn { Role = ChatRole.Assistant, Text = "the first reply" },
            new ChatTurn { Role = ChatRole.Assistant, Text = "the second reply" });

        var panel = ChatPanelTestAccess.Instance!;

        var first = BubbleSaying(panel, "first");
        var second = BubbleSaying(panel, "second");

        Select(first, 0, 3);
        Assert.Equal("the", first.SelectedText);

        // What a real drag does before SelectableTextBlock starts the new
        // selection — the panel's handler is on the tunnel route, so it runs
        // on the way down to the block that was pressed.
        second.RaiseEvent(new PointerPressedEventArgs(
            second,
            new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true),
            second,
            default,
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));

        Flush();

        Assert.True(string.IsNullOrEmpty(first.SelectedText));
    }

    // --- the right-click menu ---

    [AvaloniaFact]
    public async Task TheContextMenuCopiesTheSelectionAndTheWholeMessage()
    {
        const string source = "first line\n\nsecond line";
        Open(new ChatTurn { Role = ChatRole.Assistant, Text = source });

        var panel = ChatPanelTestAccess.Instance!;
        var block = BubbleSaying(panel, "first line");

        var menu = Assert.IsType<MenuFlyout>(block.ContextFlyout);
        var items = menu.ItemsSource!.Cast<MenuItem>().ToList();
        Assert.Equal(new[] { "Copy", "Copy message" }, items.Select(i => i.Header?.ToString()));

        // "Copy" takes the selection...
        Select(block, 0, "first".Length);
        items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal("first", await ClipboardSettlesOn(panel, "first"));

        // ...and "Copy message" takes the message as it was written, which is
        // the only way to get a reply whose paragraphs are separate controls.
        //
        // Opened rather than having Target assigned: FlyoutBase.Target has no
        // public setter, and it is ShowAt that fills it in — which is also what
        // a real right-click does, so this drives the same path.
        menu.ShowAt(block);
        Flush();
        items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(source, await ClipboardSettlesOn(panel, source));
    }

    // "Copy" with nothing selected has nothing to take, so it is offered
    // greyed rather than silently doing nothing.
    [AvaloniaFact]
    public void CopyIsDisabledWhileNothingIsSelected()
    {
        Open(new ChatTurn { Role = ChatRole.Assistant, Text = "a reply" });

        var panel = ChatPanelTestAccess.Instance!;
        var block = BubbleSaying(panel, "a reply");
        var menu = (MenuFlyout)block.ContextFlyout!;
        var items = menu.ItemsSource!.Cast<MenuItem>().ToList();

        menu.ShowAt(block);
        Flush();
        Assert.False(items[0].IsEnabled);
        menu.Hide();
        Flush();

        Select(block, 0, 1);
        menu.ShowAt(block);
        Flush();
        Assert.True(items[0].IsEnabled);
        menu.Hide();
        Flush();
    }

    // A menu is its own window, so opening one deactivates the panel — and the
    // panel hides on deactivate. Without the guard, right-clicking a message
    // would close the thing you right-clicked.
    [AvaloniaFact]
    public void ThePanelDoesNotHideWhileItsContextMenuIsOpen()
    {
        Open(new ChatTurn { Role = ChatRole.Assistant, Text = "a reply" });

        var panel = ChatPanelTestAccess.Instance!;
        var block = BubbleSaying(panel, "a reply");
        var menu = (MenuFlyout)block.ContextFlyout!;

        menu.ShowAt(block);
        Flush();

        Assert.True(ContextMenuIsOpen(panel));

        menu.Hide();
        Flush();

        Assert.False(ContextMenuIsOpen(panel));
    }

    private static bool ContextMenuIsOpen(ChatPanel panel) =>
        (bool)typeof(ChatPanel)
            .GetProperty("ContextMenuIsOpen",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(panel)!;

    // --- streaming ---

    // A streaming reply replaces its whole text per delta and rebuilds its
    // body with it. The blocks the panel searches for a selection have to be
    // the ones now on screen, not a growing pile including every earlier
    // draft — otherwise the copy gesture could return text nobody can see.
    [AvaloniaFact]
    public async Task ARebuiltTurnDropsTheBlocksItUsedToHave()
    {
        var fake = Open(new ChatTurn { Role = ChatRole.Assistant, Text = "partial" });
        var panel = ChatPanelTestAccess.Instance!;

        var before = BubbleSaying(panel, "partial");
        Select(before, 0, "partial".Length);
        Assert.Equal("partial", before.SelectedText);

        fake.History[0].Text = "partial reply, now finished";
        FlushRender();

        // Gone from the screen, which the visual tree would tell you on its
        // own...
        Assert.DoesNotContain(before, Bubbles(panel));
        Assert.Equal("partial reply, now finished", Rendered(BubbleSaying(panel, "finished")));

        // ...but the assertion that matters is that the *panel* has forgotten
        // it too, and the only way to ask that is to press copy. The discarded
        // block is still holding "partial" — it is a live object this test has
        // a reference to — so a panel that kept searching its old blocks would
        // put text on the clipboard that is nowhere on screen.
        await panel.Clipboard!.SetTextAsync("untouched");

        var input = panel.FindControl<TextBox>("Input")!;
        input.Text = "";
        input.Focus();
        Flush();

        PressCopy(panel);

        for (var i = 0; i < 10; i++)
        {
            Flush();
            await Task.Delay(10);
        }

        Assert.Equal("partial", before.SelectedText);
        Assert.Equal("untouched", await panel.Clipboard!.TryGetTextAsync());
    }
}
