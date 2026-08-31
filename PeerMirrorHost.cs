using System.Text.Json;

namespace ClaudeBuddy
{
    // Carries the mirror over a direct connection instead of through a relay.
    //
    // The two mirror halves were already transport-agnostic — their whole
    // dependency on the wire is one `SendFrame` delegate each, plus a symmetric
    // way in. This is what satisfies those seams with a socket rather than with
    // a language model retyping base64.
    //
    // **It translates rather than rewrites, and that is deliberate.** The halves
    // still speak MirrorProtocol frames; this turns each one into a
    // PeerProtocol message on the way out and back again on the way in. That
    // looks like an indirection worth removing, and it will be removed — the
    // chunking, base64 and digests come out in the next step. Keeping the
    // translation for one step means the existing mirror suites run unchanged
    // against the new transport, which is the only cheap way to prove the swap
    // altered no behaviour. Doing both at once would mean rewriting the tests
    // that were supposed to be the check.
    internal sealed class PeerMirrorHost : IDisposable
    {
        private readonly PeerLink _link;
        private readonly object _gate = new();

        // The far machine a connection belongs to, once `hello` has said so.
        // Until then an inbound connection has no name — see PeerLink.Rename.
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);

        private RemoteMirrorClient? _client;
        private RemoteMirrorServer? _server;

        internal PeerMirrorHost()
        {
            _link = new PeerLink(new PeerLink.Seams(
                Deliver: DeliverAsync,
                KnownPeer: PeerIdentity.PeerFor,
                OwnCertificate: PeerIdentity.Certificate));
        }

        internal PeerLink Link => _link;

        // The client a panel talks to, when the link is what is carrying the
        // mirror. Null until Serve has built one.
        internal RemoteMirrorClient? Client
        {
            get { lock (_gate) return _client; }
        }

        // The serving half, exposed for the same reason as the asking one: both
        // have a TickAsync that has to be driven by a clock rather than by the
        // arrival of bytes, or a deadline never lapses and a watch quietly
        // expires. See RemoteControlSessions.MirrorTickAsync.
        internal RemoteMirrorServer? Server
        {
            get { lock (_gate) return _server; }
        }

        // Builds both halves over this link.
        //
        // **Not account-scoped, and that is a simplification worth naming.** A
        // relay is one hidden Claude Code session per account, because Remote
        // Control only reaches sessions signed into the same account — so the
        // relay path needs a client and a server per account and cannot avoid
        // it. A socket has no account: this machine talks to that machine, and
        // which Anthropic login either of them uses is not the socket's
        // business. One pair serves every session on the far side.
        //
        // The account string below is therefore a label rather than a scope. It
        // is still passed because RemoteMirrorServer's local seams use it to
        // read this machine's own agent roster, which *is* per-account.
        internal void Serve(
            IReadOnlyList<string> accounts,
            Func<IReadOnlyList<(string SessionId, SessionStatus Status)>> localSessions)
        {
            var label = accounts.Count > 0 ? accounts[0] : ".claude";

            lock (_gate)
            {
                if (_client is not null) return;

                _client = new RemoteMirrorClient(
                    label, new RemoteMirrorClient.Seams(SendFrameAsync));

                var seams = RemoteMirrorServer.AllAccountSeams(
                    accounts, SendFrameAsync, localSessions);

                _server = new RemoteMirrorServer(label, seams with { PeerAllowed = MayAsk });
            }
        }

        internal void Bind(RemoteMirrorClient client, RemoteMirrorServer server)
        {
            lock (_gate)
            {
                _client = client;
                _server = server;
            }
        }

        // --- outbound --------------------------------------------------------------

        // The SendFrame seam both halves take.
        //
        // A MirrorProtocol frame goes out as the *text* of a PeerProtocol
        // message rather than being re-encoded field by field. That keeps this
        // step to one moving part: the frame is already a self-describing line,
        // and the next step replaces it wholesale rather than teaching this to
        // decompose it first.
        internal Task<bool> SendFrameAsync(string machine, string frame) =>
            _link.SendAsync(
                machine,
                PeerProtocol.Message(PeerProtocol.Fetch, PeerProtocol.NewId(),
                    body: PeerProtocol.BodyOf(frame)));

        // --- inbound ---------------------------------------------------------------

        private async Task DeliverAsync(string machine, PeerProtocol.PeerMessage message)
        {
            var text = message.Body?.ValueKind == JsonValueKind.String
                ? message.Body.Value.GetString()
                : null;

            if (string.IsNullOrEmpty(text)) return;

            var frame = MirrorProtocol.TryParseFrame(text);

            if (frame is null)
            {
                MirrorLog.Say("peer-frame-unparseable", $"from={machine} len={text.Length}");
                return;
            }

            RemoteMirrorClient? client;
            RemoteMirrorServer? server;

            lock (_gate)
            {
                client = _client;
                server = _server;
            }

            // Which half answers is decided by the frame rather than by who sent
            // it — a request is for the server here, a reply for the client, and
            // one connection carries both directions because both machines are
            // asking each other.
            switch (frame.Type)
            {
                case MirrorProtocol.Chunk:
                case MirrorProtocol.Ok:
                case MirrorProtocol.Err:
                    if (client is not null) await client.OnFrameAsync(machine, frame).ConfigureAwait(false);
                    else MirrorLog.Say("peer-dropped", $"t={frame.Type} no client bound");
                    break;

                default:
                    if (server is not null) await server.HandleAsync(machine, frame).ConfigureAwait(false);
                    else MirrorLog.Say("peer-dropped", $"t={frame.Type} no server bound");
                    break;
            }
        }

        // --- who may ask -------------------------------------------------------------

        // The PeerAllowed seam. Over this transport the question is already
        // answered by the time a message arrives: nothing reaches DeliverAsync
        // that did not complete a TLS handshake presenting a certificate we
        // pinned when somebody typed a pairing code.
        //
        // So this is a connection check rather than a name check — which is the
        // whole difference between a guard and a boundary, and the reason the
        // hard-coded prefix test was worth replacing rather than porting.
        internal bool MayAsk(string machine) => _link.IsConnected(machine);

        public void Dispose() => _link.Dispose();
    }
}
