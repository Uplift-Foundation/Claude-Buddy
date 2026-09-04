using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-88/CB-90: an agent's own generated picture, named by path in its reply
// and fetched through the gateway's read-scoped media route.
//
// Driven through FetchMediaAsync's own url-keyed cache rather than a fake
// socket. That is not a shortcut around the transport — it is the seam that
// makes the interesting half assertable: whether the route is built correctly
// from the path. FetchMediaAsync checks the cache before it reads host or
// token, so seeding the exact route these tests expect and finding the bytes
// come back *is* the assertion that the route matches. Get the escaping or
// the prefix wrong and the seeded entry is simply never found.
[Collection("Settings")]
public class OpenClawLocalMediaResolutionTests : IDisposable
{
    private readonly List<string> _seeded = new();

    private static Dictionary<string, byte[]?> MediaCache()
    {
        var field = typeof(OpenClawSessions).GetField("Media", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Dictionary<string, byte[]?>)field.GetValue(null)!;
    }

    // The route the code under test is expected to build for a path.
    private static string RouteFor(string path) =>
        OpenClawSessions.AssistantMediaRoute + Uri.EscapeDataString(path);

    private void Seed(string path, byte[]? bytes)
    {
        var key = RouteFor(path);
        _seeded.Add(key);
        MediaCache()[key] = bytes;
    }

    public void Dispose()
    {
        var cache = MediaCache();
        foreach (var key in _seeded) cache.Remove(key);
    }

    private static OpenClawChatSession Session() =>
        new("openclaw:agent:main:main", "agent:main:main", "main");

    private static JsonElement AgentText(string text) =>
        JsonDocument.Parse($"{{\"data\":{{\"text\":{JsonSerializer.Serialize(text)}}}}}").RootElement;

    // A one-pixel PNG, the same fixture this repo's other image tests use.
    private static byte[] Pixel() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

    private static async Task<byte[]?> WaitForBytes(OpenClawChatSession session)
    {
        for (var i = 0; i < 50 && session.History[0].ImageBytes is null; i++)
            await Task.Delay(10);

        return session.History[0].ImageBytes;
    }

    [Fact]
    public async Task AMediaLineResolvesToImageBytes()
    {
        const string path = "/Users/warrenthompson/.openclaw/media/lilibeth_drop.png";
        Seed(path, Pixel());

        var session = Session();
        var updated = new List<ChatTurn>();
        session.TurnUpdated += updated.Add;

        session.OnAgentEvent("agent", AgentText("here's the drop 🌸\n\nMEDIA:" + path));

        Assert.Equal(Pixel(), await WaitForBytes(session));
        Assert.Contains(session.History[0], updated);
    }

    [Fact]
    public async Task ABareFullPathAlsoResolves()
    {
        const string path = "/Users/warrenthompson/.openclaw/media/bare.png";
        Seed(path, Pixel());

        var session = Session();
        session.OnAgentEvent("agent", AgentText(path));

        Assert.NotNull(await WaitForBytes(session));
    }

    // The escaping is the part most likely to be wrong, so it gets its own
    // case: a path with a space and a non-ASCII character resolves only if
    // the route was percent-encoded the way the gateway expects.
    [Fact]
    public async Task ThePathIsPercentEncodedIntoTheRoute()
    {
        const string path = "/Users/warrenthompson/.openclaw/media/a drop ünicode.png";
        Seed(path, Pixel());

        Assert.Contains("%20", RouteFor(path));

        var bytes = await OpenClawSessions.FetchLocalMediaAsync(path, CancellationToken.None);
        Assert.Equal(Pixel(), bytes);
    }

    [Fact]
    public void TheRouteIsTheGatewaysOwnReadScopedMediaEndpoint()
    {
        Assert.Equal("/__openclaw__/assistant-media?source=", OpenClawSessions.AssistantMediaRoute);
    }

    [Fact]
    public async Task ATurnWithNoMarkerNeverResolves()
    {
        var session = Session();
        session.OnAgentEvent("agent", AgentText("just an ordinary reply, nothing attached"));
        await Task.Delay(50);

        Assert.Null(session.History[0].ImageBytes);
    }

    // QA (CB-88): a sentence that happens to start a line with the word
    // "MEDIA:" is not the marker.
    [Fact]
    public async Task AnOrdinarySentenceStartingWithMediaNeverResolves()
    {
        var session = Session();
        session.OnAgentEvent("agent", AgentText("MEDIA: is a broad term for a lot of things"));
        await Task.Delay(50);

        Assert.Null(session.History[0].ImageBytes);
    }

    // The one-shot guard, proven by observation rather than by counting
    // requests: resolve once, then change what the route would return and
    // send the same marker again. Still holding the first picture means the
    // second snapshot never went looking.
    [Fact]
    public async Task ASecondSnapshotStillCarryingTheMarkerDoesNotFetchAgain()
    {
        const string path = "/Users/warrenthompson/.openclaw/media/once.png";
        Seed(path, Pixel());

        var session = Session();
        session.OnAgentEvent("agent", AgentText("here it comes\n\nMEDIA:" + path));

        var first = await WaitForBytes(session);
        Assert.NotNull(first);

        var different = new byte[] { 9, 9, 9, 9 };
        Seed(path, different);

        session.OnAgentEvent("agent", AgentText("here it is now\n\nMEDIA:" + path));
        await Task.Delay(50);

        Assert.Equal(first, session.History[0].ImageBytes);
        Assert.NotEqual(different, session.History[0].ImageBytes);
    }

    // A path the gateway will not serve (outside its media allowlist, which
    // answers with a non-200 and so a null here) leaves the turn as text.
    [Fact]
    public async Task APathTheGatewayWillNotServeLeavesTheTurnAsTextOnly()
    {
        const string path = "/Users/warrenthompson/.openclaw/workspace-comfyui-zara/outputs/nope.png";
        Seed(path, null);

        var session = Session();
        session.OnAgentEvent("agent", AgentText("MEDIA:" + path));
        await Task.Delay(50);

        Assert.Null(session.History[0].ImageBytes);
    }

    // ---- failing closed -------------------------------------------------
    //
    // Restored after QA (CB-91) pointed out that rewriting this file onto the
    // cache seam had dropped both of them. They need no transport at all:
    // with nothing seeded and no gateway configured, FetchMediaAsync reaches
    // its own host/token guard and answers null, which is the same shape a
    // refusal takes. What they pin is that a failure leaves the turn alone
    // rather than throwing out of an async void or half-setting a picture.

    [Fact]
    public async Task AnUnservedPathLeavesTheTurnUntouchedRatherThanThrowing()
    {
        var session = Session();
        session.OnAgentEvent("agent", AgentText(
            "MEDIA:/Users/warrenthompson/.openclaw/media/never-seeded.png"));
        await Task.Delay(50);

        Assert.Null(session.History[0].ImageBytes);
        Assert.Null(session.History[0].ImageUrl);
        Assert.Contains("MEDIA:", session.History[0].Text);
    }

    [Fact]
    public async Task FetchLocalMediaReturnsNullRatherThanThrowingWhenNothingAnswers()
    {
        var bytes = await OpenClawSessions.FetchLocalMediaAsync(
            "/Users/warrenthompson/.openclaw/media/nothing-here.png", CancellationToken.None);

        Assert.Null(bytes);
    }
}
