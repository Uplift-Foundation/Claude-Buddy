using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-170: an OpenClaw orb's "Interrupt the current run" and "End the
// conversation" rows, as a user meets them — which orbs offer them, what a
// click says while it waits and afterwards, and that the two-second scan does
// not wipe an answer before anyone has read it.
//
// Driven through the orb's own seams (OpenClawCapabilities, InterruptAction,
// EndAction) rather than a gateway: the requests are covered over an
// in-memory socket in tests/UnitTests/OpenClawOrbActionRequestTests.cs, and
// the eligibility rules clause by clause in OpenClawOrbActionsTests.cs. What is
// left, and only reachable here, is the menu doing what those rules and
// answers say.
//
// [Collection("Settings")] because constructing an OrbWindow reads a colour
// setting in a field initializer — see SettingsCollection.cs.
[Collection("Settings")]
public class OrbWindowOpenClawActionsTests
{
    private const string Orb = "openclaw:agent:main:dashboard:abc";

    private static OpenClawActionContext Context(
        string[]? scopes = null, bool isMain = false, string? sessionId = "sid-1") =>
        new(true,
            new HashSet<string> { "chat.abort", "sessions.patch" },
            scopes ?? new[] { "operator.read", "operator.write" },
            isMain, sessionId, "agent:main:dashboard:abc");

    private static SessionStatus OpenClaw(bool room = false) => new()
    {
        Source = SessionSource.OpenClaw,
        State = "idle",
        IsRoom = room,
    };

    private static (OrbWindow Orb, List<string> Calls) OrbWith(
        OpenClawActionContext? context = null,
        Func<Task<(OpenClawActionOutcome, string?)>>? interrupt = null,
        Func<Task<(OpenClawActionOutcome, string?)>>? end = null,
        SessionStatus? status = null)
    {
        var calls = new List<string>();
        var orb = new OrbWindow(Orb)
        {
            OpenClawCapabilities = _ => context ?? Context(),
            InterruptAction = (id, _) =>
            {
                calls.Add("interrupt " + id);
                return interrupt?.Invoke() ?? Task.FromResult((OpenClawActionOutcome.Done, (string?)null));
            },
            EndAction = (id, _) =>
            {
                calls.Add("end " + id);
                return end?.Invoke() ?? Task.FromResult((OpenClawActionOutcome.Done, (string?)null));
            },
        };

        orb.UpdateFrom(status ?? OpenClaw());
        return (orb, calls);
    }

    private static MenuItem Row(OrbWindow orb, string name) => orb.FindControl<MenuItem>(name)!;
    private static MenuItem Interrupt(OrbWindow orb) => Row(orb, "InterruptRunItem");
    private static MenuItem End(OrbWindow orb) => Row(orb, "EndConversationItem");

    // --- which orbs offer them ---

    [AvaloniaFact]
    public void AnOpenClawOrbWithReplyingOnOffersBothRows()
    {
        var (orb, _) = OrbWith();

        Assert.True(Interrupt(orb).IsVisible);
        Assert.Equal("Interrupt the current run", Interrupt(orb).Header);
        Assert.Equal("Stops the agent generating. The conversation stays.", ToolTip.GetTip(Interrupt(orb)));

        Assert.True(End(orb).IsVisible);
        Assert.Equal("End the conversation", End(orb).Header);
        Assert.Contains("It can be restored from OpenClaw.", (string)ToolTip.GetTip(End(orb))!);

        // And the local session's row stays exactly as it was: absent here.
        Assert.False(Row(orb, "EndSessionItem").IsVisible);
    }

    // A local orb never asks the gateway at all, and keeps its own End row.
    [AvaloniaFact]
    public void ALocalOrbOffersNeitherAndKeepsItsOwnEndRow()
    {
        var asked = false;
        var orb = new OrbWindow(Guid.NewGuid().ToString())
        {
            OpenClawCapabilities = _ =>
            {
                asked = true;
                return Context();
            }
        };

        orb.UpdateFrom(new SessionStatus { Source = SessionSource.ClaudeCode, SessionPid = 4321, State = "idle" });

        Assert.False(asked);
        Assert.False(Interrupt(orb).IsVisible);
        Assert.False(End(orb).IsVisible);
        Assert.True(Row(orb, "EndSessionItem").IsVisible);
        Assert.Equal("End this session", Row(orb, "EndSessionItem").Header);
    }

    // "Allow replying to agents" off: the device holds read and nothing else.
    [AvaloniaFact]
    public void WithoutWriteScopeNeitherRowIsOffered()
    {
        var (orb, _) = OrbWith(Context(scopes: new[] { "operator.read" }));

        Assert.False(Interrupt(orb).IsVisible);
        Assert.False(End(orb).IsVisible);
    }

