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

// CB-225: a cloud orb's "Archive this session" and "Delete this session…"
// rows, as a user meets them — which orbs offer them, that both arm before
// acting, what the row says while the request is out and afterwards, and that
// neither the two-second scan nor a second click undoes any of it.
//
// Driven through the orb's CloudAction seam rather than the endpoint: the
// exchange is covered against captured answers in
// tests/IntegrationTests/ClaudeCloudLifecyclePayloadTests, the rules case by
// case in tests/UnitTests/ClaudeCloudLifecycleTests, and the session half in
// CloudLifecycleSessionTests. What is left, and only reachable here, is the
// menu doing what those say.
//
// [Collection("Settings")]: every case flips the cloud setting, and
// constructing an OrbWindow reads a colour setting in a field initializer.
[Collection("Settings")]
public class OrbWindowCloudActionsTests
{
    private const string Orb = "cloud:session_01abc";

    private static SessionStatus Cloud() => new()
    {
        Source = SessionSource.ClaudeCloud,
        Kind = SessionKind.Cloud,
        State = "idle",
        Title = "Refactor the parser",
        Cwd = "",
        Url = "https://claude.ai/code/session_01abc",
    };

    private sealed class Scope : IDisposable
    {
        internal Scope(bool enabled = true) => ClaudeBuddySettings.ClaudeCloudEnabled = enabled;

        public void Dispose() => ClaudeBuddySettings.ClaudeCloudEnabled = false;
    }

    private static (OrbWindow Orb, List<string> Calls) OrbWith(
        Func<CloudLifecycleAction, Task<CloudLifecycleResult>>? answer = null,
        string key = Orb,
        SessionStatus? status = null)
    {
        var calls = new List<string>();
        var orb = new OrbWindow(key)
        {
            CloudAction = (action, k, _) =>
            {
                calls.Add(action + " " + k);
                return answer?.Invoke(action)
                       ?? Task.FromResult(new CloudLifecycleResult(CloudLifecycleVerdict.Done));
            },
        };

        orb.UpdateFrom(status ?? Cloud());
        return (orb, calls);
    }

    private static MenuItem Row(OrbWindow orb, string name) => orb.FindControl<MenuItem>(name)!;
    private static MenuItem Archive(OrbWindow orb) => Row(orb, "ArchiveCloudItem");
    private static MenuItem Delete(OrbWindow orb) => Row(orb, "DeleteCloudItem");

    // --- which orbs offer them ---

    [AvaloniaFact]
    public void ACloudOrbOffersBothRowsInTheirPlainWords()
    {
        using var scope = new Scope();
        var (orb, _) = OrbWith();

        Assert.True(Archive(orb).IsVisible);
        Assert.Equal("Archive this session", Archive(orb).Header);
        Assert.Contains("claude.ai still lists it under archived", (string)ToolTip.GetTip(Archive(orb))!);

        Assert.True(Delete(orb).IsVisible);
        Assert.Equal("Delete this session…", Delete(orb).Header);
        Assert.Contains("cannot be restored", (string)ToolTip.GetTip(Delete(orb))!);

        // The rows that were already right to leave a cloud orb out stay out.
        Assert.False(Row(orb, "EndSessionItem").IsVisible);
        Assert.False(Row(orb, "NewChatHereItem").IsVisible);
        Assert.False(Row(orb, "EndConversationItem").IsVisible);
    }

    [AvaloniaFact]
    public void WithCloudSessionsSwitchedOffNeitherRowIsOffered()
    {
        using var scope = new Scope(enabled: false);
        var (orb, _) = OrbWith();

        Assert.False(Archive(orb).IsVisible);
        Assert.False(Delete(orb).IsVisible);
    }

    // A Remote Control orb above all: its session is somebody's live local
    // one, and archiving it from here is exactly what must never be offered.
    [AvaloniaTheory]
    [InlineData(SessionSource.ClaudeCode, "11111111-2222-3333-4444-555555555555")]
    [InlineData(SessionSource.RemoteControl, "rc:board:session_01abc")]
    [InlineData(SessionSource.OpenClaw, "openclaw:agent:main:dashboard:abc")]
    public void NoOtherOrbOffersEither(SessionSource source, string key)
    {
        using var scope = new Scope();
        var (orb, _) = OrbWith(key: key, status: new SessionStatus { Source = source, State = "idle" });

        Assert.False(Archive(orb).IsVisible);
        Assert.False(Delete(orb).IsVisible);
    }

    // --- arm, then act ---

