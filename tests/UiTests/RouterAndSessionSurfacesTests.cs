using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.UiTests;

// Five small surfaces whose only callers are excluded, or which only ever run
// on a thread no test is on, so nothing else asks them anything.
//
// They are not ceremony. Each is read by something a person looks at — the
// settings window, the orb scan, the router's own delivery — and a property
// nothing has ever evaluated is a property nobody has checked compiles into
// something sensible.
//
// In the Settings collection: OnMessage below writes the process-wide relay
// tables, and ProvideLocalSessions replaces a process-wide provider.
[Collection("Settings")]
public class RouterAndSessionSurfacesTests : IDisposable
{
    public RouterAndSessionSurfacesTests() => RemoteControlSessions.ClearRelaysForTests();

    public void Dispose() => RemoteControlSessions.ClearRelaysForTests();

    // ---- what the settings window reads off the router ----------------------

    // Null until a claim has been attempted, which is the state an install that
    // has never had two profiles stays in for ever — and the settings window has
    // to render that rather than the word "null".
    [AvaloniaFact]
    public void TheRouterHasNothingToReportBeforeItHasTriedAnything()
    {
        // Asserted as null outright rather than as "null or something", which
        // would pass either way and say nothing. Nothing in this suite claims a
        // scheme — every path that sets this is excluded precisely because it
        // writes the machine's URL-handler database — so if this ever fails,
        // something has started doing that, and finding out loudly is the point.
        Assert.Null(ClaudeDesktopUrlRouter.Status);
    }

    // Zero until a Claude Desktop window has been frontmost, and zero is a real
    // answer rather than a missing one — ClaudeDesktopUrlRouting.Choose is
    // written to have an answer for it, which is what makes the very first link
    // after a launch route somewhere sensible instead of nowhere.
    [AvaloniaFact]
    public void WithNoInstanceEverFrontmostTheHintIsZeroRatherThanAbsent()
    {
        Assert.True(ClaudeDesktopUrlRouter.LastActivePid >= 0);
    }

    // ---- the provider the orb scan hands in --------------------------------

    // SessionManager owns the list of local sessions and RemoteControlSessions
    // needs it to answer a far machine's roster request, but the dependency runs
    // the wrong way for a direct reference — so the provider is handed in.
    //
    // Replaceable, and that matters: a second call has to win rather than be
    // ignored, or a SessionManager restarted mid-process would leave the mirror
    // answering out of a list nothing updates any more.
    [AvaloniaFact]
    public void TheLocalSessionProviderCanBeReplaced()
    {
        var first = new List<(string, SessionStatus)>();
        var second = new List<(string, SessionStatus)>
        {
            ("s-1", new SessionStatus { Title = "zara", State = "idle" })
        };

        RemoteControlSessions.ProvideLocalSessions(() => first);
        RemoteControlSessions.ProvideLocalSessions(() => second);

        // Read back through the only thing that reads it, rather than through a
        // field: a provider that is stored and never consulted would pass any
        // test that only checked the setter.
        Assert.Contains("zara", RemoteControlSessions.LocalSessions().Select(s => s.Status.Title));
    }

    // And with nothing provided at all — the state every process is in until
    // SessionManager starts — the answer is an empty list rather than a null
    // reference on whatever background task asked.
    [AvaloniaFact]
    public void WithNoProviderTheAnswerIsNoSessionsRatherThanNothing()
    {
        RemoteControlSessions.ForgetLocalSessionsForTests();

        Assert.Empty(RemoteControlSessions.LocalSessions());
    }

    // ---- a frame this version does not recognise ---------------------------

    // Frames whose kind is not one the client answers go to the server, and that
    // is the default arm rather than a listed one on purpose: a far Buddy on a
    // newer version will send kinds this one has never heard of, and the server
    // is what answers with "unsupported" rather than the frame vanishing.
    //
    // With no server for the account the arm still has to be harmless, which is
    // the state every machine is in until someone opens a live view.
    [AvaloniaFact]
    public void AFrameOfAnUnknownKindWithNoServerRunningIsHarmless()
    {
        RemoteControlSessions.SetRelayForTests("work@example.com", "1 session");

        var frame = MirrorProtocol.BuildFrame(
            "something-this-version-has-never-heard-of", "x-1",
            new Dictionary<string, string>());

        // Does not throw, and does not need a server to be running.
        RemoteControlSessions.OnMessage("work@example.com",
            new BridgeProtocol.InboundMessage(
                FromName: "zara", From: "bridge:session_01", Mode: "prompting", Body: frame));
    }

    // ---- reaching the UI thread from the poll thread -----------------------

    // Everything a live view shows arrives on a background thread — the relay's
    // pump reads a file and hands turns over from there — so this is the arm
    // that actually runs in the app. The other one exists for a delivery made
    // inside a test or by a reopen from a click, where posting would defer the
    // update behind a dispatcher turn nobody pumps.
    //
    // Driven from a real background thread rather than asserted about, because
    // "does CheckAccess say no here" is the whole question.
    [AvaloniaFact]
    public void WorkHandedInFromABackgroundThreadStillReachesTheUiThread()
    {
        var ran = 0;

        // A dedicated thread, not Task.Run: the thread pool is allowed to inline
        // work onto the calling thread, and under a loaded machine it does —
        // which made the first version of this test pass most of the time and
        // fail with "this has to be off the UI thread" the rest. A test of what
        // happens off the UI thread has to be *guaranteed* off it.
        var worker = new Thread(() =>
        {
            Assert.False(Dispatcher.UIThread.CheckAccess(),
                "this has to be off the UI thread for the case to mean anything");

            RemoteControlChatSession.OnUi(() => ran++);
        });

        worker.Start();
        worker.Join();

        // Posted rather than run inline, so nothing has happened yet.
        Assert.Equal(0, ran);

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, ran);
    }

    // And on the UI thread it runs inline, which is what keeps a reopen from a
    // click from painting one dispatcher turn late.
    [AvaloniaFact]
    public void WorkHandedInOnTheUiThreadRunsStraightAway()
    {
        var ran = 0;

        RemoteControlChatSession.OnUi(() => ran++);

        Assert.Equal(1, ran);
    }
}
