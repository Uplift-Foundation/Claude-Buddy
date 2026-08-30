using System.Threading;

namespace ClaudeBuddy
{
    // "One of these at a time, whoever asks", across threads.
    //
    // Two things drain a relay's transcript: the mirror DispatcherTimer, which
    // runs on the UI thread, and the serve pump, which runs on a thread-pool
    // thread while there is no UI thread to run on (CB-39). They are meant to be
    // alternatives — the pump exists only for the window before the dispatcher
    // arrives, and EnsureTimer disposes it the moment one does — but disposing a
    // timer does not reach inside a round already running, so the handover is
    // exactly one round wide and both can be in it.
    //
    // What that costs is not hypothetical. RemoteControlBridge.Pump reads
    // _offset under its lock, reads the file outside it, then writes the offset
    // back; two rounds overlapping there both start from the same offset and
    // both route the same lines, so a frame is handled twice and a message can
    // reach a panel twice. Once per process, on the one machine nobody is
    // watching.
    //
    // A bool would very nearly do — MirrorTickAsync used one, and on the UI
    // thread alone it was sound. It stops being sound the moment the other
    // caller is on a different thread: two threads can read `false` before
    // either writes `true`. Interlocked.CompareExchange is the same guard
    // without that gap, and it is why this is a type rather than a field: the
    // rule is now shared by two call sites on two different threads, and a rule
    // in two places is a rule that gets half-changed.
    internal sealed class TickGate
    {
        private int _busy;

        // True when this call has the gate and must Exit it; false when someone
        // else holds it, which is never an error — the caller's own timer will
        // come back around.
        internal bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

        // Safe to call without holding it, so a `finally` never has to ask
        // whether it got in.
        internal void Exit() => Interlocked.Exchange(ref _busy, 0);

        // Only for tests and for a caller that wants to skip work it would have
        // had to throw away anyway. Never a substitute for TryEnter: reading it
        // and then entering is two operations and a race.
        internal bool Busy => Volatile.Read(ref _busy) == 1;
    }
}
