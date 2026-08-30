using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeBuddy
{
    // This machine's identity to another copy of this app, and its memory of the
    // ones it has been paired with.
    //
    // Three things, and each answers a different question:
    //
    //  * **A self-signed certificate** — "is this connection private?" Both ends
    //    of a peer link are this app, so there is no certificate authority in the
    //    picture and nothing to buy. TLS is here for confidentiality and
    //    integrity, not for a name.
    //
    //  * **A SHA-256 pin of that certificate** — "is this the machine I paired
    //    with?" Identity is the fingerprint, exactly as OpenClawSocket already
    //    treats the gateway's self-signed cert, and for the same reason: a
    //    generated certificate has no meaningful subject to validate. Trust on
    //    first use, remembered from then on, and a *change* is refused rather
    //    than shrugged at, because that is the one shape an interception takes.
    //
    //  * **A pairing code** — "did a person agree to this?" Shown on the far
    //    machine and typed on this one, once. Without it, anything that can
    //    reach the port could ask for a transcript.
    //
    // The private key never leaves this file and the file never leaves this
    // machine, which is the same promise OpenClawIdentity makes about its
    // keypair — and this deliberately mirrors that file's layout so there is one
    // shape to learn rather than two.
    internal static class PeerIdentity
    {
        // Alongside settings.json rather than in the temp directory. An identity
        // that vanished on reboot would ask the user to re-pair every machine
        // every time, which is the one part of this that cannot be automatic.
        private static string Path_ =>
            System.IO.Path.Combine(ClaudeBuddySettings.Directory, "peer-identity.json");

        private static readonly object Gate = new();
        private static Stored? _cached;

        // The certificate is stored as PKCS#12 rather than a keypair, because
        // unlike OpenClaw's raw Ed25519 keys this one has to be handed to
        // SslStream, which wants an X509Certificate2 with a private key attached.
        internal sealed record Stored(
            [property: JsonPropertyName("pkcs12")] string Pkcs12Base64,
            [property: JsonPropertyName("peers")] Dictionary<string, Peer> Peers);

        // A machine we have paired with. `Pin` is the identity; `Machine` is only
        // ever shown to a person.
        internal sealed record Peer(
            [property: JsonPropertyName("pin")] string Pin,
            [property: JsonPropertyName("machine")] string Machine,
            [property: JsonPropertyName("address")] string? Address = null);

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // --- this machine --------------------------------------------------------

        internal static X509Certificate2 Certificate()
        {
            lock (Gate)
            {
                _cached ??= Load() ?? Create();

                return X509CertificateLoader.LoadPkcs12(
                    Convert.FromBase64String(_cached.Pkcs12Base64), password: null,
                    X509KeyStorageFlags.Exportable);
            }
        }

        // What the far side will pin us by.
        internal static string OwnPin() => PinOf(Certificate());

        // The fingerprint of a certificate, as lowercase hex of the SHA-256 over
        // its DER bytes.
        //
        // The same computation OpenClawSocket.PinnedAuthentication performs on
        // the gateway's leaf, written the same way so the two are recognisably
        // one idea. Public so a test can assert a pin without a connection.
        internal static string PinOf(X509Certificate2 certificate) =>
            Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));

        // --- the machines we have paired with -------------------------------------

        internal static IReadOnlyDictionary<string, Peer> Peers()
        {
            lock (Gate)
            {
                _cached ??= Load() ?? Create();
                return new Dictionary<string, Peer>(_cached.Peers, StringComparer.OrdinalIgnoreCase);
            }
        }

        internal static Peer? PeerFor(string machine)
        {
            lock (Gate)
            {
                _cached ??= Load() ?? Create();
                return _cached.Peers.TryGetValue(machine, out var peer) ? peer : null;
            }
        }

        internal static void Remember(Peer peer)
        {
            lock (Gate)
            {
                _cached ??= Load() ?? Create();
                _cached.Peers[peer.Machine] = peer;
                Save(_cached);
            }
        }

        internal static void Forget(string machine)
        {
            lock (Gate)
            {
                _cached ??= Load() ?? Create();
                if (_cached.Peers.Remove(machine)) Save(_cached);
            }
        }

        // Whether a certificate offered by `machine` is the one we paired with.
        //
        // Pure, and the reason it is: this is the security decision of the whole
        // feature, and it should be assertable without a socket, a certificate
        // store or a second machine. The three arms are the whole of it — never
        // seen it (refuse; pairing is a deliberate act), seen it and it matches
        // (accept), seen it and it does not (refuse, loudly).
        internal static bool Trusts(Peer? known, string offeredPin) =>
            known is not null
            && !string.IsNullOrWhiteSpace(offeredPin)
            && string.Equals(known.Pin, offeredPin, StringComparison.OrdinalIgnoreCase);

        // --- pairing --------------------------------------------------------------

        // A short code a person reads off one screen and types into another.
        //
        // Six digits, from a cryptographic source rather than Random: it is
        // short-lived and low-entropy by design, so the entropy it does have
        // should at least be real. Digits only because it is read aloud across a
        // room as often as it is copied.
        internal static string NewPairingCode()
        {
            Span<byte> bytes = stackalloc byte[4];
            RandomNumberGenerator.Fill(bytes);

            return (BitConverter.ToUInt32(bytes) % 1_000_000).ToString("D6");
        }

        // Compared without leaking how much of it matched.
        //
        // A six-digit code guessed one digit at a time is a thousand tries
        // rather than a million, and an ordinary string compare returns as soon
        // as it disagrees. Fixed-time is cheap here and the alternative is a
        // real weakening of the only thing standing between a stranger on the
        // network and a transcript.
        internal static bool CodeMatches(string? expected, string? offered)
        {
            if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(offered)) return false;
            if (expected.Length != offered.Length) return false;

            var difference = 0;
            for (var i = 0; i < expected.Length; i++) difference |= expected[i] ^ offered[i];

            return difference == 0;
        }

        // --- disk -----------------------------------------------------------------

        // A test seam, and the only one this file needs: everything else is
        // deterministic given a certificate and a directory, and the directory
        // already moves with CLAUDE_BUDDY_SETTINGS_DIR.
        internal static void ForgetForTests()
        {
            lock (Gate) _cached = null;
        }

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        private static Stored? Load()
        {
            try
            {
                if (!File.Exists(Path_)) return null;

                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(Path_), Json);

                return stored is null || string.IsNullOrWhiteSpace(stored.Pkcs12Base64)
                    ? null
                    : stored;
            }
            catch
            {
                // A corrupt identity is replaced rather than fatal. The cost is
                // re-pairing; the alternative is an app that will not start.
                return null;
            }
        }

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        private static Stored Create()
        {
            using var key = RSA.Create(2048);

            var request = new CertificateRequest(
                $"CN={Environment.MachineName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            // Ten years, because this certificate is not trusted for being
            // unexpired — it is trusted for being the one that was pinned. An
            // expiry would only mean re-pairing every machine on a timer.
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

            var stored = new Stored(
                Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12)),
                new Dictionary<string, Peer>(StringComparer.OrdinalIgnoreCase));

            Save(stored);
            return stored;
        }

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        private static void Save(Stored stored)
        {
            try
            {
                Directory.CreateDirectory(ClaudeBuddySettings.Directory);
                File.WriteAllText(Path_, JsonSerializer.Serialize(stored, Json));

                // Owner-only, the same as openclaw-devices.json. A no-op concept
                // on Windows, where the call is simply not made rather than
                // branched on behaviour — the file lives in the user's own
                // AppData and inherits its protection from there.
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(Path_, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // An identity that cannot be written still works for this run.
                // The user re-pairs next launch, which is better than refusing
                // to run at all.
            }
        }
    }
}