    [AvaloniaTheory]
    [InlineData(CloudLifecycleAction.Archive, "Click again to archive")]
    [InlineData(CloudLifecycleAction.Delete, "Click again to delete — this can't be undone")]
    public async Task TheFirstClickArmsAndSendsNothing(CloudLifecycleAction action, string armed)
    {
        using var scope = new Scope();
        var (orb, calls) = OrbWith();

        await orb.CloudActionClickAsync(action);

        Assert.Equal(armed, (action == CloudLifecycleAction.Archive ? Archive(orb) : Delete(orb)).Header);
        Assert.Equal(action, orb.CloudArmed);
        Assert.Empty(calls);
    }

    [AvaloniaTheory]
    [InlineData(CloudLifecycleAction.Archive, "Archived")]
    [InlineData(CloudLifecycleAction.Delete, "Deleted")]
    public async Task TheSecondClickActsOnceAndTheRowSaysSo(CloudLifecycleAction action, string done)
    {
        using var scope = new Scope();
        var (orb, calls) = OrbWith();
        var row = action == CloudLifecycleAction.Archive ? Archive(orb) : Delete(orb);

        await orb.CloudActionClickAsync(action);
        await orb.CloudActionClickAsync(action);

        Assert.Equal(new[] { action + " " + Orb }, calls);
        Assert.Equal(done, row.Header);

        // Done is not offered again: the orb is about to go — and a click
        // that reaches it anyway neither re-arms it nor sends again.
        Assert.False(row.IsEnabled);
        Assert.Null(orb.CloudArmed);

        await orb.CloudActionClickAsync(action);
        Assert.Equal(done, row.Header);
        Assert.Null(orb.CloudArmed);
        Assert.Single(calls);
    }

    // A refusal is shown, verbatim, and the row is usable again.
    [AvaloniaFact]
    public async Task ARefusalIsShownOnTheRowAndNotSwallowed()
    {
        using var scope = new Scope();
        var (orb, _) = OrbWith(_ => Task.FromResult(
            new CloudLifecycleResult(CloudLifecycleVerdict.Refused, ClaudeCloudLifecycle.RouteNotFoundDetail)));

        await orb.CloudActionClickAsync(CloudLifecycleAction.Delete);
        await orb.CloudActionClickAsync(CloudLifecycleAction.Delete);

        Assert.Equal("Couldn't delete: not found — the endpoint did not recognise the request", Delete(orb).Header);
        Assert.Equal(Delete(orb).Header, ToolTip.GetTip(Delete(orb)));
        Assert.True(Delete(orb).IsEnabled);
    }

    [AvaloniaFact]
    public async Task ADeleteThatCouldNotBeConfirmedSaysSo()
    {
        using var scope = new Scope();
        var (orb, _) = OrbWith(_ => Task.FromResult(
            new CloudLifecycleResult(CloudLifecycleVerdict.DoneUnconfirmed, "the endpoint answered 503")));

        await orb.CloudActionClickAsync(CloudLifecycleAction.Delete);
        await orb.CloudActionClickAsync(CloudLifecycleAction.Delete);

        Assert.Equal("Deleted — not yet confirmed", Delete(orb).Header);
        Assert.False(Delete(orb).IsEnabled);
    }

    // An action that throws — nothing in the action layer should, which is the
    // point — is a refusal on the row, not an exception out of an async void.
    [AvaloniaFact]
    public async Task AnActionThatThrowsIsARefusalOnTheRow()
    {
        using var scope = new Scope();
        var (orb, _) = OrbWith(_ => throw new InvalidOperationException("boom"));

        await orb.CloudActionClickAsync(CloudLifecycleAction.Archive);
        await orb.CloudActionClickAsync(CloudLifecycleAction.Archive);

        Assert.Equal("Couldn't archive: boom", Archive(orb).Header);
        Assert.True(Archive(orb).IsEnabled);
    }

    // **Two fast clicks after arming send one request.** The second lands
    // while the first is out and is ignored; the row says why.
    [AvaloniaFact]
    public async Task AClickWhileTheRequestIsOutIsIgnored()
    {
        using var scope = new Scope();
        var answer = new TaskCompletionSource<CloudLifecycleResult>();
        var (orb, calls) = OrbWith(_ => answer.Task);

        await orb.CloudActionClickAsync(CloudLifecycleAction.Delete);
        var pending = orb.CloudActionClickAsync(CloudLifecycleAction.Delete);

        Assert.Equal("Deleting…", Delete(orb).Header);
        Assert.False(Delete(orb).IsEnabled);

        await orb.CloudActionClickAsync(CloudLifecycleAction.Delete);
        await orb.CloudActionClickAsync(CloudLifecycleAction.Archive);

        answer.SetResult(new CloudLifecycleResult(CloudLifecycleVerdict.Done));
        await pending;

        Assert.Single(calls);
        Assert.Equal("Deleted", Delete(orb).Header);
    }

