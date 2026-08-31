using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.UiTests;

// The clock the mirror halves run on.
//
// **This replaces RemoteControlServePumpTests, and the thing it covered is gone
// rather than moved.** That suite asserted the handover CB-39 turned on: a relay
// driven by a pump of its own until a dispatcher existed, and by the
// dispatcher's timers afterwards. There is no relay and no handover — the link
// owns one plain Timer and uses it always.
//
// The reason for a plain Timer did not go anywhere, which is why it is worth
// saying here as well as in the code: a DispatcherTimer does not fire on a Mac
// whose screen never unlocks, and that machine — headless, always up, serving
// its sessions — is the one this whole feature exists for.
[Collection("Settings")]
public class PeerMirrorPumpTests
{
    [AvaloniaFact]
    public void ThePumpIsFastEnoughToNoticeALapsedDeadline()
    {
        // Deadlines and watch renewals ride this clock rather than the arrival
        // of bytes, so an interval measured in tens of seconds would mean a
        // fetch that hangs long after it has actually given up.
        Assert.InRange(PeerSessions.PumpEvery.TotalMilliseconds, 250, 5000);
    }

    [AvaloniaFact]
    public void ThePumpIsFasterThanTheDialLoop()
    {
        // Two clocks with two jobs: one reconnects machines, one keeps the
        // conversation honest. Collapsing them would tie a deadline's precision
        // to how often a dead machine is retried.
        Assert.True(PeerSessions.PumpEvery < PeerSessions.ReconnectEvery);
    }

    [AvaloniaFact]
    public async Task TickingWithNoLinkRunningDoesNothingRatherThanThrowing()
    {
        // The ordinary case on a machine with the link switched off, and it runs
        // on a timer that discards its task — so an exception here would be
        // invisible and would stop the pump for the rest of the session.
        Assert.False(PeerSessions.Running);

        await RemoteControlSessions.MirrorTickAsync();
    }

    [AvaloniaFact]
    public async Task TickingTwiceInARowIsSafe()
    {
        // The gate is shared with anything else that ticks the halves; a second
        // caller declines rather than double-reading, and declining is never an
        // error.
        await RemoteControlSessions.MirrorTickAsync();
        await RemoteControlSessions.MirrorTickAsync();
    }
}
