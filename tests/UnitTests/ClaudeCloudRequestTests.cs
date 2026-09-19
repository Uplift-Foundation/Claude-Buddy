using System;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers the exact shape of the request the cloud arm sends.
//
// **This file used to pin six headers against claude.ai and every one of those
// assertions was wrong.** They were the headers claude.ai's own web client
// sends, asserted twice over — once off a built request and once against the
// constants — which made a confidently wrong fact look doubly confirmed. The
// host was the error, not the headers: claude.ai answers a non-browser client
// with a Cloudflare challenge whatever it is sent, and against
// api.anthropic.com only Authorization and anthropic-version do anything. Two
// independent copies of a wrong measurement are still one wrong measurement,
// which is worth remembering before adding a third.
//
// The request builder is still the single copy of this shape, and the probe
// still references the app rather than building its own: a diagnostic that
// assembled the request itself could answer differently from the app and be
// believed.
public class ClaudeCloudRequestTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

    private static string? Header(System.Net.Http.HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Fact]
    public void TheAuthorizationHeaderIsABearerTokenAndNothingElse()
    {
        using var request = CloudRequest.Build(Token, CloudRequest.SessionsPath);

        Assert.NotNull(request.Headers.Authorization);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
    }

    [Fact]
    public void TheVersionHeaderIsTheOtherHalfOfTheMeasuredSet()
    {
        using var request = CloudRequest.Build(Token, CloudRequest.SessionsPath);

        Assert.Equal("2023-06-01", Header(request, "anthropic-version"));
        Assert.Equal("anthropic-version", CloudRequest.VersionHeader);
        Assert.Equal("2023-06-01", CloudRequest.VersionValue);
    }

    // The four that were carried over from claude.ai and are not sent.
    //
    // A test for absence rather than for presence, because the failure being
    // guarded against is somebody re-adding one from the old findings doc — at
    // which point a header set that reads fine goes back to describing a host
    // this arm does not talk to.
    [Theory]
    [InlineData("anthropic-beta")]
    [InlineData("anthropic-client-feature")]
    [InlineData("anthropic-client-platform")]
    [InlineData("x-organization-uuid")]
    public void TheClaudeAiHeadersAreNotSent(string name)
    {
        using var request = CloudRequest.Build(Token, CloudRequest.SessionsPath);

        Assert.Null(Header(request, name));
    }

    // **No cookie, ever.** Ours rides the CLI's OAuth token and nothing else.
    // Sending both would make it impossible to say which one was accepted.
    [Fact]
    public void NoCookieIsSent()
    {
        using var request = CloudRequest.Build(Token, CloudRequest.SessionsPath);

        Assert.False(request.Headers.Contains("Cookie"));
        Assert.DoesNotContain(request.Headers,
            h => string.Equals(h.Key, "cookie", StringComparison.OrdinalIgnoreCase));
    }

    // And no body. The empty ByteArrayContent that used to be attached was there
    // to reproduce a browser's GET byte for byte against the wrong host.
    [Fact]
    public void NoBodyRidesAGet()
    {
        using var request = CloudRequest.Build(Token, CloudRequest.SessionsPath);

        Assert.Null(request.Content);
    }

    [Fact]
    public void ItIsAGetToTheAccountApiOverHttps()
    {
        using var request = CloudRequest.Build(Token, CloudRequest.SessionsPath);

        Assert.Equal(System.Net.Http.HttpMethod.Get, request.Method);
        Assert.NotNull(request.RequestUri);
        Assert.Equal("https", request.RequestUri!.Scheme);
        Assert.Equal("api.anthropic.com", request.RequestUri.Host);
        Assert.Equal("/v2/ccr-sessions", request.RequestUri.AbsolutePath);
    }

    [Fact]
    public void TheHostConstantIsTheAccountApiAndNotClaudeAi()
    {
        Assert.Equal("https://api.anthropic.com", CloudRequest.Host);
        Assert.DoesNotContain("claude.ai", CloudRequest.Host, StringComparison.Ordinal);
        Assert.Equal("/v2/ccr-sessions", CloudRequest.SessionsPath);
    }

    // --- the paths -----------------------------------------------------------

    [Fact]
    public void TheListingPathCarriesALimitAndNothingElse()
    {
        Assert.Equal("/v2/ccr-sessions?limit=100", CloudRequest.ListPath(100, null));
        Assert.Equal("/v2/ccr-sessions?limit=100", CloudRequest.ListPath(100, "   "));
    }

    [Fact]
    public void TheListingPathFollowsACursorWhenGivenOne()
    {
        Assert.Equal("/v2/ccr-sessions?limit=100&after_id=session_abc",
            CloudRequest.ListPath(100, "session_abc"));
    }

    // **Measured: `limit=200` is refused** with "must be greater than or equal to
    // 0 and less than 101". So a caller asking for more gets the ceiling rather
    // than a 400, which is the difference between a walk that is slower than it
    // could be and one that returns nothing at all.
    [Theory]
    [InlineData(101, 100)]
    [InlineData(200, 100)]
    [InlineData(int.MaxValue, 100)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(50, 50)]
    public void TheLimitIsClampedToWhatTheEndpointAccepts(int asked, int sent)
    {
        Assert.Equal($"/v2/ccr-sessions?limit={sent}", CloudRequest.ListPath(asked, null));
    }

    [Fact]
    public void TheMaximumPageSizeIsTheMeasuredCeiling()
    {
        Assert.Equal(100, CloudRequest.MaxPageSize);
    }

    [Fact]
    public void ASingleSessionPathHangsOffTheCollection()
    {
        Assert.Equal("/v2/ccr-sessions/session_01ABC", CloudRequest.SessionPath("session_01ABC"));
    }

    [Fact]
    public void AnEventsPathPagesTheSameWay()
    {
        Assert.Equal("/v2/ccr-sessions/session_01ABC/events?limit=100",
            CloudRequest.EventsPath("session_01ABC", 100, null));

        Assert.Equal("/v2/ccr-sessions/session_01ABC/events?limit=100&after_id=evt_9",
            CloudRequest.EventsPath("session_01ABC", 100, "evt_9"));
    }

    // An id arrives from a payload nobody here owns and ends up in a path. Escaped
    // rather than trusted — a path is the one place an external string turns into
    // a different request.
    [Fact]
    public void AnIdWithPathCharactersInItIsEscaped()
    {
        Assert.Equal("/v2/ccr-sessions/session_a%2F..%2Fadmin",
            CloudRequest.SessionPath("session_a/../admin"));

        Assert.Equal("/v2/ccr-sessions/session_a%3Fx%3D1/events?limit=100",
            CloudRequest.EventsPath("session_a?x=1", 100, null));
    }

    [Fact]
    public void ACursorWithQueryCharactersInItIsEscaped()
    {
        Assert.Contains("after_id=session_a%26limit%3D1",
            CloudRequest.ListPath(100, "session_a&limit=1"), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePathIsWhateverTheCallerAsksFor()
    {
        using var request = CloudRequest.Build(Token, "/v2/ccr-sessions?limit=1");

        Assert.Equal("https://api.anthropic.com/v2/ccr-sessions?limit=1",
            request.RequestUri!.ToString());
    }

    // --- the cadences --------------------------------------------------------

    // Not cadences, placeholders. That they are positive and finite is all a test
    // can honestly say about numbers nobody has measured; the comment on the
    // constants names the three questions that would settle them.
    [Fact]
    public void BothIntervalsArePositiveUnmeasuredPlaceholders()
    {
        Assert.True(CloudRequest.UnmeasuredWalkInterval > TimeSpan.Zero);
        Assert.True(CloudRequest.UnmeasuredFirstPageInterval > TimeSpan.Zero);
    }

    // The deep walk is the expensive one and the first-page poll is the cheap one,
    // so the walk being the rarer of the two is the one relationship between them
    // that is a decision rather than a guess.
    [Fact]
    public void TheWalkIsRarerThanTheFirstPagePoll()
    {
        Assert.True(CloudRequest.UnmeasuredWalkInterval > CloudRequest.UnmeasuredFirstPageInterval);
    }

    [Fact]
    public void ThePageCapIsPositive()
    {
        Assert.True(CloudRequest.MaxPagesPerWalk > 0);
    }

    // The constant naming the credential store, kept beside the request tests
    // because it is the other measured-from-a-real-machine string in this arm.
    [Fact]
    public void TheKeychainServiceIsTheOneTheCliWritesUnder()
    {
        Assert.Equal("Claude Code-credentials", ClaudeCliCredentials.KeychainService);
    }
}