    // The same, through the real click handlers a user's mouse reaches.
    [AvaloniaFact]
    public async Task ThreeRealClicksSendOneRequest()
    {
        using var scope = new Scope();
        var (orb, calls) = OrbWith();

        orb.ArchiveCloud_Click(null, new RoutedEventArgs());
        orb.ArchiveCloud_Click(null, new RoutedEventArgs());
        orb.ArchiveCloud_Click(null, new RoutedEventArgs());
        await WaitUntil(() => (string?)Archive(orb).Header == "Archived");

        Assert.Single(calls);
    }

    [AvaloniaFact]
    public async Task DeleteHasARealClickHandlerToo()
    {
        using var scope = new Scope();
        var (orb, calls) = OrbWith();

        orb.DeleteCloud_Click(null, new RoutedEventArgs());
        orb.DeleteCloud_Click(null, new RoutedEventArgs());
        await WaitUntil(() => (string?)Delete(orb).Header == "Deleted");

        Assert.Equal(new[] { "Delete " + Orb }, calls);
    }

    // Arming one row disarms the other, so "click again" can only ever act on
    // the row that is saying it.
    [AvaloniaFact]
    public async Task ArmingOneRowDisarmsTheOther()
    {
        using var scope = new Scope();
        var (orb, calls) = OrbWith();

        await orb.CloudActionClickAsync(CloudLifecycleAction.Archive);
        await orb.CloudActionClickAsync(CloudLifecycleAction.Delete);

        Assert.Equal("Archive this session", Archive(orb).Header);
        Assert.Equal("Click again to delete — this can't be undone", Delete(orb).Header);
        Assert.Equal(CloudLifecycleAction.Delete, orb.CloudArmed);
        Assert.Empty(calls);
    }

    [AvaloniaFact]
    public async Task AnArmedRowDisarmsOnItsOwn()
    {
        using var scope = new Scope();
        var (orb, calls) = OrbWith();
        orb.CloudDisarmAfter = TimeSpan.FromMilliseconds(20);

        await orb.CloudActionClickAsync(CloudLifecycleAction.Delete);
        await WaitUntil(() => orb.CloudArmed is null);

        Assert.Equal("Delete this session…", Delete(orb).Header);
        Assert.Empty(calls);

        // And the next click arms again rather than acting.
        await orb.CloudActionClickAsync(CloudLifecycleAction.Delete);
        Assert.Equal(CloudLifecycleAction.Delete, orb.CloudArmed);
        Assert.Empty(calls);
    }

    [AvaloniaFact]
    public void DisarmingWithNothingArmedChangesNothing()
    {
        using var scope = new Scope();
        var (orb, _) = OrbWith();

        orb.DisarmCloud();

        Assert.Equal("Archive this session", Archive(orb).Header);
        Assert.Null(orb.CloudArmed);
    }

    // --- the scan, and the menu closing ---

    [AvaloniaFact]
    public async Task TheScanLeavesAnAnswerAndAnArmedRowAlone()
    {
        using var scope = new Scope();
        var (orb, _) = OrbWith(_ => Task.FromResult(
            new CloudLifecycleResult(CloudLifecycleVerdict.Refused, "the API refused this request for this account")));

        await orb.CloudActionClickAsync(CloudLifecycleAction.Archive);
        await orb.CloudActionClickAsync(CloudLifecycleAction.Archive);
        await orb.CloudActionClickAsync(CloudLifecycleAction.Delete);
        orb.UpdateFrom(Cloud());

        Assert.Equal("Couldn't archive: the API refused this request for this account", Archive(orb).Header);
        Assert.Equal("Click again to delete — this can't be undone", Delete(orb).Header);
    }

    [AvaloniaFact]
    public async Task ClosingTheMenuPutsThePlainWordsBackAndDisarms()
    {
        using var scope = new Scope();
        var (orb, _) = OrbWith();

        await orb.CloudActionClickAsync(CloudLifecycleAction.Delete);
        orb.SessionMenu_Closed(null, new RoutedEventArgs());

        Assert.Equal("Delete this session…", Delete(orb).Header);
        Assert.Null(orb.CloudArmed);

        orb.UpdateFrom(Cloud());
        Assert.Equal("Delete this session…", Delete(orb).Header);
    }

