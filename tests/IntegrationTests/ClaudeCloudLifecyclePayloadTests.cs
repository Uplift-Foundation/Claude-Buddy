using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-225: archive and delete, end to end through the seam — the request the
// app builds, handed to an ICloudApi built exactly as HttpCloudApi builds it,
// and every answer read back through the real OutcomeFor and the lifecycle
// verdicts.
//
// **These fixtures are captured, then scrubbed.** Unlike CB-199's, which were
// written from shape output, CB-225's probe saved each raw body to a 0600 file
// (`--save`) from throwaway sessions made for the purpose and since deleted.
// The scrub kept every key and the allow-listed enum values, replaced every
// other string with "fixture" and every number with 0, cut arrays to one
// element, and put the fixture id where the session id was. The router's
// "404 page not found" is verbatim; it names nothing.
//
// Here as well as in tests/UnitTests for the reason CLAUDE.md gives: the unit
// suite checks each rule; this checks they compose into the exchange the
// endpoint actually had with the probe — and against a format this project
// does not control.
public class ClaudeCloudLifecyclePayloadTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";
    private const string SessionId = "session_01FixtureOnly";

    // POST …/archive, 200: the whole session back, archived.
    private const string Archived200 =
        """{"session":{"client_presence":[],"config":{"mcp_connector_ids":[],"model":"fixture","origin":"fixture","outcomes":[],"sources":[]},"connection_status":"connected","created_at":"2026-10-02T00:00:00Z","environment_id":"fixture","environment_kind":"anthropic_cloud","external_metadata":{"archive_container_stop_pending_epoch":0,"container_cc_version":"fixture","context_usage":{"max_tokens":0,"used_tokens":0},"cross_session_inbound":"available","last_served_model":"fixture","post_turn_summary":{"needs_action":"fixture","recent_action":"fixture","status_category":"fixture","status_detail":"fixture"},"rate_limit_info":{"isUsingOverage":false,"rateLimitType":"fixture","resetsAt":0,"status":"allowed"},"turn_handoff":{"no_query_first":true,"staged_files":true,"tools":["fixture"],"v":0,"worker_epoch":0},"usage":{"cache_read_tokens":0,"cache_write_tokens":0,"cost_usd":0,"input_tokens":0,"output_tokens":0}},"id":"fixture-id","last_event_at":"2026-10-02T00:00:00Z","metadata":{},"participants":[],"post_turn_summary":{"description":"fixture","is_noteworthy":false,"needs_action":"fixture","recent_action":"fixture","status_category":"fixture","status_detail":"fixture","title":"fixture"},"requires_action_details_list":[],"security_tier":"fixture","session_url":"fixture","status":"archived","status_bucket":"completed","tags":["fixture"],"title":"fixture","unread":true,"updated_at":"2026-10-02T00:00:00Z","worker_status":"idle"}}""";

    // GET /v2/ccr-sessions/{id} after the archive: the row the roster's Keep
    // filter reads, saying `session_status: "archived"`.
    private const string V2RowArchived =
        """{"active_mount_paths":[],"configured_model":"fixture","connection_status":"disconnected","created_at":"2026-10-02T00:00:00Z","environment_id":"fixture","environment_kind":"anthropic_cloud","external_metadata":{"archive_container_stop_pending_epoch":0,"container_cc_version":"fixture","context_usage":{"max_tokens":0,"used_tokens":0},"cross_session_inbound":"available","last_served_model":"fixture","post_turn_summary":{"needs_action":"fixture","recent_action":"fixture","status_category":"fixture","status_detail":"fixture"},"rate_limit_info":{"isUsingOverage":false,"rateLimitType":"fixture","resetsAt":0,"status":"allowed"},"turn_handoff":{"no_query_first":true,"staged_files":true,"tools":["fixture"],"v":0,"worker_epoch":0},"usage":{"cache_read_tokens":0,"cache_write_tokens":0,"cost_usd":0,"input_tokens":0,"output_tokens":0}},"github_access":"fixture","id":"session_01FixtureOnly","metadata":{},"origin":"fixture","post_turn_summary":{"description":"fixture","is_noteworthy":false,"needs_action":"fixture","recent_action":"fixture","status_category":"fixture","status_detail":"fixture","title":"fixture"},"session_context":{"allowed_tools":[],"builtin_tools":[],"cwd":"fixture","disallowed_tools":[],"environment_variables":{},"model":"fixture","outcomes":[],"sources":[]},"session_status":"archived","session_url":"fixture","status_bucket":"completed","tags":["fixture"],"title":"fixture","type":"internal_session","unread":true,"updated_at":"2026-10-02T00:00:00Z"}""";

    // DELETE /v1/code/sessions/{id}, 200.
    private const string Deleted200 = "{}";

    // GET, DELETE or archive on the deleted id: the handler's own 404.
    private const string HandlerNotFound =
        """{"error":{"message":"Session session_01FixtureOnly not found","resource_id":"fixture","resource_type":"fixture","type":"not_found_error"},"request_id":"fixture","type":"error"}""";

    // A sub-route with no handler, on the same id and token: the router's 404.
    private const string RouterNotFound = "404 page not found";

    // Either verb with no anthropic-version.
    private const string NoVersion400 =
        """{"error":{"message":"anthropic-version: header is required","reason":"fixture","type":"invalid_request_error"},"request_id":"fixture","type":"error"}""";

    // Either verb with a bogus Bearer.
    private const string Bogus401 =
        """{"type":"error","error":{"type":"authentication_error","message":"OAuth access token is invalid."},"request_id":null}""";

    // A send to the archived session.
    private const string NotActive409 =
        """{"error":{"message":"Session session_01FixtureOnly is not active","type":"session_not_active"},"request_id":"fixture","type":"error"}""";

    private sealed class FixtureApi : ICloudApi
    {
        private readonly Queue<(int Status, string? Body)> _answers;

        internal FixtureApi(params (int, string?)[] answers) =>
            _answers = new Queue<(int, string?)>(answers);

        internal List<(HttpMethod Method, string Uri, string? ContentType)> Wire { get; } = new();

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            // Built exactly as HttpCloudApi builds it, so this records what
            // would have gone on the wire.
            using var request = CloudRequest.Build(context.AccessToken, context.Path,
                context.Method, context.Body);
            Assert.Equal("Bearer " + Token, request.Headers.Authorization!.ToString());
            Wire.Add((request.Method, request.RequestUri!.ToString(),
                request.Content?.Headers.ContentType?.MediaType));

            var (status, body) = _answers.Dequeue();
            return Task.FromResult(new CloudApiResult(CloudOutcomes.OutcomeFor(status, body), body));
        }
    }

    private sealed class Login : ICloudCredentialSource
    {
        public string? Stamp() => "stamp";
        public CredentialRead Read() => new(CredentialOutcome.Found, Token, null, "present");
    }

    private static Task<CloudLifecycleResult> Run(FixtureApi api, CloudLifecycleAction action) =>
        ClaudeCloudLifecycle.RunAsync(api, new Login(), action, SessionId, CancellationToken.None);

    [Fact]
    public async Task ArchivingAsTheProbeDidIsDone()
    {
        var api = new FixtureApi((200, Archived200));

        var result = await Run(api, CloudLifecycleAction.Archive);

        Assert.Equal(CloudLifecycleVerdict.Done, result.Verdict);
        var wire = Assert.Single(api.Wire);
        Assert.Equal(HttpMethod.Post, wire.Method);
        Assert.Equal("https://api.anthropic.com/v1/code/sessions/" + SessionId + "/archive", wire.Uri);
        Assert.Equal("application/json", wire.ContentType);
    }

    // The orb goes on the next roster read with no new rule: the archived row
    // is one Keep already drops. The same row reading "idle" is the control —
    // without it, a Keep that dropped everything would pass this too.
    [Fact]
    public void TheArchivedRowIsOneTheRosterAlreadyDrops()
    {
        var archived = ClaudeCloudRoster.ParseSession(V2RowArchived);
        Assert.NotNull(archived);
        Assert.False(ClaudeCloudRoster.Keep(archived!));

        var live = ClaudeCloudRoster.ParseSession(
            V2RowArchived.Replace("\"session_status\":\"archived\"", "\"session_status\":\"idle\""));
        Assert.NotNull(live);
        Assert.True(ClaudeCloudRoster.Keep(live!));
    }

    // And an open panel on it, sending, meets the measured 409.
    [Fact]
    public void ASendToTheArchivedSessionIsTheSessionInactiveArm()
    {
        var outcome = CloudOutcomes.OutcomeFor(409, NotActive409);
        Assert.Equal(CloudOutcomeKind.SessionInactive, outcome.Kind);
    }

    [Fact]
    public async Task DeletingAsTheProbeDidIsDoneAndConfirmed()
    {
        var api = new FixtureApi((200, Deleted200), (404, HandlerNotFound));

        var result = await Run(api, CloudLifecycleAction.Delete);

        Assert.Equal(CloudLifecycleVerdict.Done, result.Verdict);
        Assert.Equal(2, api.Wire.Count);
        Assert.Equal(HttpMethod.Delete, api.Wire[0].Method);
        Assert.Null(api.Wire[0].ContentType);
        Assert.Equal(HttpMethod.Get, api.Wire[1].Method);
        Assert.Equal("https://api.anthropic.com/v1/code/sessions/" + SessionId, api.Wire[1].Uri);
    }

    // The route moving is the one failure most likely to happen to a route the
    // CLI never calls, and this is what it would look like: the router's 404.
    // It must read as a refusal on the row, not as "Deleted".
    [Theory]
    [InlineData(CloudLifecycleAction.Archive)]
    [InlineData(CloudLifecycleAction.Delete)]
    public async Task ARouteThatHasMovedIsARefusalNotASuccess(CloudLifecycleAction action)
    {
        var api = new FixtureApi((404, RouterNotFound));

        var result = await Run(api, action);

        Assert.Equal(CloudLifecycleVerdict.Refused, result.Verdict);
        Assert.Equal(ClaudeCloudLifecycle.RouteNotFoundDetail, result.Detail);
        Assert.StartsWith("Couldn't ", CloudActionText.For(action, result), StringComparison.Ordinal);
    }

    // Already gone — deleted elsewhere since the roster last looked.
    [Theory]
    [InlineData(CloudLifecycleAction.Archive)]
    [InlineData(CloudLifecycleAction.Delete)]
    public async Task AnAlreadyDeletedSessionIsReportedAsNotFound(CloudLifecycleAction action)
    {
        var api = new FixtureApi((404, HandlerNotFound));

        var result = await Run(api, action);

        Assert.Equal(CloudLifecycleVerdict.Refused, result.Verdict);
        Assert.Equal(ClaudeCloudLifecycle.SessionNotFoundDetail, result.Detail);
    }

    // The measured controls, through the real outcome arms.
    [Theory]
    [InlineData(CloudLifecycleAction.Archive, 400, NoVersion400, "ShapeChanged")]
    [InlineData(CloudLifecycleAction.Delete, 400, NoVersion400, "ShapeChanged")]
    [InlineData(CloudLifecycleAction.Archive, 401, Bogus401, "TokenRefused")]
    [InlineData(CloudLifecycleAction.Delete, 401, Bogus401, "TokenRefused")]
    public async Task TheMeasuredRefusalsReadAsTheirOutcomeArms(CloudLifecycleAction action, int status,
        string body, string kind)
    {
        Assert.Equal(kind, CloudOutcomes.OutcomeFor(status, body).Kind.ToString());

        var result = await Run(new FixtureApi((status, body)), action);

        Assert.Equal(CloudLifecycleVerdict.Refused, result.Verdict);
        Assert.Equal(CloudOutcomes.OutcomeFor(status, body).Detail, result.Detail);
    }

    // Nothing captured from the endpoint, refusal or success, reaches the row
    // with the token in it — the canary, against the real bodies.
    [Fact]
    public async Task NoCapturedAnswerPutsTheTokenOnTheRow()
    {
        foreach (var (status, body) in new (int, string?)[]
                 {
                     (200, Archived200), (404, HandlerNotFound), (404, RouterNotFound),
                     (400, NoVersion400), (401, Bogus401), (409, NotActive409),
                 })
        {
            foreach (var action in new[] { CloudLifecycleAction.Archive, CloudLifecycleAction.Delete })
            {
                var api = new FixtureApi((status, body), (404, HandlerNotFound));
                var row = CloudActionText.For(action, await Run(api, action));
                Assert.DoesNotContain(Token, row, StringComparison.Ordinal);
            }
        }
    }
}
