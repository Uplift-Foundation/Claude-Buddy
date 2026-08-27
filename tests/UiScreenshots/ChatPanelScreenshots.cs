using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace ClaudeBuddy.Tests;

// One capture per scenario in tests/UiTests/ChatPanelTests.cs. Same
// singleton-cleanup rule as that suite: ChatPanel is one window shared by
// every test in the process, so each test here calls HideFor its own
// session id when done rather than relying on process isolation.
public class ChatPanelScreenshots : IDisposable
{
    private readonly List<string> _sessionIdsToClean = new();

    private FakeChatSession NewFake(IEnumerable<ChatTurn>? history = null)
    {
        var id = "screenshot-" + Guid.NewGuid();
        _sessionIdsToClean.Add(id);
        return new FakeChatSession(history) { SessionId = id, DisplayName = "Fake Session" };
    }

    // Deliberately never closed — same reason as tests/UiTests's ChatPanelTests:
    // closing a headless Window here corrupts a process-wide FontManager
    // cache for every window built afterward in this run.
    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    // An orb whose session the gateway's heartbeat drives. The panel reads the
    // flag off the orb rather than the session (see ChatPanel.Bind), so the
    // status has to go through UpdateFrom to reach the chip.
    private static OrbWindow NewHeartbeatOrb()
    {
        var orb = NewOrb();
        orb.UpdateFrom(new SessionStatus
        {
            State = "idle",
            Cwd = "/Users/test/project",
            Title = "",
            Color = "",
            Cli = "",
            Kind = SessionKind.Channel,
            Heartbeat = true,
        });

        return orb;
    }

    [AvaloniaFact]
    public void AHeartbeatChatWearsABeatingHeartChipInItsHeader()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.Assistant, Text = "No response requested." },
        });

        ChatPanel.OpenFor(NewHeartbeatOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-heartbeat-chip.png");
    }

    // The panel for a session there is nowhere to type into: the box says so and
    // there is a gear beside it that puts the session in a terminal. Captured
    // because it is a new visible surface, and because the thing worth reviewing
    // is a judgement — whether the box's wording and one small button are enough
    // for someone who clicked a grey orb expecting to be able to talk to it.
    [AvaloniaFact]
    public void AParkedSessionsPanelSaysSoAndOffersTheAttach()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "merge the open PRs" },
            new ChatTurn { Role = ChatRole.Assistant, Text = "Done — three merged, one had conflicts." },
        });

        fake.ComposerHint = "Parked — attach to type";
        fake.CanAttach = true;

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-parked-attach.png");
    }

    public void Dispose()
    {
        foreach (var id in _sessionIdsToClean) ChatPanel.HideFor(id);
    }

    [AvaloniaFact]
    public void OpenForRendersOneRowPerHistoryTurnWithMatchingText()
    {
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

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-three-turns.png");
    }

    [AvaloniaFact]
    public void MarkdownTurnRendersAsStyledRunsNotLiteralMarkup()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.Assistant, Text = "**bold** and `code`" },
        });

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-markdown-turn.png");
    }

    [AvaloniaFact]
    public void TurnWithSpeakerShowsTheSpeakersNameOnItsRow()
    {
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

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-speaker-turn.png");
    }

    [AvaloniaFact]
    public void TypingAndPressingEnterSendsTheTypedTextAndClearsTheBox()
    {
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;

        input.Focus();
        ScreenshotHelper.Flush();
        input.RaiseEvent(new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Text = "hello from a test"
        });
        ScreenshotHelper.Flush();

        // Captured before Enter, deliberately: this is the one moment the
        // source test's own assertions distinguish (typed-but-not-sent vs.
        // sent-and-cleared) that a screenshot can actually show — after
        // Enter, the box is empty and the panel looks identical to the
        // three-turns capture above plus one more row.
        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-typed-input-before-enter.png");
    }

    // Bytes that are not a picture, which is the one thing this suite can test
    // that tests/UiTests cannot: Avalonia's headless render interface answers
    // DecodeToWidth with a stub of the requested size for any input at all, so
    // the panel's "not an image" path is unreachable under the null renderer
    // and reachable here, where Skia is real and throws.
    //
    // Worth having because a local CLI's transcript is a file this app does not
    // write, and a half-flushed image block is a normal thing to read out of
    // one. The message keeps its text; only the picture is missing.
    [AvaloniaFact]
    public void ATurnWhoseImageBytesDoNotDecodeStillShowsItsText()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn
            {
                Role = ChatRole.User,
                Text = "a screenshot",
                IsComplete = true,
                ImageBytes = new byte[] { 0x4E, 0x4F, 0x50, 0x45, 0x21, 0x21, 0x21, 0x21 }
            }
        });

        ChatPanel.OpenFor(NewOrb(), fake);

        // The decode runs on a worker and its failure is swallowed on the way
        // back, so the frames are what prove the panel survived it rather than
        // stopped drawing partway through.
        for (var i = 0; i < 40; i++) ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-undecodable-image.png");
    }
}
