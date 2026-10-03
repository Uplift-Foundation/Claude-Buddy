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

    // --- the 403, and the misdiagnosis it used to carry ----------------------

    // Both are Blocked and neither retries. What changed is what they *say*: the
    // detail used to read "this account may not list cloud sessions" for every
    // 403, which is a confident claim about an account's permissions — and what
    // had actually happened was a Cloudflare challenge in front of claude.ai,
    // refusing a non-browser client and saying nothing about the account at all.
    // A wrong explanation that reads as a finding is worse than no explanation,
    // because it ends the inquiry. These tests pin the distinction.
    [Fact]
    public void AFourOhThreeIsBlockedEitherWay()
    {
        Assert.Equal(CloudOutcomeKind.Blocked, CloudOutcomes.OutcomeFor(403, null).Kind);
        Assert.Equal(CloudOutcomeKind.Blocked,
            CloudOutcomes.OutcomeFor(403, PermissionBody).Kind);
    }

    // A real API refusal: a JSON error object with a request_id. Something
    // assigned it an id, so something in the API saw it.
    private const string PermissionBody =
        """{"type":"error","error":{"type":"permission_error","message":"not allowed"},"request_id":"req_abc123"}""";

    [Fact]
    public void AJsonPermissionErrorWithARequestIdIsTheApiRefusingUs()
    {
        Assert.False(CloudOutcomes.LooksLikeEdgeBlock(PermissionBody));
        Assert.Equal(CloudOutcomes.AccountBlockedDetail,
            CloudOutcomes.OutcomeFor(403, PermissionBody).Detail);
    }

    // An HTML challenge page. Not JSON at all, which is nobody's API error format.
    [Fact]
    public void AnHtmlBodyIsAnEdgeBlock()
    {
        const string html = "<!DOCTYPE html><html><head><title>Just a moment…</title></head></html>";

        Assert.True(CloudOutcomes.LooksLikeEdgeBlock(html));
        Assert.Equal(CloudOutcomes.EdgeBlockedDetail, CloudOutcomes.OutcomeFor(403, html).Detail);
    }

    // The header Cloudflare sets. It is a header rather than a body, which is why
    // LooksLikeEdgeBlock takes it as an argument instead of sniffing for it.
    [Fact]
    public void TheCfMitigatedHeaderIsAnEdgeBlockWhateverTheBodySays()
    {
        Assert.True(CloudOutcomes.LooksLikeEdgeBlock(PermissionBody, cfMitigated: true));
        Assert.Equal(CloudOutcomes.EdgeBlockedDetail,
            CloudOutcomes.OutcomeFor(403, PermissionBody, null, cfMitigated: true).Detail);
    }

    // Shaped like an error and carrying no request_id. Every real refusal measured
    // on this API carried one, so a body without one never reached anything that
    // assigns them.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"error":"forbidden"}""")]
    [InlineData("""{"error":{"type":"permission_error"},"request_id":null}""")]
    [InlineData("""{"error":{"type":"permission_error"},"request_id":""}""")]
    [InlineData("""["not","an","object"]""")]
    public void AnythingWithoutARealRequestIdReadsAsAnEdgeBlock(string? body)
    {
        Assert.True(CloudOutcomes.LooksLikeEdgeBlock(body));
        Assert.Equal(CloudOutcomes.EdgeBlockedDetail, CloudOutcomes.OutcomeFor(403, body).Detail);
    }

    // The wording no longer claims anything about the account when it does not
    // know anything about the account. Asserted as an absence because that is the
    // regression worth catching.
    [Fact]
    public void AnEdgeBlockNoLongerBlamesTheAccount()
    {
        var detail = CloudOutcomes.OutcomeFor(403, "<html></html>").Detail ?? "";

        Assert.DoesNotContain("account", detail, StringComparison.OrdinalIgnoreCase);
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
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(413)]
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

    // Constructed at the call site and never stored — see the custody rules.
    //
    // **Two fields, not three.** `OrganizationUuid` was here because
    // `x-organization-uuid` was believed mandatory; it is claude.ai's header and
    // api.anthropic.com ignores it. The field was removed rather than left unused,
    // so re-adding the header is a compile error rather than a quiet widening.
    [Fact]
    public void ARequestContextCarriesExactlyWhatOneCallNeeds()
    {
        var context = new CloudRequestContext("token", CloudRequest.SessionsPath);

        Assert.Equal("token", context.AccessToken);
        Assert.Equal(CloudRequest.SessionsPath, context.Path);
    }

    // And none of it is the token. The same canary rule as the credential tests:
    // a body that happens to contain one must not be echoed into a verdict.
    [Fact]
    public void NoVerdictEchoesAResponseBody()
    {
        const string canary = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

        foreach (var status in new[] { 200, 400, 401, 403, 404, 409, 413, 429, 500 })
        {
            var outcome = CloudOutcomes.OutcomeFor(status, $"leaked {canary} here");
            Assert.DoesNotContain(canary, outcome.Detail ?? "", StringComparison.Ordinal);
        }
    }

    // --- the write refusals (CB-199) -------------------------------------------

    // **Measured by CB-225**, on a send to a session archived moments
    // earlier: 409, error type `session_not_active`. The kind follows the
    // status alone, so CB-199's binary-derived `session_inactive` body maps
    // the same — pinned too, because a body the API never sends must not be
    // what this test happens to depend on.
    [Theory]
    [InlineData("""{"error":{"message":"Session session_01FixtureOnly is not active","type":"session_not_active"},"request_id":"req_1","type":"error"}""")]
    [InlineData("""{"type":"error","error":{"type":"session_inactive"},"request_id":"req_1"}""")]
    public void AFourOhNineIsASessionNoLongerTakingInput(string body)
    {
        var outcome = CloudOutcomes.OutcomeFor(409, body);

        Assert.Equal(CloudOutcomeKind.SessionInactive, outcome.Kind);
        Assert.Equal(409, outcome.Status);
        Assert.Equal(CloudOutcomes.SessionInactiveDetail, outcome.Detail);
        Assert.Contains("no longer active", outcome.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFourOhThirteenIsAMessageTooLarge()
    {
        var outcome = CloudOutcomes.OutcomeFor(413, null);

        Assert.Equal(CloudOutcomeKind.TooLarge, outcome.Kind);
        Assert.Equal(413, outcome.Status);
        Assert.Equal(CloudOutcomes.TooLargeDetail, outcome.Detail);
    }

    // The 403 split stays two-way on the write route: no device-bound body was
    // ever observed, so there is no third arm to assert and none is invented.
    [Fact]
    public void AFourOhThreeOnAWriteStillSplitsEdgeFromAccount()
    {
        Assert.Equal(CloudOutcomes.EdgeBlockedDetail,
            CloudOutcomes.OutcomeFor(403, "<html>challenge</html>").Detail);
        Assert.Equal(CloudOutcomes.AccountBlockedDetail,
            CloudOutcomes.OutcomeFor(403,
                """{"type":"error","error":{"type":"permission_error","message":"x"},"request_id":"req_2"}""").Detail);
    }

    // Both are about one request, so on a *read* they keep the retryable
    // behaviour a 409 or 413 had before CB-199 — the roster arm halts on any
    // outcome Backoff says to stop for, and a stray 409 on a direct session
    // read must not start doing that.
    [Theory]
    [InlineData(409)]
    [InlineData(413)]
    public void TheWriteRefusalsDoNotHaltTheRosterArm(int status)
    {
        var wait = Backoff.Next(CloudOutcomes.OutcomeFor(status, null), null);

        Assert.Equal(Backoff.UnavailableFloor, wait);
    }

    // --- a deleted session (CB-199) --------------------------------------------

    // **Measured** against a deleted cloud session: this body, 404, on
    // `GET /v1/code/sessions/{id}`. Ids invented.
    private const string GoneBody =
        """{"type":"error","error":{"type":"not_found_error","message":"Session session_01Invented not found"},"request_id":"req_invented"}""";

    [Fact]
    public void AFourOhFourIsASessionThatNoLongerExists()
    {
        var outcome = CloudOutcomes.OutcomeFor(404, GoneBody);

        Assert.Equal(CloudOutcomeKind.SessionGone, outcome.Kind);
        Assert.Equal(404, outcome.Status);
        Assert.Equal("this cloud session no longer exists", outcome.Detail);
        Assert.Equal(CloudOutcomes.SessionGoneDetail, outcome.Detail);
    }

    // Gone and ended are different answers: one measured, one the CLI's
    // reading. Keeping them apart is what lets the panel say which.
    [Fact]
    public void GoneIsNotInactive()
    {
        Assert.NotEqual(CloudOutcomes.OutcomeFor(404, GoneBody).Kind,
            CloudOutcomes.OutcomeFor(409, null).Kind);
    }

    // It used to be Unavailable and backed off forever. A deleted session is
    // not coming back, so waiting is a stop, like a refused token.
    [Fact]
    public void BackoffStopsForAGoneSession()
    {
        Assert.Null(Backoff.Next(CloudOutcomes.OutcomeFor(404, GoneBody), null));
        Assert.Null(Backoff.Next(CloudOutcomes.OutcomeFor(404, GoneBody), TimeSpan.FromSeconds(8)));
    }
}
