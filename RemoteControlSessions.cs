using Avalonia.Threading;

namespace ClaudeBuddy
{
    // The Remote Control half of what SessionManager displays: Claude Code
    // sessions running on the user's *other* machines, published as an immutable
    // snapshot the scan reads for free.
    //
    // Shaped like OpenClawSessions next door, and for the same reasons — a
    // static class with a private cache and a gate, off meaning Snapshot()
    // returns an empty array with nothing constructed at all. What differs is
    // when it runs. OpenClaw holds a socket open for as long as it is switched
    // on, because a socket is free; this holds a **live Claude Code session on
    // the user's own account**, which is not. So it starts on demand, stops
    // itself when nobody is looking, and polls only while it is up.
    //
    // That is the whole reason EnsureStarted exists rather than a Restart() that
    // brings everything up: turning the feature on in Settings must not by
    // itself begin spending someone's quota. Something has to ask.
    internal static class RemoteControlSessions
    {
        private static readonly object Gate = new();

        // Published whole and replaced whole, so the scan — which runs on the UI
        // thread and locks nothing else it reads — is handed a finished list.
        private static volatile IReadOnlyList<Remote> _snapshot = Array.Empty<Remote>();

        private static RemoteControlBridge? _bridge;
        private static DispatcherTimer? _poll;
        private static DateTime _lastUse = DateTime.MinValue;
        private static bool _starting;
        private static string _state = "off";
        private static string? _warning;
        private static bool _polled;

        // A session on another machine, as the orb scan wants it. Kept separate
        // from BridgeProtocol.RemoteAgent so the parser stays a parser: this one
        // carries the app's own idea of when it was last seen, which the peer
        // list has no opinion about.
        internal sealed record Remote(string Name, string Ref, string Status, DateTime Seen)
        {
            // Namespaced the way OpenClaw's keys are, so one glance at a session
            // id says which source owns it — and so a remote session called
            // "evidence" can never collide with a local one of the same name.
            public string Key => "rc:" + Name;

            // "running" is the one that matters, and it is the one the first
            // version of this missed.
            //
            // The peer list's vocabulary is **not** the same as `claude agents
            // --json`'s. That prints "busy" for a working local session, so this
            // was written against "busy" — and a remote session actually reports
            // `running`, which meant the orb sat still for the entire time a
            // machine elsewhere was working. Caught only by watching a real
            // relay transcript: idle → running → idle across four polls while
            // nothing on screen moved.
            //
            // Exactly the mistake this repo's fixture rule exists to prevent,
            // made by taking a vocabulary from the wrong source rather than from
            // the output being parsed. The other two are kept as tolerance, not
            // because either has been seen here.
            public bool Working =>
                Status.Contains("running", StringComparison.OrdinalIgnoreCase)
                || Status.Contains("busy", StringComparison.OrdinalIgnoreCase)
                || Status.Contains("working", StringComparison.OrdinalIgnoreCase);
        }

        // How often to re-ask while the bridge is up. Slower than the 2s orb
        // scan on purpose: every poll is a real prompt into a real session, so
        // this is the one poll in the app with a per-tick cost.
        private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(20);

        // Empty whenever the feature is off or the bridge is down, which is what
        // makes the scan's job trivial — it never has to know why.
        public static IReadOnlyList<Remote> Snapshot() =>
            ClaudeBuddySettings.RemoteControlEnabled && RemoteControlBridge.IsSupported
                ? _snapshot
                : Array.Empty<Remote>();

        // For the settings window's status line, in the same vocabulary
        // OpenClawSessions.StatusText uses.
        //
        // Composed from two independent facts rather than one string, because
        // the first version wrote `warning ?? count` and so hid the count from
        // anyone who had a warning — which is everybody eventually, since the
        // login-expiry notice starts three days out. "Your login expires in 3
        // days" is useful; being unable to tell whether it also found anything
        // is not.
        public static string StatusText
        {
            get
            {
                lock (Gate)
                {
                    if (_warning is null) return _state;
                    return _state is "off" or "starting" ? _warning : $"{_state} · {_warning}";
                }
            }
        }

