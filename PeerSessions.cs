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

            // Both halves, before anything connects. A machine that is dialled
            // must be able to answer immediately, and a panel opened the moment
            // the app starts must find a client rather than a null.
            var account = ClaudeBuddySettings.DefaultRemoteControlProfileDir;

            host.Serve(account, RemoteControlSessions.LocalSessions);

            // A roster landing is what puts an orb on screen and what tells an
            // open panel to look again. Both, in that order: the panel reads the
            // published rows, so raising the event first would hand it the list
            // it already had.
            if (host.Client is { } client)
            {
                client.RosterUpdated += () =>
                {
                    RemoteControlSessions.RepublishFromLink();
                    RemoteControlSessions.RaiseMirrorChanged(account);
                };
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

            await AskWhatTheyHaveAsync(host).ConfigureAwait(false);
        }

        // Asks every connected machine what sessions it has.
        //
        // With no names, which means "everything" — see CB-67. That is the only
        // possible first question on a link with no prior list, and it is what
        // puts an orb on screen for a session on another machine.
        //
        // Cheap to repeat: the client only asks about names it does not already
        // know (CB-55), so a machine whose roster has already arrived costs
        // nothing per tick.
        [ExcludeFromCodeCoverage]
        private static async Task AskWhatTheyHaveAsync(PeerMirrorHost host)
        {
            var client = host.Client;
            if (client is null) return;

            var connected = host.Link.ConnectedMachines();
            if (connected.Count == 0) return;

            try
            {
                await client.DiscoverAsync(connected, Array.Empty<string>()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MirrorLog.Say("peer-roster-failed", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // --- pairing ----------------------------------------------------------------

        // Everything the settings pane needs to pair two machines, in the four
        // calls it takes: show a code, dial with one, list what is out there,
        // and forget one.
        //
        // Deliberately thin. The decisions live in PeerLink.Judge and
        // PeerIdentity, which are pure and tested; this layer exists so the
        // window never touches a link or an identity file directly.

        // Opens this machine to one pairing and returns the code to read out.
        [ExcludeFromCodeCoverage]
        internal static string? OpenForPairing() => Host?.Link.OpenForPairing();

        [ExcludeFromCodeCoverage]
        internal static void ClosePairing() => Host?.Link.ClosePairing();

        // Dials a machine we have seen announcing itself, offering a code read
        // off its screen. True only means the connection opened and the
        // greeting went out — the far side answers `ok` or `err`, and a refusal
        // arrives on the connection rather than from here.
        [ExcludeFromCodeCoverage]
        internal static async Task<bool> PairAsync(PeerDiscovery.Seen peer, string code)
        {
            var host = Host;
            if (host is null) return false;

            // Any existing connection to this machine is on a certificate we
            // may be about to replace, and the greeting is only sent by a fresh
            // dial. Dropping first is what makes re-pairing after a reinstall
            // work rather than silently doing nothing.
            host.Link.Drop(peer.Machine);

            return await host.Link
                .ConnectAsync(peer.Machine, peer.Address, peer.Port, pairingCode: code)
                .ConfigureAwait(false);
        }

        // The announcement a machine name came from, which is where its address
        // and port live. Null for a paired machine that is not currently on the
        // network — which is exactly why pairing needs it and forgetting does
        // not.
        [ExcludeFromCodeCoverage]
        internal static PeerDiscovery.Seen? SeenFor(string machine)
        {
            PeerDiscovery? discovery;
            lock (Gate) discovery = _discovery;

            return discovery?.Peers()
                .FirstOrDefault(p => string.Equals(
                    p.Machine, machine, StringComparison.OrdinalIgnoreCase));
        }

        [ExcludeFromCodeCoverage]
        internal static void Unpair(string machine)
        {
            Host?.Link.Drop(machine);
            PeerIdentity.Forget(machine);
        }

        // What the settings pane lists: everything seen announcing, plus
        // everything paired, whether or not it is currently reachable.
        //
        // Pure, because "which of these do I show and in what state" is a rule
        // and not a socket read. A paired machine that has gone quiet still has
        // a row — it is the row that says the machine is off, which is a
        // different answer from having nothing to say about it.
        internal sealed record Listed(string Machine, bool Paired, bool Connected, bool Seen);

        internal static IReadOnlyList<Listed> Listing(
            IReadOnlyList<PeerDiscovery.Seen> seen,
            IEnumerable<string> paired,
            Func<string, bool> connected)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var peer in seen) names.Add(peer.Machine);
            foreach (var machine in paired) names.Add(machine);

            var announcing = seen
                .Select(p => p.Machine)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var known = paired.ToHashSet(StringComparer.OrdinalIgnoreCase);

            return names
                .Select(name => new Listed(
                    name, known.Contains(name), connected(name), announcing.Contains(name)))
                .ToList();
        }

        [ExcludeFromCodeCoverage]
        internal static IReadOnlyList<Listed> Listing()
        {
            PeerMirrorHost? host;
            PeerDiscovery? discovery;

            lock (Gate)
            {
                host = _host;
                discovery = _discovery;
            }

            return Listing(
                discovery?.Peers() ?? Array.Empty<PeerDiscovery.Seen>(),
                PeerIdentity.Peers().Keys,
                machine => host?.Link.IsConnected(machine) ?? false);
        }

        // What one row says about itself.
        //
        // Four states rather than two, because "paired and unreachable" and
        // "here but not paired" are different problems with different next
        // steps, and collapsing them is the whole complaint about the transport
        // this replaces.
        internal static string RowStatus(Listed row) =>
            row switch
            {
                { Connected: true } => "Connected",
                { Paired: true, Seen: true } => "Paired — connecting…",
                { Paired: true } => "Paired — not on this network",
                _ => "Found — not paired",
            };

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
