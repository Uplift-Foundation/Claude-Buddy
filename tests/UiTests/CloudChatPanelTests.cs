using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// What the chat panel does with a session it can read and not write to.
//
// Two questions, and they are not the same one. The first is what the panel
// does with IRemoteChatReadOnly — there is no composer, and there is a sentence
// where it was. The second is what a *failed read* looks like, which is the
// thing this file exists for: an unreadable conversation and an empty one are
// pixel-identical unless something insists otherwise, and the panel's only way
// of insisting is the connection dot.
//
// The real ClaudeCloudChatSession is used for the second half rather than a
// fake, against a fake ICloudApi. A fake session could be told to report Error
// and the test would pass without the production code ever having decided
// anything — the whole question is whether a refused endpoint *ends up* in
// Error rather than in an empty Connected.
[Collection("Settings")]
public class CloudChatPanelTests : IDisposable
{
    private readonly List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static Avalonia.Controls.Controls RenderedRows(ChatPanel panel) =>
        panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children;

    private static Color ColourOf(IBrush? brush) => ((ISolidColorBrush)brush!).Color;

    private static Control ComposerRow(ChatPanel panel) =>
        panel.FindControl<Grid>("ComposerRow")!;

    private static Control ReadOnlyBox(ChatPanel panel) =>
        panel.FindControl<StackPanel>("ReadOnlyBox")!;

    private static TextBlock ReadOnlyNote(ChatPanel panel) =>
        panel.FindControl<TextBlock>("ReadOnlyNote")!;

    private static TextBlock ReadOnlyLink(ChatPanel panel) =>
        panel.FindControl<TextBlock>("ReadOnlyLink")!;

    // --- a fake API, shaped after the one in tests/UnitTests ---

    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

    private sealed class FakeApi : ICloudApi
    {
        private readonly Func<CloudApiResult> _answer;

        internal FakeApi(Func<CloudApiResult> answer) => _answer = answer;

        public Task<CloudApiResult> GetAsync(CloudRequestContext context, CancellationToken token) =>
            Task.FromResult(_answer());
    }

    private sealed class FakeCredentials : ICloudCredentialSource
    {
        internal CredentialRead Reading { get; set; } =
            new(CredentialOutcome.Found, Token, null, "a credential is present");

        public string? Stamp() => "stamp-1";
        public CredentialRead Read() => Reading;
    }

    private const string UserTemplate =
        """{"type":"user","uuid":"UUID","timestamp":"2026-09-19T10:00:00Z","message":{"role":"user","content":[{"type":"text","text":"TEXT"}]}}""";

    private const string AssistantTemplate =
        """{"type":"assistant","uuid":"UUID","timestamp":"2026-09-19T10:00:05Z","message":{"role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"TEXT"}],"usage":{"input_tokens":10,"output_tokens":20},"stop_reason":"end_turn"}}""";

    private static string Row(string template, string uuid, string text) =>
        template.Replace("UUID", uuid, StringComparison.Ordinal)
            .Replace("TEXT", text, StringComparison.Ordinal);

    private static string Envelope(params string[] rows) =>
        "{\"data\":[" + string.Join(",", rows) + "],\"has_more\":false,\"last_id\":null}";

