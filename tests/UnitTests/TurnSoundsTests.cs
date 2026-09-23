using System.Threading;
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

        // Anchored to the real clock rather than Past for this one case,
        // deliberately: SchedulePending's own delay is always computed
        // against real DateTime.UtcNow, so a decision built from a `now`
        // years in the past (as every other case here uses) gives that
        // timer a delay that clamps to zero — which fires it before this
        // test's own third Deliver call could ever race it fairly. Anchored
        // to "now," the pending timer gets a real ~2 s window, and the third
        // call's *logical* clock (not the real one) is what makes it live
        // rather than deferred — so it reliably arrives first.
        var start = DateTime.UtcNow;

        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, start);
        await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Single(played);

        // Deferred — 0.1 s of logical time after the first, still inside
        // the 2 s gap. Real elapsed time so far is milliseconds, so the
        // pending timer this arms has close to a real two-second delay.
        TurnSounds.Deliver(new[] { Finished("key-b", "session-b") }, NoSummary, start.AddMilliseconds(100));

        // A third signal, logically 2.5 s after the first (so Decide calls
        // it live, not deferred) but issued in real time only milliseconds
        // after the second — well before the pending timer's real-world
        // deadline. This is what a live decision clearing a still-pending
        // one actually looks like without racing the clock that would
        // otherwise decide the test's outcome.
        signal = new TaskCompletionSource<bool>();
        TurnSounds.Deliver(new[] { Finished("key-c", "session-c") }, NoSummary, start.AddSeconds(2.5));

        var third = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Equal(signal.Task, third);

        // Wait past the real two seconds the cleared timer would have
        // needed to fire on its own, then check the count settled at two —
        // the first play and the third's live one — rather than climbing to
        // three from the second playing anyway.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(2, played.Count);
    }

    // QA (CB-167): the 2 s rate limit only spaces out when a sound is
    // *decided* — it says nothing about how long the previous one takes to
    // actually finish, and a user's own chosen file can run the full 5 s
    // cap. Two live decisions landing exactly 2 s apart (both legal, one
    // right after the gap the other closed) must still never have their
    // playback overlap if the first is still running. The seam simulates a
    // real duration with a short sleep — not a wait for a signal, an
    // honest stand-in for "afplay is still running" — so the test can
    // observe whether the second's start actually waited for the first's
    // finish rather than merely being decided after it.
    [Fact]
    public async Task TwoChimesDecidedTwoSecondsApartNeverOverlapInActualPlayback()
    {
        var events = new System.Collections.Generic.List<(string Path, DateTime Started, DateTime Finished)>();
        var done = new TaskCompletionSource<bool>();

        ChimePlayer.PlayForTests = path =>
        {
            var started = DateTime.UtcNow;
            Thread.Sleep(300); // stands in for a real, audible-length chime
            var finished = DateTime.UtcNow;
            lock (events)
            {
                events.Add((path, started, finished));
                if (events.Count == 2) done.TrySetResult(true);
            }
        };

        ClaudeBuddySettings.TurnSoundsEnabled = true;
        ClaudeBuddySettings.TurnFinishedSound = null;

        var start = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, start);
        TurnSounds.Deliver(new[] { Finished("key-b", "session-b") }, NoSummary, start.AddSeconds(2));

        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.Equal(2, events.Count);
        Assert.True(
            events[1].Started >= events[0].Finished,
            "the second chime started before the first had finished playing");
    }

    // QA (CB-167): a deferred signal must never fire late for a session
    // that has already been told to forget what it was doing — a husk
    // pruned from the scan, or a manual reset. CancelPendingFor and
    // CancelPendingUnlessSeen are SessionManager's two call sites for that;
    // both are anchored to the real clock for the same race-avoidance
    // reason ALiveDecisionAfterADeferredOneDoesNotAlsoPlayThePendingOne is.
    [Fact]
    public async Task CancelPendingForDropsAPendingSignalBeforeItsTimerFires()
    {
        var played = new System.Collections.Generic.List<string>();
        var signal = new TaskCompletionSource<bool>();
        ChimePlayer.PlayForTests = path =>
        {
            lock (played) played.Add(path);
            signal.TrySetResult(true);
        };

        ClaudeBuddySettings.TurnSoundsEnabled = true;
        ClaudeBuddySettings.TurnFinishedSound = null;

        var start = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, start);
        await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Single(played);

        // Deferred — real delay close to two seconds, plenty of window to
        // cancel it before it would fire on its own.
        TurnSounds.Deliver(new[] { Finished("key-b", "session-b") }, NoSummary, start.AddMilliseconds(100));

        TurnSounds.CancelPendingFor("session-b");

        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Single(played); // still just the first — the second never played
    }

    // The negative control: cancelling a *different* session's pending slot
    // must not touch this one's — otherwise CancelPendingFor could not be
    // told apart from ClearPending, which is precisely the bug this
    // wouldn't catch if the session id check were ever dropped.
    [Fact]
    public async Task CancelPendingForADifferentSessionLeavesTheRealPendingSignalAlone()
    {
        var played = new System.Collections.Generic.List<string>();
        var signal = new TaskCompletionSource<bool>();
        ChimePlayer.PlayForTests = path =>
        {
            lock (played) played.Add(path);
            signal.TrySetResult(true);
        };

        ClaudeBuddySettings.TurnSoundsEnabled = true;
        ClaudeBuddySettings.TurnFinishedSound = null;

        var start = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, start);
        await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Single(played);

        signal = new TaskCompletionSource<bool>();
        TurnSounds.Deliver(new[] { Finished("key-b", "session-b") }, NoSummary, start.AddMilliseconds(100));

        TurnSounds.CancelPendingFor("some-other-session-entirely");

        var second = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Equal(signal.Task, second);
        Assert.Equal(2, played.Count);
    }

    // The Prune-shaped guard: a session missing from `seen` (a husk the
    // scan just dropped) loses its pending signal the same way a Settled
    // one does.
    [Fact]
    public async Task CancelPendingUnlessSeenDropsAPendingSignalForASessionTheScanNoLongerSees()
    {
        var played = new System.Collections.Generic.List<string>();
        var signal = new TaskCompletionSource<bool>();
        ChimePlayer.PlayForTests = path =>
        {
            lock (played) played.Add(path);
            signal.TrySetResult(true);
        };

        ClaudeBuddySettings.TurnSoundsEnabled = true;
        ClaudeBuddySettings.TurnFinishedSound = null;

        var start = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, start);
        await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Single(played);

        TurnSounds.Deliver(new[] { Finished("key-b", "session-b") }, NoSummary, start.AddMilliseconds(100));

        // "session-b" is the husk this pass no longer sees.
        TurnSounds.CancelPendingUnlessSeen(new HashSet<string> { "session-a" });

        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Single(played);
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
