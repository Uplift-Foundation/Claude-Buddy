using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.Tests;

// The cloud roster, end to end, against a fixture with every field a real row
// carries.
//
// Here as well as in tests/UnitTests because this is the seam CLAUDE.md says to
// cover twice: the unit suite checks the parsing rules a case at a time, and this
// checks the whole exchange — three real-shaped pages walked into orbs, with the
// cursor followed, the filter applied and the link built. The two fail
// differently. A unit test catches a field read wrong; only this catches a
// payload whose shape is not what the rules assume.
//
// **Every value in this fixture is invented.** The structure is real — the union
// of keys observed across 578 captured rows, *including the two that were empty
// on every one of them* — and the ids, titles, models and instants are not. This
// repository is public and an account's cloud sessions are not all work; what
// this fixture is for is the shape, and the shape is all it carries.
//
// Two fields are deliberately empty rather than omitted: `session_url` and
// `session_context.cwd`. They were empty on all 578 rows, which is why the link
// is built from the id — and a fixture that quietly filled them in would let a
// regression there pass unnoticed.
public class ClaudeCloudRosterPayloadTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

    private sealed class FixtureApi : ICloudApi
    {
        private readonly Queue<string> _pages;

        internal FixtureApi(IEnumerable<string> pages) => _pages = new Queue<string>(pages);

        internal List<string> Paths { get; } = new();

        public Task<CloudApiResult> GetAsync(CloudRequestContext context, CancellationToken token)
        {
            Paths.Add(context.Path);

            // The negative control for the fixture itself: if the walk ever asks
            // for a page that is not there, that is a bug in the walk rather than
            // a quietly short answer.
            Assert.NotEmpty(_pages);

            var body = _pages.Dequeue();
            return Task.FromResult(new CloudApiResult(CloudOutcomes.OutcomeFor(200, body), body));
        }
    }

    private sealed class FixtureCredentials : ICloudCredentialSource
    {
        public string? Stamp() => "638000000000000000";

        public CredentialRead Read() =>
            new(CredentialOutcome.Found, Token, DateTimeOffset.MaxValue, "a credential is present");
    }

    // A full row, every observed key present. `post_turn_summary` is a dict on a
    // cloud row and **null on a bridge row**, which is exactly how the payload
    // behaves and is the asymmetry most likely to trip a parser.
    private static string CloudRow(string id, string status, string bucket,
        string title, string updated, bool needsAction) =>
        "{"
        + "\"id\":\"" + id + "\","
        + "\"environment_kind\":\"anthropic_cloud\","
        + "\"session_status\":\"" + status + "\","
        + "\"status_bucket\":\"" + bucket + "\","
        + "\"title\":\"" + title + "\","
        + "\"created_at\":\"2026-09-01T09:00:00Z\","
        + "\"updated_at\":\"" + updated + "\","
        + "\"last_event_at\":\"" + updated + "\","
        + "\"environment_id\":\"env_0001\","
        + "\"connection_status\":\"connected\","
        + "\"worker_status\":\"ready\","
        + "\"user_message_count\":7,"
        + "\"unread\":false,"
        + "\"tags\":[],"
        + "\"participants\":[],"
        + "\"relations\":{},"
        + "\"config\":{\"effort_level\":\"high\"},"
        + "\"session_url\":\"\","
        + "\"session_context\":{\"cwd\":\"\"},"
        + "\"external_metadata\":{"
        + "\"model\":\"claude-opus-5\","
        + "\"effort_level\":\"high\","
        + "\"container_cc_version\":\"2.1.278\","
        + "\"cross_session_inbound\":\"accept\","
        + "\"flag_settings\":{},"
        + "\"rate_limit_info\":null,"
        + "\"context_usage\":{\"max_tokens\":200000,\"used_tokens\":64000},"
        + "\"post_turn_summary\":{"
        + "\"needs_action\":" + (needsAction ? "true" : "false") + ","
        + "\"status_detail\":\"a status detail\","
        + "\"recent_action\":\"a recent action\""
        + "}}}";

    private static string BridgeRow(string id, string status) =>
        "{"
        + "\"id\":\"" + id + "\","
        + "\"environment_kind\":\"bridge\","
        + "\"session_status\":\"" + status + "\","
        + "\"status_bucket\":\"" + status + "\","
        + "\"title\":\"a local session\","
        + "\"created_at\":\"2026-09-01T09:00:00Z\","
        + "\"updated_at\":\"2026-09-19T09:00:00Z\","
        + "\"session_url\":\"\","
        + "\"session_context\":{\"cwd\":\"\"},"
        + "\"external_metadata\":{\"model\":\"claude-sonnet-5\",\"post_turn_summary\":null}}";

    private static string Envelope(IEnumerable<string> rows, string? lastId)
    {
        var list = rows.ToList();
        var last = lastId is null ? "null" : "\"" + lastId + "\"";
        return "{\"data\":[" + string.Join(",", list) + "],"
               + "\"first_id\":\"" + (list.Count > 0 ? "session_first" : "") + "\","
               + "\"has_more\":" + (lastId is null ? "false" : "true") + ","
               + "\"last_id\":" + last + "}";
    }

    // 578 rows across three pages, in the proportions actually measured: 573
    // bridge, 5 cloud, 4 of those archived. **One orb.**
    private static IReadOnlyList<string> RealShapedRoster()
    {
        var bridge = Enumerable.Range(0, 573)
            .Select(i => BridgeRow($"session_b{i:D4}", i % 4 == 0 ? "running" : "idle"))
            .ToList();

        var cloud = new[]
        {
            CloudRow("session_c0", "archived", "archived", "an old one", "2026-09-05T10:00:00Z", false),
            CloudRow("session_c1", "archived", "archived", "another old one", "2026-09-06T10:00:00Z", false),
            CloudRow("session_c2", "archived", "archived", "a third old one", "2026-09-07T10:00:00Z", false),
            CloudRow("session_c3", "archived", "archived", "a fourth old one", "2026-09-08T10:00:00Z", false),
            CloudRow("session_c4", "running", "running", "the live one", "2026-09-19T11:45:00Z", false),
        };

        var all = bridge.Concat(cloud).ToList();

        return new[]
        {
            Envelope(all.Take(200), "session_b0199"),
            Envelope(all.Skip(200).Take(200), "session_b0399"),
            Envelope(all.Skip(400), null),
        };
    }

    [Fact]
    public async Task AFullRosterWalksIntoExactlyOneOrb()
    {
        var api = new FixtureApi(RealShapedRoster());

        var step = await ClaudeCloudSessions.StepAsync(api, new FixtureCredentials(),
            ClaudeCloudSessions.ArmState.Initial,
            new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        // Three pages, the cursor followed each time.
        Assert.Equal(new[]
        {
            "/v2/ccr-sessions?limit=100",
            "/v2/ccr-sessions?limit=100&after_id=session_b0199",
            "/v2/ccr-sessions?limit=100&after_id=session_b0399",
        }, api.Paths);

        var session = Assert.Single(step.Snapshot!);

        Assert.Equal("session_c4", session.Id);
        Assert.Equal("the live one", session.Title);
        Assert.Equal("generating", session.State);
        Assert.Equal("running", session.StatusBucket);
        Assert.False(session.NeedsAction);
        Assert.Equal("claude-opus-5", session.Model);
        Assert.Equal(32, session.ContextPercent);
        Assert.Equal("a status detail", session.StatusDetail);
        Assert.Equal("a recent action", session.RecentAction);

        // Built from the id, because `session_url` is empty on every row — which
        // this fixture reproduces rather than papering over.
        Assert.Equal("https://claude.ai/code/session_c4", session.Url);

        // The session's own `updated_at`, never the time of the read. Stamping now
        // would give every session the account has ever had a permanent orb.
        Assert.Equal(new DateTime(2026, 9, 19, 11, 45, 0, DateTimeKind.Utc), session.LastActivity);

        // And the sentence names what was inspected, so "one cloud session" is a
        // claim somebody can check rather than one they have to trust.
        Assert.Equal("1 cloud session (578 sessions inspected)", step.Status);
    }

    // **The negative control.** 573 of those rows are the user's own local
    // sessions, already drawn from hooks — this is the assertion that says so in
    // numbers rather than in a comment, and keeping them would double every local
    // orb on the screen.
    [Fact]
    public void TheFixtureReallyDoesHoldFiveHundredAndSeventyThreeBridgeRows()
    {
        var reduction = ClaudeCloudRoster.Reduce(
            RealShapedRoster().Select(ClaudeCloudRoster.ParsePage), false);

        Assert.Equal(578, reduction.Inspected);
        Assert.Equal(573, reduction.Kinds["bridge"]);
        Assert.Equal(5, reduction.Kinds["anthropic_cloud"]);
        Assert.Single(reduction.Sessions);
        Assert.False(reduction.ShapeChanged);
        Assert.False(reduction.Truncated);
        Assert.Empty(reduction.UnknownKinds);
    }

    // Some of those bridge rows are `running`, so the filter is doing real work
    // rather than being carried by every local session happening to be idle.
    [Fact]
    public void SomeOfTheBridgeRowsAreRunningSoTheFilterIsNotCarriedByLuck()
    {
        var pages = RealShapedRoster().Select(ClaudeCloudRoster.ParsePage).ToList();

        var runningBridge = pages.SelectMany(p => p.Rows)
            .Count(r => r.Kind == "bridge" && r.Status == "running");

        Assert.True(runningBridge > 100,
            $"expected the fixture to hold plenty of running bridge rows; it holds {runningBridge}");
    }

    // The token never reaches anything rendered, on the largest payload this
    // repository has for the arm.
    [Fact]
    public async Task NothingInTheStatusEchoesTheCredential()
    {
        var api = new FixtureApi(RealShapedRoster());

        var step = await ClaudeCloudSessions.StepAsync(api, new FixtureCredentials(),
            ClaudeCloudSessions.ArmState.Initial,
            new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        Assert.DoesNotContain(Token, step.Status, StringComparison.Ordinal);
        Assert.All(step.Snapshot!, s =>
        {
            Assert.DoesNotContain(Token, s.Title, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, s.Url, StringComparison.Ordinal);
        });
    }
}
