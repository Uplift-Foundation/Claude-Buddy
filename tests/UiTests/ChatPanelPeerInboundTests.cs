using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-267's box in the chat panel: offered before anything is sent, armed
// two-step, and "Send it again" once a held message can go straight in. The
// session behind it is FakeChatSession, so what is asserted is the panel's own
// half — which state it is in, what it draws, and what it calls — and never a
// settings.json. LocalCliChatSessionPeerInboundTests covers the session's half.
//
// In Settings because the text-scale case writes ChatTextScale.
[Collection("Settings")]
public class ChatPanelPeerInboundTests : IDisposable
{
    private readonly List<string> _toClean = new();
    private readonly double _scaleBefore = OrbweaverSettings.ChatTextScale;

    public void Dispose()
    {
        OrbweaverSettings.ChatTextScale = _scaleBefore;
        ChatPanel.ReapplyTextScale();
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private const string Home = "/Users/someone";

    private static PeerInboundOffer Offer(bool canEdit = true) => new(
        PeerInbound.OfferText("job-hunter"), Home + "/.claude-board/settings.json", canEdit);

    private FakeChatSession Open(PeerInboundOffer? offer = null, bool held = false)
    {
        var fake = new FakeChatSession
        {
            SessionId = "inbound-" + Guid.NewGuid(),
            DisplayName = "job-hunter",
            InboundOffer = offer,
            HasHeldMessage = held,
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        Flush();
        return fake;
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static ChatPanel Panel => ChatPanelTestAccess.Instance!;

    private static T Find<T>(string name) where T : Control => Panel.FindControl<T>(name)!;

    private static bool Shown(string name) => Find<Control>(name).IsVisible;

    private static void Click(string name)
    {
        var control = Find<Control>(name);
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        control.RaiseEvent(new PointerPressedEventArgs(
            control, pointer, Panel, new Point(1, 1), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
        Flush();
    }

    // --- the panel, driven ---------------------------------------------------

    // A session that does not implement the interface at all — every transport
    // but the local CLI one — gets no box, and the two entry points do nothing.
    [AvaloniaFact]
    public async Task ASessionWithoutTheInterfaceHasNoBox()
    {
        var bare = new BareSession();
        _toClean.Add(bare.SessionId);
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), bare);
        Flush();

        Assert.False(Shown("InboundBox"));

        await Panel.ConfirmInboundAsync();
        await Panel.ResendInboundAsync();
        Assert.False(Shown("InboundBox"));
    }

    [AvaloniaFact]
    public void NoOfferMeansNoBox()
    {
        Open();

        Assert.False(Shown("InboundBox"));
    }

    [AvaloniaFact]
    public void AskingThenCancellingChangesNothing()
    {
        var fake = Open(Offer());

        Assert.True(Shown("InboundBox"));
        Assert.Equal(PeerInbound.OfferText("job-hunter"), Find<TextBlock>("InboundText").Text);
        Assert.True(Shown("InboundPath"));
        Assert.True(Shown("InboundAskButton"));
        Assert.False(Shown("InboundConfirmButton"));
        Assert.False(Shown("InboundCancelButton"));
        Assert.False(Shown("InboundResendButton"));
        Assert.Equal(PeerInbound.ButtonLabel, Find<TextBlock>("InboundAskLabel").Text);

        Click("InboundAskButton");

        Assert.Equal(PeerInbound.TradeOff, Find<TextBlock>("InboundText").Text);
        Assert.False(Shown("InboundAskButton"));
        Assert.True(Shown("InboundConfirmButton"));
        Assert.True(Shown("InboundCancelButton"));
        Assert.Equal(PeerInbound.ConfirmLabel, Find<TextBlock>("InboundConfirmLabel").Text);

        Click("InboundCancelButton");

        Assert.Equal(PeerInbound.OfferText("job-hunter"), Find<TextBlock>("InboundText").Text);
        Assert.True(Shown("InboundAskButton"));
        Assert.Equal(0, fake.EnableCalls);
    }

    // Confirming writes once however many times it is clicked, and lands on the
    // done text with no resend when nothing was held.
    [AvaloniaFact]
    public async Task ConfirmingTurnsItOnOnce()
    {
        var fake = Open(Offer());
        var gate = new TaskCompletionSource<bool>();
        fake.EnableGate = gate;

        Click("InboundAskButton");
        Click("InboundConfirmButton");
        await Panel.ConfirmInboundAsync();

        Assert.Equal(1, fake.EnableCalls);
        Assert.Equal(PeerInbound.TradeOff, Find<TextBlock>("InboundText").Text);
        Assert.False(Shown("InboundButtons"));

        fake.InboundOffer = null;
        gate.SetResult(true);
        Flush();

        Assert.True(Shown("InboundBox"));
        Assert.Equal(PeerInbound.DoneText, Find<TextBlock>("InboundText").Text);
        Assert.False(Shown("InboundPath"));
        Assert.False(Shown("InboundResendButton"));
    }

    [AvaloniaFact]
    public void SendItAgainIsOfferedWhenAMessageIsHeldAndReachesTheSession()
    {
        var fake = Open(Offer(), held: true);

        Click("InboundAskButton");
        Click("InboundConfirmButton");
        Flush();

        Assert.True(Shown("InboundResendButton"));
        Assert.Equal(ChatPanel.ResendLabel, Find<TextBlock>("InboundResendLabel").Text);

        Click("InboundResendButton");

        Assert.Equal(1, fake.ResendCalls);
        Assert.False(Shown("InboundResendButton"));
        Assert.Equal(PeerInbound.DoneText, Find<TextBlock>("InboundText").Text);
    }

    [AvaloniaFact]
    public void AFailedWriteSaysSoAndCanBeTriedAgain()
    {
        var fake = Open(Offer());
        fake.EnableResult = false;

        Click("InboundAskButton");
        Click("InboundConfirmButton");
        Flush();

        Assert.Equal(ChatPanel.InboundFailedText, Find<TextBlock>("InboundText").Text);
        Assert.True(Shown("InboundAskButton"));

        Click("InboundAskButton");
        Assert.True(Shown("InboundConfirmButton"));
    }

    [AvaloniaFact]
    public void AnUneditableOfferHasNoButtons()
    {
        Open(Offer(canEdit: false));

        Assert.True(Shown("InboundBox"));
        Assert.False(Shown("InboundButtons"));
    }

    // The offer arrives after bind (the transcript's mode row lands late) and
    // goes again (settings.json edited by hand), each time with no rebind.
    [AvaloniaFact]
    public void TheBoxFollowsTheSessionLive()
    {
        var fake = Open();
        Assert.False(Shown("InboundBox"));

        fake.InboundOffer = Offer();
        fake.RaiseInboundChanged();
        Assert.True(Shown("InboundBox"));

        fake.InboundOffer = null;
        fake.RaiseInboundChanged();
        Assert.False(Shown("InboundBox"));
    }

    // The panel is reused across orbs: the previous session is let go of, and a
    // write still running for it does not land on the next one's box.
    [AvaloniaFact]
    public void RebindingLetsGoAndStartsTheBoxOver()
    {
        var first = Open(Offer());
        var gate = new TaskCompletionSource<bool>();
        first.EnableGate = gate;
        Click("InboundAskButton");
        Click("InboundConfirmButton");
        Assert.Equal(1, first.InboundSubscribers);

        var second = Open(Offer());

        Assert.Equal(0, first.InboundSubscribers);
        Assert.Equal(1, second.InboundSubscribers);
        Assert.True(Shown("InboundAskButton"));

        gate.SetResult(true);
        Flush();

        Assert.Equal(PeerInbound.OfferText("job-hunter"), Find<TextBlock>("InboundText").Text);
        Assert.True(Shown("InboundAskButton"));
    }

    [AvaloniaFact]
    public void TheBoxScalesWithTheChatText()
    {
        Open(Offer());

        OrbweaverSettings.ChatTextScale = 2.0;
        ChatPanel.ReapplyTextScale();
        Flush();

        Assert.Equal(22, Find<TextBlock>("InboundText").FontSize, 3);
        Assert.Equal(20, Find<TextBlock>("InboundPath").FontSize, 3);
        foreach (var label in new[] { "InboundAskLabel", "InboundConfirmLabel", "InboundCancelLabel", "InboundResendLabel" })
            Assert.Equal(22, Find<TextBlock>(label).FontSize, 3);
    }

    private sealed class BareSession : IRemoteChatSession
    {
        public string SessionId { get; } = "bare-inbound-" + Guid.NewGuid();
        public string DisplayName => "Bare";
        public RemoteChatState State => RemoteChatState.Connected;
        public IReadOnlyList<ChatTurn> History { get; } = new List<ChatTurn>();

        public event Action<ChatTurn>? TurnAdded { add { } remove { } }
        public event Action<ChatTurn>? TurnUpdated { add { } remove { } }
        public event Action<RemoteChatState>? StateChanged { add { } remove { } }

        public Task<ChatSendOutcome> SendAsync(string text) => Task.FromResult(ChatSendOutcome.Sent);

        public void Cancel()
        {
        }
    }
}
