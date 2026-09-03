using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.Tests;

// The live half of the fix: an agent's streamed reply that mentions
// "[media attached: ...]" gets a thumbnail by asking the gateway's own
// chat.history for the newest page and matching the nearest picture in it —
// see OpenClawChatSession.TryResolveLiveImage and
// OpenClawSessions.BestImageMatch (covered on its own, against plain
// HistoryTurns, in OpenClawLiveImageResolutionTests's sibling
// OpenClawBestImageMatchTests). This is the seam OpenClawRoomSendTests
// established: a fake WebSocket answering a real OpenClawGateway, so the
// request that actually goes out is what is asserted rather than the return
// value alone.
[Collection("Settings")]
public class OpenClawLiveImageResolutionTests : IDisposable
{
    public void Dispose() => OpenClawSessions.SetGatewayForTests(null);

    private static OpenClawGateway Gateway(WebSocket socket) =>
        new("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(
                new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    // A socket that completes the handshake and answers "chat.history" with
    // whatever the test hands it, and everything else with an empty ok.
    private static FakeGatewaySocket Socket(Func<FakeGatewaySocket.Request, object> onHistory)
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
                : request.Method == "chat.history"
                    ? onHistory(request)
                    : FakeGatewaySocket.Ok(request.Id, new { });

        return socket;
    }

    private static async Task<(FakeGatewaySocket Socket, OpenClawChatSession Session)> ConnectedAsync(
        Func<FakeGatewaySocket.Request, object> onHistory)
    {
        var socket = Socket(onHistory);
        var gateway = Gateway(socket);

        var result = await gateway.ConnectAsync(null, CancellationToken.None);
        Assert.Equal(OpenClawGateway.Outcome.Connected, result.Outcome);

        OpenClawSessions.SetGatewayForTests(gateway);

        var session = new OpenClawChatSession(
            "openclaw:agent:main:main", "agent:main:main", "main");

        return (socket, session);
    }

    private static JsonElement AgentText(string text) =>
        JsonDocument.Parse($"{{\"data\":{{\"text\":{JsonSerializer.Serialize(text)}}}}}").RootElement;

    private const string Marker = "[media attached: /Users/x/media/inbound/staged-1.png]";

    [Fact]
    public async Task AnAttachmentMarkerInALiveReplyResolvesAThumbnail()
    {
        var (_, session) = await ConnectedAsync(request => FakeGatewaySocket.Ok(request.Id, new
        {
            messages = new object[]
            {
                new
                {
                    role = "assistant",
                    content = new object[]
                    {
                        new { type = "image", url = "https://x/pic.png", alt = "a graph" }
                    }
                }
            }
        }));

        var updated = new List<ChatTurn>();
        session.TurnUpdated += updated.Add;

        session.OnAgentEvent("agent", AgentText("Here you go " + Marker));

        for (var i = 0; i < 50 && session.History[0].ImageUrl is null; i++)
            await Task.Delay(10);

        Assert.Equal("https://x/pic.png", session.History[0].ImageUrl);
        Assert.Equal("a graph", session.History[0].ImageAlt);
        Assert.Contains(session.History[0], updated);
    }

    [Fact]
    public async Task ATurnWithNoMarkerNeverAsksTheGateway()
    {
        var asked = false;
        var (_, session) = await ConnectedAsync(request =>
        {
            asked = true;
            return FakeGatewaySocket.Ok(request.Id, new { messages = Array.Empty<object>() });
        });

        session.OnAgentEvent("agent", AgentText("just an ordinary reply, nothing attached"));
        await Task.Delay(50);

        Assert.False(asked);
        Assert.Null(session.History[0].ImageUrl);
    }

    [Fact]
    public async Task APageWithNoMatchingPictureLeavesTheTurnAsTextOnly()
    {
        var (_, session) = await ConnectedAsync(request =>
            FakeGatewaySocket.Ok(request.Id, new { messages = Array.Empty<object>() }));

        session.OnAgentEvent("agent", AgentText("Here you go " + Marker));
        await Task.Delay(50);

        Assert.Null(session.History[0].ImageUrl);
    }

    [Fact]
    public async Task ASecondSnapshotStillCarryingTheMarkerAsksOnlyOnce()
    {
        var requests = 0;
        var (_, session) = await ConnectedAsync(request =>
        {
            Interlocked.Increment(ref requests);
            return FakeGatewaySocket.Ok(request.Id, new { messages = Array.Empty<object>() });
        });

        session.OnAgentEvent("agent", AgentText("Here " + Marker));
        session.OnAgentEvent("agent", AgentText("Here it is: " + Marker));
        await Task.Delay(50);

        Assert.Equal(1, requests);
    }
}
