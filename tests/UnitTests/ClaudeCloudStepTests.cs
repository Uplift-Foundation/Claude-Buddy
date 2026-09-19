using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers one tick of the cloud arm: which credential state stops it, which cycle
// is due, what it asks for, and — the one that matters most — what it publishes
// when a fetch fails halfway through.
//
// Driven entirely through fakes. Nothing here opens a socket or reads a
// credential store, which is the whole reason StepAsync takes its API, its
// credential source and its clock as arguments rather than reaching for them.
public class ClaudeCloudStepTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

    // --- fakes ---------------------------------------------------------------

    private sealed class FakeApi : ICloudApi
    {
        private readonly Func<string, CloudApiResult> _answer;

        internal FakeApi(Func<string, CloudApiResult> answer) => _answer = answer;

        internal List<string> Paths { get; } = new();

        public Task<CloudApiResult> GetAsync(CloudRequestContext context, CancellationToken token)
        {
            Paths.Add(context.Path);
            return Task.FromResult(_answer(context.Path));
        }
    }

    private sealed class FakeCredentials : ICloudCredentialSource
    {
        internal string? StampValue { get; set; } = "stamp-1";
        internal CredentialRead Reading { get; set; } =
            new(CredentialOutcome.Found, Token, null, "a credential is present");

        internal int Reads { get; private set; }
        internal int Stamps { get; private set; }

        public string? Stamp()
        {
            Stamps++;
            return StampValue;
        }

        public CredentialRead Read()
        {
            Reads++;
            return Reading;
        }
    }

    private static CloudApiResult Ok(string body) =>
        new(CloudOutcomes.OutcomeFor(200, body), body);

    private static CloudApiResult Fail(int status, string? body = null) =>
        new(CloudOutcomes.OutcomeFor(status, body), null);

    private static string Row(string id, string kind = "anthropic_cloud", string status = "running") =>
        "{\"id\":\"" + id + "\",\"environment_kind\":\"" + kind
        + "\",\"session_status\":\"" + status + "\",\"status_bucket\":\"" + status
        + "\",\"title\":\"a session\",\"updated_at\":\"2026-09-19T10:00:00Z\"}";

    private static string Envelope(IEnumerable<string> rows, bool hasMore = false, string? lastId = null)
    {
        var last = lastId is null ? "null" : "\"" + lastId + "\"";
        return "{\"data\":[" + string.Join(",", rows) + "],\"has_more\":"
               + (hasMore ? "true" : "false") + ",\"last_id\":" + last + "}";
    }

    private static readonly DateTime Now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    private static ClaudeCloudSessions.Session Session(string id, string state = "idle") =>
        new(id, id, state, new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc),
            "https://claude.ai/code/" + id, "idle", false, null, null, null, null);

    // --- the walk ------------------------------------------------------------

    // Follows `last_id` until `has_more` is false, and never asks for more than the
    // measured ceiling — `limit=200` is a 400, so a walk that asked for it would
    // return nothing rather than fewer rows.
    [Fact]
    public async Task TheWalkFollowsTheCursorUntilThereIsNoMore()
    {
        var pages = new Queue<string>(new[]
        {
            Envelope(new[] { Row("session_a") }, hasMore: true, lastId: "session_a"),
            Envelope(new[] { Row("session_b") }, hasMore: true, lastId: "session_b"),
            Envelope(new[] { Row("session_c") }),
        });

        var api = new FakeApi(_ => Ok(pages.Dequeue()));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.Equal(new[]
        {
            "/v2/ccr-sessions?limit=100",
            "/v2/ccr-sessions?limit=100&after_id=session_a",
            "/v2/ccr-sessions?limit=100&after_id=session_b",
        }, api.Paths);

        Assert.NotNull(step.Snapshot);
        Assert.Equal(new[] { "session_a", "session_b", "session_c" },
            step.Snapshot!.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task NoRequestEverAsksForMoreThanAHundred()
    {
        var api = new FakeApi(_ => Ok(Envelope(new[] { Row("session_a") })));

        await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.All(api.Paths, path => Assert.Contains("limit=100", path, StringComparison.Ordinal));
        Assert.DoesNotContain(api.Paths, path => path.Contains("limit=200", StringComparison.Ordinal));
    }

    // Eleven pages of roster, ten pages of cap. Keep what was found, mark it, and
    // **say so** — a cap that silently showed fewer sessions would be the same
    // class of bug as the filter getting it wrong, and much harder to notice.
    [Fact]
    public async Task ElevenPagesStopAtTheCapAndTheStatusNamesTheCutoff()
    {
        var page = 0;
        var api = new FakeApi(_ =>
        {
            page++;
            return Ok(Envelope(new[] { Row($"session_p{page:D2}") },
                hasMore: page < 11, lastId: $"session_p{page:D2}"));
        });

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.Equal(CloudRequest.MaxPagesPerWalk, api.Paths.Count);
        Assert.Equal(CloudRequest.MaxPagesPerWalk, step.Snapshot!.Count);
        Assert.Contains($"stopped after {CloudRequest.MaxPagesPerWalk} pages", step.Status,
            StringComparison.Ordinal);
        Assert.Contains("there may be more", step.Status, StringComparison.Ordinal);
    }

    // A walk that ends exactly on the cap with nothing left to fetch is **not**
    // truncated, and must not say it is. The negative control for the case above.
    [Fact]
    public async Task ExactlyTenPagesWithNothingLeftIsNotCalledTruncated()
    {
        var page = 0;
        var api = new FakeApi(_ =>
        {
            page++;
            return Ok(Envelope(new[] { Row($"session_p{page:D2}") },
                hasMore: page < CloudRequest.MaxPagesPerWalk, lastId: $"session_p{page:D2}"));
        });

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.Equal(CloudRequest.MaxPagesPerWalk, api.Paths.Count);
        Assert.DoesNotContain("stopped after", step.Status, StringComparison.Ordinal);
    }

    // A page claiming `has_more` with no cursor to follow ends the walk rather than
    // asking for page one forever.
    [Fact]
    public async Task HasMoreWithNoCursorEndsTheWalk()
    {
        var api = new FakeApi(_ => Ok(
            """{"data":[],"has_more":true,"last_id":null}"""));

        await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.Single(api.Paths);
    }

    [Fact]
    public async Task ASuccessfulWalkRecordsWhenItHappenedAndClearsAnyBackoff()
    {
        var api = new FakeApi(_ => Ok(Envelope(new[] { Row("session_a") })));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with { Backoff = TimeSpan.FromSeconds(8) },
            Now, CancellationToken.None);

        Assert.Equal(Now, step.Next.LastWalkUtc);
        Assert.Null(step.Next.Backoff);
        Assert.False(step.Next.Halted);
        Assert.Equal(CloudRequest.UnmeasuredFirstPageInterval, step.Wait);
    }

    // --- a failure mid-walk --------------------------------------------------

    // **The requirement this whole nullable-snapshot design exists for.** A walk
    // that dies on page two must not replace a good roster with an empty one:
    // orbs vanishing because a request timed out would read as sessions having
    // ended, which is a lie told on the strength of a network hiccup.
    [Fact]
    public async Task AMidWalkFailureNeverPublishesAnEmptySnapshotOverAGoodOne()
    {
        var page = 0;
        var api = new FakeApi(_ =>
        {
            page++;
            return page == 1
                ? Ok(Envelope(new[] { Row("session_a") }, hasMore: true, lastId: "session_a"))
                : Fail(503);
        });

        var good = new[] { Session("session_known") };

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with { Sessions = good },
            Now, CancellationToken.None);

        Assert.Null(step.Snapshot);
        Assert.Equal(good, step.Next.Sessions);
        Assert.False(step.Next.Halted);
        Assert.Equal(Backoff.UnavailableFloor, step.Wait);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(400)]
    [InlineData(0)]
    public async Task EveryRetryableFailureLeavesTheOrbsWhereTheyAre(int status)
    {
        var api = new FakeApi(_ => Fail(status));
        var good = new[] { Session("session_known") };

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with { Sessions = good },
            Now, CancellationToken.None);

        Assert.Null(step.Snapshot);
        Assert.Equal(good, step.Next.Sessions);
    }

    // A definite refusal is the opposite case, and it *should* clear the orbs:
    // there is no access, so there is nothing to draw. It also halts, so nothing
    // asks again — and on macOS that is what keeps "never a repeated OS prompt"
    // true.
    [Theory]
    [InlineData(401, null)]
    [InlineData(401, """{"error":{"message":"OAuth access token is invalid."}}""")]
    [InlineData(403, null)]
    public async Task ADefiniteRefusalClearsTheOrbsAndHalts(int status, string? body)
    {
        var api = new FakeApi(_ => Fail(status, body));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with { Sessions = new[] { Session("session_known") } },
            Now, CancellationToken.None);

        Assert.NotNull(step.Snapshot);
        Assert.Empty(step.Snapshot!);
        Assert.True(step.Next.Halted);
        Assert.Empty(step.Next.Sessions);
        Assert.Equal(ClaudeCloudSessions.HaltedRecheckInterval, step.Wait);
    }

    // A retryable failure doubles, so a host that is down for an hour is not asked
    // once a second for an hour.
    [Fact]
    public async Task ARetryableFailureBacksOff()
    {
        var api = new FakeApi(_ => Fail(503));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with { Backoff = TimeSpan.FromSeconds(8) },
            Now, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(16), step.Wait);
        Assert.Equal(TimeSpan.FromSeconds(16), step.Next.Backoff);
    }

    // --- the credential ------------------------------------------------------

    // **Halted means halted.** No read, no socket, no prompt — only Stamp(), which
    // on macOS is the attributes-only query the consent dialog does not guard.
    [Fact]
    public async Task AHaltedArmReadsNoSecretAndOpensNoSocket()
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));
        var credentials = new FakeCredentials { StampValue = "stamp-1" };

        var halted = ClaudeCloudSessions.ArmState.Initial with
        {
            Halted = true,
            CredentialStamp = "stamp-1",
            Status = "access to the Claude Code login was denied",
        };

        var step = await ClaudeCloudSessions.StepAsync(api, credentials, halted, Now,
            CancellationToken.None);

        Assert.Empty(api.Paths);
        Assert.Equal(0, credentials.Reads);
        Assert.Equal(1, credentials.Stamps);
        Assert.Null(step.Snapshot);
        Assert.Equal(halted, step.Next);
        Assert.Equal("access to the Claude Code login was denied", step.Status);
    }

    // And it comes back on its own once the stamp moves — the user having signed
    // in again, or re-approved us.
    [Fact]
    public async Task AMovedStampLetsAHaltedArmTryAgain()
    {
        var api = new FakeApi(_ => Ok(Envelope(new[] { Row("session_a") })));
        var credentials = new FakeCredentials { StampValue = "stamp-2" };

        var step = await ClaudeCloudSessions.StepAsync(api, credentials,
            ClaudeCloudSessions.ArmState.Initial with { Halted = true, CredentialStamp = "stamp-1" },
            Now, CancellationToken.None);

        Assert.Equal(1, credentials.Reads);
        Assert.False(step.Next.Halted);
        Assert.Equal("stamp-2", step.Next.CredentialStamp);
        Assert.Single(step.Snapshot!);
    }

    // Two facts rather than a theory: CredentialOutcome is internal, and an xUnit
    // theory's parameters have to be as accessible as the method, which is public.
    [Fact]
    public Task ADeniedLoginHaltsWithoutAskingTheEndpointAnything() =>
        ARefusedOrAbsentLoginHalts(CredentialOutcome.Denied);

    [Fact]
    public Task AnAbsentLoginHaltsWithoutAskingTheEndpointAnything() =>
        ARefusedOrAbsentLoginHalts(CredentialOutcome.NotLoggedIn);

    private static async Task ARefusedOrAbsentLoginHalts(CredentialOutcome outcome)
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));
        var credentials = new FakeCredentials
        {
            Reading = new CredentialRead(outcome, null, null, "…"),
        };

        var step = await ClaudeCloudSessions.StepAsync(api, credentials,
            ClaudeCloudSessions.ArmState.Initial with { Sessions = new[] { Session("session_known") } },
            Now, CancellationToken.None);

        Assert.Empty(api.Paths);
        Assert.True(step.Next.Halted);
        Assert.Empty(step.Snapshot!);
        Assert.Equal(ClaudeCliCredentials.Describe(outcome), step.Status);
    }

    // Unreadable and Malformed are transient — the file was locked, the shape was
    // briefly odd — so they retry rather than halt, and leave the orbs alone.
    [Fact]
    public Task AnUnreadableCredentialRetriesAndKeepsTheOrbs() =>
        ATransientCredentialProblemRetries(CredentialOutcome.Unreadable);

    [Fact]
    public Task AMalformedCredentialRetriesAndKeepsTheOrbs() =>
        ATransientCredentialProblemRetries(CredentialOutcome.Malformed);

    private static async Task ATransientCredentialProblemRetries(CredentialOutcome outcome)
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));
        var good = new[] { Session("session_known") };

        var step = await ClaudeCloudSessions.StepAsync(api,
            new FakeCredentials { Reading = new CredentialRead(outcome, null, null, "…") },
            ClaudeCloudSessions.ArmState.Initial with { Sessions = good },
            Now, CancellationToken.None);

        Assert.Null(step.Snapshot);
        Assert.False(step.Next.Halted);
        Assert.Equal(good, step.Next.Sessions);
        Assert.Equal(Backoff.UnavailableFloor, step.Wait);
    }

    // A Found reading with no token in it is a contradiction, and it must stop
    // rather than send `Bearer `.
    [Fact]
    public async Task AFoundReadingWithNoTokenStillStops()
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));

        var step = await ClaudeCloudSessions.StepAsync(api,
            new FakeCredentials { Reading = new CredentialRead(CredentialOutcome.Found, null, null, "…") },
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.Empty(api.Paths);
        Assert.True(step.Next.Halted);
    }

    // --- the short cycle -----------------------------------------------------

    [Fact]
    public async Task BetweenWalksTheCycleIsPageOneThenOneReadPerKnownSession()
    {
        var api = new FakeApi(path => path.Contains("after_id", StringComparison.Ordinal)
            ? Fail(400)
            : path.EndsWith("session_b", StringComparison.Ordinal)
                ? Ok(Row("session_b"))
                : Ok(Envelope(new[] { Row("session_a") })));

        var state = ClaudeCloudSessions.ArmState.Initial with
        {
            LastWalkUtc = Now - TimeSpan.FromSeconds(1),
            Sessions = new[] { Session("session_a"), Session("session_b") },
        };

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(), state, Now,
            CancellationToken.None);

        Assert.Equal(new[]
        {
            "/v2/ccr-sessions?limit=100",
            "/v2/ccr-sessions/session_b",
        }, api.Paths);

        Assert.Equal(new[] { "session_a", "session_b" },
            step.Snapshot!.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    // A session that appeared on page one is not asked about again. The cheapest
    // possible optimisation and the one that keeps a short cycle short.
    [Fact]
    public async Task ASessionAlreadyOnPageOneIsNotReadASecondTime()
    {
        var api = new FakeApi(_ => Ok(Envelope(new[] { Row("session_a") })));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with
            {
                LastWalkUtc = Now - TimeSpan.FromSeconds(1),
                Sessions = new[] { Session("session_a") },
            },
            Now, CancellationToken.None);

        Assert.Single(api.Paths);
        Assert.Single(step.Snapshot!);
    }

    // The point of the per-session reads: a cloud session created last month that
    // resumes today does **not** move to the front of a `created_at` DESC roster,
    // so page one alone would show it frozen at whatever the last walk saw.
    [Fact]
    public async Task APerSessionReadIsWhatMovesASessionOffPageOneOutOfIdle()
    {
        var api = new FakeApi(path => path.EndsWith("session_old", StringComparison.Ordinal)
            ? Ok(Row("session_old", status: "running"))
            : Ok(Envelope(new[] { Row("session_new") })));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with
            {
                LastWalkUtc = Now - TimeSpan.FromSeconds(1),
                Sessions = new[] { Session("session_old") },
            },
            Now, CancellationToken.None);

        Assert.Equal("generating", step.Snapshot!.Single(s => s.Id == "session_old").State);
    }

    // A session that has been archived since the last walk is definitively gone,
    // and its orb goes with it.
    [Fact]
    public async Task AnArchivedSessionLosesItsOrbOnTheShortCycle()
    {
        var api = new FakeApi(path => path.EndsWith("session_gone", StringComparison.Ordinal)
            ? Ok(Row("session_gone", status: "archived"))
            : Ok(Envelope(Array.Empty<string>())));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with
            {
                LastWalkUtc = Now - TimeSpan.FromSeconds(1),
                Sessions = new[] { Session("session_gone") },
            },
            Now, CancellationToken.None);

        Assert.Empty(step.Snapshot!);
    }

    // A per-session read that merely failed is **no news**, not a death. The orb
    // survives on what the last deep walk saw, and the next walk is the authority
    // that will drop it if it has really gone.
    [Fact]
    public async Task ARetryablePerSessionFailureLeavesThatOrbAlone()
    {
        var api = new FakeApi(path => path.EndsWith("session_known", StringComparison.Ordinal)
            ? Fail(404)
            : Ok(Envelope(Array.Empty<string>())));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with
            {
                LastWalkUtc = Now - TimeSpan.FromSeconds(1),
                Sessions = new[] { Session("session_known") },
            },
            Now, CancellationToken.None);

        Assert.Equal("session_known", Assert.Single(step.Snapshot!).Id);
    }

    // A per-session read refused for a reason that would stop the arm stops it
    // here too, rather than being refused once per known session.
    [Fact]
    public async Task ADefinitePerSessionRefusalStopsTheWholeCycle()
    {
        var api = new FakeApi(path => path.Contains("/session_", StringComparison.Ordinal)
            ? Fail(401)
            : Ok(Envelope(Array.Empty<string>())));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with
            {
                LastWalkUtc = Now - TimeSpan.FromSeconds(1),
                Sessions = new[] { Session("session_a"), Session("session_b") },
            },
            Now, CancellationToken.None);

        // Page one, then one refusal, and no attempt at the second session.
        Assert.Equal(2, api.Paths.Count);
        Assert.True(step.Next.Halted);
    }

    // Page one failing on a short cycle is the same rule as a walk failing
    // mid-way: the orbs stay, nothing is published, and the arm backs off. Worth
    // its own case because the short cycle reaches it by a different path and a
    // reader could reasonably assume the walk's test covered both.
    [Fact]
    public async Task PageOneFailingOnAShortCycleLeavesTheOrbsAlone()
    {
        var api = new FakeApi(_ => Fail(503));
        var good = new[] { Session("session_known") };

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with
            {
                LastWalkUtc = Now - TimeSpan.FromSeconds(1),
                Sessions = good,
            },
            Now, CancellationToken.None);

        Assert.Single(api.Paths);
        Assert.Null(step.Snapshot);
        Assert.Equal(good, step.Next.Sessions);
        Assert.Equal(Backoff.UnavailableFloor, step.Wait);
    }

    [Fact]
    public async Task AShortCycleDoesNotMoveTheWalkClock()
    {
        var api = new FakeApi(_ => Ok(Envelope(Array.Empty<string>())));
        var lastWalk = Now - TimeSpan.FromSeconds(1);

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with { LastWalkUtc = lastWalk },
            Now, CancellationToken.None);

        Assert.Equal(lastWalk, step.Next.LastWalkUtc);
    }

    // --- the shape change ----------------------------------------------------

    // Rows came back and none carried a kind this version knows. Reported as the
    // filter having stopped matching, never as "no cloud sessions".
    [Fact]
    public async Task AllUnknownKindsReachTheStatusAsAShapeChange()
    {
        var api = new FakeApi(_ => Ok(Envelope(new[]
        {
            Row("session_a", kind: "managed_runtime"),
            Row("session_b", kind: "managed_runtime"),
        })));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.Contains("filter may no longer match", step.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("no cloud sessions", step.Status, StringComparison.Ordinal);
    }

    // --- the canary ----------------------------------------------------------

    // **The token reaches no status string, ever.** Fed through every arm this
    // test can reach: a good walk, a shape change, each refusal, and the halted
    // no-op. The canary is chosen to be findable if it ever leaked.
    [Fact]
    public async Task TheTokenNeverReachesAStatusString()
    {
        var bodies = new[]
        {
            Envelope(new[] { Row("session_a") }),
            Envelope(new[] { Row("session_a").Replace("a session", Token, StringComparison.Ordinal) }),
            "<html>",
            "{\"error\":\"" + Token + "\"}",
        };

        foreach (var body in bodies)
        {
            foreach (var status in new[] { 200, 400, 401, 403, 429, 500 })
            {
                var api = new FakeApi(_ => status == 200
                    ? new CloudApiResult(CloudOutcomes.OutcomeFor(200, body), body)
                    : new CloudApiResult(CloudOutcomes.OutcomeFor(status, body), null));

                var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
                    ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

                Assert.DoesNotContain(Token, step.Status, StringComparison.Ordinal);
                Assert.DoesNotContain(Token, step.Next.Status, StringComparison.Ordinal);
                Assert.DoesNotContain(Token, step.Next.CredentialStamp ?? "", StringComparison.Ordinal);
            }
        }
    }

    // The stamp is a change detector and never secret bytes. Asserted here as well
    // as in the credential tests because this is where it is carried around.
    [Fact]
    public async Task TheStampCarriedThroughTheArmIsWhateverTheSourceGaveAndNotATokens()
    {
        var api = new FakeApi(_ => Ok(Envelope(Array.Empty<string>())));

        var step = await ClaudeCloudSessions.StepAsync(api,
            new FakeCredentials { StampValue = "638000000000000000" },
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.Equal("638000000000000000", step.Next.CredentialStamp);
    }

    // --- the initial state ---------------------------------------------------

    [Fact]
    public void TheInitialStateIsEmptyAndNotHalted()
    {
        var initial = ClaudeCloudSessions.ArmState.Initial;

        Assert.Empty(initial.Sessions);
        Assert.False(initial.Halted);
        Assert.Null(initial.Backoff);
        Assert.Null(initial.CredentialStamp);
        Assert.Equal(default, initial.LastWalkUtc);
    }
}
