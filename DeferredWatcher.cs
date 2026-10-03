namespace ClaudeBuddy
{
    // A FileSystemWatcher that is switched on somewhere that cannot hold the
    // caller up (CB-234).
    //
    // On macOS, .NET's FSEvents start runs the kernel's global sync(2) before it
    // returns, and sync waits for every dirty buffer on the machine to reach
    // disk. Measured on this Mac at a load average of 16-23: 118.7 s once, then
    // 0.4-1.8 s four times running. A stack taken from a hung UiTests host had
    // its threads in exactly that call. Both places this app turns a watcher on
    // are reached from the UI thread -- SessionManager.Start() at launch, and
    // LocalCliChatSession.Start() when a chat panel opens -- so an inline start
    // froze the app for as long as the disk was busy, and a hung test host with
    // it.
    //
    // Every watcher here is an optimisation over a poll timer that is already
    // running, never a requirement, so nothing waits for it: the start runs on a
    // dedicated background thread (not the pool, which a start that stalls would
    // otherwise drain one worker per caller), is allowed to fail silently, and
    // is disposed on arrival if its owner has gone in the meantime.
    internal sealed class DeferredWatcher : IDisposable
    {
        private readonly object _gate = new();
        private FileSystemWatcher? _watcher;
        private bool _disposed;

        // Kept so a test can join it instead of sleeping.
        internal Thread Starter { get; }

        // start builds AND turns on the watcher -- the call that can block. wire
        // attaches its handlers to the result.
        public DeferredWatcher(Func<FileSystemWatcher> start, Action<FileSystemWatcher> wire, string name)
        {
            Starter = new Thread(() => Run(start, wire)) { IsBackground = true, Name = name };
            Starter.Start();
        }

        // The watcher once it has arrived; null before that, after a failed
        // start, and after Dispose.
        internal FileSystemWatcher? Watcher
        {
            get { lock (_gate) return _watcher; }
        }

        private void Run(Func<FileSystemWatcher> start, Action<FileSystemWatcher> wire)
        {
            FileSystemWatcher watcher;
            try
            {
                watcher = start();
            }
            catch
            {
                // Losing the watcher costs latency, not correctness.
                return;
            }

            wire(watcher);

            lock (_gate)
            {
                // Disposed while the start was still in flight: nobody is left
                // to own it.
                if (_disposed) { watcher.Dispose(); return; }
                _watcher = watcher;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _watcher?.Dispose();
                _watcher = null;
            }
        }
    }
}
