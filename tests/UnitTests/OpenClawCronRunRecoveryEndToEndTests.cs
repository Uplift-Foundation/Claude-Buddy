using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeBuddy.Tests;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// CB-115, end to end: OpenClawSessions.FetchHistoryPageAsync's cron-recovery
// pass and OpenClawSessions.RecoverCronMediaPathAsync's paging and caching,
// against a fake gateway that answers both "chat.history" and "cron.runs" —
// the same seam OpenClawLiveImageResolutionTests already established for the
// marker-based live path. What is pure (matching a basename, folding a page
// into a map) is covered on its own in OpenClawCronRecoveryTests; this file
// is the wiring: does the right RPC go out, at the right time, and does the
// right turn come back with a picture.
//
// Every test here asserts a positive, recovered value — a specific path
// coming back for a specific basename — rather than merely "no exception" or
// "returned null gracefully". That distinction is the point: every known
// failure mode in this mechanism (an inert parameter, an off-by-65ms
// timestamp match, a small page limit silently dropping old runs) fails by
// finding *nothing*, so a null-tolerant assertion would have passed against
// all three. Where a test does assert a negative (not found, refused for
// ambiguity), it sits beside a positive case in the same area, so a
// wholesale failure of the mechanism cannot masquerade as "correctly found
// nothing".
[Collection("Settings")]
public class OpenClawCronRunRecoveryEndToEndTests : IDisposable
{
    public void Dispose() => OpenClawSessions.SetGatewayForTests(null);

