using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace ClaudeBuddy.Tests;

// Everything on the panel that is a click or a keystroke rather than a
// rendering decision: the heartbeat chip's own beat, the speak button's two
// safe branches (a real utterance is excluded — see ChatPanel's own comment on
// Speak), Escape and Cmd+W, the slash popup's up-arrow and mouse-click paths,
// removing a pasted attachment, and pasting plain text rather than a picture.
//
// Same conventions as ChatPanelTests next door: orbs are never closed, and
// every test unbinds the panel in Dispose.
[Collection("Settings")]
public class ChatPanelInteractionTests : IDisposable
{
    private readonly List<string> _toClean = new();

    private FakeChatSession NewFake(IEnumerable<ChatTurn>? history = null, string? sessionId = null,
        IReadOnlyList<SlashCommand>? slashCommands = null)
    {
        var id = sessionId ?? "fake-" + Guid.NewGuid();
        _toClean.Add(id);
        return new FakeChatSession(history)
        {
            SessionId = id,
            DisplayName = "Fake Session",
            SlashCommands = slashCommands ?? Array.Empty<SlashCommand>(),
        };
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);

        // Speech state is process-wide (see TextToSpeech's own comment on
        // State); a test that forced it to Speaking must not leave the next
        // test in the suite believing something is still talking.
        TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task PumpUntil(Func<bool> done, string what, int attempts = 300)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) return;
            await Task.Delay(5);
        }

        Assert.Fail($"timed out waiting for {what}");
    }

    // Raises a single PointerPressed directly on the control, sidestepping
    // hit-testing the same way ChatPanelTests' Drag helper does — the point
    // being to exercise the real production handler, not the layout that
    // would otherwise have to be forced first.
    private static void Click(Control control, Control root)
    {
        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var at = control.Bounds.Width > 0
            ? new Point(control.Bounds.Width / 2, control.Bounds.Height / 2)
            : new Point(1, 1);

        control.RaiseEvent(new PointerPressedEventArgs(
            control, pointer, root, at, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
    }

    private static void SetRecordingField(OrbWindow orb, bool value)
    {
        var field = typeof(OrbWindow).GetField("_recording", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(orb, value);
    }

    private static bool ChatOpenField(OrbWindow orb)
    {
        var field = typeof(OrbWindow).GetField("_chatOpen", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (bool)field.GetValue(orb)!;
    }

    // --- the heartbeat chip's own beat ---

    // Same curve the orb's own badge pulses on (OpenClawHeartbeat.Beat), but
    // driven by a timer of the panel's own — real wall-clock time, the same
    // reason OrbFlyoutTests' PumpUntil gives: ForceRenderTimerTick advances
    // the render clock, not a DispatcherTimer.
    [AvaloniaFact]
    public async Task TheHeartbeatChipScalesOnATimerTickWhileItIsShown()
    {
        var orb = NewOrb();
        orb.UpdateFrom(new SessionStatus { Source = SessionSource.ClaudeCode, Heartbeat = true, Title = "beating" });

        var fake = NewFake();
        ChatPanel.OpenFor(orb, fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.True(panel.HeartChip.IsVisible);

        var scale = Assert.IsType<ScaleTransform>(panel.HeartChipText.RenderTransform);

        await PumpUntil(() => scale.ScaleX != 1.0, "the heart to scale on a tick");
    }

    // --- the speak button's two safe branches ---

    // Already speaking: the click cancels rather than starting a second
    // utterance. Cancel() is safe to call for real here because nothing is
    // actually mid-speech — only the process-wide state says so — so there is
    // no real process for it to kill.
    [AvaloniaFact]
    public void ClickingSpeakWhileAlreadySpeakingCancelsInsteadOfStartingAnother()
    {
        var fake = NewFake(new[] { new ChatTurn { Role = ChatRole.Assistant, Text = "a reply" } });
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        TextToSpeech.Enter(TextToSpeech.SpeakState.Speaking);

        Click(panel.SpeakButton, panel);
        Flush();

        Assert.Equal(TextToSpeech.SpeakState.Idle, TextToSpeech.State);
    }

    // No assistant reply at all yet: clicking speak is a no-op rather than an
    // error, the same "nothing to act on" rule the prompt buttons follow.
    [AvaloniaFact]
    public void ClickingSpeakWithNoAssistantReplyYetDoesNothing()
    {
        var fake = NewFake(new[] { new ChatTurn { Role = ChatRole.User, Text = "hi" } });
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);

        Click(panel.SpeakButton, panel);
        Flush();

        Assert.Equal(TextToSpeech.SpeakState.Idle, TextToSpeech.State);
    }

    // A reply that is only whitespace is the same as no reply at all — there
    // is nothing worth reading aloud.
    [AvaloniaFact]
    public void ClickingSpeakWithOnlyAWhitespaceReplyDoesNothing()
    {
        var fake = NewFake(new[] { new ChatTurn { Role = ChatRole.Assistant, Text = "   " } });
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);

        Click(panel.SpeakButton, panel);
        Flush();

        Assert.Equal(TextToSpeech.SpeakState.Idle, TextToSpeech.State);
    }

    // --- OnPanelKeyDown: Escape and Cmd+W ---

    [AvaloniaFact]
    public void EscapeWithNothingElseGoingOnClosesThePanel()
    {
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.True(panel.IsVisible);

        panel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Flush();

        Assert.False(panel.IsVisible);
    }

    [AvaloniaFact]
    public void CmdWClosesThePanelTheSameWayEscapeDoes()
    {
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        panel.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = Key.W, KeyModifiers = KeyModifiers.Meta,
        });
        Flush();

        Assert.False(panel.IsVisible);
    }

    // A key that means neither "close" nor anything else the panel's own
    // KeyDown handler cares about must not do anything at all.
    [AvaloniaFact]
    public void AnUnrelatedKeyOnThePanelDoesNothing()
    {
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        panel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A });
        Flush();

        Assert.True(panel.IsVisible);
    }

    // Escape while recording stops the recording rather than closing the
    // panel — the same two-step precedent already covered for the slash
    // popup. _recording is forced by reflection rather than through a real
    // capture: VoiceRecorder's constructor calls PvRecorder.Create, which has
    // no headless seam, but the *decision* ChatPanel makes off IsRecording
    // does not need a real microphone behind it to be tested.
    [AvaloniaFact]
    public void EscapeWhileRecordingStopsTheRecordingRatherThanClosingThePanel()
    {
        var orb = NewOrb();
        var fake = NewFake();
        ChatPanel.OpenFor(orb, fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        SetRecordingField(orb, true);

        panel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Flush();

        Assert.True(panel.IsVisible);
    }

    // --- slash suggestions: the up arrow and a mouse click ---

    [AvaloniaFact]
    public void ArrowUpWrapsToTheLastSuggestion()
    {
        var fake = NewFake(slashCommands: new[]
        {
            new SlashCommand("/clear", "Clear the conversation"),
            new SlashCommand("/clean-cache", "Shares a prefix with /clear"),
        });

        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;

        input.Text = "/cl";
        input.CaretIndex = input.Text.Length;
        Flush();

        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up });
        Flush();
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Flush();

        // From index 0, Up wraps to the last match rather than doing nothing —
        // "/clean-cache" here, the second and last of the two.
        Assert.Equal("/clean-cache ", input.Text);
        Assert.Empty(fake.SentTexts);
    }

    // While the popup is up, only the keys it actually claims — Up, Down,
    // Escape, Tab, and an unmodified Enter — are intercepted. Anything else
    // has to fall all the way through OnInputKeyDown's slash-handling block
    // untouched, the same as it would with no popup open at all, so that
    // typing the rest of a command keeps working while suggestions are shown.
    [AvaloniaFact]
    public void AnUnrelatedKeyWhileSuggestionsAreShowingFallsThroughUntouched()
    {
        var fake = NewFake(slashCommands: new[]
        {
            new SlashCommand("/clear", "Clear the conversation"),
            new SlashCommand("/color", "Set the prompt color"),
        });

        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;
        var slashBox = panel.FindControl<Border>("SlashBox")!;

        input.Text = "/c";
        input.CaretIndex = input.Text.Length;
        Flush();
        Assert.True(slashBox.IsVisible);

        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A });
        Flush();

        Assert.True(slashBox.IsVisible);
        Assert.Empty(fake.SentTexts);
    }

    [AvaloniaFact]
    public void ClickingASlashSuggestionAcceptsItTheSameWayEnterDoes()
    {
        var fake = NewFake(slashCommands: new[]
        {
            new SlashCommand("/clear", "Clear the conversation"),
            new SlashCommand("/color", "Set the prompt color"),
        });

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;

        input.Text = "/c";
        input.CaretIndex = input.Text.Length;
        FlushRender();

        // ItemsPanelRoot's children are the realized ContentPresenters, one
        // per item (see ChatPanelTests' own RenderedRows comment) — the
        // Border carrying PointerPressed is nested inside each one, the same
        // reason BubbleBorderOf descends rather than using the child itself.
        var list = panel.FindControl<ItemsControl>("SlashList")!;
        var presenter = (Control)list.ItemsPanelRoot!.Children[1];
        var row = presenter.GetVisualDescendants().OfType<Border>().First();

        Click(row, panel);
        Flush();

        Assert.Equal("/color ", input.Text);
    }

    // --- Shift+Enter inserts a newline instead of sending ---

    // OnInputKeyDown deliberately leaves Shift+Enter alone — its own comment
    // says so — so the TextBox's own class handler is what inserts the
    // newline here, not ChatPanel. What this test pins is ChatPanel's half:
    // Send() never runs for it.
    [AvaloniaFact]
    public void ShiftEnterInsertsANewlineRatherThanSending()
    {
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;
        input.Text = "a message";
        input.CaretIndex = input.Text.Length;
        Flush();

        input.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Shift,
        });
        Flush();

        Assert.Empty(fake.SentTexts);
        Assert.StartsWith("a message", input.Text);
    }

    // --- removing a pasted attachment before sending ---

    [AvaloniaFact]
    public async Task ClickingAnAttachmentsRemoveButtonTakesItOutBeforeSending()
    {
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
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
            RoutedEvent = InputElement.KeyDownEvent, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers,
        });

        for (var i = 0; i < 40; i++)
        {
            FlushRender();
            if (attachments.IsVisible) break;
            await Task.Delay(10);
        }

        Assert.True(attachments.IsVisible);

        var row = (Control)attachments.ItemsPanelRoot!.Children[0];
        var removeButton = row.GetVisualDescendants().OfType<Grid>().First(g => g.Width == 16);

        Click(removeButton, panel);
        Flush();

        Assert.False(attachments.IsVisible);

        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Flush();

        // The removed picture must not still ride along with whatever gets
        // typed next.
        Assert.Empty(fake.SentWithImages);
    }

    // --- pasting text rather than a picture, on a session that could take one ---

    // FakeChatSession implements IRemoteChatImages, so its paste gesture is
    // intercepted the same way a real picture's would be — but with only text
    // on the clipboard, it has to fall through to an ordinary paste rather
    // than being silently swallowed.
    [AvaloniaFact]
    public async Task PastingPlainTextOnAnImageCapableSessionPastesTheTextNormally()
    {
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;
        input.Focus();
        Flush();

        await panel.Clipboard!.SetTextAsync("pasted words");

        var gesture = TextBox.PasteGesture!;
        input.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers,
        });

        for (var i = 0; i < 40; i++)
        {
            Flush();
            if (input.Text == "pasted words") break;
            await Task.Delay(10);
        }

        Assert.Equal("pasted words", input.Text);
        Assert.False(panel.FindControl<ItemsControl>("Attachments")!.IsVisible);
    }

    // Nothing at all on the clipboard: neither a picture nor text to fall
    // back to, so the paste is simply a no-op rather than an error.
    [AvaloniaFact]
    public async Task PastingWithNothingOnTheClipboardIsHarmless()
    {
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;
        input.Text = "";
        input.Focus();
        Flush();

        var gesture = TextBox.PasteGesture!;
        input.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers,
        });

        for (var i = 0; i < 20; i++)
        {
            Flush();
            await Task.Delay(10);
        }

        Assert.Equal("", input.Text);
        Assert.False(panel.FindControl<ItemsControl>("Attachments")!.IsVisible);
    }

    // --- clicking a picture that isn't there ---

    // A row with no picture still carries the (invisible) Image element the
    // template always draws — clicking it must not throw just because there
    // is nothing to open full size.
    [AvaloniaFact]
    public void ClickingAPictureOnATextOnlyTurnIsANoOp()
    {
        var fake = NewFake(new[] { new ChatTurn { Role = ChatRole.User, Text = "just text" } });
        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        var row = (Control)panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children[0];
        var image = row.GetVisualDescendants().OfType<Image>().First(im => im.Width == 228);

        Click(image, panel);
        Flush();

        Assert.True(panel.IsVisible);
    }

    // --- Bind() telling the outgoing orb its chat closed ---

    // Only HideNow used to clear this; opening a second orb's panel without
    // going through Hide first left the first orb believing its own panel was
    // still open, and its hover flyout never came back for the life of the
    // process.
    [AvaloniaFact]
    public void OpeningASecondOrbTellsTheFirstOneItsChatClosed()
    {
        var first = NewOrb();
        first.SetChatOpen(true);

        var second = NewOrb();

        ChatPanel.OpenFor(first, NewFake());
        Flush();
        ChatPanel.OpenFor(second, NewFake());
        Flush();

        Assert.False(ChatOpenField(first));
    }
}
