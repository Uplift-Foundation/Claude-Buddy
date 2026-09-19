using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers the exact shape of the request the cloud arm sends.
//
// **This is a test about six header values and it is the most load-bearing test
// in the cloud arm.** CB-164 measured that claude.ai's /v1/code/sessions returns
// 400 without them — a bare request is refused, which is also the negative
// control establishing that the 200 the browser gets is a real answer rather
// than an open endpoint. So a typo in any one of them is not a subtle
// degradation; it is the whole feature failing, against an undocumented API
// where the failure looks like the API having changed.
//
// It is also why the probe references the app rather than building its own
// request: there is exactly one copy of this header set and this asserts on it.
public class ClaudeCloudRequestTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";
    private const string Org = "11111111-2222-3333-4444-555555555555";

    private static string? Header(System.Net.Http.HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Fact]
    public void TheAuthorizationHeaderIsABearerTokenAndNothingElse()
    {
        using var request = CloudRequest.Build(Token, Org, CloudRequest.SessionsPath);

        Assert.NotNull(request.Headers.Authorization);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
    }

    [Fact]
    public void TheSixRequiredHeadersAreExactlyWhatWasMeasured()
    {
        using var request = CloudRequest.Build(Token, Org, CloudRequest.SessionsPath);

        Assert.Equal("web_claude_ai", Header(request, "anthropic-client-platform"));
        Assert.Equal("2023-06-01", Header(request, "anthropic-version"));
        Assert.Equal("ccr-byoc-2025-07-29", Header(request, "anthropic-beta"));
        Assert.Equal("ccr", Header(request, "anthropic-client-feature"));
        Assert.Equal(Org, Header(request, "x-organization-uuid"));

        // content-type rides the (empty) body, because .NET will not accept a
        // content header on the request's general collection. No charset
        // parameter — the browser sends a bare application/json and an empty
        // ByteArrayContent is what reproduces that exactly.
        Assert.NotNull(request.Content);
        Assert.Equal("application/json", request.Content!.Headers.ContentType?.ToString());
    }

    // The constants are the single source of truth, and the literals above are a
    // second, independent statement of the same thing. If a refactor changes one,
    // the test above fails; this one guards the other direction — that the
    // constants the probe and the app both read are the values the literals name.
    [Fact]
    public void TheHeaderConstantsAgreeWithTheLiteralsAbove()
    {
        Assert.Equal("anthropic-client-platform", CloudRequest.ClientPlatformHeader);
        Assert.Equal("web_claude_ai", CloudRequest.ClientPlatformValue);
        Assert.Equal("anthropic-version", CloudRequest.VersionHeader);
        Assert.Equal("2023-06-01", CloudRequest.VersionValue);
        Assert.Equal("anthropic-beta", CloudRequest.BetaHeader);
        Assert.Equal("ccr-byoc-2025-07-29", CloudRequest.BetaValue);
        Assert.Equal("anthropic-client-feature", CloudRequest.ClientFeatureHeader);
        Assert.Equal("ccr", CloudRequest.ClientFeatureValue);
        Assert.Equal("x-organization-uuid", CloudRequest.OrganizationHeader);
    }

    // **No cookie, ever.** The browser observation this endpoint was discovered
    // through rode a session cookie; ours rides the CLI's OAuth token. Sending
    // both would make it impossible to say which one was accepted, and the whole
    // point of the probe is to answer exactly that.
    [Fact]
    public void NoCookieIsSent()
    {
        using var request = CloudRequest.Build(Token, Org, CloudRequest.SessionsPath);

        Assert.False(request.Headers.Contains("Cookie"));
        Assert.DoesNotContain(request.Headers,
            h => string.Equals(h.Key, "cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ItIsAGetToClaudeAiOverHttps()
    {
        using var request = CloudRequest.Build(Token, Org, CloudRequest.SessionsPath);

        Assert.Equal(System.Net.Http.HttpMethod.Get, request.Method);
        Assert.NotNull(request.RequestUri);
        Assert.Equal("https", request.RequestUri!.Scheme);
        Assert.Equal("claude.ai", request.RequestUri.Host);
        Assert.Equal("/v1/code/sessions", request.RequestUri.AbsolutePath);
    }

    // Both status values, because the roster the web client shows is their union
    // and a Buddy showing fewer sessions than the page a click leads to would be
    // quietly wrong.
    [Fact]
    public void TheListingPathAsksForActiveAndPausedSessions()
    {
        using var request = CloudRequest.Build(Token, Org, CloudRequest.SessionsPath);

        var query = request.RequestUri!.Query;
        Assert.Contains("statuses=active", query, StringComparison.Ordinal);
        Assert.Contains("statuses=paused", query, StringComparison.Ordinal);
        Assert.Contains("limit=50", query, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePathIsWhateverTheCallerAsksFor()
    {
        using var request = CloudRequest.Build(Token, Org, "/v1/code/sessions?limit=1");

        Assert.Equal("https://claude.ai/v1/code/sessions?limit=1",
            request.RequestUri!.ToString());
    }

    // Not a cadence, a placeholder. Asserting that it is positive and finite is
    // all a test can honestly say about a number nobody has measured; the comment
    // on the constant says the rest.
    [Fact]
    public void ThePollIntervalIsAPositiveUnmeasuredPlaceholder()
    {
        Assert.True(CloudRequest.UnmeasuredPollInterval > TimeSpan.Zero);
    }

    // The constant naming the credential store, kept beside the request tests
    // because it is the other measured-from-a-real-machine string in this arm.
    [Fact]
    public void TheKeychainServiceIsTheOneTheCliWritesUnder()
    {
        Assert.Equal("Claude Code-credentials", ClaudeCliCredentials.KeychainService);
    }
}