    [AvaloniaFact]
    public void AnAgentsMainSessionOffersInterruptOnly()
    {
        var (orb, _) = OrbWith(Context(isMain: true));

        Assert.True(Interrupt(orb).IsVisible);
        Assert.False(End(orb).IsVisible);
    }

    [AvaloniaFact]
    public void ARoomOrbOffersNeither()
    {
        var (orb, _) = OrbWith(status: OpenClaw(room: true));

        Assert.False(Interrupt(orb).IsVisible);
        Assert.False(End(orb).IsVisible);
    }

    // The real seams, with no gateway connected: nothing offered, and the
    // default actions answer "not connected" rather than throwing.
    [AvaloniaFact]
    public async Task TheRealSeamsOfferNothingWhileDisconnected()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var orb = new OrbWindow(Orb);

        orb.UpdateFrom(OpenClaw());

        Assert.False(Interrupt(orb).IsVisible);
        Assert.False(End(orb).IsVisible);
        Assert.Equal(OpenClawActionOutcome.NotConnected,
            (await orb.InterruptAction(Orb, CancellationToken.None)).Outcome);
        Assert.Equal(OpenClawActionOutcome.NotConnected,
            (await orb.EndAction(Orb, CancellationToken.None)).Outcome);
    }

    // --- Interrupt ---

    [AvaloniaFact]
    public async Task InterruptSaysWhatTheGatewaySaid()
    {
        var (orb, calls) = OrbWith(interrupt: () =>
            Task.FromResult((OpenClawActionOutcome.NothingRunning, (string?)null)));

        await orb.InterruptRunAsync();

        Assert.Equal(new[] { "interrupt " + Orb }, calls);
        Assert.Equal("Nothing was running", Interrupt(orb).Header);
        Assert.Equal("Nothing was running", ToolTip.GetTip(Interrupt(orb)));
        Assert.True(Interrupt(orb).IsEnabled);
    }

    // While the request is out the row says so, is disabled, and a second
    // click does not send a second request.
    [AvaloniaFact]
    public async Task AnInterruptInFlightSaysSoAndIgnoresAnotherClick()
    {
        var answer = new TaskCompletionSource<(OpenClawActionOutcome, string?)>();
        var (orb, calls) = OrbWith(interrupt: () => answer.Task);

        var first = orb.InterruptRunAsync();

        Assert.Equal("Interrupting…", Interrupt(orb).Header);
        Assert.False(Interrupt(orb).IsEnabled);

        await orb.InterruptRunAsync();
        await orb.EndConversationClickAsync();
        Assert.Single(calls);
        Assert.False(orb.EndConversationArmed);

        answer.SetResult((OpenClawActionOutcome.Done, null));
        await first;

        Assert.Equal("Interrupted", Interrupt(orb).Header);
    }

    // The click handler itself, which is the path a user takes.
    [AvaloniaFact]
    public async Task ClickingInterruptSendsIt()
    {
        var answered = new TaskCompletionSource<(OpenClawActionOutcome, string?)>();
        var (orb, calls) = OrbWith(interrupt: () => answered.Task);

        orb.InterruptRun_Click(null, new RoutedEventArgs());
        answered.SetResult((OpenClawActionOutcome.OtherDevice, null));
        await WaitUntil(() => (string?)Interrupt(orb).Header == "Started from another device");

        Assert.Single(calls);
    }

    // --- End ---

    [AvaloniaFact]
    public async Task TheFirstClickOnEndArmsItAndSendsNothing()
    {
        var (orb, calls) = OrbWith();

        await orb.EndConversationClickAsync();

        Assert.True(orb.EndConversationArmed);
        Assert.Equal("End it? Click again (can be restored on the gateway)", End(orb).Header);
        Assert.Empty(calls);
    }

    [AvaloniaFact]
    public async Task TheSecondClickEndsAndARefusalIsShownVerbatim()
    {
        var (orb, calls) = OrbWith(end: () =>
            Task.FromResult((OpenClawActionOutcome.Refused, (string?)"Cannot archive an agent's main session.")));

        await orb.EndConversationClickAsync();
        await orb.EndConversationClickAsync();

        Assert.Equal(new[] { "end " + Orb }, calls);
        Assert.False(orb.EndConversationArmed);
        Assert.Equal("Couldn't end: Cannot archive an agent's main session.", End(orb).Header);
        Assert.Equal("Couldn't end: Cannot archive an agent's main session.", ToolTip.GetTip(End(orb)));
        Assert.True(End(orb).IsEnabled);
    }

    [AvaloniaFact]
    public async Task AnEndInFlightSaysEndingAndIsDisabled()
    {
        var answer = new TaskCompletionSource<(OpenClawActionOutcome, string?)>();
        var (orb, _) = OrbWith(end: () => answer.Task);

        await orb.EndConversationClickAsync();
        var second = orb.EndConversationClickAsync();

        Assert.Equal("Ending…", End(orb).Header);
        Assert.False(End(orb).IsEnabled);

        answer.SetResult((OpenClawActionOutcome.Done, null));
        await second;

        Assert.Equal("Ended", End(orb).Header);
    }

    // Left armed, it gives up on its own — the real timer, shortened.
    [AvaloniaFact]
    public async Task AnArmedEndDisarmsOnItsOwn()
    {
        var (orb, calls) = OrbWith();
        orb.EndDisarmAfter = TimeSpan.FromMilliseconds(20);

        await orb.EndConversationClickAsync();
        await WaitUntil(() => !orb.EndConversationArmed);

        Assert.Equal("End the conversation", End(orb).Header);
        Assert.Empty(calls);

        // And the next click arms again rather than acting.
        await orb.EndConversationClickAsync();
        Assert.True(orb.EndConversationArmed);
        Assert.Empty(calls);
    }

    // Disarming puts back only End's wording: an Interrupt answer beside it is
    // still worth reading.
    [AvaloniaFact]
    public async Task DisarmingEndLeavesAnInterruptAnswerAlone()
    {
        var (orb, _) = OrbWith(interrupt: () =>
            Task.FromResult((OpenClawActionOutcome.NothingRunning, (string?)null)));

        await orb.InterruptRunAsync();
        await orb.EndConversationClickAsync();
        orb.DisarmEndConversation();

        Assert.Equal("End the conversation", End(orb).Header);
        Assert.Equal("Nothing was running", Interrupt(orb).Header);
    }

    [AvaloniaFact]
    public async Task ClickingEndTwiceSendsIt()
    {
        var (orb, calls) = OrbWith();

        orb.EndConversation_Click(null, new RoutedEventArgs());
        orb.EndConversation_Click(null, new RoutedEventArgs());
        await WaitUntil(() => (string?)End(orb).Header == "Ended");

        Assert.Single(calls);
    }

    // --- the scan, and the menu closing ---

    // UpdateFrom runs every couple of seconds; it must not erase an answer.
    [AvaloniaFact]
    public async Task TheScanLeavesAnAnswerOnTheRow()
    {
        var (orb, _) = OrbWith(end: () =>
            Task.FromResult((OpenClawActionOutcome.Refused, (string?)"missing scope: operator.write")));

        await orb.EndConversationClickAsync();
        await orb.EndConversationClickAsync();
        orb.UpdateFrom(OpenClaw());

        Assert.Equal("Couldn't end: missing scope: operator.write", End(orb).Header);
    }

    // ...nor an armed row.
    [AvaloniaFact]
    public async Task TheScanLeavesAnArmedRowArmed()
    {
        var (orb, _) = OrbWith();

        await orb.EndConversationClickAsync();
        orb.UpdateFrom(OpenClaw());

        Assert.Equal("End it? Click again (can be restored on the gateway)", End(orb).Header);
        Assert.True(orb.EndConversationArmed);
    }

    // The menu closing is what puts the plain words back, and disarms.
    [AvaloniaFact]
    public async Task ClosingTheMenuPutsThePlainWordsBack()
    {
        var (orb, _) = OrbWith(interrupt: () =>
            Task.FromResult((OpenClawActionOutcome.Done, (string?)null)));

        await orb.InterruptRunAsync();
        await orb.EndConversationClickAsync();
        orb.SessionMenu_Closed(null, new RoutedEventArgs());

        Assert.Equal("Interrupt the current run", Interrupt(orb).Header);
        Assert.Equal("End the conversation", End(orb).Header);
        Assert.False(orb.EndConversationArmed);

        // And the scan owns the wording again.
        orb.UpdateFrom(OpenClaw());
        Assert.Equal("End the conversation", End(orb).Header);
    }

    // Closed while the request is out: its answer, when it lands, has nobody
    // to be shown to, so the rows go back to their plain words instead.
    [AvaloniaFact]
    public async Task AnAnswerAfterTheMenuClosedIsNotLeftOnTheRow()
    {
        var answer = new TaskCompletionSource<(OpenClawActionOutcome, string?)>();
        var (orb, _) = OrbWith(interrupt: () => answer.Task);

        var pending = orb.InterruptRunAsync();
        orb.SessionMenu_Closed(null, new RoutedEventArgs());

        Assert.Equal("Interrupting…", Interrupt(orb).Header);

        answer.SetResult((OpenClawActionOutcome.Done, null));
        await pending;

        Assert.Equal("Interrupt the current run", Interrupt(orb).Header);
        Assert.True(Interrupt(orb).IsEnabled);

        // Released, not held: the next open starts clean.
        await orb.EndConversationClickAsync();
        Assert.True(orb.EndConversationArmed);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "timed out waiting");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }
}
