using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-225: the rules for archiving and deleting a cloud session, one case per
// outcome. The answers these cases stand in for were measured on throwaway
// sessions and are recorded in docs/claude-cloud-findings.md; the captured,
// scrubbed bodies themselves are exercised in
// tests/IntegrationTests/ClaudeCloudLifecyclePayloadTests.
public class ClaudeCloudLifecycleTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";
    private const string Id = "session_01FixtureOnly";

    private const string NotFoundJson =
        """{"error":{"message":"Session session_01FixtureOnly not found","type":"not_found_error"},"request_id":"req_1","type":"error"}""";

    // The router's own answer to a path it has no handler for. Measured.
    private const string RouterNotFound = "404 page not found";

    private static CloudApiResult Answer(int status, string? body) =>
        new(CloudOutcomes.OutcomeFor(status, body), body);

    // --- the request ------------------------------------------------------------

    [Fact]
    public void ArchiveIsAPostOfAnEmptyObjectToTheArchiveRoute()
    {
        var request = ClaudeCloudLifecycle.RequestFor(CloudLifecycleAction.Archive, Id)!.Value;

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/code/sessions/" + Id + "/archive", request.Path);
        Assert.Equal("{}", request.Body);
    }

    [Fact]
    public void DeleteIsABodilessDeleteOfTheSessionItself()
    {
        var request = ClaudeCloudLifecycle.RequestFor(CloudLifecycleAction.Delete, Id)!.Value;

        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/v1/code/sessions/" + Id, request.Path);
        Assert.Null(request.Body);
    }

    // A malformed id never becomes a write — refused locally, not escaped.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("session_a/../../v1/organizations/me")]
    [InlineData("not-a-session")]
    public void AMalformedIdBuildsNoRequestForEitherVerb(string? id)
    {
        Assert.Null(ClaudeCloudLifecycle.RequestFor(CloudLifecycleAction.Archive, id));
        Assert.Null(ClaudeCloudLifecycle.RequestFor(CloudLifecycleAction.Delete, id));
        Assert.Null(CloudRequest.ArchivePath(id));
    }

    // Built through the shipped builder, the archive carries its body as JSON
    // and the delete carries none — what the probe sent and was answered for.
    [Fact]
    public void TheBuiltRequestsCarryExactlyTheMeasuredHeaders()
    {
        var archive = ClaudeCloudLifecycle.RequestFor(CloudLifecycleAction.Archive, Id)!.Value;
        using var post = CloudRequest.Build(Token, archive.Path, archive.Method, archive.Body);
        Assert.Equal("application/json", post.Content!.Headers.ContentType!.MediaType);
        Assert.True(post.Headers.Contains(CloudRequest.VersionHeader));

        var delete = ClaudeCloudLifecycle.RequestFor(CloudLifecycleAction.Delete, Id)!.Value;
        using var del = CloudRequest.Build(Token, delete.Path, delete.Method, delete.Body);
        Assert.Null(del.Content);
        Assert.True(del.Headers.Contains(CloudRequest.VersionHeader));
    }

    // --- which 404 is which -----------------------------------------------------

    [Fact]
    public void OnlyTheHandlersJsonErrorIsSessionNotFound()
    {
        Assert.True(ClaudeCloudLifecycle.IsSessionNotFound(NotFoundJson));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(RouterNotFound)]
    [InlineData("""{"error":{"type":"invalid_request_error"},"type":"error"}""")]
    [InlineData("""[{"error":{"type":"not_found_error"}}]""")]
    [InlineData("""{"error":"not_found_error"}""")]
    [InlineData("""{"error":{"type":404}}""")]
    [InlineData("""{"type":"error"}""")]
    public void AnythingElseIsNot(string? body)
    {
        Assert.False(ClaudeCloudLifecycle.IsSessionNotFound(body));
    }

    // --- the action's own answer ------------------------------------------------

    [Fact]
    public void A200IsDone()
    {
        var result = ClaudeCloudLifecycle.VerdictFor(CloudOutcomes.OutcomeFor(200, "{}"), "{}");

        Assert.Equal(CloudLifecycleVerdict.Done, result.Verdict);
        Assert.True(result.Succeeded);
    }

    // **The case this file exists for.** OutcomeFor reads every 404 as
    // SessionGone; the router's plain-text 404 is a missing route, and must
    // never read on the row as the session having been archived or deleted.
    [Fact]
    public void ARouterFourOhFourIsNotSuccessEvenThoughOutcomeForCallsItGone()
    {
        var outcome = CloudOutcomes.OutcomeFor(404, RouterNotFound);
        Assert.Equal(CloudOutcomeKind.SessionGone, outcome.Kind);

        var result = ClaudeCloudLifecycle.VerdictFor(outcome, RouterNotFound);

        Assert.Equal(CloudLifecycleVerdict.Refused, result.Verdict);
        Assert.False(result.Succeeded);
        Assert.Equal(ClaudeCloudLifecycle.RouteNotFoundDetail, result.Detail);
    }

    [Fact]
    public void AHandlerFourOhFourIsReportedAsNotFoundAndStillNotSuccess()
    {
        var result = ClaudeCloudLifecycle.VerdictFor(CloudOutcomes.OutcomeFor(404, NotFoundJson), NotFoundJson);

        Assert.Equal(CloudLifecycleVerdict.Refused, result.Verdict);
        Assert.Equal(ClaudeCloudLifecycle.SessionNotFoundDetail, result.Detail);
    }

    // Every other refusal is worded by the existing outcome arms, which the
    // measurement showed already answer these routes the same as a send.
    [Theory]
    [InlineData(400, """{"error":{"message":"anthropic-version: header is required","type":"invalid_request_error"},"request_id":"r","type":"error"}""")]
    [InlineData(401, """{"type":"error","error":{"type":"authentication_error","message":"OAuth access token is invalid."},"request_id":null}""")]
    [InlineData(401, """{"type":"error","error":{"type":"authentication_error","message":"Authentication failed"},"request_id":"r"}""")]
    [InlineData(403, """{"type":"error","error":{"type":"permission_error"},"request_id":"r"}""")]
    [InlineData(409, """{"error":{"message":"Session x is not active","type":"session_not_active"},"request_id":"r","type":"error"}""")]
    [InlineData(429, "")]
    [InlineData(500, "")]
    public void EveryOtherStatusIsARefusalInTheOutcomesOwnWords(int status, string body)
    {
        var outcome = CloudOutcomes.OutcomeFor(status, body);
        var result = ClaudeCloudLifecycle.VerdictFor(outcome, body);

        Assert.Equal(CloudLifecycleVerdict.Refused, result.Verdict);
        Assert.Equal(outcome.Detail, result.Detail);
        Assert.False(string.IsNullOrEmpty(result.Detail));
    }

    // --- the read that confirms a delete -----------------------------------------

    [Fact]
    public void TheHandlersFourOhFourConfirmsTheDelete()
    {
        var result = ClaudeCloudLifecycle.ConfirmationFor(CloudOutcomes.OutcomeFor(404, NotFoundJson), NotFoundJson);
        Assert.Equal(CloudLifecycleVerdict.Done, result.Verdict);
    }

    [Fact]
    public void TheSessionStillAnsweringIsARefusalWhateverTheDeleteSaid()
    {
        var result = ClaudeCloudLifecycle.ConfirmationFor(CloudOutcomes.OutcomeFor(200, "{}"), "{}");

        Assert.Equal(CloudLifecycleVerdict.Refused, result.Verdict);
        Assert.Equal(ClaudeCloudLifecycle.StillThereDetail, result.Detail);
    }

    [Theory]
    [InlineData(404, RouterNotFound)]
    [InlineData(500, "")]
    [InlineData(429, "")]
    public void AConfirmationThatCannotBeReadLeavesTheDeleteStandingUnconfirmed(int status, string body)
    {
        var outcome = CloudOutcomes.OutcomeFor(status, body);
        var result = ClaudeCloudLifecycle.ConfirmationFor(outcome, body);

        Assert.Equal(CloudLifecycleVerdict.DoneUnconfirmed, result.Verdict);
        Assert.True(result.Succeeded);
        Assert.Equal(outcome.Detail, result.Detail);
    }

    // A transport failure carries no status and may carry no detail; the row
    // still says something rather than "couldn't confirm: ".
    [Fact]
    public void AConfirmationWithNoDetailSaysWhatTheStatusWas()
    {
        var result = ClaudeCloudLifecycle.ConfirmationFor(
            new CloudOutcome(CloudOutcomeKind.Unavailable, 0), null);

        Assert.Equal(CloudLifecycleVerdict.DoneUnconfirmed, result.Verdict);
        Assert.Equal("the endpoint answered 0", result.Detail);
    }

    // --- the exchange -----------------------------------------------------------

    private sealed class FakeApi : ICloudApi
    {
        private readonly Queue<CloudApiResult> _answers;

        internal FakeApi(params CloudApiResult[] answers) => _answers = new Queue<CloudApiResult>(answers);

        internal List<CloudRequestContext> Requests { get; } = new();

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            Requests.Add(context);
            return Task.FromResult(_answers.Dequeue());
        }
    }

    private sealed class FakeCredentials : ICloudCredentialSource
    {
        internal CredentialRead Reading { get; set; } =
            new(CredentialOutcome.Found, Token, null, "a credential is present");

        internal int Reads { get; private set; }

        public string? Stamp() => "stamp-1";

        public CredentialRead Read()
        {
            Reads++;
            return Reading;
        }
    }

    [Fact]
    public async Task ArchiveSendsOneRequestWithTheOwnersTokenAndAsksNothingElse()
    {
        var api = new FakeApi(Answer(200, """{"session":{"status":"archived"}}"""));

        var result = await ClaudeCloudLifecycle.RunAsync(api, new FakeCredentials(),
            CloudLifecycleAction.Archive, Id, CancellationToken.None);

        Assert.Equal(CloudLifecycleVerdict.Done, result.Verdict);
        var request = Assert.Single(api.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/code/sessions/" + Id + "/archive", request.Path);
        Assert.Equal("{}", request.Body);
        Assert.Equal(Token, request.AccessToken);
    }

    [Fact]
    public async Task DeleteIsConfirmedByAReadOfTheSameSession()
    {
        var api = new FakeApi(Answer(200, "{}"), Answer(404, NotFoundJson));

        var result = await ClaudeCloudLifecycle.RunAsync(api, new FakeCredentials(),
            CloudLifecycleAction.Delete, Id, CancellationToken.None);

        Assert.Equal(CloudLifecycleVerdict.Done, result.Verdict);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(HttpMethod.Delete, api.Requests[0].Method);
        Assert.Null(api.Requests[1].Method);
        Assert.Equal("/v1/code/sessions/" + Id, api.Requests[1].Path);
        Assert.All(api.Requests, r => Assert.Equal(Token, r.AccessToken));
    }

    [Fact]
    public async Task ADeleteTheReadContradictsIsARefusal()
    {
        var api = new FakeApi(Answer(200, "{}"), Answer(200, """{"id":"session_01FixtureOnly"}"""));

        var result = await ClaudeCloudLifecycle.RunAsync(api, new FakeCredentials(),
            CloudLifecycleAction.Delete, Id, CancellationToken.None);

        Assert.Equal(CloudLifecycleVerdict.Refused, result.Verdict);
        Assert.Equal(ClaudeCloudLifecycle.StillThereDetail, result.Detail);
    }

    // A refused delete is not followed by a confirmation read: there is
    // nothing to confirm, and a second request would be spent on nothing.
    [Fact]
    public async Task ARefusedDeleteIsNotFollowedByARead()
    {
        var api = new FakeApi(Answer(404, RouterNotFound));

        var result = await ClaudeCloudLifecycle.RunAsync(api, new FakeCredentials(),
            CloudLifecycleAction.Delete, Id, CancellationToken.None);

        Assert.Equal(CloudLifecycleVerdict.Refused, result.Verdict);
        Assert.Equal(ClaudeCloudLifecycle.RouteNotFoundDetail, result.Detail);
        Assert.Single(api.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoUsableLoginSendsNothing(bool malformed)
    {
        var api = new FakeApi();
        var outcome = malformed ? CredentialOutcome.Malformed : CredentialOutcome.NotLoggedIn;
        var creds = new FakeCredentials { Reading = new(outcome, null, null, "no") };

        var result = await ClaudeCloudLifecycle.RunAsync(api, creds,
            CloudLifecycleAction.Archive, Id, CancellationToken.None);

        Assert.Equal(CloudLifecycleVerdict.Refused, result.Verdict);
        Assert.Equal(ClaudeCloudLifecycle.NoCredentialDetail, result.Detail);
        Assert.Empty(api.Requests);
    }

    // A Found reading that somehow carries no token is no login either.
    [Fact]
    public async Task AFoundReadingWithNoTokenSendsNothing()
    {
        var api = new FakeApi();
        var creds = new FakeCredentials { Reading = new(CredentialOutcome.Found, null, null, "odd") };

        var result = await ClaudeCloudLifecycle.RunAsync(api, creds,
            CloudLifecycleAction.Delete, Id, CancellationToken.None);

        Assert.Equal(ClaudeCloudLifecycle.NoCredentialDetail, result.Detail);
        Assert.Empty(api.Requests);
    }

    // A malformed id is refused before the secret is even read: the cheapest
    // refusal is the one that asks the Keychain for nothing.
    [Fact]
    public async Task AMalformedIdReadsNoCredentialAndSendsNothing()
    {
        var api = new FakeApi();
        var creds = new FakeCredentials();

        var result = await ClaudeCloudLifecycle.RunAsync(api, creds,
            CloudLifecycleAction.Delete, "not-a-session", CancellationToken.None);

        Assert.Equal(CloudLifecycleVerdict.Refused, result.Verdict);
        Assert.Equal(0, creds.Reads);
        Assert.Empty(api.Requests);
    }

    // The token canary: whatever the endpoint answers, for either verb and
    // either step, no detail the row could show carries the token.
    [Theory]
    [InlineData(CloudLifecycleAction.Archive)]
    [InlineData(CloudLifecycleAction.Delete)]
    public async Task NoDetailEverCarriesTheToken(CloudLifecycleAction action)
    {
        foreach (var status in new[] { 400, 401, 403, 404, 409, 413, 429, 500, 503 })
        {
            foreach (var body in new[] { "", RouterNotFound, NotFoundJson, "{\"echo\":\"" + Token + "\"}" })
            {
                var first = new FakeApi(Answer(status, body));
                var refused = await ClaudeCloudLifecycle.RunAsync(first, new FakeCredentials(),
                    action, Id, CancellationToken.None);
                Assert.DoesNotContain(Token, refused.Detail ?? "", StringComparison.Ordinal);
                Assert.DoesNotContain(Token, CloudActionText.For(action, refused), StringComparison.Ordinal);

                var confirmed = new FakeApi(Answer(200, "{}"), Answer(status, body));
                var afterDelete = await ClaudeCloudLifecycle.RunAsync(confirmed, new FakeCredentials(),
                    CloudLifecycleAction.Delete, Id, CancellationToken.None);
                Assert.DoesNotContain(Token, CloudActionText.For(CloudLifecycleAction.Delete, afterDelete),
                    StringComparison.Ordinal);
            }
        }
    }

    // --- which rows an orb is offered ---------------------------------------------

    [Fact]
    public void ACloudOrbWithTheSettingOnIsOfferedBoth()
    {
        Assert.Equal(new CloudOrbOffer(true, true),
            CloudOrbActions.Offer(SessionSource.ClaudeCloud, "cloud:" + Id, cloudEnabled: true));
    }

    [Fact]
    public void TheSettingOffOffersNeither()
    {
        Assert.Equal(CloudOrbOffer.None,
            CloudOrbActions.Offer(SessionSource.ClaudeCloud, "cloud:" + Id, cloudEnabled: false));
    }

    // Every other source — a remote-control (bridge) session above all — gets
    // neither, whatever its key looks like.
    [Theory]
    [MemberData(nameof(OtherSources))]
    public void NoOtherSourceIsOfferedEither(SessionSource source)
    {
        Assert.Equal(CloudOrbOffer.None, CloudOrbActions.Offer(source, "cloud:" + Id, cloudEnabled: true));
    }

    public static IEnumerable<object[]> OtherSources() =>
        Enum.GetValues<SessionSource>().Where(s => s != SessionSource.ClaudeCloud).Select(s => new object[] { s });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Id)]
    [InlineData("rc:board:session_01FixtureOnly")]
    [InlineData("cloud:")]
    [InlineData("cloud:not-a-session")]
    public void AKeyThatIsNotAWellFormedCloudKeyIsOfferedNeither(string? key)
    {
        Assert.Equal(CloudOrbOffer.None, CloudOrbActions.Offer(SessionSource.ClaudeCloud, key, cloudEnabled: true));
        Assert.Null(CloudOrbActions.IdFromKey(key));
    }

    [Fact]
    public void TheIdComesOutOfTheKeyTheScanMinted()
    {
        Assert.Equal(Id, CloudOrbActions.IdFromKey("cloud:" + Id));
    }

    // --- the wording ----------------------------------------------------------

    [Fact]
    public void ArchiveSaysWhatItDoesAndThatItIsNotPermanent()
    {
        Assert.Equal("Archive this session", CloudActionText.Header(CloudLifecycleAction.Archive));
        Assert.Equal("Click again to archive", CloudActionText.Armed(CloudLifecycleAction.Archive));
        Assert.Equal("Archiving…", CloudActionText.Working(CloudLifecycleAction.Archive));
        Assert.Contains("archived", CloudActionText.Tip(CloudLifecycleAction.Archive), StringComparison.Ordinal);
        Assert.Equal("Archived",
            CloudActionText.For(CloudLifecycleAction.Archive, new CloudLifecycleResult(CloudLifecycleVerdict.Done)));
        Assert.Equal("Couldn't archive: why",
            CloudActionText.For(CloudLifecycleAction.Archive,
                new CloudLifecycleResult(CloudLifecycleVerdict.Refused, "why")));
    }

    [Fact]
    public void DeleteSaysItCannotBeUndone()
    {
        Assert.Equal("Delete this session…", CloudActionText.Header(CloudLifecycleAction.Delete));
        Assert.Equal("Click again to delete — this can't be undone", CloudActionText.Armed(CloudLifecycleAction.Delete));
        Assert.Equal("Deleting…", CloudActionText.Working(CloudLifecycleAction.Delete));
        Assert.Contains("cannot be restored", CloudActionText.Tip(CloudLifecycleAction.Delete), StringComparison.Ordinal);
        Assert.Equal("Deleted",
            CloudActionText.For(CloudLifecycleAction.Delete, new CloudLifecycleResult(CloudLifecycleVerdict.Done)));
        Assert.Equal("Deleted — couldn't confirm: slow",
            CloudActionText.For(CloudLifecycleAction.Delete,
                new CloudLifecycleResult(CloudLifecycleVerdict.DoneUnconfirmed, "slow")));
        Assert.Equal("Couldn't delete: why",
            CloudActionText.For(CloudLifecycleAction.Delete,
                new CloudLifecycleResult(CloudLifecycleVerdict.Refused, "why")));
    }
}
