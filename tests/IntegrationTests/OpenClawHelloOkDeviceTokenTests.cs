using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-278, end to end: a whole hello-ok driven through OpenClawGateway, through
// the 0600 credential file on disk, to the bearer a media fetch would present.
//
// The unit suite takes the same rules a case at a time (OpenClawDeviceTokenTests).
// This one takes the whole exchange, because the seam fails differently from
// the parse: a field read wrong is the unit suite's catch, and a hello-ok
// whose token sits somewhere the parse does not look, or a token that is
// parsed and then never reaches the file the fetch reads, is this one's.
//
// The fixture's structure is the hello-ok shape recorded against openclaw
// 2026.10.5 (CB-278's architect read, `auth: { method, role, scopes,
// deviceToken, issuedAtMs }`) and docs/openclaw-findings.md ("Numbers the
// implementation needs"). **Every value in it is invented**, and the device
// token is a placeholder of the same length the real one has (43 characters),
// so a length assumption anywhere downstream would still be exercised.
// It is not, and never was, a token any gateway issued.
[Collection("Settings")]
public class OpenClawHelloOkDeviceTokenTests
{
    private const string Host = "gateway.example.test";

    private const string Placeholder = "PLACEHOLDER-device-token-not-issued-by-any0";

    private const string HelloOk = """
        {
          "type": "hello-ok",
          "protocol": 4,
          "server": { "version": "2026.10.5" },
          "features": { "methods": ["sessions.list", "chat.history", "agents.list"] },
          "snapshot": { "presence": [], "health": {}, "stateVersion": 1, "uptimeMs": 1000, "sessionDefaults": {} },
          "auth": {
            "method": "token",
            "role": "operator",
            "scopes": ["operator.read"],
            "deviceToken": "PLACEHOLDER-device-token-not-issued-by-any0",
            "issuedAtMs": 1700000000000
          },
          "policy": { "tickIntervalMs": 30000, "maxPayload": 26214400 }
        }
        """;

    [Fact]
    public async Task AHelloOkDeviceTokenReachesTheFileAndIsWhatAMediaFetchPresents()
    {
        Assert.Equal(43, Placeholder.Length);

        var previous = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");
        var scratch = Path.Combine(Path.GetTempPath(), "cb278-hello-" + Guid.NewGuid());
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", scratch);
        OpenClawIdentity.ResetForTests();

        try
        {
            OpenClawIdentity.SetGatewayTokenFor(Host, "shared-placeholder");

            // Before any connect: no device token, so the fetch falls back to
            // the shared secret. That is today's behaviour and the control for
            // the assertion after the connect.
            Assert.Equal(OpenClawMediaCredential.Kind.Shared, OpenClawMediaCredential.Pick(
                OpenClawIdentity.TokenFor(Host), OpenClawIdentity.GatewayTokenFor(Host)).Kind);

            var hello = JsonDocument.Parse(HelloOk).RootElement;
            var socket = new FakeGatewaySocket();
            socket.PushEvent("connect.challenge", new { nonce = "nonce-placeholder" });
            socket.OnRequest = r => FakeGatewaySocket.Ok(r.Id, hello);

            using (var gateway = new OpenClawGateway(Host, 18789, "shared-placeholder",
                (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection(socket, Stream.Null, "fp")),
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan))
            {
                var result = await gateway.ConnectAsync(null, CancellationToken.None);
                Assert.Equal(OpenClawGateway.Outcome.Connected, result.Outcome);
                Assert.Equal(new[] { "operator.read" }, gateway.GrantedScopes);
            }

            // On disk, read back as JSON rather than through the cache: the
            // file is what a restarted app reads, so it has to hold both keys.
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(scratch, "openclaw-devices.json")))!;
            Assert.Equal(Placeholder, stored[Host]);
            Assert.Equal("shared-placeholder", stored["gateway:" + Host]);

            // And through a fresh read of that file, the pick a media fetch
            // makes is now the device token.
            OpenClawIdentity.ResetForTests();
            var (token, kind) = OpenClawMediaCredential.Pick(
                OpenClawIdentity.TokenFor(Host), OpenClawIdentity.GatewayTokenFor(Host));
            Assert.Equal(OpenClawMediaCredential.Kind.Device, kind);
            Assert.Equal(Placeholder, token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", previous);
            OpenClawIdentity.ResetForTests();
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }
}
