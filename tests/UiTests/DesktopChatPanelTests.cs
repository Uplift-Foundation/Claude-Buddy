using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// The chat panel bound to a *real* LocalCliChatSession for a session Claude
// Desktop hosts (CB-265), rather than a fake told to be read-only. The fake
// would pass whatever the production class decided; the question here is
// whether LocalCliChatSession itself says "read-only" and what the panel then
// draws — no composer, the hint where it was, no link, and no attach button
// offered for a conversation `claude attach` could not find.
[Collection("Settings")]
public class DesktopChatPanelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-desktop-chat-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _toClean = new();

    public DesktopChatPanelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.CloseFor(id);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private string Transcript()
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path,
            "{\"type\":\"user\",\"uuid\":\"u1\",\"timestamp\":\"2026-10-14T12:00:00Z\","
            + "\"message\":{\"role\":\"user\",\"content\":\"summarise the open PRs\"}}\n"
            + "{\"type\":\"assistant\",\"uuid\":\"a1\",\"timestamp\":\"2026-10-14T12:00:01Z\","
            + "\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\","
            + "\"text\":\"Three are ready to merge; one is waiting on review.\"}]}}\n");
        return path;
    }

    private static SessionStatus DesktopStatus(string transcript, string entrypoint = SessionStatus.DesktopEntrypoint) => new()
    {
        Source = SessionSource.ClaudeCode,
        Cli = "claude",
        Entrypoint = entrypoint,
        TranscriptPath = transcript,
        SessionPid = Environment.ProcessId,
        State = "idle",
    };

    private LocalCliChatSession Open(SessionStatus status)
    {
        var id = "desktop-" + Guid.NewGuid();
        _toClean.Add(id);
        var session = new LocalCliChatSession(id, status);
        session.Start();

        for (var i = 0; i < 400 && session.History.Count == 0; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        ChatPanel.OpenFor(NewOrb(), session);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return session;
    }

    private static T Find<T>(string name) where T : Control =>
        ChatPanelTestAccess.Instance!.FindControl<T>(name)!;

    // --- the session itself ---------------------------------------------------

    [AvaloniaFact]
    public void ADesktopSessionIsReadOnlyWithNowhereToLinkTo()
    {
        var session = new LocalCliChatSession("s1", DesktopStatus(Transcript()));

        Assert.True(session.IsReadOnly);
        Assert.Null(session.ReplyUrl);
        Assert.Equal(LocalCliChatSession.DesktopNoBridgeHint, session.ComposerHint);
        Assert.False(session.CanOpenElsewhere);

        // ReadOnlyChanged is part of the contract and never raised; subscribing
        // and unsubscribing must be harmless.
        void Handler() { }
        session.ReadOnlyChanged += Handler;
        session.ReadOnlyChanged -= Handler;
    }

    // Negative controls: the same transcript under any other entrypoint is the
    // ordinary writable session it always was.
    [AvaloniaTheory]
    [InlineData("cli")]
    [InlineData("sdk-cli")]
    [InlineData("")]
    public void TheSameSessionUnderAnyOtherEntrypointIsNotReadOnly(string entrypoint)
    {
        var session = new LocalCliChatSession("s1", DesktopStatus(Transcript(), entrypoint));

        Assert.False(session.IsReadOnly);
        Assert.NotEqual(LocalCliChatSession.DesktopNoBridgeHint, session.ComposerHint);
    }

    // --- the panel ---------------------------------------------------------------

    [AvaloniaFact]
    public void ThePanelHidesTheComposerAndSaysWhereTypingGoes()
    {
        Open(DesktopStatus(Transcript()));

        Assert.False(Find<Grid>("ComposerRow").IsVisible);
        Assert.True(Find<Control>("ReadOnlyBox").IsVisible);
        Assert.Equal(LocalCliChatSession.DesktopNoBridgeHint, Find<TextBlock>("ReadOnlyNote").Text);
        Assert.False(Find<TextBlock>("ReadOnlyLink").IsVisible);
        Assert.False(Find<TextBlock>("SessionLink").IsVisible);
        Assert.False(Find<Control>("AttachButton").IsVisible);
    }

    // The control for the panel half: an ordinary session bound the same way
    // keeps its composer, so the assertions above are about the entrypoint and
    // not about something every local session does.
    [AvaloniaFact]
    public void AnOrdinarySessionKeepsItsComposerAndHasNoReadOnlyBox()
    {
        Open(DesktopStatus(Transcript(), entrypoint: "cli"));

        Assert.False(Find<Control>("ReadOnlyBox").IsVisible);
    }
}
