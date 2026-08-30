using System;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.UiTests;

// The handover CB-39 turns on: a relay is driven by a pump of its own until
// there is a dispatcher, and by the dispatcher's timers afterwards.
//
// Here rather than in tests/UnitTests for the same reason
// RemoteControlTransitionTests is — the relay table is a process-wide static,
// and the stop half is called from EnsureTimer, which only ever runs on the UI
// thread. Asserting it from the dispatcher is asserting it where it happens.
//
// ServePump's own rules — overlap, throwing, idempotence — are unit tests; this
// is only about the one pump RemoteControlSessions owns.
[Collection("Settings")]
public class RemoteControlServePumpTests : IDisposable
{
    public RemoteControlServePumpTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        RemoteControlSessions.StopServePump();
        RemoteControlSessions.ClearRelaysForTests();
    }

    [AvaloniaFact]
    public void Starts_and_stops_the_stand_in_pump()
    {
        Assert.False(RemoteControlSessions.ServePumpRunning);

        RemoteControlSessions.EnsureServePump();
        Assert.True(RemoteControlSessions.ServePumpRunning);

        // What EnsureTimer does the moment the dispatcher exists. Called on the
        // UI thread here because that is the only thread it is ever called from.
        RemoteControlSessions.StopServePump();
        Assert.False(RemoteControlSessions.ServePumpRunning);
    }

    [AvaloniaFact]
    public void Asking_twice_keeps_one_pump()
    {
        RemoteControlSessions.EnsureServePump();
        RemoteControlSessions.EnsureServePump();

        Assert.True(RemoteControlSessions.ServePumpRunning);

        // One stop is enough, which is the proof there was only ever one.
        RemoteControlSessions.StopServePump();
        Assert.False(RemoteControlSessions.ServePumpRunning);
    }

    // EnsureTimer calls this unconditionally, so it runs on every app that never
    // served anything at all — by far the common case.
    [AvaloniaFact]
    public void Stopping_a_pump_that_was_never_started_is_not_an_error()
    {
        RemoteControlSessions.StopServePump();
        RemoteControlSessions.StopServePump();

        Assert.False(RemoteControlSessions.ServePumpRunning);
    }

    // A pump that starts before any relay does — the real order, since
    // ServeOnLaunch starts the relays asynchronously and the pump immediately
    // after.
    [AvaloniaFact]
    public async Task A_tick_with_no_relays_yet_does_nothing_and_says_so_by_returning()
    {
        await RemoteControlSessions.ServeTickAsync();

        Assert.Empty(RemoteControlSessions.Snapshot());
    }

    // The pump is restartable, because a settings change that stops every relay
    // and a later one that starts them again both go through EnsureStarted.
    [AvaloniaFact]
    public void A_stopped_pump_can_be_started_again()
    {
        RemoteControlSessions.EnsureServePump();
        RemoteControlSessions.StopServePump();
        RemoteControlSessions.EnsureServePump();

        Assert.True(RemoteControlSessions.ServePumpRunning);
    }

    // The other side of the shared gate (CB-28). ServeOneAsync declining while
    // the mirror round holds it is IntegrationTests' to assert; this is the
    // mirror round declining while the serve pump holds it, which can only be
    // asserted on the thread the mirror round actually runs on.
    //
    // The second assertion is the one with teeth: a decliner that released the
    // gate on its way out — a `finally` written one line too high — would hand
    // the transcript to the very round it was standing down for.
    [AvaloniaFact]
    public async Task Mirror_round_stands_down_while_the_serve_pump_holds_the_gate()
    {
        Assert.True(RemoteControlSessions.PumpGate.TryEnter());

        try
        {
            await RemoteControlSessions.MirrorTickAsync();
            Assert.True(RemoteControlSessions.PumpGate.Busy);
        }
        finally
        {
            RemoteControlSessions.PumpGate.Exit();
        }

        // A round that does get in leaves the gate free behind it.
        await RemoteControlSessions.MirrorTickAsync();
        Assert.False(RemoteControlSessions.PumpGate.Busy);
    }

    // ---- the handover, and why it is no longer a disposal -------------------

    // EnsureTimer used to dispose the stand-in outright, on the reasoning that
    // reaching it "*is* the proof that a dispatcher now exists". It is proof
    // that the loop ran *once* — EnsureTimer is delivered by
    // Dispatcher.UIThread.Post — and a DispatcherTimer only fires while that
    // loop keeps pumping.
    //
    // On a headless machine the two are not the same thing. Measured on
    // job-hunter-mac-mini: Buddy alive at 0% CPU, its relay receiving
    // well-formed HELLOs from a correctly-named peer, and two ListAgents in
    // seven minutes — the first of which was StartAsync's own direct call, not
    // a tick. Nothing drained the transcript, nothing reached the mirror
    // server, and it answered none of them. From the far end: "the other
    // machine didn't answer in time", for hours.
    //
    // So the rule is now about the dispatcher *firing*, not existing.

    // A dispatcher that has never ticked is not alive, however long ago the app
    // started. This is the state a headless machine sits in, and the one that
    // used to be read as healthy.
    [Fact]
    public void ADispatcherThatHasNeverTickedIsNotAlive() =>
        Assert.False(RemoteControlSessions.DispatcherLooksAlive(DateTime.UtcNow, default));

    // One that ticked a moment ago is doing the work, and the stand-in has
    // nothing to add.
    [Fact]
    public void ADispatcherThatJustTickedIsAlive() =>
        Assert.True(RemoteControlSessions.DispatcherLooksAlive(
            DateTime.UtcNow, DateTime.UtcNow));

    // And one that has gone quiet hands the work back. This is the arm that
    // actually fixes the bug: without it the machine stays dark for ever.
    [Fact]
    public void ADispatcherThatHasGoneQuietHandsTheWorkBack()
    {
        var now = DateTime.UtcNow;
        var quiet = now - RemoteControlSessions.DispatcherSilenceBeforeStandIn
                        - TimeSpan.FromSeconds(1);

        Assert.False(RemoteControlSessions.DispatcherLooksAlive(now, quiet));
    }

    // The window has to clear the mirror timer's own interval comfortably, or a
    // perfectly healthy machine double-pumps every round — which PumpGate would
    // survive but should never be asked to.
    [Fact]
    public void TheSilenceWindowIsLongerThanAHealthyTickInterval() =>
        Assert.True(RemoteControlSessions.DispatcherSilenceBeforeStandIn
                    > TimeSpan.FromSeconds(5));
}
