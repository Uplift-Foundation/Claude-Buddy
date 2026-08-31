using Xunit;

namespace ClaudeBuddy.Tests;

// The one guard both transcript pumps take (CB-28).
//
// Small enough that these read like tautologies, and worth writing anyway: the
// bool this replaced also looked obviously correct, and was, right up until the
// second caller arrived on a different thread. What is being pinned here is not
// arithmetic but the contract the two call sites lean on — TryEnter is the
// decision, Exit is unconditional, and neither is allowed to grow a second
// meaning later.
public class TickGateTests
{
    [Fact]
    public void Lets_the_first_caller_in()
    {
        var gate = new TickGate();

        Assert.False(gate.Busy);
        Assert.True(gate.TryEnter());
        Assert.True(gate.Busy);
    }

    [Fact]
    public void Turns_the_second_caller_away_while_the_first_is_inside()
    {
        var gate = new TickGate();

        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        Assert.False(gate.TryEnter());
    }

    [Fact]
    public void Lets_the_next_caller_in_once_the_first_leaves()
    {
        var gate = new TickGate();

        Assert.True(gate.TryEnter());
        gate.Exit();

        // Not a latch. A round that declined must not have cost the pump every
        // round after it — which is the bug shape this whole area keeps having.
        Assert.False(gate.Busy);
        Assert.True(gate.TryEnter());
    }

    [Fact]
    public void Can_be_left_by_a_caller_that_never_got_in()
    {
        var gate = new TickGate();

        // Both call sites Exit from a `finally` that does not know whether it
        // entered, so this has to be harmless rather than merely unusual.
        gate.Exit();
        gate.Exit();

        Assert.False(gate.Busy);
        Assert.True(gate.TryEnter());
    }

    [Fact]
    public async Task Admits_exactly_one_of_many_threads_asking_at_once()
    {
        // The reason this is a type and not a bool. Two threads reading a bool
        // both see false and both proceed; here exactly one wins, and the count
        // is the assertion.
        var gate = new TickGate();
        var start = new TaskCompletionSource();
        var admitted = 0;

        var racers = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            if (gate.TryEnter()) Interlocked.Increment(ref admitted);
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(racers);

        Assert.Equal(1, admitted);
    }

    [Fact]
    public async Task Hands_the_gate_on_cleanly_under_contention()
    {
        // Enter/Exit in a loop from several threads: every entry is matched by
        // an exit, so the gate must end free and must never have admitted two
        // at once.
        var gate = new TickGate();
        var inside = 0;
        var everOverlapped = false;

        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                if (!gate.TryEnter()) continue;

                if (Interlocked.Increment(ref inside) != 1) everOverlapped = true;
                Interlocked.Decrement(ref inside);

                gate.Exit();
            }
        })).ToArray();

        await Task.WhenAll(workers);

        Assert.False(everOverlapped);
        Assert.False(gate.Busy);
    }
}
