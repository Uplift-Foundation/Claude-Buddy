using Xunit;

namespace ClaudeBuddy.Tests;

// The one thing about ServePump a fake clock cannot say: that it fires at all,
// on a thread nobody arranged, in a process with no Avalonia dispatcher in it.
//
// That is the whole of CB-39 in one sentence — the pumps it replaces were
// DispatcherTimers, and a DispatcherTimer in this process would never tick
// because there is no dispatcher here to tick it. So this suite is the honest
// place for the assertion, and a passing case here is the difference between
// "the timer is wired" and "the timer runs".
//
// Waits on a signal rather than a sleep, and asserts nothing about *when*: a
// loaded CI runner is allowed to be late, and a test that said otherwise would
// be the flake this repo has already fixed four of.
public class ServePumpTimerTests
{
    [Fact]
    public async Task Fires_repeatedly_with_no_dispatcher_in_the_process()
    {
        var twice = new TaskCompletionSource();
        var runs = 0;

        using var pump = new ServePump(
            () =>
            {
                if (Interlocked.Increment(ref runs) >= 2) twice.TrySetResult();
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(20));

        pump.Start();

        var finished = await Task.WhenAny(twice.Task, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.True(
            finished == twice.Task,
            $"the pump did not tick twice within 30s (ticks: {Volatile.Read(ref runs)})");
    }

    // Disposing while a tick may be in flight is what the handover to the
    // dispatcher looks like from the timer's side, and it must not throw.
    [Fact]
    public async Task Disposing_a_running_pump_stops_it()
    {
        var once = new TaskCompletionSource();

        var pump = new ServePump(
            () => { once.TrySetResult(); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(20));

        pump.Start();

        var finished = await Task.WhenAny(once.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(finished == once.Task, "the pump never ticked");

        pump.Dispose();

        Assert.False(pump.Running);
        Assert.False(await pump.TickOnceAsync());
    }
}