    // Closed while the request is out: the answer, when it lands, has nobody
    // to be shown to, so the rows go back to their plain words.
    [AvaloniaFact]
    public async Task AnAnswerAfterTheMenuClosedIsNotLeftOnTheRow()
    {
        using var scope = new Scope();
        var answer = new TaskCompletionSource<CloudLifecycleResult>();
        var (orb, _) = OrbWith(_ => answer.Task);

        await orb.CloudActionClickAsync(CloudLifecycleAction.Archive);
        var pending = orb.CloudActionClickAsync(CloudLifecycleAction.Archive);
        orb.SessionMenu_Closed(null, new RoutedEventArgs());

        Assert.Equal("Archiving…", Archive(orb).Header);

        answer.SetResult(new CloudLifecycleResult(CloudLifecycleVerdict.Refused, "no"));
        await pending;

        Assert.Equal("Archive this session", Archive(orb).Header);
        Assert.True(Archive(orb).IsEnabled);

        // Released, not held: the next open starts clean.
        await orb.CloudActionClickAsync(CloudLifecycleAction.Archive);
        Assert.Equal(CloudLifecycleAction.Archive, orb.CloudArmed);
    }

    // Hana's repro: the request is out, the menu closes, and the answer is a
    // success. The row must not come back enabled — reopening the menu before
    // the scan takes the orb would otherwise offer the same action again. A
    // refusal, the control, does come back usable.
    [AvaloniaTheory]
    [InlineData(CloudLifecycleAction.Archive, true)]
    [InlineData(CloudLifecycleAction.Delete, true)]
    [InlineData(CloudLifecycleAction.Archive, false)]
    [InlineData(CloudLifecycleAction.Delete, false)]
    public async Task AnAnswerAfterTheMenuClosedKeepsASuccessDisabled(CloudLifecycleAction action, bool succeeded)
    {
        using var scope = new Scope();
        var answer = new TaskCompletionSource<CloudLifecycleResult>();
        var (orb, calls) = OrbWith(_ => answer.Task);
        var row = action == CloudLifecycleAction.Archive ? Archive(orb) : Delete(orb);

        await orb.CloudActionClickAsync(action);
        var pending = orb.CloudActionClickAsync(action);
        orb.SessionMenu_Closed(null, new RoutedEventArgs());

        answer.SetResult(succeeded
            ? new CloudLifecycleResult(CloudLifecycleVerdict.Done)
            : new CloudLifecycleResult(CloudLifecycleVerdict.Refused, "no"));
        await pending;

        // The menu reopening and the scan both restore the rows.
        orb.UpdateFrom(Cloud());
        orb.SessionMenu_Closed(null, new RoutedEventArgs());

        await orb.CloudActionClickAsync(action);
        await orb.CloudActionClickAsync(action);

        if (succeeded)
        {
            Assert.Equal(action == CloudLifecycleAction.Archive ? "Archived" : "Deleted", row.Header);
            Assert.False(row.IsEnabled);
            Assert.Single(calls);
        }
        else
        {
            Assert.True(row.IsEnabled);
            Assert.Equal(2, calls.Count);
        }
    }

    // A success shown in the open menu survives the menu closing and the
    // scan, too — the same rule from the other direction.
    [AvaloniaFact]
    public async Task ASuccessSurvivesTheMenuClosingAndTheScan()
    {
        using var scope = new Scope();
        var (orb, calls) = OrbWith();

        await orb.CloudActionClickAsync(CloudLifecycleAction.Archive);
        await orb.CloudActionClickAsync(CloudLifecycleAction.Archive);
        orb.SessionMenu_Closed(null, new RoutedEventArgs());
        orb.UpdateFrom(Cloud());

        Assert.Equal("Archived", Archive(orb).Header);
        Assert.False(Archive(orb).IsEnabled);

        // Delete is still offered: deleting an archived session is real.
        Assert.Equal("Delete this session…", Delete(orb).Header);
        Assert.True(Delete(orb).IsEnabled);
        Assert.Single(calls);
    }

    // The production seam, reached without a socket: an orb whose session the
    // roster no longer lists is refused by the session layer before anything
    // is asked, and the row says so.
    [AvaloniaFact]
    public async Task TheRealSeamRefusesASessionTheRosterNoLongerLists()
    {
        using var scope = new Scope();
        ClaudeCloudSessions.SetSnapshotForTests(Array.Empty<ClaudeCloudSessions.Session>());
        var orb = new OrbWindow(Orb);
        orb.UpdateFrom(Cloud());

        await orb.CloudActionClickAsync(CloudLifecycleAction.Archive);
        await orb.CloudActionClickAsync(CloudLifecycleAction.Archive);

        Assert.Equal("Couldn't archive: " + ClaudeCloudSessions.NotListedDetail, Archive(orb).Header);
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
