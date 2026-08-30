using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace ClaudeBuddy.Tests;

// Cmd+ / Cmd- / Cmd+0 in the chat panel, driven as real key events and
// asserted on the font sizes a user would actually be looking at.
//
// ChatZoomTests already pins the ladder and the key mapping; nothing here
// re-checks the arithmetic. What this suite covers is the half that only
// exists once there is a window: that the keystroke reaches the panel while
// the composer holds focus, that a row built *before* the keystroke ends up
// the same size as one built after it, and that the chrome does not come
// along for the ride.
[Collection("Settings")]
public class ChatPanelTextScaleTests : IDisposable
{
    private readonly List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);

        // The scale is a process-wide setting and this suite is the only one
        // that moves it. Left at 1.5, every font-size assertion in every other
        // ChatPanel class would be measuring this class's leftovers.
        ClaudeBuddySettings.ChatTextScale = ChatZoom.Default;
        ChatPanel.ReapplyTextScale();
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private FakeChatSession NewFake(params ChatTurn[] history)
    {
        var id = "scale-" + Guid.NewGuid();
        _toClean.Add(id);
        return new FakeChatSession(history) { SessionId = id, DisplayName = "Scale Session" };
    }

    private static ChatTurn Reply(string text) =>
        new() { Role = ChatRole.Assistant, Text = text };

    private static Control RowOf(ChatPanel panel, int index) =>
        (Control)panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children[index];

    private static string RenderedText(TextBlock tb)
    {
        if (!string.IsNullOrEmpty(tb.Text)) return tb.Text;
        if (tb.Inlines is null || tb.Inlines.Count == 0) return "";
        return string.Concat(tb.Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
    }

    private static double SizeOf(ChatPanel panel, int row, string text) =>
        RowOf(panel, row).GetVisualDescendants().OfType<TextBlock>()
            .First(tb => RenderedText(tb) == text).FontSize;

    // The gesture is registered as a tunnel handler on the *window*, so raising
    // KeyDownEvent at the composer still reaches it: Avalonia's tunnel route
    // runs from the visual root down to whatever RaiseEvent was called on.
    // Driving it from the composer rather than from the window is the point —
    // that is where the caret is nearly all the time this panel is open, and
    // the bug this guards against is a handler that only works when it isn't.
    private static bool Press(ChatPanel panel, Key key, KeyModifiers? modifiers = null)
    {
        var input = panel.FindControl<TextBox>("Input")!;
        input.Focus();
        Flush();

        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers ?? ChatZoom.Accelerator
        };

        input.RaiseEvent(args);
        Flush();
        return args.Handled;
    }

    // --- the gesture, end to end -----------------------------------------

    [AvaloniaFact]
    public void CmdPlusGrowsTheConversationAndCmdMinusShrinksItBack()
    {
        ClaudeBuddySettings.ChatTextScale = ChatZoom.Default;

        var fake = NewFake(Reply("a reply worth reading"));
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        ChatPanel.ReapplyTextScale();
        Flush();

        Assert.Equal(11.5, SizeOf(panel, 0, "a reply worth reading"), 3);

        Assert.True(Press(panel, Key.OemPlus));
        Assert.Equal(11.5 * 1.15, SizeOf(panel, 0, "a reply worth reading"), 3);
        Assert.Equal(1.15, ClaudeBuddySettings.ChatTextScale, 3);

        Assert.True(Press(panel, Key.OemMinus));
        Assert.Equal(11.5, SizeOf(panel, 0, "a reply worth reading"), 3);
        Assert.Equal(ChatZoom.Default, ClaudeBuddySettings.ChatTextScale, 3);
    }

    [AvaloniaFact]
    public void CmdZeroPutsAnEnlargedPanelBackToTheShippedSize()
    {
        ClaudeBuddySettings.ChatTextScale = 2.0;

        var fake = NewFake(Reply("back to normal"));
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        ChatPanel.ReapplyTextScale();
        Flush();

        Assert.Equal(11.5 * 2.0, SizeOf(panel, 0, "back to normal"), 3);

        Assert.True(Press(panel, Key.D0));
        Assert.Equal(11.5, SizeOf(panel, 0, "back to normal"), 3);
    }

    [AvaloniaFact]
    public void TheGestureStopsAtTheEndsOfTheLadderAndStaysHandled()
    {
        ClaudeBuddySettings.ChatTextScale = ChatZoom.Default;

        var fake = NewFake(Reply("as far as it goes"));
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        for (var i = 0; i < 20; i++) Assert.True(Press(panel, Key.OemPlus));
        Assert.Equal(ChatZoom.Max, ClaudeBuddySettings.ChatTextScale, 3);
        Assert.Equal(11.5 * ChatZoom.Max, SizeOf(panel, 0, "as far as it goes"), 3);

        // Still handled at the top of the ladder. If it weren't, the keystroke
        // would travel on into the composer, and a user leaning on Cmd+0 would
        // find zeroes in their message.
        Assert.True(Press(panel, Key.OemPlus));

        for (var i = 0; i < 20; i++) Assert.True(Press(panel, Key.OemMinus));
        Assert.Equal(ChatZoom.Min, ClaudeBuddySettings.ChatTextScale, 3);
    }

    [AvaloniaFact]
    public void AKeystrokeThatIsNotTheGestureIsLeftForWhateverWantedIt()
    {
        ClaudeBuddySettings.ChatTextScale = ChatZoom.Default;

        var fake = NewFake(Reply("untouched"));
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        ChatPanel.ReapplyTextScale();
        Flush();

        // A bare "=" is a character someone is typing, not a zoom.
        Assert.False(Press(panel, Key.OemPlus, KeyModifiers.None));
        Assert.Equal(ChatZoom.Default, ClaudeBuddySettings.ChatTextScale, 3);
        Assert.Equal(11.5, SizeOf(panel, 0, "untouched"), 3);
    }

    // --- what moves, and what deliberately does not ----------------------

    [AvaloniaFact]
    public void EveryPartOfAConversationGrowsTogether()
    {
        ClaudeBuddySettings.ChatTextScale = ChatZoom.Default;

        var fake = NewFake(
            Reply("# a heading\n\nsome prose\n\n```\nfenced code\n```"),
            new ChatTurn { Role = ChatRole.System, Text = "a system note" });

        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        ChatPanel.ReapplyTextScale();
        Flush();

        var before = new[]
        {
            SizeOf(panel, 0, "a heading"),
            SizeOf(panel, 0, "some prose"),
            SizeOf(panel, 0, "fenced code"),
            SizeOf(panel, 1, "a system note")
        };

        Assert.True(Press(panel, Key.OemPlus));

        var after = new[]
        {
            SizeOf(panel, 0, "a heading"),
            SizeOf(panel, 0, "some prose"),
            SizeOf(panel, 0, "fenced code"),
            SizeOf(panel, 1, "a system note")
        };

        // Each grew by the same factor, which is the part that matters: a
        // heading stays a size above its prose, a system note stays quieter
        // than a reply. A single "chat font size" would have flattened them.
        for (var i = 0; i < before.Length; i++)
            Assert.Equal(before[i] * 1.15, after[i], 3);
    }

    [AvaloniaFact]
    public void TheComposerGrowsWithTheConversationAndSoDoesItsBox()
    {
        ClaudeBuddySettings.ChatTextScale = ChatZoom.Default;

        var fake = NewFake(Reply("typing room"));
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        ChatPanel.ReapplyTextScale();
        Flush();

        var input = panel.FindControl<TextBox>("Input")!;
        Assert.Equal(11.5, input.FontSize, 3);

        Assert.True(Press(panel, Key.OemPlus));

        Assert.Equal(11.5 * 1.15, input.FontSize, 3);

        // The box grows too, or doubled text would be drawn into a slot still
        // sized for one line of the old size and clip.
        Assert.Equal(26 * 1.15, input.MinHeight, 3);
        Assert.Equal(66 * 1.15, input.MaxHeight, 3);
    }

    [AvaloniaFact]
    public void TheWindowsOwnChromeKeepsItsSize()
    {
        ClaudeBuddySettings.ChatTextScale = ChatZoom.Default;

        var fake = NewFake(Reply("chrome stays put"));
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        ChatPanel.ReapplyTextScale();
        Flush();

        var title = panel.FindControl<TextBlock>("TitleText")!;
        var titleBefore = title.FontSize;

        Assert.True(Press(panel, Key.OemPlus));

        // Messages scales messages, not its toolbar. A header that grew with
        // the text would push the transcript out of a panel that is only 340pt
        // tall to begin with.
        Assert.Equal(titleBefore, title.FontSize, 3);
    }

    [AvaloniaFact]
    public void APermissionPromptGrowsToo()
    {
        ClaudeBuddySettings.ChatTextScale = ChatZoom.Default;

        var session = new PromptingSession();
        _toClean.Add(session.SessionId);

        ChatPanel.OpenFor(NewOrb(), session);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        ChatPanel.ReapplyTextScale();
        Flush();

        session.RaisePrompt(new ChatPrompt("Allow this tool call?", new[]
        {
            new ChatPromptOption("1", "Yes")
        }));
        Flush();

        var title = panel.FindControl<TextBlock>("PromptTitle")!;
        var elsewhere = panel.FindControl<TextBlock>("PromptElsewhere")!;
        var options = panel.FindControl<ItemsControl>("PromptOptions")!;

        Assert.Equal(11, title.FontSize, 3);
        Assert.Equal(10.5, elsewhere.FontSize, 3);

        Assert.True(Press(panel, Key.OemPlus));

        // The dialog is the most important text in the window while it is up —
        // it is the thing being agreed to — so it is the last thing that should
        // stay small when someone has asked for bigger.
        Assert.Equal(11 * 1.15, title.FontSize, 3);
        Assert.Equal(10.5 * 1.15, elsewhere.FontSize, 3);
        Assert.Equal(11 * 1.15, options.FontSize, 3);

        // The option's own label has no FontSize of its own and inherits the
        // ItemsControl's, which is the whole reason the template gives it none.
        var label = options.GetVisualDescendants().OfType<TextBlock>()
            .First(tb => tb.Text == "Yes");
        Assert.Equal(11 * 1.15, label.FontSize, 3);
    }

    // --- rows built on either side of the keystroke ----------------------

    [AvaloniaFact]
    public void ATurnThatArrivesAfterTheZoomIsDrawnAtTheSameSizeAsOneBeforeIt()
    {
        // The reason the scale is a shared box rather than a value copied into
        // each row: copied, a transcript would fan out into as many sizes as
        // the user had pressed the key.
        ClaudeBuddySettings.ChatTextScale = ChatZoom.Default;

        var fake = NewFake(Reply("said before"));
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        ChatPanel.ReapplyTextScale();
        Flush();

        Assert.True(Press(panel, Key.OemPlus));

        fake.RaiseTurnAdded(Reply("said after"));
        Flush();

        Assert.Equal(
            SizeOf(panel, 0, "said before"),
            SizeOf(panel, 1, "said after"), 3);
    }

    [AvaloniaFact]
    public void APanelOpenedAfterwardsOpensAtTheSizeItWasLeftAt()
    {
        // The setting's whole promise. Bind a second session over the first —
        // the panel is a singleton, so this is exactly what opening another
        // orb's chat does.
        ClaudeBuddySettings.ChatTextScale = 1.5;

        var fake = NewFake(Reply("still large"));
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        ChatPanel.ReapplyTextScale();
        Flush();

        var second = NewFake(Reply("also large"));
        ChatPanel.OpenFor(NewOrb(), second);
        Flush();

        Assert.Equal(11.5 * 1.5, SizeOf(panel, 0, "also large"), 3);
    }

    // --- the settings slider reaches an open panel ------------------------

    [AvaloniaFact]
    public void ChangingTheSettingResizesAPanelThatIsAlreadyOpen()
    {
        ClaudeBuddySettings.ChatTextScale = ChatZoom.Default;

        var fake = NewFake(Reply("resized from settings"));
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        ChatPanel.ReapplyTextScale();
        Flush();

        ClaudeBuddySettings.ChatTextScale = 1.75;
        ChatPanel.ReapplyTextScale();
        Flush();

        Assert.Equal(11.5 * 1.75, SizeOf(panel, 0, "resized from settings"), 3);
    }

    [AvaloniaFact]
    public void ReapplyingWithNoPanelOpenIsHarmless()
    {
        // The settings window can be open with no chat panel ever having been
        // built, and the static hook has to survive that rather than throw its
        // way out of a slider drag.
        using var _ = ChatPanelTestAccess.WithNoPanel();

        ClaudeBuddySettings.ChatTextScale = 1.3;
        ChatPanel.ReapplyTextScale();
    }

    // A minimal session that can raise a permission prompt — the same shape
    // ChatPanelHistoryTests uses, kept local rather than shared because it
    // exists to answer one question in each suite.
    private sealed class PromptingSession : IRemoteChatSession, IRemoteChatPrompts
    {
        public string SessionId { get; } = "scale-prompting-" + Guid.NewGuid();
        public string DisplayName { get; } = "Prompting Session";
        public RemoteChatState State => RemoteChatState.Connected;
        public IReadOnlyList<ChatTurn> History { get; } = Array.Empty<ChatTurn>();

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;

        public ChatPrompt? Prompt { get; private set; }
        public event Action? PromptChanged;

        public void RaisePrompt(ChatPrompt? prompt)
        {
            Prompt = prompt;
            PromptChanged?.Invoke();
        }

        public Task AnswerAsync(ChatPromptOption option)
        {
            Prompt = null;
            PromptChanged?.Invoke();
            return Task.CompletedTask;
        }

        public void AnswerElsewhere()
        {
        }

        public Task SendAsync(string text) => Task.CompletedTask;

        public void Cancel()
        {
        }

        // Never raised; present so nothing throws for lack of a subscriber.
        private void Unused()
        {
            TurnAdded?.Invoke(new ChatTurn());
            TurnUpdated?.Invoke(new ChatTurn());
            StateChanged?.Invoke(State);
        }
    }
}