    private static ClaudeCloudSessions.Session Session(string id) =>
        new(id, "a cloud session", "idle",
            new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc),
            "https://claude.ai/code/" + id, "idle", false, null, null, null, null);

    // The post hook runs actions inline, so a test can await LoadAsync and then
    // assert without chasing the dispatcher — the same seam and the same reason
    // the unit suite uses it.
    private static ClaudeCloudChatSession Chat(string id, ICloudApi api,
        ICloudCredentialSource? creds = null) =>
        new(Session(id), api, creds ?? new FakeCredentials(), action => action());

    // --- the composer ---

    // The whole of CB-164's composer decision, driven through the fake so both
    // sides of it come from one class.
    [AvaloniaFact]
    public void AReadOnlySessionGetsNoComposerAtAll()
    {
        var fake = new FakeChatSession(null)
        {
            SessionId = "cloud-readonly-" + Guid.NewGuid(),
            IsReadOnly = true,
            ComposerHint = "this cloud session can be read here and replied to at claude.ai/code",
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.False(ComposerRow(panel).IsVisible);
    }

    // Hidden, not merely emptied — and the hint is kept, because hiding the box
    // and explaining nothing leaves a panel that looks truncated rather than
    // one that has said something.
    [AvaloniaFact]
    public void TheHintTakesTheComposersPlaceRatherThanVanishingWithIt()
    {
        var fake = new FakeChatSession(null)
        {
            SessionId = "cloud-hint-" + Guid.NewGuid(),
            IsReadOnly = true,
            ComposerHint = "this cloud session can be read here and replied to at claude.ai/code",
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.True(ReadOnlyBox(panel).IsVisible);
        Assert.Equal(fake.ComposerHint, ReadOnlyNote(panel).Text);
    }

    // The link is the half that matters, and it is a link rather than the
    // address in prose: an orb click goes to the session, and a panel that can
    // only *name* where the session lives is asking the user to go and find it.
    [AvaloniaFact]
    public void AReadOnlySessionOffersALinkToWhereItCanBeRepliedTo()
    {
        var fake = new FakeChatSession(null)
        {
            SessionId = "cloud-link-" + Guid.NewGuid(),
            IsReadOnly = true,
            ComposerHint = "This conversation is read-only here.",
            ReplyUrl = "https://claude.ai/code/session_01abc",
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var link = ReadOnlyLink(ChatPanelTestAccess.Instance!);

        Assert.True(link.IsVisible);
        Assert.False(string.IsNullOrWhiteSpace(link.Text));

        // The label is a label and not the address. A per-session URL is long
        // enough to swamp the sentence beside it, and what the click does is the
        // part worth reading.
        Assert.DoesNotContain("https://", link.Text!);
    }

    // The link looks like one under the pointer.
    //
    // Small, and it is the only thing that says the sentence is clickable — the
    // text is a plain TextBlock in the panel's own foreground, so without the
    // underline a user has no way of knowing there is anything to press. Driven
    // with raised routed events rather than a real pointer, the way
    // SettingsWindow's own link rows are: a synthesized mouse on a machine
    // someone is using interleaves with their real input.
    [AvaloniaFact]
    public void TheReadOnlyLinkUnderlinesUnderThePointerAndClearsWhenItLeaves()
    {
        var fake = new FakeChatSession(null)
        {
            SessionId = "cloud-hover-" + Guid.NewGuid(),
            IsReadOnly = true,
            ComposerHint = "This conversation is read-only here.",
            ReplyUrl = "https://claude.ai/code/session_01abc",
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var link = ReadOnlyLink(ChatPanelTestAccess.Instance!);

        // Nothing drawn until the pointer is over it, which is the half a test
        // that only hovered would not catch.
        Assert.Null(link.TextDecorations);

        Hover(link, InputElement.PointerEnteredEvent);
        Assert.Equal(TextDecorations.Underline, link.TextDecorations);

        Hover(link, InputElement.PointerExitedEvent);
        Assert.Null(link.TextDecorations);
    }

    private static void Hover(Control target, RoutedEvent<PointerEventArgs> which) =>
        target.RaiseEvent(new PointerEventArgs(
            which, target,
            new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true),
            target, default, 0, new PointerPointProperties(), KeyModifiers.None));

    // No address, no link — rather than a link that opens nothing. Both halves
    // asserted because a link drawn unconditionally would pass the case above.
    [AvaloniaFact]
    public void AReadOnlySessionWithNowhereToGoDrawsNoLink()
    {
        var fake = new FakeChatSession(null)
        {
            SessionId = "cloud-nolink-" + Guid.NewGuid(),
            IsReadOnly = true,
            ComposerHint = "This conversation is read-only here.",
            ReplyUrl = null,
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.True(ReadOnlyBox(panel).IsVisible);
        Assert.False(ReadOnlyLink(panel).IsVisible);
    }

    // A real cloud session carries the address the roster gave it, rather than
    // one rebuilt at the panel — which is the point of Session.Url existing.
    [AvaloniaFact]
    public void ACloudSessionsLinkIsTheRostersOwnAddress()
    {
        var id = "session_link_" + Guid.NewGuid().ToString("N");
        var api = new FakeApi(() => new CloudApiResult(
            CloudOutcomes.OutcomeFor(200, ""), Envelope()));

        var chat = Chat(id, api);

        Assert.Equal("https://claude.ai/code/" + id, ((IRemoteChatReadOnly)chat).ReplyUrl);
    }

    // The negative control for the pair above: an ordinary session is untouched
    // by any of this. Without it, a bug that hid every composer would pass both
    // of the assertions above.
    [AvaloniaFact]
    public void AnOrdinarySessionKeepsItsComposer()
    {
        var fake = new FakeChatSession(null)
        {
            SessionId = "cloud-ordinary-" + Guid.NewGuid(),
            IsReadOnly = false,
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.True(ComposerRow(panel).IsVisible);
        Assert.False(ReadOnlyBox(panel).IsVisible);
    }

    // ...and the panel is reused across orbs, so a read-only session must not
    // leave the next conversation without a box. That is the failure this shape
    // of panel has had before — see the class comment on ChatPanel about the
    // transient being rebound rather than recreated.
    [AvaloniaFact]
    public void TheComposerComesBackForTheNextSession()
    {
        var readOnly = new FakeChatSession(null)
        {
            SessionId = "cloud-first-" + Guid.NewGuid(),
            IsReadOnly = true,
        };
        var ordinary = new FakeChatSession(null)
        {
            SessionId = "cloud-second-" + Guid.NewGuid(),
        };
        _toClean.Add(readOnly.SessionId);
        _toClean.Add(ordinary.SessionId);

        var orb = NewOrb();
        ChatPanel.OpenFor(orb, readOnly);
        FlushRender();
        Assert.False(ComposerRow(ChatPanelTestAccess.Instance!).IsVisible);

        ChatPanel.OpenFor(orb, ordinary);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.True(ComposerRow(panel).IsVisible);
        Assert.False(ReadOnlyBox(panel).IsVisible);
    }

    // --- a read that worked ---

    [AvaloniaFact]
    public async Task ACloudTranscriptRendersItsTurns()
    {
        var api = new FakeApi(() => new CloudApiResult(
            CloudOutcomes.OutcomeFor(200, ""),
            Envelope(
                Row(UserTemplate, "u1", "run the tests"),
                Row(AssistantTemplate, "a1", "running them now"))));

        var chat = Chat("session_ok_" + Guid.NewGuid().ToString("N"), api);
        _toClean.Add(chat.SessionId);

        Assert.True(await chat.LoadAsync(CancellationToken.None));

        ChatPanel.OpenFor(NewOrb(), chat);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.Equal(2, RenderedRows(panel).Count);
        Assert.False(ComposerRow(panel).IsVisible);
    }

    // --- a read that did not ---

    // **The point of this file.** A refused endpoint must not leave the panel
    // showing a conversation with nothing in it, because that is exactly what a
    // genuinely empty session looks like and the user has no way to tell them
    // apart. LoadAsync answers false and leaves itself in Error; the panel's
    // connection dot is what carries that onto the screen.
    [AvaloniaFact]
    public async Task AnUnreadableCloudSessionDoesNotRenderAsAnEmptyConversation()
    {
        var api = new FakeApi(() => new CloudApiResult(
            CloudOutcomes.OutcomeFor(500, "{}"), null));

        var chat = Chat("session_bad_" + Guid.NewGuid().ToString("N"), api);
        _toClean.Add(chat.SessionId);

        Assert.False(await chat.LoadAsync(CancellationToken.None));
        Assert.Equal(RemoteChatState.Error, chat.State);
        Assert.Empty(chat.History);

        ChatPanel.OpenFor(NewOrb(), chat);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;

        // Nothing in the transcript, which is expected — and the dot is the
        // thing that says why. Compared against the colour a *successfully*
        // empty conversation would wear rather than against a literal hex, so
        // this keeps meaning the same thing if the palette is ever retuned.
        var errored = ColourOf(panel.StateDot.Fill);

        var emptyButFine = new FakeChatSession(null)
        {
            SessionId = "cloud-empty-" + Guid.NewGuid(),
            State = RemoteChatState.Connected,
        };
        _toClean.Add(emptyButFine.SessionId);

        ChatPanel.OpenFor(NewOrb(), emptyButFine);
        FlushRender();

        var fine = ColourOf(ChatPanelTestAccess.Instance!.StateDot.Fill);

        Assert.NotEqual(fine, errored);
    }

    // --- where the header says this session lives ---

    // A cloud session names its machine, and the name is not this machine.
    //
    // ChatHeaderMeta.MachineFor reads silence as "on this one", which was a
    // sound rule while only the mirror implemented IRemoteChatMachine. A cloud
    // session is the first thing that is neither local nor on a paired machine,
    // and left silent the panel would have printed the user's own laptop's name
    // in the one line that exists to answer "where is this".
    [AvaloniaFact]
    public void ACloudSessionDoesNotClaimToBeOnThisMachine()
    {
        var api = new FakeApi(() => new CloudApiResult(
            CloudOutcomes.OutcomeFor(200, ""),
            Envelope(Row(UserTemplate, "u1", "hello"))));

        var chat = Chat("session_where_" + Guid.NewGuid().ToString("N"), api);

        var named = (IRemoteChatMachine)chat;

        Assert.False(string.IsNullOrWhiteSpace(named.MachineName));

        // This machine's name is a literal rather than MachineNames.Mine(),
        // which shells out to scutil — a real subprocess, which this suite must
        // never fire. MachineFor takes the name as an argument for exactly that
        // reason, and its own comment says so: the interesting case is a far
        // session reporting the *same* name as this machine, which cannot be
        // arranged on a real one at all.
        var (name, isLocal) = ChatHeaderMeta.MachineFor(named.MachineName, "some-laptop");

        Assert.False(isLocal);
        Assert.Equal(named.MachineName, name);
    }

    // The negative control, and the one that would have caught this: a session
    // saying nothing is still read as local, which is correct for every other
    // transport and is precisely why a cloud session has to speak up.
    [AvaloniaFact]
    public void ASilentSessionIsStillReadAsLocal()
    {
        var (name, isLocal) = ChatHeaderMeta.MachineFor(null, "some-laptop");

        Assert.True(isLocal);
        Assert.Equal("some-laptop", name);
    }

    // A missing credential is the other way in to the same state, and it is the
    // common one: the user declined the Keychain prompt. It must not read as an
    // empty conversation either.
    [AvaloniaFact]
    public async Task ADeclinedCredentialLeavesTheSessionInError()
    {
        var api = new FakeApi(() => throw new InvalidOperationException(
            "the endpoint must not be reached without a credential"));

        var creds = new FakeCredentials
        {
            Reading = new CredentialRead(CredentialOutcome.Denied, null, null, "denied"),
        };

        var chat = Chat("session_denied_" + Guid.NewGuid().ToString("N"), api, creds);
        _toClean.Add(chat.SessionId);

        Assert.False(await chat.LoadAsync(CancellationToken.None));
        Assert.Equal(RemoteChatState.Error, chat.State);
        Assert.Empty(chat.History);
    }
}