        // True once a poll has actually completed, so a caller can tell "up, and
        // has looked" from "up, about to look". Without it the only observable
        // is the status line, which reads as connected the moment the process
        // starts — a distinction a test cannot otherwise make, and the reason
        // the first live test of this passed while measuring nothing.
        public static bool HasPolled
        {
            get { lock (Gate) return _polled; }
        }

        public static event Action<BridgeProtocol.InboundMessage>? MessageReceived;

        // Raised when a remote session starts or stops working, so an open chat
        // panel can say so. Separate from the orb, which learns the same thing
        // from the snapshot on the next scan — a panel has no scan to wait for
        // and would otherwise show nothing at all between a send and a reply
        // that can be minutes apart.
        public static event Action<string, bool>? WorkingChanged;

        // Last known working state per remote, so only transitions are raised.
        // Re-announcing "still working" every 20 seconds would fill a panel with
        // the same line.
        private static readonly Dictionary<string, bool> WorkingNow =
            new(StringComparer.OrdinalIgnoreCase);

        // Brings the bridge up if it isn't, and marks it as wanted either way.
        // Every entry point that means "a person is looking at remote sessions"
        // calls this — the tray item, opening a remote chat, sending to one.
        public static void EnsureStarted()
        {
            if (!ClaudeBuddySettings.RemoteControlEnabled) return;
            if (!RemoteControlBridge.IsSupported) return;

            lock (Gate)
            {
                _lastUse = DateTime.UtcNow;
                if (_bridge is not null || _starting) return;
                _starting = true;
                _state = "starting";
            }

            _ = StartAsync();
        }

        // Keeps a bridge that is being used from being idled out from under
        // whoever is using it. Cheap enough to call on every send.
        public static void Touch()
        {
            lock (Gate) _lastUse = DateTime.UtcNow;
        }

        private static async Task StartAsync()
        {
            var bridge = new RemoteControlBridge();
            bridge.MessageReceived += OnMessage;

            bool ok;
            try
            {
                ok = await bridge.StartAsync().ConfigureAwait(false);
            }
            catch
            {
                ok = false;
            }

            if (!ok)
            {
                bridge.MessageReceived -= OnMessage;
                bridge.Dispose();

                lock (Gate)
                {
                    _starting = false;
                    _state = "failed to start";
                    _warning = null;
                }

                return;
            }

            lock (Gate)
            {
                _bridge = bridge;
                _starting = false;
                _polled = false;
                _state = "connected";
                _warning = bridge.Warning;
            }

            // Posted, not awaited. The timer has to be created on the UI thread
            // because DispatcherTimer belongs to it, but waiting for that to
            // happen would put the first poll behind the dispatcher being free —
            // and awaiting InvokeAsync where no dispatcher is pumping blocks
            // forever, which is exactly what it did: the relay reported
            // "connected" and then never asked for a peer list, because startup
            // stopped one line short of doing so.
            //
            // The recurring poll is a convenience; the first one is the point.
            // So the timer is arranged in the background and the first poll runs
            // right here, on the thread that started the bridge.
            Dispatcher.UIThread.Post(() =>
            {
                _poll?.Stop();
                _poll = new DispatcherTimer { Interval = PollEvery };
                _poll.Tick += (_, _) => _ = TickAsync();
                _poll.Start();
            });

            await TickAsync().ConfigureAwait(false);
        }

