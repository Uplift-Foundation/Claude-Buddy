using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.UiTests;

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

        // Every comparison below brackets its reading between two of the
        // platform's, rather than subtracting one reading from another, so a
        // runner that is slow between two lines cannot move the answer. A
        // Windows CI runner did, by 4 ms, when this test subtracted.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        var startedFrom = platform();
        timer.Start();
        var startedBy = platform();

        try
        {
            // Stamped on the old clock, so due half a second later than it
            // asked for on the platform's.
            Assert.InRange(DueTime(timer), startedFrom + 10_500, startedBy + 10_500);

            var alignedFrom = platform();
            var result = DispatcherClock.Align(Dispatcher.UIThread);
            var alignedBy = platform();

            Assert.Equal(DispatcherClock.Outcome.Aligned, result.Outcome);
            Assert.InRange(result.OffsetMs, 500 - (alignedBy - alignedFrom), 500);
            Assert.True(result.TimersRestarted >= 1);

            // Restamped on the platform's clock, at some moment during Align.
            Assert.InRange(DueTime(timer), alignedFrom + 10_000, alignedBy + 10_000);
            Assert.True(timer.IsEnabled);

            var readFrom = platform();
            var now = DispatcherNow();
            var readBy = platform();
            Assert.InRange(now, readFrom, readBy);
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

            var readFrom = platform();
            var now = DispatcherNow();
            var readBy = platform();
            Assert.InRange(now, readFrom + 500, readBy + 500);
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
