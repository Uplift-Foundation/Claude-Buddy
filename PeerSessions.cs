using System.Diagnostics.CodeAnalysis;

namespace ClaudeBuddy
{
    // The peer link as the app runs it: listening, announcing, and keeping a
    // connection to every machine the user has paired with.
    //
    // Shaped like RemoteControlSessions and OpenClawSessions next door — a
    // static with a private cache and a gate, off meaning nothing is
    // constructed at all. What differs is what "off" costs. A relay is a live
    // Claude Code session on the user's account, so it starts on demand and
    // stops itself when nobody is looking; this is a socket, so the only reason
    // it is not simply always on is that listening is a permission on both
    // platforms and should be the user's choice rather than a surprise.
    //
    // Nothing here spends anything. There is no model in the path, so a machine
    // that sits connected all day costs what an idle socket costs.
    internal static class PeerSessions
    {
        private static readonly object Gate = new();

        private static PeerMirrorHost? _host;
        private static PeerDiscovery? _discovery;
        private static Timer? _connecting;

        // How often to try the machines we are paired with but not connected
        // to. A machine that has just woken should come back within a few
        // seconds of being seen, and a machine that is off costs one refused
        // connection per interval, which is nothing.
        internal static readonly TimeSpan ReconnectEvery = TimeSpan.FromSeconds(10);

        internal static PeerMirrorHost? Host
        {
            get { lock (Gate) return _host; }
        }

        internal static bool Running
        {
            get { lock (Gate) return _host is not null; }
        }

        // Which machines are worth trying: paired, seen announcing, and not
        // already connected.
        //
        // Pure so the rule is testable without a socket or a settings file. The
        // three arms are the whole of it, and the middle one is the reason
        // discovery exists — a paired machine we have never heard from has no
        // address to dial.
        internal static List<PeerDiscovery.Seen> WorthDialling(
            IReadOnlyList<PeerDiscovery.Seen> seen,
            Func<string, bool> paired,
            Func<string, bool> connected) =>
            seen.Where(p => paired(p.Machine) && !connected(p.Machine)).ToList();

        [ExcludeFromCodeCoverage]
        internal static void Start()
        {
            if (!ClaudeBuddySettings.PeerLinkEnabled) return;

            PeerMirrorHost host;
            PeerDiscovery discovery;

            lock (Gate)
            {
                if (_host is not null) return;

                _host = host = new PeerMirrorHost();
                _discovery = discovery = new PeerDiscovery();
            }

            try
            {
                host.Link.Listen(ClaudeBuddySettings.PeerLinkPort);
                discovery.Start(host.Link.BoundPort);

                MirrorLog.Say("peer-listening", $"port={host.Link.BoundPort}");
            }
            catch (Exception ex)
            {
                // On macOS a missing Local Network grant surfaces here or at the
                // first connect, and looks like an ordinary network error. The
                // hint is appended rather than substituted, exactly as the
                // gateway does it.
                MirrorLog.Say("peer-listen-failed",
                    OpenClawGateway.ExplainConnectFailure(ex, OperatingSystem.IsMacOS()));
            }

            lock (Gate)
            {
                _connecting = new Timer(
                    _ => _ = DialAsync(), null, TimeSpan.Zero, ReconnectEvery);
            }
        }

        // Connects to everything paired that we can see and are not already
        // talking to.
        //
        // A plain Timer rather than a DispatcherTimer, deliberately: this has to
        // keep working on a machine whose screen never unlocks, where the
        // Avalonia dispatcher does not pump. That is not hypothetical — it is
        // exactly the state the mini was found in, and the reason ServePump
        // exists at all (CB-39, CB-61).
        [ExcludeFromCodeCoverage]
        private static async Task DialAsync()
        {
            PeerMirrorHost? host;
            PeerDiscovery? discovery;

            lock (Gate)
            {
                host = _host;
                discovery = _discovery;
            }

            if (host is null || discovery is null) return;

            var worth = WorthDialling(
                discovery.Peers(),
                machine => PeerIdentity.PeerFor(machine) is not null,
                host.Link.IsConnected);

            foreach (var peer in worth)
            {
                try
                {
                    await host.Link
                        .ConnectAsync(peer.Machine, peer.Address, peer.Port)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    MirrorLog.Say("peer-dial-failed",
                        $"to={peer.Machine} {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // What the settings window shows, and the tray beside it.
        //
        // Deliberately says which of the three states it is in rather than
        // "connected" or nothing: a link that is on but has found nobody, and a
        // link that is off, are different problems with different fixes, and
        // telling them apart is the whole complaint about the transport this
        // replaces.
        internal static string StatusText(
            bool enabled, bool running, int listening, int connected, int seen)
        {
            if (!enabled) return "Off";
            if (!running) return "Starting…";

            if (connected > 0)
                return connected == 1
                    ? $"Connected to 1 machine, listening on {listening}"
                    : $"Connected to {connected} machines, listening on {listening}";

            if (seen > 0)
                return seen == 1
                    ? $"1 machine found, not paired yet — listening on {listening}"
                    : $"{seen} machines found, none paired yet — listening on {listening}";

            return $"Listening on {listening}, no machines found yet";
        }

        [ExcludeFromCodeCoverage]
        internal static string StatusText()
        {
            PeerMirrorHost? host;
            PeerDiscovery? discovery;

            lock (Gate)
            {
                host = _host;
                discovery = _discovery;
            }

            return StatusText(
                ClaudeBuddySettings.PeerLinkEnabled,
                host is not null,
                host?.Link.BoundPort ?? 0,
                host?.Link.ConnectedMachines().Count ?? 0,
                discovery?.Peers().Count ?? 0);
        }

        [ExcludeFromCodeCoverage]
        internal static void Stop()
        {
            PeerMirrorHost? host;
            PeerDiscovery? discovery;
            Timer? connecting;

            lock (Gate)
            {
                host = _host;
                discovery = _discovery;
                connecting = _connecting;

                _host = null;
                _discovery = null;
                _connecting = null;
            }

            connecting?.Dispose();
            discovery?.Dispose();
            host?.Dispose();
        }

        // Turning the setting off should take the socket down with it rather
        // than waiting for a restart, and turning it on should not need one.
        [ExcludeFromCodeCoverage]
        internal static void Restart()
        {
            Stop();
            Start();
        }
    }
}
