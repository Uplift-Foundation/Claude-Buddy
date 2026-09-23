using Xunit;

namespace ClaudeBuddy.Tests;

// TurnSounds.Deliver itself: the seam between TurnSoundPolicy's pure
// decision and the world. TurnSoundPolicyTests already covers what gets
// decided; this covers what Deliver actually *does* with a Chime, a
// Summary, or — the part QA's fix added — a deferred one: the pending
// timer that plays it once the rate-limit gap opens, and the two places a
// background failure is caught rather than left to crash an unobserved
// Task.
//
// No Avalonia needed here — Deliver has no dispatcher dependency of its
// own (that lives in the callback SessionManager hands it, exercised in
// tests/UiTests instead). [Collection("Settings")] because Snapshot()
// reads ClaudeBuddySettings, which every class in that collection can
// otherwise leave in a state this file did not choose.
[Collection("Settings")]
public class TurnSoundsTests : IDisposable
{
    // Deliberately years in the past rather than DateTime.UtcNow: a
    // deferred decision's PlayAt is computed from the `now` a test hands
    // Deliver, but the pending timer itself schedules against the *real*
    // wall clock (SchedulePending's own delay calculation). A PlayAt this
    // far in the past clamps that delay to zero, so the timer fires almost
    // immediately instead of a test having to wait out a real two-second
    // gap.
    private static readonly DateTime Past = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public TurnSoundsTests() => TurnSounds.ResetForTests();

    public void Dispose()
    {
        ChimePlayer.PlayForTests = null;
        TurnSounds.ResetForTests();
    }

    private static TurnSoundEvent Finished(string key, string sessionId) =>
        new(TurnSignal.Finished, key, sessionId);

    private static Func<string, Task<bool>> NoSummary => _ => Task.FromResult(false);

    // QA (CB-167): the drop-not-defer fix's scan-side half. A signal that
    // lands inside the gap is held rather than thrown away, and the timer
    // that was armed for it is what actually plays it once the gap opens —
    // this is the part TurnSoundPolicyTests' pure Decide test cannot reach
    // on its own, since Decide only ever returns the deferred *action*, it
    // never schedules anything.
    [Fact]
    public async Task ADeferredDecisionEventuallyPlaysThroughThePendingTimer()
    {
        var played = new System.Collections.Generic.List<string>();
        var signal = new TaskCompletionSource<bool>();
        ChimePlayer.PlayForTests = path =>
        {
            lock (played)
            {
                played.Add(path);
            }

            signal.TrySetResult(true);
        };

        ClaudeBuddySettings.TurnSoundsEnabled = true;
        ClaudeBuddySettings.TurnFinishedSound = null; // platform default (Glass on macOS)

        // First signal: nothing has played yet, so this plays immediately
        // and sets _lastPlayed to Past.
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, Past);

        var first = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Equal(signal.Task, first);
        Assert.Single(played);

        signal = new TaskCompletionSource<bool>();

        // Second signal: one second after the first, inside the 2s gap —
        // Decide returns it deferred rather than Silent, and the pending
        // timer is what has to carry it out.
        TurnSounds.Deliver(new[] { Finished("key-b", "session-b") }, NoSummary, Past.AddSeconds(1));

        var second = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Equal(signal.Task, second);
        Assert.Equal(2, played.Count);
    }

    // The other half of "later signals re-coalesce into it": a live
    // decision — the gap has genuinely opened by the time this one is
    // decided — clears whatever was still pending rather than letting a
    // stale timer double the sound up.
    [Fact]
    public async Task ALiveDecisionAfterADeferredOneDoesNotAlsoPlayThePendingOne()
    {
        var played = new System.Collections.Generic.List<string>();
        var signal = new TaskCompletionSource<bool>();
        ChimePlayer.PlayForTests = path =>
        {
            lock (played)
            {
                played.Add(path);
            }

            signal.TrySetResult(true);
        };

        ClaudeBuddySettings.TurnSoundsEnabled = true;
        ClaudeBuddySettings.TurnFinishedSound = null;

        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, Past);
        await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Single(played);

        // Deferred — still inside the gap.
        TurnSounds.Deliver(new[] { Finished("key-b", "session-b") }, NoSummary, Past.AddSeconds(1));

        // A third signal, this time genuinely past the gap (Decide's own
        // clock, not the pending timer's real one) — a live decision that
        // must clear the still-pending second one before it plays.
        signal = new TaskCompletionSource<bool>();
        TurnSounds.Deliver(new[] { Finished("key-c", "session-c") }, NoSummary, Past.AddSeconds(10));

        var third = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Equal(signal.Task, third);

        // Give the cleared timer a moment it would have needed to fire if
        // clearing it had not actually worked, then check the count settled
        // rather than kept climbing.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.Equal(2, played.Count);
    }

    // Both background catches: a chime backend and a summary attempt can
    // each fail for reasons this app does not control (a missing binary, a
    // gateway timeout), and neither may crash the process by throwing out
    // of an unobserved Task. Coverage rather than a stronger assertion is
    // the point — the completion source is only proof this ran to the catch
    // and back out again without taking the test process down with it.
    [Fact]
    public async Task AChimePlayerThatThrowsIsCaughtRatherThanCrashingTheBackgroundTask()
    {
        var threw = new TaskCompletionSource<bool>();
        ChimePlayer.PlayForTests = _ =>
        {
            threw.TrySetResult(true);
            throw new InvalidOperationException("no audio device");
        };

        ClaudeBuddySettings.TurnSoundsEnabled = true;
        ClaudeBuddySettings.TurnFinishedSound = null;

        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, Past);

        var winner = await Task.WhenAny(threw.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Equal(threw.Task, winner);
    }

    [Fact]
    public async Task ATrySpeakTurnSummaryThatThrowsIsCaughtRatherThanCrashingTheBackgroundTask()
    {
        ClaudeBuddySettings.TurnSoundsEnabled = true;
        ClaudeBuddySettings.TurnFinishedSound = "summary";

        var threw = new TaskCompletionSource<bool>();
        Task<bool> ThrowingSpeak(string sessionId)
        {
            threw.TrySetResult(true);
            throw new InvalidOperationException("gateway timed out");
        }

        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, ThrowingSpeak, Past);

        var winner = await Task.WhenAny(threw.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Equal(threw.Task, winner);
    }
}
