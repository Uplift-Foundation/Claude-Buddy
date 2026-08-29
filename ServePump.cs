namespace ClaudeBuddy
{
    // A repeating tick that does not belong to the UI thread.
    //
    // Everything else that drives a relay is a DispatcherTimer, which is right
    // while there is a dispatcher: the poll republishes a peer list the scan
    // draws, and drawing is the UI thread's job. Serving is not. Answering
    // another machine's mirror request is files, a subprocess and a tmux pane —
    // no display anywhere in it — and CB-39 is what happens when the two are
    // conflated.
    //
    // On a machine whose screen never unlocks, Program.Main starts the relay,
    // then sleeps in MacOSScreenLock.WaitForUnlock for up to two hours before
    // Avalonia starts. Both DispatcherTimers are created by a
    // Dispatcher.UIThread.Post, so for those two hours the post only queues: the
    // relay is up, registered, and visible to every other machine, and nothing
    // ever reads a byte of its transcript. Measured on the mini on 29 Aug 2026 —
    // one poll per relay at startup and none after, 38 mirror HELLOs in and 0
    // answers out, with the main thread 2249 samples out of 2249 inside
    // Thread.Sleep. From the asking machine that is indistinguishable from a
    // machine with Buddy switched off, and it says so: "the other machine isn't
    // running Claude Buddy's Remote Control for this session."
    //
    // So this exists to cover exactly the window where there is no dispatcher.
    // It is deliberately not a second poll loop — see RemoteControlSessions'
    // ServeTickAsync, which drains transcripts and ticks the mirror halves and
    // pastes no prompt, so the window costs nothing beyond what a request
    // actually served costs. When the dispatcher does arrive it takes the timers
    // over and this stops.
    //
    // Wrapping System.Threading.Timer rather than using one directly is what
    // makes the two rules below testable without waiting on a real clock:
    // a tick never overlaps another, and a tick that throws does not stop the
    // ones after it. Both were learned from the DispatcherTimer pumps next door
    // — _mirrorTicking guards the first, and the per-relay try/catch the second
    // — and a serving machine is precisely where nobody would notice either
    // going wrong.
    internal sealed class ServePump : IDisposable
    {
        private readonly Func<Task> _tick;
        private readonly TimeSpan _every;
        private readonly object _gate = new();

        private Timer? _timer;
        private bool _ticking;
        private bool _disposed;

        public ServePump(Func<Task> tick, TimeSpan every)
        {
            _tick = tick;
            _every = every;
        }

        // Whether a tick is in flight. Only here so a test can watch the overlap
        // guard rather than infer it from a count.
        internal bool Ticking
        {
            get { lock (_gate) return _ticking; }
        }

        internal bool Running
        {
            get { lock (_gate) return _timer is not null; }
        }

        // Idempotent, because both callers mean "make sure this is going" rather
        // than "start another one" — the same shape as EnsureStarted itself.
        // Excluded from coverage: creates a real System.Threading.Timer. What it
        // decides is Start being idempotent and Dispose stopping it, both of
        // which are asserted through Running without waiting for a tick.
        public void Start()
        {
            lock (_gate)
            {
                if (_disposed || _timer is not null) return;

                // The first tick waits a full interval rather than firing at
                // zero: the caller has just started the relays, and there is
                // nothing on a transcript to read until one of them answers.
                _timer = new Timer(_ => _ = TickOnceAsync(), null, _every, _every);
            }
        }

        // One round, guarded. Returns false when it declined because another
        // round was still running — which is the only reason it ever declines.
        internal async Task<bool> TickOnceAsync()
        {
            lock (_gate)
            {
                if (_disposed || _ticking) return false;
                _ticking = true;
            }

            try
            {
                await _tick().ConfigureAwait(false);
            }
            catch
            {
                // A serving machine has nobody watching it, so a throw here must
                // cost this round and not the loop. The tick's own body is where
                // a failure worth reporting is reported.
            }
            finally
            {
                lock (_gate) _ticking = false;
            }

            return true;
        }

        public void Dispose()
        {
            Timer? timer;

            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                timer = _timer;
                _timer = null;
            }

            timer?.Dispose();
        }
    }
}
