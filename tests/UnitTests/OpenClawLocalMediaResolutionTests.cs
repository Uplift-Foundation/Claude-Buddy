using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-88: an agent's own generated picture, resolved via the gateway's
// media.get RPC rather than a fetchable URL — see
// OpenClawSessions.FetchLocalMediaAsync's own comment for why the success
// response shape here is the one genuinely unverified piece of this feature
// (media.get requires operator.admin, which was never obtainable to capture
// a real response against). These tests exercise everything that IS
// verifiable without one: the marker detection reaching the fetch, the
// one-shot guard, and graceful failure when the gateway refuses the scope —
// which is exactly what a real gateway does today.
[Collection("Settings")]
public class OpenClawLocalMediaResolutionTests : IDisposable
{
    public void Dispose() => OpenClawSessions.SetGatewayForTests(null);

    private static OpenClawGateway Gateway(WebSocket socket) =>
        new("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(
                new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    private static FakeGatewaySocket Socket(Func<FakeGatewaySocket.Request, object?> onMediaGet)
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });

        socket.OnRequest = request =>
            request.Method == "connect"
                ? FakeGatewaySocket.Ok(request.Id, new
                {
                    protocol = 4,
                    server = new { version = "1.2.3" },
                    auth = new { scopes = new[] { "operator.read" } },
                    policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576 }
                })
                : request.Method == "media.get"
                    ? onMediaGet(request)
                    : FakeGatewaySocket.Ok(request.Id, new { });

        return socket;
    }

    private static async Task<OpenClawChatSession> ConnectedAsync(
        Func<FakeGatewaySocket.Request, object?> onMediaGet)
    {
        var socket = Socket(onMediaGet);
        var gateway = Gateway(socket);

        var result = await gateway.ConnectAsync(null, CancellationToken.None);
        Assert.Equal(OpenClawGateway.Outcome.Connected, result.Outcome);

        OpenClawSessions.SetGatewayForTests(gateway);

        return new OpenClawChatSession("openclaw:agent:main:main", "agent:main:main", "main");
    }

    private static JsonElement AgentText(string text) =>
        JsonDocument.Parse($"{{\"data\":{{\"text\":{JsonSerializer.Serialize(text)}}}}}").RootElement;

    // A one-pixel PNG, the same fixture this repo's other image tests use.
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==";

    [Fact]
    public async Task AMediaLineResolvesToImageBytesViaDataUrl()
    {
        var session = await ConnectedAsync(request => FakeGatewaySocket.Ok(request.Id, new
        {
            dataUrl = $"data:image/png;base64,{OnePixelPngBase64}"
        }));

        var updated = new System.Collections.Generic.List<ChatTurn>();
        session.TurnUpdated += updated.Add;

        session.OnAgentEvent("agent", AgentText("here's the drop 🌸\n\nMEDIA:/tmp/pic.png"));

        for (var i = 0; i < 50 && session.History[0].ImageBytes is null; i++)
            await Task.Delay(10);

        Assert.Equal(Convert.FromBase64String(OnePixelPngBase64), session.History[0].ImageBytes);
        Assert.Contains(session.History[0], updated);
    }

    [Fact]
    public async Task ABareFullPathAlsoResolves()
    {
        var session = await ConnectedAsync(request => FakeGatewaySocket.Ok(request.Id, new
        {
            dataUrl = $"data:image/png;base64,{OnePixelPngBase64}"
        }));

        session.OnAgentEvent("agent", AgentText("/tmp/pic.png"));

        for (var i = 0; i < 50 && session.History[0].ImageBytes is null; i++)
            await Task.Delay(10);

        Assert.NotNull(session.History[0].ImageBytes);
    }

    // A plain "data" field with no data: prefix — one of FetchLocalMediaAsync's
    // fallback field names, in case the real response doesn't use dataUrl.
    [Fact]
    public async Task ABareBase64DataFieldIsAlsoDecoded()
    {
        var session = await ConnectedAsync(request => FakeGatewaySocket.Ok(request.Id, new
        {
            data = OnePixelPngBase64
        }));

        session.OnAgentEvent("agent", AgentText("MEDIA:/tmp/pic.png"));

        for (var i = 0; i < 50 && session.History[0].ImageBytes is null; i++)
            await Task.Delay(10);

        Assert.Equal(Convert.FromBase64String(OnePixelPngBase64), session.History[0].ImageBytes);
    }

    // The real, current state of the world: the gateway refuses media.get
    // for a device that doesn't hold operator.admin. Confirmed live against
    // the actual gateway this session (CB-88) — not a hypothetical case.
    [Fact]
    public async Task AMissingScopeErrorLeavesTheTurnAsTextOnly()
    {
        var session = await ConnectedAsync(request => FakeGatewaySocket.Error(
            request.Id, "forbidden", "missing scope: operator.admin", null));

        session.OnAgentEvent("agent", AgentText("MEDIA:/tmp/pic.png"));
        await Task.Delay(50);

        Assert.Null(session.History[0].ImageBytes);
    }

    [Fact]
    public async Task ATurnWithNoMarkerNeverAsksTheGateway()
    {
        var asked = false;
        var session = await ConnectedAsync(request =>
        {
            asked = true;
            return FakeGatewaySocket.Ok(request.Id, new { });
        });

        session.OnAgentEvent("agent", AgentText("just an ordinary reply, nothing attached"));
        await Task.Delay(50);

        Assert.False(asked);
        Assert.Null(session.History[0].ImageBytes);
    }

    // QA (CB-88): a sentence that happens to start a line with the word
    // "MEDIA:" is not the marker — end to end, not just at the pure parser.
    [Fact]
    public async Task AnOrdinarySentenceStartingWithMediaNeverAsksTheGateway()
    {
        var asked = false;
        var session = await ConnectedAsync(request =>
        {
            asked = true;
            return FakeGatewaySocket.Ok(request.Id, new { });
        });

        session.OnAgentEvent("agent", AgentText("MEDIA: is a broad term for a lot of things"));
        await Task.Delay(50);

        Assert.False(asked);
        Assert.Null(session.History[0].ImageBytes);
    }

    [Fact]
    public async Task NoGatewayConfiguredLeavesTheTurnAsTextOnly()
    {
        OpenClawSessions.SetGatewayForTests(null);

        var session = new OpenClawChatSession("openclaw:agent:main:main", "agent:main:main", "main");

        session.OnAgentEvent("agent", AgentText("MEDIA:/tmp/pic.png"));
        await Task.Delay(50);

        Assert.Null(session.History[0].ImageBytes);
    }

    [Fact]
    public async Task ASecondSnapshotStillCarryingTheMarkerAsksOnlyOnce()
    {
        var requests = 0;
        var session = await ConnectedAsync(request =>
        {
            Interlocked.Increment(ref requests);
            return FakeGatewaySocket.Ok(request.Id, new { });
        });

        session.OnAgentEvent("agent", AgentText("here it comes\n\nMEDIA:/tmp/pic.png"));
        session.OnAgentEvent("agent", AgentText("here it comes now\n\nMEDIA:/tmp/pic.png"));
        await Task.Delay(50);

        Assert.Equal(1, requests);
    }
}