    private static OpenClawGateway Gateway(WebSocket socket) =>
        new("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(
                new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    private static FakeGatewaySocket Socket(
        Func<FakeGatewaySocket.Request, object> onHistory,
        Func<FakeGatewaySocket.Request, object> onCronRuns)
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });

        socket.OnRequest = request => request.Method switch
        {
            "connect" => FakeGatewaySocket.Ok(request.Id, new
            {
                protocol = 4,
                server = new { version = "1.2.3" },
                auth = new { scopes = new[] { "operator.read" } },
                policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576 }
            }),
            "chat.history" => onHistory(request),
            "cron.runs" => onCronRuns(request),
            _ => FakeGatewaySocket.Ok(request.Id, new { })
        };

        return socket;
    }

    private static async Task<(FakeGatewaySocket Socket, OpenClawChatSession Session)> ConnectedAsync(
        Func<FakeGatewaySocket.Request, object> onHistory,
        Func<FakeGatewaySocket.Request, object> onCronRuns)
    {
        var socket = Socket(onHistory, onCronRuns);
        var gateway = Gateway(socket);

        var result = await gateway.ConnectAsync(null, CancellationToken.None);
        Assert.Equal(OpenClawGateway.Outcome.Connected, result.Outcome);

        OpenClawSessions.SetGatewayForTests(gateway);

        var session = new OpenClawChatSession(
            "openclaw:agent:main:main", "agent:main:main", "main");

        return (socket, session);
    }

    // One assistant message: a caption plus the bare filename the delivery
    // route left behind, tagged with the automation that produced it.
    private static object AutomationMessage(string jobId, string text) => new
    {
        role = "assistant",
        content = text,
        openclawAutomation = new { kind = "cron", jobId, runId = $"cron:{jobId}:1" }
    };

    private static object CronRunsPage(
        object[] entries, bool hasMore = false, int? nextOffset = null) => new
        {
            entries,
            total = entries.Length,
            offset = 0,
            limit = 60,
            hasMore,
            nextOffset
        };

    private static object CronRun(string jobId, string summary) => new { jobId, summary };

    // ---- the happy path: basename found on the first page -----------------

    [Fact]
    public async Task ABareFilenameCaptionRecoversItsRealPathFromTheCronRun()
    {
        var (_, session) = await ConnectedAsync(
            onHistory: request => FakeGatewaySocket.Ok(request.Id, new
            {
                messages = new[]
                {
                    AutomationMessage("job-bare-1",
                        "sunset over the harbour today\nposter_sunset_1.png")
                }
            }),
            onCronRuns: request => FakeGatewaySocket.Ok(request.Id, CronRunsPage(new[]
            {
                CronRun("job-bare-1", "sunset over the harbour today\n"
                                + "MEDIA:/home/agent/outputs/posters/poster_sunset_1.png")
            })));

        var page = await OpenClawSessions.FetchPageAsync(session, 0, CancellationToken.None);

        var turn = Assert.Single(page!.Value.Turns);
        Assert.NotNull(turn.ImageUrl);
        Assert.Equal("/home/agent/outputs/posters/poster_sunset_1.png", turn.ImageSourcePath);
        Assert.Equal("poster_sunset_1.png", turn.ImageAlt);
    }

    // CB-116: a path recovered from the cron run that produced the delivery
    // is the most confirmed of the five provenance tiers — High, and
    // restated explicitly by FetchHistoryPageAsync's own override rather
    // than left to whatever TurnsFromHistory already set. This is that
    // override's own regression pin, distinct from the (already-High, by
    // construction) pre-recovery value: a recovered turn's Automation is
    // always non-null, so TurnsFromHistory's own tiering would already say
    // High here without the override — this test exists so that if a future
    // change ever lets recovery run on a turn TurnsFromHistory tiered Low,
    // the override still wins rather than silently inheriting it.
    [Fact]
    public async Task ARecoveredPathIsHighConfidence()
    {
        var (_, session) = await ConnectedAsync(
            onHistory: request => FakeGatewaySocket.Ok(request.Id, new
            {
                messages = new[]
                {
                    AutomationMessage("job-bare-confidence",
                        "sunset over the harbour today\nposter_sunset_2.png")
                }
            }),
            onCronRuns: request => FakeGatewaySocket.Ok(request.Id, CronRunsPage(new[]
            {
                CronRun("job-bare-confidence", "sunset over the harbour today\n"
                                + "MEDIA:/home/agent/outputs/posters/poster_sunset_2.png")
            })));

        var page = await OpenClawSessions.FetchPageAsync(session, 0, CancellationToken.None);

        var turn = Assert.Single(page!.Value.Turns);
        Assert.Equal("/home/agent/outputs/posters/poster_sunset_2.png", turn.ImageSourcePath);
        Assert.Equal(MediaConfidence.High, turn.Confidence);
    }

    // ---- efficiency: the two things that must cost nothing -----------------

    // The most valuable assertion in this ticket, per its own brief: a turn
    // whose picture already resolved through an earlier arm (the delivered
    // mirror, here) must not cost a cron.runs round trip at all, even though
    // it carries the same openclawAutomation as a turn that does need one.
    [Fact]
    public async Task ATurnThatAlreadyResolvedItsPictureNeverAsksCronRuns()
    {
        var asked = false;

        var (_, session) = await ConnectedAsync(
            onHistory: request => FakeGatewaySocket.Ok(request.Id, new
            {
                messages = new[]
                {
                    new
                    {
                        role = "assistant",
                        model = "delivery-mirror",
                        content = "already-delivered.png",
                        openclawAutomation = new { kind = "cron", jobId = "job-skip-resolved" }
                    }
                }
            }),
            onCronRuns: request =>
            {
                asked = true;
                return FakeGatewaySocket.Ok(request.Id, CronRunsPage(Array.Empty<object>()));
            });

        var page = await OpenClawSessions.FetchPageAsync(session, 0, CancellationToken.None);

        Assert.False(asked);
        Assert.NotNull(Assert.Single(page!.Value.Turns).ImageUrl);
    }

    // A text-only cron reply — a heartbeat — must not cost a lookup either:
    // its trailing token is not image-shaped, which is the trigger's second
    // conjunct failing.
    [Fact]
    public async Task ATextOnlyAutomationTurnNeverAsksCronRuns()
    {
        var asked = false;

        var (_, session) = await ConnectedAsync(
            onHistory: request => FakeGatewaySocket.Ok(request.Id, new
            {
                messages = new[] { AutomationMessage("job-skip-textonly", "all systems nominal, nothing to report") }
            }),
            onCronRuns: request =>
            {
                asked = true;
                return FakeGatewaySocket.Ok(request.Id, CronRunsPage(Array.Empty<object>()));
            });

        var page = await OpenClawSessions.FetchPageAsync(session, 0, CancellationToken.None);

        Assert.False(asked);
Assert.Null(Assert.Single(page!.Value.Turns).ImageUrl);
    }

    // One RPC per job, not per picture: two turns from the same job cost
    // exactly one cron.runs round trip.
    [Fact]
    public async Task TwoPicturesFromTheSameJobCostOneCronRunsRequest()
    {
        var requests = 0;

        var (_, session) = await ConnectedAsync(
            onHistory: request => FakeGatewaySocket.Ok(request.Id, new
            {
                messages = new[]
                {
                    AutomationMessage("job-two-pics", "first\nfirst.png"),
                    AutomationMessage("job-two-pics", "second\nsecond.png")
                }
            }),
            onCronRuns: request =>
            {
                Interlocked.Increment(ref requests);
                return FakeGatewaySocket.Ok(request.Id, CronRunsPage(new[]
                {
                    CronRun("job-two-pics", "first\nMEDIA:/home/agent/first.png"),
                    CronRun("job-two-pics", "second\nMEDIA:/home/agent/second.png")
                }));
            });

        var page = await OpenClawSessions.FetchPageAsync(session, 0, CancellationToken.None);

        Assert.Equal(1, requests);
        Assert.All(page!.Value.Turns, t => Assert.NotNull(t.ImageUrl));
        Assert.Equal("/home/agent/first.png", page.Value.Turns[0].ImageSourcePath);
        Assert.Equal("/home/agent/second.png", page.Value.Turns[1].ImageSourcePath);
    }

    // A picture delivered AFTER the job was cached still recovers — the case
    // that made this feature useless in ongoing use and that no other test
    // could see, since every one of them starts with an empty cache and a
    // fresh jobId.
    //
    // Owner's job fires about every 25 minutes. Reading a cached miss as
    // "absent" meant he opened the panel, fourteen pictures recovered, and
    // then every later delivery silently took the wrong-directory guess —
    // within half an hour, and failing the way everything in this mechanism
    // fails: by finding nothing.
    //
    // Two positive witnesses, because one alone would pass against the bug:
    // the second picture resolves to its real path, AND a second cron.runs
    // request actually happened.
    [Fact]
    public async Task APictureDeliveredAfterTheJobWasCachedStillRecovers()
    {
        var requests = 0;
        var secondDelivered = false;

        var (_, session) = await ConnectedAsync(
            onHistory: request => FakeGatewaySocket.Ok(request.Id, new
            {
                messages = secondDelivered
                    ? new[]
                    {
                        AutomationMessage("job-later", "first\nfirst.png"),
                        AutomationMessage("job-later", "later\nlater.png")
                    }
                    : new[] { AutomationMessage("job-later", "first\nfirst.png") }
            }),
            onCronRuns: request =>
            {
                Interlocked.Increment(ref requests);

                var runs = secondDelivered
                    ? new[]
                    {
                        CronRun("job-later", "first\nMEDIA:/home/agent/first.png"),
                        CronRun("job-later", "later\nMEDIA:/home/agent/later.png")
                    }
                    : new[] { CronRun("job-later", "first\nMEDIA:/home/agent/first.png") };

                return FakeGatewaySocket.Ok(request.Id, CronRunsPage(runs));
            });

        var first = await OpenClawSessions.FetchPageAsync(session, 0, CancellationToken.None);
        Assert.Equal("/home/agent/first.png", Assert.Single(first!.Value.Turns).ImageSourcePath);
        Assert.Equal(1, requests);

        // The job is now cached. A later run arrives.
        secondDelivered = true;

        var second = await OpenClawSessions.FetchPageAsync(session, 0, CancellationToken.None);

        Assert.Equal("/home/agent/later.png", second!.Value.Turns[1].ImageSourcePath);
        Assert.Equal(2, requests);
    }

    // The other half of that fix: a basename this job has genuinely never
    // delivered is remembered as absent, so re-asking on a cached miss does
    // not become a cron.runs request on every render. Without this the fix
    // above is the same bug inverted.
    [Fact]
    public async Task ABasenameTheJobNeverDeliveredIsAskedForOnceAndThenRemembered()
    {
        var requests = 0;

        var (_, session) = await ConnectedAsync(
            onHistory: request => FakeGatewaySocket.Ok(request.Id, new
            {
                messages = new[] { AutomationMessage("job-never", "nope\nnever_sent.png") }
            }),
            onCronRuns: request =>
            {
                Interlocked.Increment(ref requests);
                return FakeGatewaySocket.Ok(request.Id, CronRunsPage(new[]
                {
                    CronRun("job-never", "something else\nMEDIA:/home/agent/other.png")
                }));
            });

        await OpenClawSessions.FetchPageAsync(session, 0, CancellationToken.None);
        await OpenClawSessions.FetchPageAsync(session, 0, CancellationToken.None);
        var third = await OpenClawSessions.FetchPageAsync(session, 0, CancellationToken.None);

        Assert.Equal(1, requests);
        Assert.StartsWith("~/.openclaw/media/", Assert.Single(third!.Value.Turns).ImageSourcePath);
    }

    // ---- paging: a picture whose run fell off the first page ---------------

    // Needs more history than one page to mean anything — a paging loop that
    // exists but never runs proves nothing. The target basename sits only on
    // the second page; a version that read one page and stopped would find
    // nothing, and this asserts the specific recovered path rather than
    // merely "not null" so a broken loop can't pass by accident.
    [Fact]
    public async Task APictureWhoseRunFellOffTheFirstPageIsStillFoundByPaging()
    {
        var pagesRequested = 0;

        var (_, session) = await ConnectedAsync(
            onHistory: request => FakeGatewaySocket.Ok(request.Id, new
            {
                messages = new[] { AutomationMessage("job-paging-fallback", "old drop\nold_pic.png") }
            }),
            onCronRuns: request =>
            {
                pagesRequested++;
                var offset = request.Params.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;

                return offset == 0
                    ? FakeGatewaySocket.Ok(request.Id, CronRunsPage(
                        new[] { CronRun("job-paging-fallback", "recent\nMEDIA:/home/agent/recent.png") },
                        hasMore: true, nextOffset: 60))
                    : FakeGatewaySocket.Ok(request.Id, CronRunsPage(
                        new[] { CronRun("job-paging-fallback", "old drop\nMEDIA:/home/agent/old_pic.png") }));
            });

        var page = await OpenClawSessions.FetchPageAsync(session, 0, CancellationToken.None);

        Assert.Equal(2, pagesRequested);
        Assert.Equal("/home/agent/old_pic.png", Assert.Single(page!.Value.Turns).ImageSourcePath);
    }

    // The bound on paging: a job whose history never says hasMore:false
    // within the bound does not loop forever.
    [Fact]
    public async Task PagingStopsAtItsBoundRatherThanLoopingForever()
    {
        var pagesRequested = 0;

        var (_, session) = await ConnectedAsync(
            onHistory: request => FakeGatewaySocket.Ok(request.Id, new
            {
                messages = new[] { AutomationMessage("job-paging-bound", "caption\nnever_seen.png") }
            }),
            onCronRuns: request =>
            {
                pagesRequested++;
                var offset = request.Params.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
                return FakeGatewaySocket.Ok(request.Id, CronRunsPage(
                    new[] { CronRun("job-paging-bound", $"page at {offset}\nno media here") },
                    hasMore: true, nextOffset: offset + 60));
            });

        var page = await OpenClawSessions.FetchPageAsync(session, 0, CancellationToken.None);

        Assert.Equal(5, pagesRequested);
        // CB-115: ImageUrl is no longer the witness for "recovery did not
        // fire". Since CB-107 landed on develop, the named-path arm resolves a
        // bare filename to the ~/.openclaw/media/ guess and sets a route for
        // it independently of this feature — so Assert.Null(ImageUrl) asserted
        // pre-CB-107 behaviour, not this test's subject. ImageSourcePath is
        // the witness that separates them: the guess means no recovery, a
        // rooted run-record path means recovery happened.
        Assert.StartsWith("~/.openclaw/media/", Assert.Single(page!.Value.Turns).ImageSourcePath);
    }

    // ---- ambiguity and absence ---------------------------------------------

    [Fact]
    public async Task ABasenameNeverDeliveredByTheJobLeavesTheTurnAsTextOnly()
    {
        var (_, session) = await ConnectedAsync(
            onHistory: request => FakeGatewaySocket.Ok(request.Id, new
            {
                messages = new[] { AutomationMessage("job-not-found", "caption\nmissing.png") }
            }),
            onCronRuns: request => FakeGatewaySocket.Ok(request.Id, CronRunsPage(new[]
            {
                CronRun("job-not-found", "unrelated\nMEDIA:/home/agent/unrelated.png")
            })));

        var page = await OpenClawSessions.FetchPageAsync(session, 0, CancellationToken.None);

        // CB-115: ImageUrl is no longer the witness for "recovery did not
        // fire". Since CB-107 landed on develop, the named-path arm resolves a
        // bare filename to the ~/.openclaw/media/ guess and sets a route for
        // it independently of this feature — so Assert.Null(ImageUrl) asserted
        // pre-CB-107 behaviour, not this test's subject. ImageSourcePath is
        // the witness that separates them: the guess means no recovery, a
        // rooted run-record path means recovery happened.
        Assert.StartsWith("~/.openclaw/media/", Assert.Single(page!.Value.Turns).ImageSourcePath);
    }

    [Fact]
    public async Task TwoRunsClaimingTheSameBasenameWithDifferentPathsRefuseRatherThanGuess()
    {
        var (_, session) = await ConnectedAsync(
            onHistory: request => FakeGatewaySocket.Ok(request.Id, new
            {
                messages = new[] { AutomationMessage("job-ambiguous", "caption\ndup.png") }
            }),
            onCronRuns: request => FakeGatewaySocket.Ok(request.Id, CronRunsPage(new[]
            {
                CronRun("job-ambiguous", "MEDIA:/home/agent/poster/dup.png"),
                CronRun("job-ambiguous", "MEDIA:/home/agent/nova/dup.png")
            })));

        var page = await OpenClawSessions.FetchPageAsync(session, 0, CancellationToken.None);

        // CB-115: ImageUrl is no longer the witness for "recovery did not
        // fire". Since CB-107 landed on develop, the named-path arm resolves a
        // bare filename to the ~/.openclaw/media/ guess and sets a route for
        // it independently of this feature — so Assert.Null(ImageUrl) asserted
        // pre-CB-107 behaviour, not this test's subject. ImageSourcePath is
        // the witness that separates them: the guess means no recovery, a
        // rooted run-record path means recovery happened.
        Assert.StartsWith("~/.openclaw/media/", Assert.Single(page!.Value.Turns).ImageSourcePath);
    }

    // ---- the live path ------------------------------------------------------

    private static JsonElement AgentText(string text) =>
        JsonDocument.Parse($"{{\"data\":{{\"text\":{JsonSerializer.Serialize(text)}}}}}").RootElement;

    // A live streamed reply ending in what looks like an image reference asks
    // chat.history (the same plumbing MediaAttachedMarker already triggers),
    // and the real recovery happens there against the true
    // openclawAutomation on the fetched message.
    [Fact]
    public async Task ALiveReplyEndingInABareFilenameResolvesThroughARefetch()
    {
        var (_, session) = await ConnectedAsync(
            onHistory: request => FakeGatewaySocket.Ok(request.Id, new
            {
                messages = new[]
                {
                    AutomationMessage("job-live", "here you go\nlive_pic.png")
                }
            }),
            onCronRuns: request => FakeGatewaySocket.Ok(request.Id, CronRunsPage(new[]
            {
                CronRun("job-live", "here you go\nMEDIA:/home/agent/live_pic.png")
            })));

        session.OnAgentEvent("agent", AgentText("here you go\nlive_pic.png"));

        for (var i = 0; i < 50 && session.History[0].ImageUrl is null; i++)
            await Task.Delay(10);

        Assert.Equal("/home/agent/live_pic.png", session.History[0].ImageSourcePath);
    }

    // An ordinary live reply — no marker, no local-media path, no trailing
    // image-shaped token — must never ask chat.history at all. This is the
    // happy path the whole trigger design exists to keep free.
    [Fact]
    public async Task AnOrdinaryLiveReplyNeverAsksChatHistory()
    {
        var asked = false;

        var (_, session) = await ConnectedAsync(
            onHistory: request =>
            {
                asked = true;
                return FakeGatewaySocket.Ok(request.Id, new { messages = Array.Empty<object>() });
            },
            onCronRuns: request => FakeGatewaySocket.Ok(request.Id, CronRunsPage(Array.Empty<object>())));

        session.OnAgentEvent("agent", AgentText("just an ordinary reply, nothing attached"));
        await Task.Delay(50);

        Assert.False(asked);
    }
}
