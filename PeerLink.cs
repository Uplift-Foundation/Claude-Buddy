using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ClaudeBuddy
{
    // A direct connection to another copy of this app.
    //
    // **This is the first socket this application has ever listened on**, which
    // is worth stating because it is the source of most of what can go wrong
    // here. Everything else in this app either connects out (OpenClawSocket) or
    // is handed something by the OS (the claude:// URL scheme). Listening is new,
    // and both platforms gate it: macOS behind Local Network consent, Windows
    // behind the firewall.
    //
    // What it replaces: frames typed into a hidden Claude Code session so a
    // *model* could retype them into a peer-messaging tool. That cost a model
    // turn per frame — 222 to 247 seconds for one 6KB chunk, measured — and
    // occasionally corrupted the text it carried. Here a transcript is a write
    // and a read.
    //
    // Two halves, deliberately symmetric: every machine both listens and
    // connects, because either end can be the one being looked at. A connection
    // carries traffic in both directions once established, so two machines need
    // one connection between them rather than one each way.
    internal sealed class PeerLink : IDisposable
    {
        // Everything this needs from the world outside itself, so the whole
        // request/response contract can be driven in a test with no socket, no
        // certificate and no second machine — the same arrangement
        // RemoteMirrorServer.Seams already uses, and for the same reason.
        internal sealed record Seams(
            Func<string, PeerProtocol.PeerMessage, Task> Deliver,
            Func<string, PeerIdentity.Peer?> KnownPeer,
            Func<X509Certificate2> OwnCertificate);

        // Chosen from the unassigned range and fixed rather than negotiated: a
        // port that moved would have to be discovered before it could be
        // connected to, and discovery is what announces the port.
        public const int DefaultPort = 7677;

        private readonly Seams _seams;
        private readonly object _gate = new();
        private readonly Dictionary<string, Connected> _peers = new(StringComparer.OrdinalIgnoreCase);

        private TcpListener? _listener;
        private CancellationTokenSource? _stopping;

        internal PeerLink(Seams seams) => _seams = seams;

        // One live connection, and the lock that keeps two writers from
        // interleaving on it.
        //
        // A stream is not safe for concurrent writes, and both halves of the
        // mirror share this one: the server answering a fetch while the client
        // renews a watch is the ordinary case, not a rare one. The old transport
        // had the same problem and solved it the same way — one relay, one turn
        // at a time.
        private sealed class Connected(Stream stream, string machine, string pin) : IDisposable
        {
            public Stream Stream { get; } = stream;
            public string Machine { get; } = machine;
            public string Pin { get; } = pin;
            public SemaphoreSlim Writing { get; } = new(1, 1);

            public void Dispose()
            {
                Writing.Dispose();
                Stream.Dispose();
            }
        }

        // --- what the mirror halves use -------------------------------------------

        // The SendFrame seam, in the shape both mirror halves already take.
        //
        // False means "not delivered" and nothing more — the same contract the
        // relay's version had. A reply, when there is one, arrives through
        // Deliver rather than from here.
        internal async Task<bool> SendAsync(string machine, PeerProtocol.PeerMessage message)
        {
            Connected? peer;
            lock (_gate) _peers.TryGetValue(machine, out peer);

            if (peer is null)
            {
                MirrorLog.Say("peer-send-no-link", $"to={machine} t={message.Type}");
                return false;
            }

            await peer.Writing.WaitAsync().ConfigureAwait(false);

            try
            {
                await PeerProtocol.WriteAsync(peer.Stream, message).ConfigureAwait(false);
                MirrorLog.Say("peer-sent", $"to={machine} t={message.Type} id={message.Id}");
                return true;
            }
            catch (Exception ex)
            {
                // A write that fails has killed the connection: TLS cannot
                // resynchronise mid-record, so there is nothing to retry on it.
                MirrorLog.Say("peer-send-failed", $"to={machine} {ex.GetType().Name}: {ex.Message}");
                Drop(machine);
                return false;
            }
            finally
            {
                if (peer.Writing.CurrentCount == 0) peer.Writing.Release();
            }
        }

        internal IReadOnlyList<string> ConnectedMachines()
        {
            lock (_gate) return _peers.Keys.ToList();
        }

        internal bool IsConnected(string machine)
        {
            lock (_gate) return _peers.ContainsKey(machine);
        }

        // --- reading ---------------------------------------------------------------

        // Reads until the peer hangs up or the connection breaks.
        //
        // Internal and taking a Stream so the whole loop can be driven over a
        // MemoryStream: this is where a framing mistake would show up as a
        // corrupted transcript rather than an error, so it is the part that most
        // wants asserting without a socket in the way.
        internal async Task PumpAsync(string machine, Stream stream, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var message = await PeerProtocol.ReadAsync(stream, ct).ConfigureAwait(false);

                    // Null is a clean hangup between messages — an ordinary
                    // disconnect, not a fault worth reporting.
                    if (message is null) break;

                    if (message.Version != PeerProtocol.Version)
                    {
                        MirrorLog.Say("peer-version",
                            $"from={machine} theirs={message.Version} ours={PeerProtocol.Version}");
                        continue;
                    }

                    MirrorLog.Say("peer-in", $"from={machine} t={message.Type} id={message.Id}");

                    try
                    {
                        await _seams.Deliver(machine, message).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // One bad message must not take the connection down with
                        // it. The relay learned this the expensive way: a
                        // handler that threw was discarded silently and the
                        // machine went on looking healthy while answering
                        // nothing.
                        MirrorLog.Say("peer-handler-threw",
                            $"t={message.Type} {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                MirrorLog.Say("peer-read-failed", $"from={machine} {ex.GetType().Name}: {ex.Message}");
            }
        }

        // --- listening and connecting -----------------------------------------------

        [ExcludeFromCodeCoverage]
        internal void Listen(int port = DefaultPort)
        {
            lock (_gate)
            {
                if (_listener is not null) return;

                _stopping ??= new CancellationTokenSource();

                // Any address rather than loopback: the whole point is another
                // machine. On macOS this is the line that needs Local Network
                // consent, and without it the failure arrives at the *client* as
                // EHOSTUNREACH rather than here as anything at all.
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
            }

            _ = AcceptLoopAsync(_stopping!.Token);
        }

        [ExcludeFromCodeCoverage]
        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    MirrorLog.Say("peer-accept-failed", $"{ex.GetType().Name}: {ex.Message}");
                    break;
                }

                _ = HandshakeInboundAsync(client, ct);
            }
        }

        [ExcludeFromCodeCoverage]
        private async Task HandshakeInboundAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                // No callback on the constructor: setting one here *and* in the
                // authentication options below is refused by .NET outright, and
                // the options are where the client certificate is judged.
                var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _seams.OwnCertificate(),
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null
                }, ct).ConfigureAwait(false);

                var offered = tls.RemoteCertificate is null
                    ? null
                    : PeerIdentity.PinOf(X509CertificateLoader.LoadCertificate(tls.RemoteCertificate.GetRawCertData()));

                // Which machine this is comes from `hello`, so the connection is
                // held unnamed until then; the pin is what will be checked
                // against the name it claims.
                Adopt("(inbound)", tls, offered ?? "", ct);
            }
            catch (Exception ex)
            {
                MirrorLog.Say("peer-inbound-failed", $"{ex.GetType().Name}: {ex.Message}");
                client.Dispose();
            }
        }

        [ExcludeFromCodeCoverage]
        internal async Task<bool> ConnectAsync(
            string machine, string host, int port = DefaultPort, CancellationToken ct = default)
        {
            if (IsConnected(machine)) return true;

            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(host, port, ct).ConfigureAwait(false);

                var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = machine,
                    ClientCertificates = new X509Certificate2Collection(_seams.OwnCertificate()),
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,

                    // The certificate is self-signed and has no meaningful name
                    // to validate, exactly as the OpenClaw gateway's does. The
                    // fingerprint is the identity, and it is checked below
                    // against what we paired with rather than here against a
                    // chain that was never going to verify.
                    RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null
                }, ct).ConfigureAwait(false);

                var offered = tls.RemoteCertificate is null
                    ? ""
                    : PeerIdentity.PinOf(X509CertificateLoader.LoadCertificate(tls.RemoteCertificate.GetRawCertData()));

                if (!Accepts(_seams.KnownPeer(machine), offered))
                {
                    MirrorLog.Say("peer-refused", $"to={machine} pin-not-trusted");
                    tls.Dispose();
                    client.Dispose();
                    return false;
                }

                Adopt(machine, tls, offered, ct);
                return true;
            }
            catch (Exception ex)
            {
                MirrorLog.Say("peer-connect-failed",
                    $"to={machine} {OpenClawGateway.ExplainConnectFailure(ex, OperatingSystem.IsMacOS())}");
                return false;
            }
        }

        // Registers the connection and starts reading it.
        //
        // **The pump is started, not awaited, and that distinction was a real
        // bug.** Returning the pump's task made ConnectAsync wait for the
        // connection to *close* before reporting that it had opened — so
        // connecting appeared to hang until its own timeout, and then reported
        // failure on a handshake that had in fact succeeded. Caught by the
        // loopback test, which is the only thing that could have caught it: it
        // is invisible over a MemoryStream, where the stream ends immediately.
        [ExcludeFromCodeCoverage]
        private void Adopt(string machine, Stream stream, string pin, CancellationToken ct)
        {
            var peer = new Connected(stream, machine, pin);

            lock (_gate)
            {
                if (_peers.Remove(machine, out var existing)) existing.Dispose();
                _peers[machine] = peer;
            }

            _ = PumpAsync(machine, stream, ct)
                .ContinueWith(_ => Drop(machine), TaskScheduler.Default);
        }

        // --- the decision ------------------------------------------------------------

        // Whether a certificate offered by a machine is the one we paired with.
        //
        // Split out and pure because it is the security of the link, and because
        // the alternative is asserting it through a TLS handshake — which would
        // make the one decision that must never be wrong the hardest thing here
        // to test. See PeerIdentity.Trusts, which this defers to; this exists so
        // the *link's* use of it can be exercised on its own.
        internal static bool Accepts(PeerIdentity.Peer? known, string offeredPin) =>
            PeerIdentity.Trusts(known, offeredPin);

        internal void Drop(string machine)
        {
            Connected? peer;
            lock (_gate) _peers.Remove(machine, out peer);

            if (peer is null) return;

            MirrorLog.Say("peer-dropped", $"machine={machine}");
            peer.Dispose();
        }

        // A connection that arrived before it said who it was, renamed once
        // `hello` settles it. Kept separate from Adopt so the naming is one step
        // with one reason rather than a special case inside the handshake.
        internal void Rename(string from, string to)
        {
            lock (_gate)
            {
                if (!_peers.Remove(from, out var peer)) return;

                if (_peers.Remove(to, out var replaced)) replaced.Dispose();
                _peers[to] = new Connected(peer.Stream, to, peer.Pin);
            }
        }

        [ExcludeFromCodeCoverage]
        public void Dispose()
        {
            CancellationTokenSource? stopping;
            TcpListener? listener;
            List<Connected> peers;

            lock (_gate)
            {
                stopping = _stopping;
                listener = _listener;
                peers = _peers.Values.ToList();

                _stopping = null;
                _listener = null;
                _peers.Clear();
            }

            try { stopping?.Cancel(); } catch { }
            try { listener?.Stop(); } catch { }
            foreach (var peer in peers) peer.Dispose();
            stopping?.Dispose();
        }
    }
}
