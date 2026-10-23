using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// CB-278: the device token hello-ok issues, kept, and presented to the HTTP
// media route in place of the gateway's shared secret.
//
// Three rules, one section each. Which `auth.deviceToken` counts as issued
// (DeviceTokenIn), that a connect stores it and never clears one on absence
// (ConnectAsync over the fake socket), and which bearer a media fetch presents
// (OpenClawMediaCredential.Pick). The fourth rule, that the store is not
// rewritten when nothing changed, is in OpenClawIdentityTests beside the rest
// of the store's rules.
//
// Every token below is an invented placeholder. None was ever issued by a
// gateway.
[Collection("Settings")]
public class OpenClawDeviceTokenTests
{
    private const string Host = "gw.local";

    // --- which auth.deviceToken counts as issued ---

    private static string? TokenIn(string json) =>
        OpenClawGateway.DeviceTokenIn(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void ADeviceTokenInTheAuthBlockIsIssued() =>
        Assert.Equal("dev-placeholder", TokenIn("""{"auth":{"scopes":["operator.read"],"deviceToken":"dev-placeholder"}}"""));

    [Theory]
    [InlineData("""{"protocol":4}""")]                                // no auth block at all
    [InlineData("""{"auth":"token"}""")]                              // auth that is not an object
    [InlineData("""{"auth":{"scopes":["operator.read"]}}""")]         // an auth block without one
    [InlineData("""{"auth":{"deviceToken":42}}""")]                   // not a string
    [InlineData("""{"auth":{"deviceToken":null}}""")]                 // JSON null
    [InlineData("""{"auth":{"deviceToken":""}}""")]                   // empty
    [InlineData("""{"auth":{"deviceToken":"   "}}""")]                // whitespace, which the pick would refuse to send
    [InlineData("""["auth"]""")]                                      // a response that is not an object
    public void AnythingElseIsNotIssued(string json) => Assert.Null(TokenIn(json));

    // --- a connect stores it ---

    // A settings directory of its own, so the token a connect stores is the
    // only one in the table and nothing leaks into the next test's connect
    // (a held token is sent back as `auth.deviceToken`).
    private static async Task InIsolation(Func<string, Task> body)
    {
        var previous = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");
        var scratch = Path.Combine(Path.GetTempPath(), "cb-devtoken-" + Guid.NewGuid());

        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", scratch);
        OpenClawIdentity.ResetForTests();

        try
        {
            await body(scratch);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", previous);
            OpenClawIdentity.ResetForTests();
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    // A gateway that accepts the connect, with whatever `auth` block the case
    // wants in its hello-ok.
    private static FakeGatewaySocket Accepting(object auth)
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });
        socket.OnRequest = request => request.Method == "connect"
            ? FakeGatewaySocket.Ok(request.Id, new { protocol = 4, server = new { version = "1.2.3" }, auth })
            : FakeGatewaySocket.Ok(request.Id, new { });
        return socket;
    }

