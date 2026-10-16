using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// A send into a Remote Control-bridged Claude Desktop chat (CB-266), end to end
// through the seam: LocalCliChatSession builds the request via CloudTurnSender,
// an ICloudApi answers with a status and body read back through the real
// OutcomeFor, and the session decides what the composer does about it.
//
// Fixtures are the ones ClaudeCloudSendPayloadTests documents — the 200 and the
// 409 are captured shapes, the 404 is the measured read answer assumed for the
// write. **The API-side 403 body is hand-written**: no refusal of that kind has
// been captured, so it is a JSON object with an error type and a request id (the
// two things OutcomeFor reads to tell it from an edge block) with an invented
// message. The edge 403 is the bare HTML body the cloud suite already uses.
//
// The transcript half of the exchange — the peer row coming back and folding
// onto the bubble — needs a dispatcher and lives in tests/UiTests.
[Collection("Settings")]
public class DesktopBridgeSendPayloadTests : IDisposable
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";
    private const string Bridge = "session_01FixtureOnly";

    private const string SentBody =
        """{"attestation_feedback":null,"results":[{"duplicate":false,"event_id":"11111111-2222-4333-8444-555555555555","sequence_num":"20"}]}""";

    private const string InactiveBody =
        """{"error":{"message":"Session session_01FixtureOnly is not active","type":"session_not_active"},"request_id":"req_fixture_4","type":"error"}""";

    private const string GoneBody =
        """{"type":"error","error":{"type":"not_found_error","message":"Session session_01FixtureOnly not found"},"request_id":"req_fixture_5"}""";

    private const string ApiForbiddenBody =
        """{"type":"error","error":{"type":"permission_error","message":"a fixture refusal"},"request_id":"req_fixture_6"}""";

    private const string EdgeForbiddenBody = "<html></html>";

    private readonly bool _replyBefore = OrbweaverSettings.ClaudeCodeReplyEnabled;

    public DesktopBridgeSendPayloadTests() => OrbweaverSettings.ClaudeCodeReplyEnabled = true;

    public void Dispose() => OrbweaverSettings.ClaudeCodeReplyEnabled = _replyBefore;

    private sealed class FixtureApi : ICloudApi
    {
        private readonly int _status;
        private readonly string? _body;

        internal FixtureApi(int status, string? body)
        {
            _status = status;
            _body = body;
        }

        internal List<CloudRequestContext> Requests { get; } = new();

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            Requests.Add(context);

            // Built exactly as HttpCloudApi builds it, so what this records is
            // what would have gone on the wire.
            using var request = CloudRequest.Build(context.AccessToken, context.Path, context.Method, context.Body);
            Assert.Equal(HttpMethod.Post, request.Method);

            var outcome = CloudOutcomes.OutcomeFor(_status, _body);
            return Task.FromResult(new CloudApiResult(outcome, outcome.Kind == CloudOutcomeKind.Ok ? _body : null));
        }
    }

    private sealed class Credentials : ICloudCredentialSource
    {
        public string? Stamp() => "stamp-1";

        public CredentialRead Read() => new(CredentialOutcome.Found, Token, null, "a credential is present");
    }

    private static LocalCliChatSession Session(FixtureApi api) =>
        new("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            Entrypoint = SessionStatus.DesktopEntrypoint,
            BridgeSessionId = Bridge,
            State = "idle",
            Title = "",
        }, cloudApi: api, credentialsFor: _ => new Credentials(), cloudEnabled: () => true)
        {
            Delay = (_, _) => Task.CompletedTask,
        };

    [Fact]
    public async Task ASendGoesToTheBridgeEventsPathWithTheMeasuredBody()
    {
        var api = new FixtureApi(200, SentBody);
        var session = Session(api);

        var outcome = await session.SendAsync("please summarise the open PRs");

        Assert.Equal(ChatSendOutcome.Sent, outcome);

        var request = Assert.Single(api.Requests);
        Assert.Equal($"/v1/code/sessions/{Bridge}/events", request.Path);
        Assert.Equal(Token, request.AccessToken);

        // CB-199's exact body, with the uuid the transcript row will carry back.
        using var doc = JsonDocument.Parse(request.Body!);
        var payload = doc.RootElement.GetProperty("events")[0].GetProperty("payload");
        Assert.Equal(Bridge, payload.GetProperty("session_id").GetString());
        Assert.Equal("user", payload.GetProperty("type").GetString());
        Assert.Equal("please summarise the open PRs", payload.GetProperty("message").GetProperty("content").GetString());
        Assert.True(Guid.TryParse(payload.GetProperty("uuid").GetString(), out _));

        // The token is in the header's input and nowhere else.
        Assert.DoesNotContain(Token, request.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, request.Path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(409, InactiveBody)]
    [InlineData(404, GoneBody)]
    public async Task AnEndedOrMissingBridgeFlipsReadOnlyOnceWithTheReconnectHint(int status, string body)
    {
        var api = new FixtureApi(status, body);
        var session = Session(api);
        var raised = 0;
        session.ReadOnlyChanged += () => raised++;

        Assert.Equal(ChatSendOutcome.Failed, await session.SendAsync("hello"));
        Assert.Equal(ChatSendOutcome.Failed, await session.SendAsync("hello again"));

        Assert.True(session.IsReadOnly);
        Assert.Equal(1, raised);
        Assert.Equal(LocalCliChatSession.DesktopBridgeGoneHint, session.ComposerHint);

        // The second send never reached the API: the box was already gone.
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task AnApiRefusalOfTheLoginSaysNotPermitted()
    {
        var api = new FixtureApi(403, ApiForbiddenBody);
        var session = Session(api);

        Assert.Equal(ChatSendOutcome.Failed, await session.SendAsync("hello"));

        Assert.True(session.IsReadOnly);
        Assert.Equal(LocalCliChatSession.DesktopNotPermittedHint, session.ComposerHint);
        Assert.Contains(session.History, t => t.Text.StartsWith("Not sent: ", StringComparison.Ordinal));
    }

    // The negative control to the one above: the same status from something in
    // front of the API says nothing about the account and must not take the box.
    [Fact]
    public async Task AnEdgeBlockedSendDoesNotTakeTheBoxAway()
    {
        var api = new FixtureApi(403, EdgeForbiddenBody);
        var session = Session(api);

        Assert.Equal(ChatSendOutcome.Failed, await session.SendAsync("hello"));

        Assert.False(session.IsReadOnly);
        Assert.Equal(LocalCliChatSession.DesktopBridgedHint, session.ComposerHint);
        Assert.Contains(session.History, t => t.Text.Contains(CloudOutcomes.EdgeBlockedDetail));
    }
}
