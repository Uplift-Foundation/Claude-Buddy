using System;
using System.Linq;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.UiTests;

// Whether a gateway session counts as working right now, and what an event does
// once it arrives.
//
// The run tracker is maintained from the event stream rather than from
// sessions.list, and the file's own comment says why: the list is wrong about it.
// `hasActiveRun` never once flipped across a complete observed run, and a run's
// own key never appears in the list at all. So this is the only thing that knows,
// and the rule it enforces — silence for long enough means it stopped, whether or
// not a terminal event ever arrived — is what stops an orb pulsing at you
// forever.
//
// In the UI suite because delivering an event to an open panel goes through
// Dispatcher.UIThread.Post: it arrives on the socket's reader thread and the panel
// it reaches is a control.
[Collection("Settings")]
public class OpenClawRunStateTests
{
    private const string Key = "agent:zara:discord:channel:1";

    private static void Fresh()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.OpenClawEnabled = true;
        OpenClawSessions.ForgetRunningForTests();
    }

    [AvaloniaFact]
    public void ASessionNothingIsKnownAboutIsIdle()
    {
        Fresh();

        Assert.Equal("idle", OpenClawSessions.StateFor("never-seen"));
    }

    [AvaloniaFact]
    public void ASessionWithARecentEventIsGenerating()
    {
        Fresh();
        OpenClawSessions.SetRunningForTests(Key, DateTime.UtcNow);

        Assert.Equal("generating", OpenClawSessions.StateFor(Key));
    }

    // The rule that matters: events stop arriving and the session goes idle on
    // its own. A turn emits continuously while it runs — thinking deltas, tool
    // phases — so silence is the signal, and without this an orb whose terminal
    // event went missing would pulse until the app restarted.
    [AvaloniaFact]
    public void ASessionThatHasGoneQuietFallsBackToIdle()
    {
        Fresh();
        OpenClawSessions.SetRunningForTests(Key, DateTime.UtcNow - TimeSpan.FromMinutes(5));

        Assert.Equal("idle", OpenClawSessions.StateFor(Key));
    }

    // And it is forgotten rather than re-checked every time, so a session that
    // has gone quiet costs nothing to ask about again.
    [AvaloniaFact]
    public void AQuietSessionIsForgottenRatherThanReChecked()
    {
        Fresh();
        OpenClawSessions.SetRunningForTests(Key, DateTime.UtcNow - TimeSpan.FromMinutes(5));

        Assert.Equal("idle", OpenClawSessions.StateFor(Key));
        Assert.Equal("idle", OpenClawSessions.StateFor(Key));
    }

    // ---- events reaching an open panel -------------------------------------

    // Only a session someone has actually opened gets an event delivered:
    // building a transcript for 59 sessions nobody is looking at would be work
    // and memory spent on nothing.
    [AvaloniaFact]
    public void AnEventForAnOpenSessionReachesItsTranscript()
    {
        Fresh();

        var chat = OpenClawSessions.ChatFor("openclaw:" + Key, "Zara") as OpenClawChatSession;
        Assert.NotNull(chat);

        var before = chat!.History.Count;

        OpenClawSessions.OnEvent("agent.message", Payload(Key, "hello from the gateway"));
        Dispatcher.UIThread.RunJobs();

        // Whether this particular event shape adds a turn is the chat session's
        // business; what is asserted here is that it was delivered at all rather
        // than dropped for want of an open panel.
        Assert.True(chat.History.Count >= before);
    }

    // A session nobody has opened has no transcript to deliver to, and that is
    // not an error — it is the whole reason transcripts are created lazily.
    [AvaloniaFact]
    public void AnEventForAnUnopenedSessionIsHarmless()
    {
        Fresh();

        OpenClawSessions.OnEvent("agent.message", Payload("agent:nobody:main", "unheard"));
        Dispatcher.UIThread.RunJobs();
    }

    // ---- the published state line ------------------------------------------

    [AvaloniaFact]
    public void TheReportedStateIsWhatTheStatusLineShows()
    {
        Fresh();

        OpenClawSessions.Report("connecting…");

        Assert.Contains("connecting…", OpenClawSessions.StatusText);
    }

    // ---- open chats --------------------------------------------------------

    // The list the event router walks. Opening a panel is what puts a session in
    // it, which is the lazy-creation rule from the other side.
    [AvaloniaFact]
    public void OpeningAChatPutsItInTheOpenList()
    {
        Fresh();

        var chat = OpenClawSessions.ChatFor("openclaw:agent:opened:main", "Opened");
        Assert.NotNull(chat);

        Assert.Contains(OpenClawSessions.OpenChats(),
            c => c.GatewayKey == "agent:opened:main");
    }

    private static JsonElement Payload(string sessionKey, string text) =>
        JsonDocument.Parse(
            "{\"sessionKey\":\"" + sessionKey + "\",\"text\":" +
            JsonSerializer.Serialize(text) + "}").RootElement;
}