    private static async Task ConnectAsync(FakeGatewaySocket socket)
    {
        using var gateway = new OpenClawGateway(Host, 4443, "gw-placeholder",
            (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection(socket, Stream.Null, "fp")),
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        var result = await gateway.ConnectAsync(null, CancellationToken.None);
        Assert.True(result.Outcome == OpenClawGateway.Outcome.Connected,
            $"the handshake did not connect: {result.Outcome} — {result.Detail ?? "no detail"}");
    }

    [Fact]
    public Task AnIssuedTokenIsStoredUnderTheGatewaysHost() => InIsolation(async _ =>
    {
        Assert.Null(OpenClawIdentity.TokenFor(Host));

        await ConnectAsync(Accepting(new { scopes = new[] { "operator.read" }, deviceToken = "dev-placeholder" }));

        Assert.Equal("dev-placeholder", OpenClawIdentity.TokenFor(Host));

        // Beside the shared secret, not instead of it: the connect still needs
        // that one, and it lives under its own prefixed key.
        Assert.Null(OpenClawIdentity.GatewayTokenFor(Host));
    });

    // A rotated token replaces the held one.
    [Fact]
    public Task ANewTokenReplacesTheHeldOne() => InIsolation(async _ =>
    {
        OpenClawIdentity.SetTokenFor(Host, "dev-old");

        await ConnectAsync(Accepting(new { scopes = new[] { "operator.read" }, deviceToken = "dev-new" }));

        Assert.Equal("dev-new", OpenClawIdentity.TokenFor(Host));
    });

    // Never cleared on absence: clearing would put the admin-scored shared
    // secret back on every media fetch. Each shape DeviceTokenIn refuses is
    // run through a real connect too, so the store is shown untouched by all
    // of them and not only by the parse.
    public static TheoryData<string> Absent => new()
    {
        """{"scopes":["operator.read"]}""",
        """{"scopes":["operator.read"],"deviceToken":""}""",
        """{"scopes":["operator.read"],"deviceToken":"  "}""",
        """{"scopes":["operator.read"],"deviceToken":7}""",
    };

    [Theory]
    [MemberData(nameof(Absent))]
    public Task ANoTokenAnswerLeavesTheHeldOneAlone(string auth) => InIsolation(async _ =>
    {
        OpenClawIdentity.SetTokenFor(Host, "dev-held");

        await ConnectAsync(Accepting(JsonDocument.Parse(auth).RootElement));

        Assert.Equal("dev-held", OpenClawIdentity.TokenFor(Host));
    });

    // And with nothing held, nothing is invented.
    [Fact]
    public Task ANoTokenAnswerStoresNothing() => InIsolation(async dir =>
    {
        await ConnectAsync(Accepting(new { scopes = new[] { "operator.read" } }));

        Assert.Null(OpenClawIdentity.TokenFor(Host));
        Assert.False(File.Exists(Path.Combine(dir, "openclaw-devices.json")));
    });

    // The stored token goes back on the next connect as `auth.deviceToken`,
    // which the code did already whenever one was held. Pinned here because
    // CB-278 is what makes that path live in production for the first time:
    // the shared secret is still sent, and is still what gets signed.
    [Fact]
    public Task TheNextConnectSendsTheStoredTokenBesideTheSharedSecret() => InIsolation(async _ =>
    {
        await ConnectAsync(Accepting(new { scopes = new[] { "operator.read" }, deviceToken = "dev-placeholder" }));

        var second = Accepting(new { scopes = new[] { "operator.read" }, deviceToken = "dev-placeholder" });
        await ConnectAsync(second);

        var auth = Assert.Single(second.Requests, r => r.Method == "connect").Params.GetProperty("auth");
        Assert.Equal("gw-placeholder", auth.GetProperty("token").GetString());
        Assert.Equal("dev-placeholder", auth.GetProperty("deviceToken").GetString());
    });

    // --- which bearer a media fetch presents ---

    [Fact]
    public void TheDeviceTokenWinsWhenBothAreHeld() =>
        Assert.Equal(("dev", OpenClawMediaCredential.Kind.Device), OpenClawMediaCredential.Pick("dev", "shared"));

    [Fact]
    public void TheDeviceTokenAloneIsPresented() =>
        Assert.Equal(("dev", OpenClawMediaCredential.Kind.Device), OpenClawMediaCredential.Pick("dev", null));

    // The fallback, which is today's behaviour: no device token yet, so the
    // shared secret, admin-scored as it always was.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheSharedSecretIsTheFallbackWhileNoDeviceTokenIsHeld(string? device) =>
        Assert.Equal(("shared", OpenClawMediaCredential.Kind.Shared), OpenClawMediaCredential.Pick(device, "shared"));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" ", "\t")]
    public void NothingUsableMeansNoFetch(string? device, string? shared) =>
        Assert.Equal(((string?)null, OpenClawMediaCredential.Kind.None), OpenClawMediaCredential.Pick(device, shared));
}
