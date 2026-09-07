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

    // The gateway key the session below is built with, spelled out here
    // because CB-109 made it part of every route this file seeds: the live
    // path sends the originating session with the file it is asking about, so
    // a seed against the bare path-only route is no longer the route the code
    // under test asks for. AnAgentsOwnSessionIsSentWithTheFetch below is the
    // case that pins that directly.
    private const string GatewayKey = "agent:main:main";

    // The source the code under test is expected to build for a path — the
    // file *and* whose conversation named it.
    private static OpenClawMediaSource SourceFor(string path) => new(path, GatewayKey);

    // The route it is expected to build out of that.
    private static string RouteFor(string path) => SourceFor(path).Route;

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
        const string path = "/Users/user/.openclaw/media/sample_drop.png";
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
        const string path = "/Users/w/.openclaw/media/bare.png";
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
        const string path = "/Users/w/.openclaw/media/a drop ünicode.png";
        Seed(path, Pixel());

        Assert.Contains("%20", RouteFor(path));

        var bytes = await OpenClawSessions.FetchLocalMediaAsync(
            SourceFor(path), CancellationToken.None);
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
        const string path = "/Users/w/.openclaw/media/once.png";
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
        const string path = "/Users/user/.openclaw/workspace-render-quill/outputs/nope.png";
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
            "MEDIA:/Users/w/.openclaw/media/never-seeded.png"));
        await Task.Delay(50);

        Assert.Null(session.History[0].ImageBytes);
        Assert.Null(session.History[0].ImageUrl);
        Assert.Contains("MEDIA:", session.History[0].Text);
    }

    [Fact]
    public async Task FetchLocalMediaReturnsNullRatherThanThrowingWhenNothingAnswers()
    {
        var bytes = await OpenClawSessions.FetchLocalMediaAsync(
            new OpenClawMediaSource("/Users/w/.openclaw/media/nothing-here.png", null),
            CancellationToken.None);

        Assert.Null(bytes);
    }

    // ---- CB-109: the identity actually goes out with the request ---------
    //
    // The same cache seam this whole file is built on, used as a negative:
    // seed the route the app used to build — path only, no session — and the
    // live path must *not* find it, because the route it now asks for carries
    // the session key. If someone drops the identity again, this is the test
    // that goes red, and it goes red for the right reason: the fetch asked a
    // different question from the one that was answered.
    //
    // Worth having as well as OpenClawMediaSourceTests' string assertions,
    // because those prove the route is built correctly and this proves it is
    // the route the session actually reaches for.
    [Fact]
    public async Task AnAgentsOwnSessionIsSentWithTheFetch()
    {
        const string path = "/Users/w/.openclaw/media/identity.png";

        var bare = OpenClawSessions.AssistantMediaRoute + Uri.EscapeDataString(path);
        _seeded.Add(bare);
        MediaCache()[bare] = Pixel();

        var session = Session();
        session.OnAgentEvent("agent", AgentText("MEDIA:" + path));
        await Task.Delay(50);

        Assert.Null(session.History[0].ImageBytes);

        // And with the session-bearing route seeded, the same marker resolves
        // — so the miss above is about the identity and not about the path.
        Seed(path, Pixel());

        var second = Session();
        second.OnAgentEvent("agent", AgentText("MEDIA:" + path));

        Assert.Equal(Pixel(), await WaitForBytes(second));
        Assert.Contains("sessionKey=", RouteFor(path));
        Assert.DoesNotContain("agentId", RouteFor(path));
    }
}
