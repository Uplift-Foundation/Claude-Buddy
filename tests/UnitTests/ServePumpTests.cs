using Xunit;

namespace ClaudeBuddy.Tests;

// The stand-in that keeps a relay served while there is no dispatcher (CB-39).
//
// Every case here drives TickOnceAsync by hand rather than waiting on the
// timer. That is not only for speed: the two rules worth asserting — a tick
// never overlaps another, and a throw costs its own round and not the loop —
// are decisions about one round, and a test that slept would be asserting them
// through a clock that can be slow on a loaded CI runner. Whether the timer
// actually fires is IntegrationTests' ServePumpTimerTests, which is where a real
// clock belongs.
public class ServePumpTests
{
    [Fact]
    public async Task Runs_the_tick_it_was_given()
    {
        var runs = 0;
        using var pump = new ServePump(() => { runs++; return Task.CompletedTask; }, TimeSpan.FromHours(1));

        Assert.True(await pump.TickOnceAsync());
        Assert.True(await pump.TickOnceAsync());
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Refuses_to_overlap_a_tick_that_is_still_running()
    {
        var gate = new TaskCompletionSource();
        var runs = 0;

        using var pump = new ServePump(
            () => { runs++; return gate.Task; },
            TimeSpan.FromHours(1));

        var first = pump.TickOnceAsync();

        // The first round is inside the tick and has not returned, which is
        // exactly the state a slow file read leaves a live pump in.
        Assert.True(pump.Ticking);
        Assert.False(await pump.TickOnceAsync());
        Assert.Equal(1, runs);

        gate.SetResult();

        Assert.True(await first);
        Assert.False(pump.Ticking);

        // ...and the next round is allowed again, so declining once is not a
        // latch.
        Assert.True(await pump.TickOnceAsync());
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task A_throwing_tick_costs_its_own_round_and_nothing_after_it()
    {
        var runs = 0;

        using var pump = new ServePump(
            () =>
            {
                runs++;
                if (runs == 1) throw new InvalidOperationException("relay went away");
                return Task.CompletedTask;
            },
            TimeSpan.FromHours(1));

        // Reported as having run, because it did — and the guard is clear
        // afterwards, which is the part a swallowed exception could have left
        // wrong forever on a machine nobody is watching.
        Assert.True(await pump.TickOnceAsync());
        Assert.False(pump.Ticking);

        Assert.True(await pump.TickOnceAsync());
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task A_tick_that_faults_its_task_is_caught_the_same_way()
    {
        var runs = 0;

        using var pump = new ServePump(
            () =>
            {
                runs++;
                return Task.FromException(new IOException("transcript vanished"));
            },
            TimeSpan.FromHours(1));

        Assert.True(await pump.TickOnceAsync());
        Assert.False(pump.Ticking);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Stops_for_good_once_disposed()
    {
        var runs = 0;

        // An hour, like every other case in this file, and not the 5ms this
        // case used to run with: this is a claim about Dispose stopping the
        // pump, not about a real clock, and a short period raced its own
        // assertion on a loaded runner — Start() queues the first tick one
        // period out, so a scheduler delay between Start() and Dispose() of
        // 5ms or more (ordinary under contention) let that first tick land
        // before Dispose() ran, and runs was 1 rather than 0. Whether the
        // timer fires at all belongs to ServePumpTimerTests, which watches a
        // signal instead of a wall clock for exactly this reason.
        var pump = new ServePump(() => { runs++; return Task.CompletedTask; }, TimeSpan.FromHours(1));

        pump.Start();
        Assert.True(pump.Running);

        pump.Dispose();

        Assert.False(pump.Running);

        // These two are what the case now rests on, and both bite: Running
        // reads _timer, which Dispose nulls, and TickOnceAsync is guarded by
        // _disposed. Removing either line from Dispose turns this red.
        Assert.False(await pump.TickOnceAsync());

        // Said plainly because the alternative is a reader trusting it: with
        // an hour-long period this one cannot fail on its own — a timer that
        // was never stopped still would not have ticked by now. It is kept
        // because it is the assertion that catches a leaked callback should
        // TickOnceAsync ever stop declining post-dispose, not because it
        // carries the case. ServePumpTimerTests owns the real-clock half:
        // Disposing_a_running_pump_stops_it waits for a genuine tick to land
        // before disposing, so the live-timer disposal path is still covered
        // against a moving clock rather than a constant.
        Assert.Equal(0, runs);

        // Disposing twice is what a handover racing a shutdown looks like.
        pump.Dispose();
        Assert.False(pump.Running);
    }

    [Fact]
    public void Start_is_idempotent_so_a_second_ask_does_not_add_a_second_timer()
    {
        using var pump = new ServePump(() => Task.CompletedTask, TimeSpan.FromHours(1));

        pump.Start();
        var first = pump.Running;

        pump.Start();

        Assert.True(first);
        Assert.True(pump.Running);
    }

    [Fact]
    public void Start_after_dispose_does_nothing()
    {
        var pump = new ServePump(() => Task.CompletedTask, TimeSpan.FromHours(1));

        pump.Dispose();
        pump.Start();

        Assert.False(pump.Running);
    }
}
