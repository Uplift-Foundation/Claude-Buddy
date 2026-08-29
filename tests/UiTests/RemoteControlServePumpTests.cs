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
}
