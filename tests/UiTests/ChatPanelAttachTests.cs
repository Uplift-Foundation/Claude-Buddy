using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// The chat panel's answer for a session it cannot type into.
//
// This is the gesture the ticket missed. Warren's `clickAction` is `chat`, so a
// single click on a grey orb opens this panel rather than going to a terminal —
// and for a background job the panel was a dead end twice over: the composer had
// nothing to send through (a daemon-hosted session has no tmux pane, which is
// what CanSendQuietly requires), and the note it left when you pressed send
// anyway said "reply in the terminal instead", naming a window that does not
// exist. A daemon runs the session precisely so that none has to.
//
// So the panel now says what is true and offers the way out: the same
// destination the click fallback reaches for the same session — the
// `claude agents` roster. What is
// asserted here is the panel's half — the hint, the button's visibility, and
// that clicking it reaches the session. The attach itself opens a terminal and
// is counted rather than performed (see FakeChatSession.AttachCalls); its real
// implementation is excluded from coverage for exactly that reason.
[Collection("Settings")]
public class ChatPanelAttachTests : IDisposable
{
    private readonly List<string> _sessionIdsToClean = new();

    private FakeChatSession NewFake(string hint, bool canOpen)
    {
        var id = "attach-" + Guid.NewGuid();
        _sessionIdsToClean.Add(id);
        return new FakeChatSession
        {
            SessionId = id,
            DisplayName = "Fake Session",
            ComposerHint = hint,
            CanOpenElsewhere = canOpen,
        };
    }

