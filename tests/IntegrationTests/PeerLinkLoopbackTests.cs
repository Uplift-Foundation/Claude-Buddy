using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace ClaudeBuddy.IntegrationTests;

// Two PeerLinks talking to each other over a real socket on loopback.
//
// **This is the one thing about the peer link that unit tests cannot reach and
// that most needs to be true.** Everything else — framing, the trust decision,
// the read loop — is asserted over a MemoryStream, deliberately. What that
// leaves untested is the handshake: a self-signed certificate offered by both
// ends, a client certificate demanded by the server, TLS 1.2 or 1.3 negotiated
// between two .NET processes on two different operating systems. That is
// exactly the shape of thing that compiles, passes every offline test, and then
// fails on one platform during bring-up.
//
// Loopback rather than a real interface on purpose: 127.0.0.1 is exempt from
// macOS Local Network consent, so this tests the TLS and framing without also
// testing a permission dialog that CI cannot answer. The permission is a
// packaging concern and is handled in the Info.plist, not here.
//
// Port 0 lets the OS pick, so two of these can never collide with each other or
// with a developer's running copy of the app.
public class PeerLinkLoopbackTests
{
    // One certificate, exported and reloaded rather than used as created.
    //
    // Not a formality: an ephemeral key is not always usable by SslStream on
    // macOS, and a certificate that works in-process but not in a handshake is
    // the least helpful possible test result. PeerIdentity stores PKCS#12 for
    // the same reason.
    private static readonly Lazy<X509Certificate2> Cert = new(() =>
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=loopback", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using var made = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        return X509CertificateLoader.LoadPkcs12(
            made.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.Exportable);
    });

    private static PeerLink Link(
        List<PeerProtocol.PeerMessage> got, string pin, TaskCompletionSource? arrived = null) =>
        new(new PeerLink.Seams(
            Deliver: (_, message) =>
            {
                got.Add(message);
                arrived?.TrySetResult();
                return Task.CompletedTask;
            },
            // Both ends present the same certificate here, so each is the peer
            // the other paired with.
            KnownPeer: machine => new PeerIdentity.Peer(pin, machine),
            OwnCertificate: () => Cert.Value));

    [Fact]
    public async Task TwoLinksCompleteATlsHandshakeAndExchangeAMessage()
    {
        var pin = PeerIdentity.PinOf(Cert.Value);

        var onServer = new List<PeerProtocol.PeerMessage>();
        var reachedServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var server = Link(onServer, pin, reachedServer);
        using var client = Link(new List<PeerProtocol.PeerMessage>(), pin);

        var port = FreePort();
        server.Listen(port);

        Assert.True(
            await client.ConnectAsync("loopback", "127.0.0.1", port, Timeout(10)),
            "the client could not complete a TLS handshake against the listener");

        Assert.True(client.IsConnected("loopback"));

        Assert.True(
            await client.SendAsync("loopback", PeerProtocol.Message(PeerProtocol.Hello, "over-tls")),
            "the message was not accepted for sending");

        // Waited for rather than slept on: the far side reads on its own task,
        // and a sleep would be a wall-clock claim a loaded runner will not
        // honour — a mistake this repository has already fixed four times.
        await reachedServer.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var received = Assert.Single(onServer);
        Assert.Equal(PeerProtocol.Hello, received.Type);
        Assert.Equal("over-tls", received.Id);
    }

    // The payload that motivated the whole change: a transcript-sized message,
    // sent whole. Under the old transport this was 6KB chunks of base64 retyped
    // by a model at roughly four minutes each.
    [Fact]
    public async Task ATranscriptSizedMessageCrossesInOnePiece()
    {
        var pin = PeerIdentity.PinOf(Cert.Value);

        var onServer = new List<PeerProtocol.PeerMessage>();
        var reachedServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var server = Link(onServer, pin, reachedServer);
        using var client = Link(new List<PeerProtocol.PeerMessage>(), pin);

        var port = FreePort();
        server.Listen(port);

        Assert.True(await client.ConnectAsync("loopback", "127.0.0.1", port, Timeout(10)));

        // Half a megabyte, which is the size of a real window measured off the
        // mini (bytes 3293843..3817982 of its transcript).
        var big = new string('x', 512 * 1024);

        Assert.True(await client.SendAsync(
            "loopback",
            PeerProtocol.Message(PeerProtocol.Window, "big", body: PeerProtocol.BodyOf(big))));

        await reachedServer.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var received = Assert.Single(onServer);
        Assert.Equal(big.Length, received.Body!.Value.GetString()!.Length);
    }

    // A machine offering a certificate we did not pair with is refused at the
    // client, before anything is exchanged. The pin is the only identity check
    // there is — the certificate is self-signed and has no name worth trusting —
    // so this is the whole of the link's security.
    [Fact]
    public async Task APeerWhosePinWeDoNotKnowIsRefused()
    {
        var onServer = new List<PeerProtocol.PeerMessage>();

        using var server = Link(onServer, PeerIdentity.PinOf(Cert.Value));

        // The client has paired with something else entirely.
        using var client = new PeerLink(new PeerLink.Seams(
            Deliver: (_, _) => Task.CompletedTask,
            KnownPeer: machine => new PeerIdentity.Peer(new string('0', 64), machine),
            OwnCertificate: () => Cert.Value));

        var port = FreePort();
        server.Listen(port);

        Assert.False(
            await client.ConnectAsync("loopback", "127.0.0.1", port, Timeout(10)),
            "a peer offering an unknown certificate should be refused");

        Assert.False(client.IsConnected("loopback"));
    }

    private static CancellationToken Timeout(int seconds) =>
        new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    // Asked of the OS rather than hard-coded, so two runs of this suite — or a
    // developer's own running copy of the app — can never collide on a port.
    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }
}
