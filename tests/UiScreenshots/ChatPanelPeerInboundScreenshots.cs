using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace Orbweaver.Tests;

// CB-267's box, in the three states a reviewer has to judge by eye: the offer
// before anything is sent, the armed confirmation with its trade-off, and done
// with "Send it again" for a message that was held. Whether the trade-off reads
// as a real choice rather than a speed bump, and whether the box reads as a
// note about the conversation rather than a dialog the session is stopped on,
// are judgements no assertion can make.
//
// The clicks are on the panel's own box, never an orb — the rule the other
// captures give about OrbWindow's pointer handling reaching TerminalFocuser
// does not apply to a Border in the chat panel.
[Collection("Settings")]
public class ChatPanelPeerInboundScreenshots : IDisposable
{
    private readonly List<string> _panelsToClean = new();

    public void Dispose()
    {
        foreach (var id in _panelsToClean) ChatPanel.HideFor(id);
    }

    private ChatPanel Open(string name, bool held)
    {
        var id = "screenshot-inbound-" + name + "-" + Guid.NewGuid();
        _panelsToClean.Add(id);

        var fake = new FakeChatSession(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "Check the deploy and tell me if anything is red." },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "Two jobs are still running; the third finished green. I'll report back when they land.",
            },
        })
        {
            SessionId = id,
            DisplayName = "job-hunter",
            ComposerHint = "Message it — it reads this at its next turn",
            InboundOffer = new PeerInboundOffer(
                PeerInbound.OfferText("job-hunter"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-board", "settings.json"),
                CanEdit: true),
            HasHeldMessage = held,
        };

        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        ScreenshotHelper.Flush();
        return ChatPanelTestAccess.Instance!;
    }

    private static void Click(ChatPanel panel, string name)
    {
        var control = panel.FindControl<Control>(name)!;
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        control.RaiseEvent(new PointerPressedEventArgs(
            control, pointer, panel, new Point(1, 1), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
        ScreenshotHelper.Flush();
    }

    [AvaloniaFact]
    public void TheOfferBeforeAnythingIsSent()
    {
        var panel = Open("offer", held: false);

        Assert.True(panel.FindControl<Control>("InboundAskButton")!.IsVisible);

        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-peer-inbound-offer.png");
    }

    [AvaloniaFact]
    public void TheArmedConfirmationSaysTheTradeOff()
    {
        var panel = Open("confirm", held: false);
        Click(panel, "InboundAskButton");

        Assert.True(panel.FindControl<Control>("InboundConfirmButton")!.IsVisible);

        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-peer-inbound-confirm.png");
    }

    [AvaloniaFact]
    public void DoneWithAHeldMessageOffersToSendItAgain()
    {
        var panel = Open("done", held: true);
        Click(panel, "InboundAskButton");
        Click(panel, "InboundConfirmButton");

        Assert.True(panel.FindControl<Control>("InboundResendButton")!.IsVisible);

        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-peer-inbound-done-resend.png");
    }
}
