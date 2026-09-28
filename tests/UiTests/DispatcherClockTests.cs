using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.UiTests;

// DispatcherClock against the real dispatcher: the members it reaches for by
// reflection are still there, and pointing the clock at the platform's does
// what it says to the dispatcher and to a timer already waiting.
//
// The headless dispatcher is built the way Startup.ClaimUiThread builds the
// app's — with no platform behind it — so its two clocks start microseconds
// apart rather than a startup apart. The offset test manufactures the gap the
// app has instead of waiting for one.
public class DispatcherClockTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static long DispatcherNow() =>
        (long)typeof(Dispatcher).GetProperty("Now", Instance)!.GetValue(Dispatcher.UIThread)!;

    private static Func<long> PlatformNow()
    {
        var impl = typeof(Dispatcher).GetField("_impl", Instance)!.GetValue(Dispatcher.UIThread)!;
        var getter = typeof(IDispatcherImpl).GetProperty("Now")!.GetGetMethod()!;
        return (Func<long>)getter.CreateDelegate(typeof(Func<long>), impl);
    }

    private static void SetDispatcherClock(Func<long> clock) =>
        typeof(Dispatcher).GetField("_timeProvider", Instance)!.SetValue(Dispatcher.UIThread, clock);

    private static long DueTime(DispatcherTimer timer) =>
        (long)typeof(DispatcherTimer).GetProperty("DueTimeInMs", Instance)!.GetValue(timer)!;

    // The one that fails first after an Avalonia upgrade that moves any of
    // them, and says which half broke.
    [Fact]
    public void Avalonia_still_has_every_member_the_alignment_reaches_for() =>
        Assert.True(DispatcherClock.Supported);

    [AvaloniaFact]
    public void A_dispatcher_already_on_the_platform_clock_is_left_alone()
    {
        DispatcherClock.Align(Dispatcher.UIThread);

        var again = DispatcherClock.Align(Dispatcher.UIThread);

        Assert.Equal(DispatcherClock.Outcome.AlreadyAligned, again.Outcome);
        Assert.InRange(again.OffsetMs, -DispatcherClock.ToleranceMs, DispatcherClock.ToleranceMs);
        Assert.Equal(0, again.TimersRestarted);
    }

    [AvaloniaFact]
    public void A_clock_ahead_of_the_platform_is_put_back_and_a_waiting_timer_restamped()
    {
        var platform = PlatformNow();

        // What ClaimUiThread leaves behind: the dispatcher's clock started half
        // a second before the platform's did.
        SetDispatcherClock(() => platform() + 500);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Start();

        try
        {
            // Stamped on the old clock, so due half a second later than it
            // asked for on the platform's.
            Assert.InRange(DueTime(timer) - platform(), 10_499, 10_501);

            var result = DispatcherClock.Align(Dispatcher.UIThread);

            Assert.Equal(DispatcherClock.Outcome.Aligned, result.Outcome);
            Assert.InRange(result.OffsetMs, 499, 501);
            Assert.True(result.TimersRestarted >= 1);

            Assert.InRange(DispatcherNow() - platform(), -1, 1);
            Assert.InRange(DueTime(timer) - platform(), 9_998, 10_001);
            Assert.True(timer.IsEnabled);
        }
        finally
        {
            timer.Stop();
        }
    }

    [AvaloniaFact]
    public void Without_the_members_nothing_is_touched()
    {
        var platform = PlatformNow();
        SetDispatcherClock(() => platform() + 500);

        try
        {
            var result = DispatcherClock.Align(Dispatcher.UIThread, supported: false);

            Assert.Equal(DispatcherClock.Outcome.Unavailable, result.Outcome);
            Assert.InRange(DispatcherNow() - platform(), 499, 501);
        }
        finally
        {
            SetDispatcherClock(platform);
        }
    }

    [AvaloniaFact]
    public async Task Off_the_ui_thread_nothing_is_touched()
    {
        var result = await Task.Run(() => DispatcherClock.Align(Dispatcher.UIThread));

        Assert.Equal(DispatcherClock.Outcome.Unavailable, result.Outcome);
    }
}
