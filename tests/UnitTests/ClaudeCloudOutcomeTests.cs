using System;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers turning an HTTP answer into a verdict.
//
// The asymmetry in the two 401 cases is the interesting part and it is measured
// rather than reasoned: CB-164 sent five variants at this endpoint, and only a
// well-formed-but-invalid Bearer token got "OAuth access token is invalid." —
// no credential, an empty Bearer and a bogus x-api-key all fell through to a
// generic "Authentication failed" with a real request_id. The other three are
// the negative controls that make it a measurement.
//
// For us the distinction decides what to tell the user: "the login we read was
// refused, sign in again" versus "we presented something this endpoint did not
// recognise as a credential at all", which is our bug.
public class ClaudeCloudOutcomeTests
{
    private const string RefusedBody =
        """{"type":"error","error":{"type":"authentication_error","message":"OAuth access token is invalid."},"request_id":null}""";

    private const string GenericBody =
        """{"type":"error","error":{"type":"authentication_error","message":"Authentication failed"},"request_id":"req_abc123"}""";

    [Fact]
    public void ATwoHundredIsOk()
    {
        var outcome = CloudOutcomes.OutcomeFor(200, """{"data":[],"resume_token":"t"}""");

        Assert.Equal(CloudOutcomeKind.Ok, outcome.Kind);
        Assert.Equal(200, outcome.Status);
    }

    [Theory]
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(299)]
    public void AnyTwoHundredIsOk(int status)
    {
        Assert.Equal(CloudOutcomeKind.Ok, CloudOutcomes.OutcomeFor(status, null).Kind);
    }

    [Fact]
    public void TheOauthSpecificFourOhOneMeansOurTokenWasRefused()
    {
        var outcome = CloudOutcomes.OutcomeFor(401, RefusedBody);

        Assert.Equal(CloudOutcomeKind.TokenRefused, outcome.Kind);
        Assert.Equal(401, outcome.Status);
    }

    [Fact]
    public void TheGenericFourOhOneMeansNoCredentialWasRecognisedAtAll()
    {
        var outcome = CloudOutcomes.OutcomeFor(401, GenericBody);

        Assert.Equal(CloudOutcomeKind.AuthFailed, outcome.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something else entirely")]
    public void AFourOhOneWeCannotReadFallsBackToTheGenericReading(string? body)
    {
        Assert.Equal(CloudOutcomeKind.AuthFailed, CloudOutcomes.OutcomeFor(401, body).Kind);
    }

    // The marker is matched ordinally and in full, including the trailing stop,
    // because that is how it was measured. A looser match on "OAuth" would start
    // claiming TokenRefused for messages nobody has seen.
    [Fact]
    public void TheMarkerIsTheWholeMeasuredSentence()
    {
        Assert.Equal("OAuth access token is invalid.", CloudOutcomes.TokenRefusedMarker);
        Assert.Equal(CloudOutcomeKind.AuthFailed,
            CloudOutcomes.OutcomeFor(401, "OAuth access token is invalid").Kind);
    }

    [Fact]
    public void AFourHundredMeansTheShapeMoved()
    {
        var outcome = CloudOutcomes.OutcomeFor(400, """{"error":"missing header"}""");

        Assert.Equal(CloudOutcomeKind.ShapeChanged, outcome.Kind);
        Assert.Equal(400, outcome.Status);
    }

    [Fact]
    public void AFourOhThreeIsBlocked()
    {
        Assert.Equal(CloudOutcomeKind.Blocked, CloudOutcomes.OutcomeFor(403, null).Kind);
    }

    [Fact]
    public void AFourTwoNineIsRateLimitedAndKeepsWhateverRetryAfterSaid()
    {
        var outcome = CloudOutcomes.OutcomeFor(429, null, TimeSpan.FromSeconds(90));

        Assert.Equal(CloudOutcomeKind.RateLimited, outcome.Kind);
        Assert.Equal(TimeSpan.FromSeconds(90), outcome.RetryAfter);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(418)]
    [InlineData(0)]
    public void EverythingElseIsUnavailableAndRetryable(int status)
    {
        var outcome = CloudOutcomes.OutcomeFor(status, null);

        Assert.Equal(CloudOutcomeKind.Unavailable, outcome.Kind);
        Assert.Equal(status, outcome.Status);
    }

    // Every verdict says something, because the user-facing half of "degrade
    // quietly" is a legible reason rather than silence.
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public void EveryFailureCarriesWording(int status)
    {
        Assert.False(string.IsNullOrWhiteSpace(CloudOutcomes.OutcomeFor(status, null).Detail));
    }

    // The two carrier records. They hold no logic, and they are asserted on here
    // rather than left to the excluded HttpCloudApi because that is the only
    // place they would otherwise be constructed — which would leave the shape a
    // future arm depends on verified nowhere at all.
    [Fact]
    public void AResultCarriesItsVerdictAndItsBody()
    {
        var result = new CloudApiResult(CloudOutcomes.OutcomeFor(200, null), """{"data":[]}""");

        Assert.Equal(CloudOutcomeKind.Ok, result.Outcome.Kind);
        Assert.Equal("""{"data":[]}""", result.Body);
    }

    // Constructed at the call site and never stored — see the custody rules. The
    // test asserts the three fields survive the trip and nothing else, because
    // there is nothing else it should do.
    [Fact]
    public void ARequestContextCarriesExactlyWhatOneCallNeeds()
    {
        var context = new CloudRequestContext("token", "org", CloudRequest.SessionsPath);

        Assert.Equal("token", context.AccessToken);
        Assert.Equal("org", context.OrganizationUuid);
        Assert.Equal(CloudRequest.SessionsPath, context.Path);
    }

    // And none of it is the token. The same canary rule as the credential tests:
    // a body that happens to contain one must not be echoed into a verdict.
    [Fact]
    public void NoVerdictEchoesAResponseBody()
    {
        const string canary = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

        foreach (var status in new[] { 200, 400, 401, 403, 429, 500 })
        {
            var outcome = CloudOutcomes.OutcomeFor(status, $"leaked {canary} here");
            Assert.DoesNotContain(canary, outcome.Detail ?? "", StringComparison.Ordinal);
        }
    }
}