    // Never closed, per every sibling in this suite: closing a headless orb
    // corrupts a process-wide font cache shared with every other window.
    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    public void Dispose()
    {
        foreach (var id in _sessionIdsToClean) ChatPanel.HideFor(id);
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    // The nine required members and nothing else, the way RemoteChat.cs's own
    // comment describes the floor every transport has to clear.
    private sealed class BareSession : IRemoteChatSession
    {
        public string SessionId { get; } = "attach-bare-" + Guid.NewGuid();
        public string DisplayName => "Bare Session";
        public RemoteChatState State => RemoteChatState.Connected;
        public IReadOnlyList<ChatTurn> History { get; } = new List<ChatTurn>();

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;

        public Task SendAsync(string text)
        {
            // Nothing is ever sent through this one; the events exist because the
            // interface has them and the panel subscribes.
            TurnAdded?.Invoke(new ChatTurn { Role = ChatRole.User, Text = text });
            TurnUpdated?.Invoke(new ChatTurn());
            StateChanged?.Invoke(RemoteChatState.Connected);
            return Task.CompletedTask;
        }

        public void Cancel()
        {
        }
    }

    private static (TextBox Input, Grid Attach) Composer(ChatPanel panel) =>
        (panel.FindControl<TextBox>("Input")!, panel.FindControl<Grid>("AttachButton")!);

    [AvaloniaFact]
    public void AParkedSessionsPanelNamesTheStateAndOffersTheAttach()
    {
        var fake = NewFake("Needs input — attach to reply", canOpen: true);

        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var (input, attach) = Composer(ChatPanelTestAccess.Instance!);

        // Says what is true, on the box itself rather than after a failed send.
        Assert.Equal("Needs input — attach to reply", input.Watermark);

        // And offers the one thing that would change it.
        Assert.True(attach.IsVisible);
    }

    // An ordinary session in a tmux pane: nothing to attach, so no button and
    // the ordinary hint. This is the regression that would be easiest to cause —
    // a button on every panel would be a mark that distinguishes nothing, which
    // is the same argument the orb's badges are held to.
    [AvaloniaFact]
    public void AnOrdinarySessionsPanelIsUnchanged()
    {
        var fake = NewFake("Message…", canOpen: false);

        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var (input, attach) = Composer(ChatPanelTestAccess.Instance!);

        Assert.Equal("Message…", input.Watermark);
        Assert.False(attach.IsVisible);
    }

    // The button reaches the session, which is the whole of the panel's job
    // here: what happens next belongs to TerminalFocuser and is one shared
    // implementation with the click on the orb.
    [AvaloniaFact]
    public void ClickingTheButtonAsksTheSessionToOpenItElsewhere()
    {
        var fake = NewFake("Needs input — attach to reply", canOpen: true);

        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var (_, attach) = Composer(panel);

        Assert.Equal(0, fake.OpenElsewhereCalls);

        attach.RaiseEvent(new PointerPressedEventArgs(
            attach, new Pointer(1, PointerType.Mouse, true), attach, default, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton,
                PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        Flush();

        Assert.Equal(1, fake.OpenElsewhereCalls);
    }

    // A transport that implements neither optional interface — which is the shape
    // RemoteChat.cs's own comment says every one of them must degrade to. The
    // panel falls back to the ordinary hint and shows no button, and clicking the
    // button anyway does nothing: the panel is a process-wide singleton and the
    // handler runs against whatever is bound at the time, so null-safety there is
    // the difference between a stale click and an exception in the UI thread.
    [AvaloniaFact]
    public void ASessionWithNoOptionalInterfacesGetsTheOrdinaryComposerAndNoButton()
    {
        var bare = new BareSession();
        _sessionIdsToClean.Add(bare.SessionId);

        ChatPanel.OpenFor(NewOrb(), bare);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var (input, attach) = Composer(panel);

        Assert.Equal("Message…", input.Watermark);
        Assert.False(attach.IsVisible);

        attach.RaiseEvent(new PointerPressedEventArgs(
            attach, new Pointer(2, PointerType.Mouse, true), attach, default, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton,
                PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        Flush();
    }

    // The header chip, which is the other place the presence word has to appear:
    // the orb's tooltip and `claude agents` both say "needs input", and a panel
    // that said only "background job" would be the one surface hiding the
    // interesting half.
    [AvaloniaFact]
    public void TheHeaderChipNamesTheKindAndWhatItIsWaitingFor()
    {
        var orb = NewOrb();
        orb.UpdateFrom(new SessionStatus
        {
            State = "idle",
            Cwd = "/Users/warren/project",
            Kind = SessionKind.Background,
            Shape = LocalSessionShape.Background,
            Presence = OrbPresence.NeedsInput,
        });

        var fake = NewFake("Needs input — attach to reply", canOpen: true);
        ChatPanel.OpenFor(orb, fake);
        Flush();

        var chip = ChatPanelTestAccess.Instance!.FindControl<TextBlock>("KindChipText")!;

        Assert.Contains("background job", chip.Text);
        Assert.Contains("needs input", chip.Text);
    }

    // A kind with nothing to say about presence — every gateway and bridged
    // session, which is what this chip was built for. The word is appended, not
    // substituted, so those are unchanged.
    [AvaloniaFact]
    public void TheHeaderChipIsUnchangedForASessionWithNoPresenceToReport()
    {
        var orb = NewOrb();
        orb.UpdateFrom(new SessionStatus
        {
            State = "idle",
            Cwd = "/Users/warren/project",
            Kind = SessionKind.Channel,
        });

        var fake = NewFake("Message…", canOpen: false);
        ChatPanel.OpenFor(orb, fake);
        Flush();

        var chip = ChatPanelTestAccess.Instance!.FindControl<TextBlock>("KindChipText")!;

        Assert.Contains("channel", chip.Text);
        Assert.DoesNotContain("·", chip.Text);
    }

    // Rebinding the panel to a different session re-reads both halves. The panel
    // is a process-wide singleton, so the state left by the last session it
    // showed is the state the next one inherits unless something says otherwise —
    // and a button left over from a parked job would offer to attach a session
    // that is already in a terminal.
    [AvaloniaFact]
    public void OpeningASecondSessionReplacesBothHalvesOfTheComposer()
    {
        var parked = NewFake("Needs input — attach to reply", canOpen: true);
        ChatPanel.OpenFor(NewOrb(), parked);
        Flush();

        Assert.True(Composer(ChatPanelTestAccess.Instance!).Attach.IsVisible);

        var ordinary = NewFake("Message…", canOpen: false);
        ChatPanel.OpenFor(NewOrb(), ordinary);
        Flush();

        var (input, attach) = Composer(ChatPanelTestAccess.Instance!);
        Assert.Equal("Message…", input.Watermark);
        Assert.False(attach.IsVisible);
    }
}