        // One poll: refresh the peer list, then decide whether the bridge has
        // outlived its welcome.
        private static async Task TickAsync()
        {
            RemoteControlBridge? bridge;
            lock (Gate) bridge = _bridge;
            if (bridge is null) return;

            // Idle check first, so a bridge nobody wants isn't kept alive by the
            // very poll that was about to retire it.
            if (IdleExpired())
            {
                Stop("idle");
                return;
            }

            if (!ClaudeBuddySettings.RemoteControlEnabled)
            {
                Stop("off");
                return;
            }

            // Also drains the transcript, which is how an inbound reply that
            // arrived while nobody was asking anything gets noticed at all.
            bridge.Pump();

            IReadOnlyList<BridgeProtocol.RemoteAgent>? agents;
            try
            {
                agents = await bridge.ListAgentsAsync().ConfigureAwait(false);
            }
            catch
            {
                agents = null;
            }

            if (agents is null)
            {
                // A bridge that has stopped answering is worse than none: it
                // would publish a peer list frozen at whatever was true when it
                // died. Retiring it means the orbs go away, which is at least
                // honest, and the next EnsureStarted brings a fresh one up.
                if (!bridge.IsRunning) Stop("bridge stopped");
                else lock (Gate) _state = "not answering";

                return;
            }

            var now = DateTime.UtcNow;
            var remotes = agents
                .Where(a => a.IsRemoteControl)
                .Select(a => new Remote(a.Name, a.Ref, a.Status, now))
                .ToList();

            _snapshot = remotes;

            // Transitions only, and computed before the state is overwritten.
            foreach (var remote in remotes)
            {
                bool was;
                lock (Gate) WorkingNow.TryGetValue(remote.Name, out was);

                if (was == remote.Working) continue;

                lock (Gate) WorkingNow[remote.Name] = remote.Working;

                var name = remote.Name;
                var working = remote.Working;
                Dispatcher.UIThread.Post(() => WorkingChanged?.Invoke(name, working));
            }

            lock (Gate)
            {
                _polled = true;
                _warning = bridge.Warning;
                _state = remotes.Count switch
                {
                    0 => "no remote sessions found",
                    1 => "1 remote session",
                    _ => $"{remotes.Count} remote sessions"
                };
            }
        }

        private static bool IdleExpired()
        {
            var minutes = ClaudeBuddySettings.RemoteControlIdleMinutes;
            if (minutes <= ClaudeBuddySettings.RemoteControlIdleNever) return false;

            DateTime last;
            lock (Gate) last = _lastUse;

            return DateTime.UtcNow - last > TimeSpan.FromMinutes(minutes);
        }

        // Hands text to a session on another machine. Null when there is no
        // bridge to send it through, which the caller shows as a system line
        // rather than swallowing.
        public static async Task<string?> SendToAsync(string remoteName, string text)
        {
            EnsureStarted();

            RemoteControlBridge? bridge;
            lock (Gate) bridge = _bridge;
            if (bridge is null) return null;

            Touch();
            return await bridge.SendToAsync(remoteName, text).ConfigureAwait(false);
        }

        public static void Stop(string why = "off")
        {
            RemoteControlBridge? bridge;

            lock (Gate)
            {
                bridge = _bridge;
                _bridge = null;
                _state = why;
                _warning = null;
                _polled = false;
                WorkingNow.Clear();
            }

            _snapshot = Array.Empty<Remote>();

            if (bridge is not null)
            {
                bridge.MessageReceived -= OnMessage;
                bridge.Dispose();
            }

            // Stop() is reachable from a poll tick, which is already on the UI
            // thread, and from a settings change, which may not be.
            if (Dispatcher.UIThread.CheckAccess()) StopTimer();
            else Dispatcher.UIThread.Post(StopTimer);
        }

        private static void StopTimer()
        {
            _poll?.Stop();
            _poll = null;
        }

        // Settings changed under us. Unlike OpenClawSessions.Restart this only
        // ever tears down: bringing the bridge back has to be asked for, because
        // starting it costs the user something.
        public static void Restart()
        {
            if (!ClaudeBuddySettings.RemoteControlEnabled || !RemoteControlBridge.IsSupported)
            {
                Stop("off");
                return;
            }

            lock (Gate)
            {
                if (_bridge is null && !_starting) return;
            }

            Stop("restarting");
        }

        private static void OnMessage(BridgeProtocol.InboundMessage message) =>
            Dispatcher.UIThread.Post(() => MessageReceived?.Invoke(message));
    }
}
