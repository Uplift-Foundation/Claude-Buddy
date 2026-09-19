using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers reading the roster: the envelope, the filter, the state, the link, and
// the two ways an empty answer can be a bug rather than an answer.
//
// **Every value in these fixtures is invented.** The structure is real, derived
// field by field from a captured payload including the fields observed absent;
// the ids, titles and instants are not. Same rule as OpenClawGatewayPayloadTests,
// and for the same reason — an account's cloud sessions are not all work.
public class ClaudeCloudRosterTests
{
    // --- fixture helpers -----------------------------------------------------

    // Assembled by concatenation rather than by interpolation. These rows are dense
    // with braces and a raw interpolated string whose content ends in `}}` is a
    // counting exercise; the shape is the point of the fixture, and it should be
    // readable.
    //
    // **`session_url` and `session_context.cwd` are empty on purpose.** They were
    // empty on all 578 rows measured, which is why the link is built from the id,
    // and a fixture that quietly filled them in would let a regression there pass.
    private static string Row(string id, string kind, string status,
        string bucket = "idle", string title = "a session", string updated = "2026-09-19T10:00:00Z",
        string extra = "") =>
        "{\"id\":\"" + id + "\",\"environment_kind\":\"" + kind
        + "\",\"session_status\":\"" + status + "\",\"status_bucket\":\"" + bucket
        + "\",\"title\":\"" + title + "\",\"updated_at\":\"" + updated
        + "\",\"created_at\":\"2026-09-01T00:00:00Z\",\"session_url\":\"\""
        + ",\"session_context\":{\"cwd\":\"\"}" + extra + "}";

    private static string Envelope(IEnumerable<string> rows, bool hasMore = false,
        string? lastId = null)
    {
        var last = lastId is null ? "null" : "\"" + lastId + "\"";
        return "{\"data\":[" + string.Join(",", rows)
               + "],\"first_id\":\"session_first\",\"has_more\":"
               + (hasMore ? "true" : "false") + ",\"last_id\":" + last + "}";
    }

    // --- the envelope --------------------------------------------------------

    [Fact]
    public void APageCarriesItsRowsItsCursorAndItsInspectedCount()
    {
        var page = ClaudeCloudRoster.ParsePage(Envelope(
            new[] { Row("session_a", "anthropic_cloud", "running"), Row("session_b", "bridge", "idle") },
            hasMore: true, lastId: "session_b"));

        Assert.Equal(ClaudeCloudRoster.PageShape.Ok, page.Shape);
        Assert.Equal(2, page.Rows.Count);
        Assert.Equal(2, page.Inspected);
        Assert.True(page.HasMore);
        Assert.Equal("session_b", page.LastId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("<!DOCTYPE html>")]
    public void ABodyThatIsNotJsonSaysSo(string? body)
    {
        var page = ClaudeCloudRoster.ParsePage(body);

        Assert.Equal(ClaudeCloudRoster.PageShape.NotJson, page.Shape);
        Assert.Empty(page.Rows);
    }

    // The envelope losing its `data` array is the shape moving, not an empty
    // roster. Those two produce the same number of orbs and mean opposite things.
    [Theory]
    [InlineData("""{"has_more":false}""")]
    [InlineData("""{"data":{"session_a":{}}}""")]
    [InlineData("""{"data":"nope"}""")]
    [InlineData("""[]""")]
    public void AnEnvelopeWithNoDataArrayIsAShapeChange(string body)
    {
        Assert.Equal(ClaudeCloudRoster.PageShape.NoData, ClaudeCloudRoster.ParsePage(body).Shape);
    }

    // A row without the two fields every decision is made on is refused, and the
    // page says so. Defaulting the field would be the quiet version of this.
    [Theory]
    [InlineData("""{"id":"session_a","session_status":"running"}""")]
    [InlineData("""{"id":"session_a","environment_kind":"anthropic_cloud"}""")]
    [InlineData("""{"environment_kind":"anthropic_cloud","session_status":"running"}""")]
    [InlineData("""{"id":"","environment_kind":"anthropic_cloud","session_status":"running"}""")]
    [InlineData("\"a string, not an object\"")]
    public void ARowMissingItsKindOrStatusIsAShapeChange(string row)
    {
        var page = ClaudeCloudRoster.ParsePage("{\"data\":[" + row + "]}");

        Assert.Equal(ClaudeCloudRoster.PageShape.RowMissingFields, page.Shape);
        Assert.Empty(page.Rows);
        Assert.Equal(1, page.Inspected);
    }

    // Degrading rather than breaking: the good rows survive a bad neighbour, and
    // the page still says the shape moved.
    [Fact]
    public void AGoodRowSurvivesABadNeighbourAndTheShapeIsStillReported()
    {
        var page = ClaudeCloudRoster.ParsePage(Envelope(new[]
        {
            Row("session_good", "anthropic_cloud", "running"),
            """{"id":"session_bad"}""",
        }));

        Assert.Equal(ClaudeCloudRoster.PageShape.RowMissingFields, page.Shape);
        Assert.Equal("session_good", Assert.Single(page.Rows).Id);
        Assert.Equal(2, page.Inspected);
    }

    [Fact]
    public void ABlankLastIdIsNoCursorAtAll()
    {
        var page = ClaudeCloudRoster.ParsePage(
            """{"data":[],"has_more":true,"last_id":"  "}""");

        Assert.Null(page.LastId);
    }

    // --- the fields ----------------------------------------------------------

    [Fact]
    public void TheMetadataFieldsTheOrbDrawsAreRead()
    {
        var page = ClaudeCloudRoster.ParsePage(Envelope(new[]
        {
            Row("session_a", "anthropic_cloud", "blocked", bucket: "blocked", title: "a title",
                extra: ",\"external_metadata\":{\"model\":\"claude-opus-5\","
                       + "\"context_usage\":{\"max_tokens\":200000,\"used_tokens\":50000},"
                       + "\"post_turn_summary\":{\"needs_action\":true,"
                       + "\"status_detail\":\"waiting on you\","
                       + "\"recent_action\":\"ran the tests\"}}"),
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal("a title", row.Title);
        Assert.Equal("claude-opus-5", row.Model);
        Assert.Equal(25, row.ContextPercent);
        Assert.True(row.NeedsAction);
        Assert.Equal("waiting on you", row.StatusDetail);
        Assert.Equal("ran the tests", row.RecentAction);
    }

    // `post_turn_summary` is **null on every bridge row measured** and a dict on
    // cloud rows, so its absence is ordinary rather than a shape change.
    [Fact]
    public void ANullPostTurnSummaryIsOrdinary()
    {
        var page = ClaudeCloudRoster.ParsePage(Envelope(new[]
        {
            Row("session_a", "bridge", "idle",
                extra: ",\"external_metadata\":{\"post_turn_summary\":null,"
                       + "\"model\":\"claude-sonnet-5\"}"),
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal(ClaudeCloudRoster.PageShape.Ok, page.Shape);
        Assert.False(row.NeedsAction);
        Assert.Null(row.StatusDetail);
        Assert.Equal("claude-sonnet-5", row.Model);
    }

    // A zero or absent maximum is "we do not know", not "0%". The ring drawn for
    // those two is different.
    [Theory]
    [InlineData("")]
    [InlineData(",\"external_metadata\":{}")]
    [InlineData(",\"external_metadata\":{\"context_usage\":{\"max_tokens\":0,\"used_tokens\":10}}")]
    [InlineData(",\"external_metadata\":{\"context_usage\":{\"used_tokens\":10}}")]
    [InlineData(",\"external_metadata\":{\"context_usage\":{\"max_tokens\":100}}")]
    [InlineData(",\"external_metadata\":{\"context_usage\":null}")]
    [InlineData(",\"external_metadata\":{\"context_usage\":{\"max_tokens\":\"lots\",\"used_tokens\":10}}")]
    [InlineData(",\"external_metadata\":{\"context_usage\":{\"max_tokens\":100,\"used_tokens\":\"some\"}}")]
    public void AnUnusableContextUsageIsNullRatherThanZero(string extra)
    {
        var body = Envelope(new[] { Row("session_a", "anthropic_cloud", "idle", extra: extra) });

        Assert.Null(Assert.Single(ClaudeCloudRoster.ParsePage(body).Rows).ContextPercent);
    }

    [Fact]
    public void AContextUsageOverItsOwnMaximumIsCappedRatherThanReportedAbsurd()
    {
        var page = ClaudeCloudRoster.ParsePage(Envelope(new[]
        {
            Row("session_a", "anthropic_cloud", "idle",
                extra: ",\"external_metadata\":{\"context_usage\":"
                       + "{\"max_tokens\":100,\"used_tokens\":250}}"),
        }));

        Assert.Equal(100, Assert.Single(page.Rows).ContextPercent);
    }

    // **MinValue, never now.** Stamping the read time would give every session the
    // account has ever had a permanent orb, because the recency window would be
    // measuring a time this app invented.
    [Theory]
    [InlineData("")]
    [InlineData("not a date")]
    public void AnUnreadableUpdatedAtIsMinValueAndNotNow(string updated)
    {
        var page = ClaudeCloudRoster.ParsePage(
            Envelope(new[] { Row("session_a", "anthropic_cloud", "idle", updated: updated) }));

        Assert.Equal(DateTime.MinValue, Assert.Single(page.Rows).UpdatedAt);
    }

    [Fact]
    public void AnUpdatedAtIsReadAsUtc()
    {
        var page = ClaudeCloudRoster.ParsePage(Envelope(new[]
        {
            Row("session_a", "anthropic_cloud", "idle", updated: "2026-09-19T10:30:00Z"),
        }));

        Assert.Equal(new DateTime(2026, 9, 19, 10, 30, 0, DateTimeKind.Utc),
            Assert.Single(page.Rows).UpdatedAt);
    }

    // --- the filter ----------------------------------------------------------

    [Theory]
    [InlineData("anthropic_cloud", "running", true)]
    [InlineData("anthropic_cloud", "idle", true)]
    [InlineData("anthropic_cloud", "blocked", true)]
    [InlineData("anthropic_cloud", "archived", false)]
    [InlineData("anthropic_cloud", "ARCHIVED", false)]
    [InlineData("bridge", "running", false)]
    [InlineData("bridge", "archived", false)]
    [InlineData("something_new", "running", false)]
    [InlineData("ANTHROPIC_CLOUD", "running", false)]
    public void KeepIsCloudAndNotArchived(string kind, string status, bool kept)
    {
        var page = ClaudeCloudRoster.ParsePage(Envelope(new[] { Row("session_a", kind, status) }));

        Assert.Equal(kept, ClaudeCloudRoster.Keep(Assert.Single(page.Rows)));
    }

    // **The measurement this feature rests on, at scale, with bridge as the
    // negative control.** 573 bridge rows are the user's own local sessions,
    // already drawn from hooks — keeping them would double every local orb, and
    // the doubling would read as a layout bug rather than a filter bug. Of the 5
    // cloud rows, 4 are archived. Exactly one orb.
    [Fact]
    public void AFullRosterOfFiveSevenThreeBridgeAndFiveCloudKeepsExactlyOne()
    {
        var rows = new List<string>();
        for (var i = 0; i < 573; i++)
        {
            rows.Add(Row($"session_bridge{i:D4}", "bridge", i % 3 == 0 ? "running" : "idle"));
        }

        rows.Add(Row("session_cloud0", "anthropic_cloud", "archived"));
        rows.Add(Row("session_cloud1", "anthropic_cloud", "archived"));
        rows.Add(Row("session_cloud2", "anthropic_cloud", "archived"));
        rows.Add(Row("session_cloud3", "anthropic_cloud", "archived"));
        rows.Add(Row("session_cloud4", "anthropic_cloud", "running"));

        var reduction = ClaudeCloudRoster.Reduce(
            new[] { ClaudeCloudRoster.ParsePage(Envelope(rows)) }, false);

        Assert.Equal(578, reduction.Inspected);
        Assert.Equal("session_cloud4", Assert.Single(reduction.Sessions).Id);
        Assert.Equal(573, reduction.Kinds["bridge"]);
        Assert.Equal(5, reduction.Kinds["anthropic_cloud"]);
        Assert.False(reduction.ShapeChanged);
        Assert.Empty(reduction.UnknownKinds);

        // And the sentence carries the count of what was inspected, so a reader
        // can tell a working filter finding nothing from a fetch reading nothing.
        Assert.Contains("578 sessions inspected", ClaudeCloudRoster.Describe(reduction),
            StringComparison.Ordinal);
    }

    // --- the state -----------------------------------------------------------

    [Theory]
    [InlineData("running", "", "generating")]
    [InlineData("", "running", "generating")]
    [InlineData("blocked", "", "waiting")]
    [InlineData("", "blocked", "waiting")]
    [InlineData("review_ready", "", "waiting")]
    [InlineData("", "review_ready", "waiting")]
    [InlineData("idle", "idle", "idle")]
    public void StateForMapsTheMeasuredValues(string status, string bucket, string expected)
    {
        Assert.Equal(expected, ClaudeCloudRoster.StateFor(status, bucket));
    }

    // Needing attention outranks being busy. A session that is both is one
    // somebody has to go and deal with, and "generating" would hide that.
    [Fact]
    public void NeedingAttentionOutranksBeingBusy()
    {
        Assert.Equal("waiting", ClaudeCloudRoster.StateFor("running", "blocked"));
        Assert.Equal("waiting", ClaudeCloudRoster.StateFor("blocked", "running"));
    }

    // **The rule that matters.** "generating" is the state that animates, so a
    // value this version has never heard of defaulting to it would turn a server
    // shape change into every cloud session appearing permanently busy. Falling to
    // "idle" makes the same change show up as orbs that sit still — wrong in the
    // direction nobody acts on.
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("in_progress", "in_progress")]
    [InlineData("executing", "busy")]
    [InlineData("streaming", "active")]
    [InlineData("running_turn", "running_turn")]
    [InlineData("generating", "generating")]
    public void AnUnrecognisedValueIsNeverGenerating(string? status, string? bucket)
    {
        Assert.NotEqual("generating", ClaudeCloudRoster.StateFor(status, bucket));
    }

    // The case-insensitive arms are deliberate and asserted, so the theory above
    // is not quietly passing because *everything* returns idle.
    [Fact]
    public void TheRecognisedValuesAreMatchedRegardlessOfCase()
    {
        Assert.Equal("generating", ClaudeCloudRoster.StateFor("Running", null));
        Assert.Equal("waiting", ClaudeCloudRoster.StateFor("Review_Ready", null));
    }

    // --- the link ------------------------------------------------------------

    [Fact]
    public void AGoodIdBecomesAClaudeAiCodeUrl()
    {
        Assert.Equal("https://claude.ai/code/session_01ABCdef-_123",
            ClaudeCloudRoster.UrlFor("session_01ABCdef-_123"));
    }

    // `session_url` is empty on every row measured, so the id builds the address —
    // which puts an external string in a URL, and on macOS a URL reaches `open`.
    // An allow-list of the characters an id is made of, not a deny-list of the
    // ones it must not contain.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("session_")]
    [InlineData("sess_01ABC")]
    [InlineData("01ABC")]
    [InlineData("Session_01ABC")]
    [InlineData(" session_01ABC")]
    [InlineData("session_01ABC/../admin")]
    [InlineData("session_01ABC?x=1")]
    [InlineData("session_01ABC#frag")]
    [InlineData("session_01 ABC")]
    [InlineData("session_01ABC\nx")]
    [InlineData("session_01ABC'; open -a Calculator;'")]
    [InlineData("session_01ABC%2F")]
    public void ABadIdGetsNoUrlAtAll(string? id)
    {
        Assert.Null(ClaudeCloudRoster.UrlFor(id));
    }

    // And a row whose id cannot make a URL makes no orb, rather than an orb whose
    // click goes nowhere.
    [Fact]
    public void ARowWithAnUnusableIdIsDroppedRatherThanDrawn()
    {
        var page = ClaudeCloudRoster.ParsePage(Envelope(new[]
        {
            Row("../evil", "anthropic_cloud", "running"),
            Row("session_fine", "anthropic_cloud", "running"),
        }));

        var reduction = ClaudeCloudRoster.Reduce(new[] { page }, false);

        Assert.Equal("session_fine", Assert.Single(reduction.Sessions).Id);
        Assert.Equal(2, reduction.Inspected);
    }

    // --- the silently-empty filter ------------------------------------------

    // **Rows came back and none of them carried a kind this version knows.** That
    // is a filter that has stopped matching, and it is indistinguishable from an
    // account with no cloud sessions unless it is reported differently.
    [Fact]
    public void RowsWithNoKnownKindAtAllAreAShapeChangeAndNotAnEmptyRoster()
    {
        var page = ClaudeCloudRoster.ParsePage(Envelope(new[]
        {
            Row("session_a", "managed_runtime", "running"),
            Row("session_b", "managed_runtime", "idle"),
        }));

        var reduction = ClaudeCloudRoster.Reduce(new[] { page }, false);

        Assert.True(reduction.ShapeChanged);
        Assert.Equal(ClaudeCloudRoster.PageShape.NoKnownKind, reduction.Shape);
        Assert.Empty(reduction.Sessions);

        // And it does **not** also say "no cloud sessions". Printing both invites a
        // reader to take the reassuring half and skip the alarming one.
        var status = ClaudeCloudRoster.Describe(reduction);
        Assert.Contains("filter may no longer match", status, StringComparison.Ordinal);
        Assert.DoesNotContain("no cloud sessions", status, StringComparison.Ordinal);
        Assert.Contains("2 sessions inspected", status, StringComparison.Ordinal);
    }

    // An unknown kind standing *beside* known ones is not a shape change — the
    // filter still works — but it is still mentioned, because it is the earliest
    // warning that something new exists.
    [Fact]
    public void AnUnknownKindBesideKnownOnesKeepsWorkingAndIsStillMentioned()
    {
        var page = ClaudeCloudRoster.ParsePage(Envelope(new[]
        {
            Row("session_a", "anthropic_cloud", "running"),
            Row("session_b", "bridge", "idle"),
            Row("session_c", "managed_runtime", "running"),
        }));

        var reduction = ClaudeCloudRoster.Reduce(new[] { page }, false);

        Assert.False(reduction.ShapeChanged);
        Assert.Equal("session_a", Assert.Single(reduction.Sessions).Id);
        Assert.Equal(new[] { "managed_runtime" }, reduction.UnknownKinds);
        Assert.Contains("managed_runtime", ClaudeCloudRoster.Describe(reduction),
            StringComparison.Ordinal);
    }

    // An empty roster is allowed to say "no sessions came back", which is a
    // different sentence from "no cloud sessions" and means a different thing.
    [Fact]
    public void AnEmptyPageIsNotDescribedAsAWorkingFilterFindingNothing()
    {
        var reduction = ClaudeCloudRoster.Reduce(
            new[] { ClaudeCloudRoster.ParsePage(Envelope(Array.Empty<string>())) }, false);

        Assert.False(reduction.ShapeChanged);
        Assert.Equal("no sessions came back", ClaudeCloudRoster.Describe(reduction));
    }

    // **"No cloud sessions" is never said without the count beside it.**
    [Fact]
    public void NoCloudSessionsAlwaysCarriesTheCountOfWhatWasInspected()
    {
        var reduction = ClaudeCloudRoster.Reduce(new[]
        {
            ClaudeCloudRoster.ParsePage(Envelope(new[]
            {
                Row("session_a", "bridge", "running"),
                Row("session_b", "bridge", "idle"),
            })),
        }, false);

        Assert.Equal("no cloud sessions (2 sessions inspected)", ClaudeCloudRoster.Describe(reduction));
    }

    [Fact]
    public void ASingleCloudSessionIsDescribedInTheSingular()
    {
        var reduction = ClaudeCloudRoster.Reduce(new[]
        {
            ClaudeCloudRoster.ParsePage(Envelope(new[] { Row("session_a", "anthropic_cloud", "running") })),
        }, false);

        Assert.Equal("1 cloud session (1 sessions inspected)", ClaudeCloudRoster.Describe(reduction));
    }

    // Truncation is said out loud. A cap that silently showed fewer sessions would
    // be the same class of bug as the filter getting it wrong, and harder to spot.
    [Fact]
    public void ATruncatedWalkSaysSo()
    {
        var reduction = ClaudeCloudRoster.Reduce(new[]
        {
            ClaudeCloudRoster.ParsePage(Envelope(new[] { Row("session_a", "anthropic_cloud", "running") })),
        }, truncated: true);

        var status = ClaudeCloudRoster.Describe(reduction);

        Assert.Contains($"stopped after {CloudRequest.MaxPagesPerWalk} pages", status,
            StringComparison.Ordinal);
        Assert.Contains("there may be more", status, StringComparison.Ordinal);
    }

    [Fact]
    public void ANotJsonPageIsDescribedAsSuchRatherThanAsAnEmptyRoster()
    {
        var reduction = ClaudeCloudRoster.Reduce(
            new[] { ClaudeCloudRoster.ParsePage("<html>") }, false);

        Assert.True(reduction.ShapeChanged);
        Assert.Contains("did not come back as JSON", ClaudeCloudRoster.Describe(reduction),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingDataArrayIsDescribedAsSuch()
    {
        var reduction = ClaudeCloudRoster.Reduce(
            new[] { ClaudeCloudRoster.ParsePage("""{"has_more":false}""") }, false);

        Assert.Contains("without a `data` array", ClaudeCloudRoster.Describe(reduction),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ARowMissingFieldsIsDescribedAcrossTheWholeReduction()
    {
        var reduction = ClaudeCloudRoster.Reduce(new[]
        {
            ClaudeCloudRoster.ParsePage(Envelope(new[] { Row("session_a", "anthropic_cloud", "running") })),
            ClaudeCloudRoster.ParsePage("""{"data":[{"id":"session_b"}]}"""),
        }, false);

        Assert.True(reduction.ShapeChanged);
        Assert.Contains("without an environment kind", ClaudeCloudRoster.Describe(reduction),
            StringComparison.Ordinal);

        // Still draws the good one. Degrade, do not break.
        Assert.Equal("session_a", Assert.Single(reduction.Sessions).Id);
    }

    // --- ordering ------------------------------------------------------------

    // The payload is `created_at` DESC and **`updated_at` is unsorted**, so the
    // roster's own order is not the order anything happened in. Ordering here is
    // what keeps two reductions over the same sessions from differing for no
    // reason a reader could see.
    [Fact]
    public void SessionsComeBackNewestFirstWhateverOrderThePayloadUsed()
    {
        var reduction = ClaudeCloudRoster.Reduce(new[]
        {
            ClaudeCloudRoster.ParsePage(Envelope(new[]
            {
                Row("session_mid", "anthropic_cloud", "idle", updated: "2026-09-19T10:00:00Z"),
                Row("session_new", "anthropic_cloud", "idle", updated: "2026-09-19T12:00:00Z"),
                Row("session_old", "anthropic_cloud", "idle", updated: "2026-09-19T08:00:00Z"),
            })),
        }, false);

        Assert.Equal(new[] { "session_new", "session_mid", "session_old" },
            reduction.Sessions.Select(s => s.Id));
    }

    [Fact]
    public void TiesAreBrokenByIdSoTheOrderIsTotal()
    {
        var reduction = ClaudeCloudRoster.Reduce(new[]
        {
            ClaudeCloudRoster.ParsePage(Envelope(new[]
            {
                Row("session_b", "anthropic_cloud", "idle", updated: "2026-09-19T10:00:00Z"),
                Row("session_a", "anthropic_cloud", "idle", updated: "2026-09-19T10:00:00Z"),
            })),
        }, false);

        Assert.Equal(new[] { "session_a", "session_b" }, reduction.Sessions.Select(s => s.Id));
    }

    // --- the plan ------------------------------------------------------------

    [Fact]
    public void AFirstEverCycleIsADeepWalk()
    {
        var plan = ClaudeCloudRoster.PlanFor(default, new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc),
            Array.Empty<ClaudeCloudSessions.Session>());

        Assert.True(plan.DeepWalk);
        Assert.Empty(plan.RefreshIds);
    }

    [Fact]
    public void AWalkIsDueOnceTheIntervalHasPassed()
    {
        var now = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
        var lastWalk = now - CloudRequest.UnmeasuredWalkInterval;

        Assert.True(ClaudeCloudRoster.PlanFor(lastWalk, now,
            Array.Empty<ClaudeCloudSessions.Session>()).DeepWalk);
    }

    // The short cycle asks about every session already known, by id. The roster is
    // `created_at` DESC, so an older cloud session resuming does not move to the
    // front of page one — without these it would stay invisible, or frozen, until
    // the next walk.
    [Fact]
    public void BetweenWalksTheCycleIsPageOnePlusEveryKnownSession()
    {
        var now = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
        var lastWalk = now - TimeSpan.FromSeconds(1);

        var plan = ClaudeCloudRoster.PlanFor(lastWalk, now, new[]
        {
            Session("session_a"), Session("session_b"),
        });

        Assert.False(plan.DeepWalk);
        Assert.Equal(new[] { "session_a", "session_b" }, plan.RefreshIds);
    }

    private static ClaudeCloudSessions.Session Session(string id,
        DateTime? updated = null, string state = "idle") =>
        new(id, id, state, updated ?? new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc),
            "https://claude.ai/code/" + id, "idle", false, null, null, null, null);

    // --- the merge -----------------------------------------------------------

    // A session this cycle never asked about survives untouched. A short cycle is
    // a partial view by construction, and treating a partial view as authoritative
    // is how an orb disappears for no reason.
    [Fact]
    public void ASessionTheCycleNeverAskedAboutSurvives()
    {
        var known = new[] { Session("session_a"), Session("session_b") };
        var fresh = new[] { Session("session_a", state: "generating") };

        var merged = ClaudeCloudRoster.Merge(known, fresh, new[] { "session_a" });

        Assert.Equal(2, merged.Count);
        Assert.Equal("generating", merged.Single(s => s.Id == "session_a").State);
        Assert.Contains(merged, s => s.Id == "session_b");
    }

    // A session the cycle *did* get a definitive answer about, and which is no
    // longer kept, has genuinely ended. Its orb goes.
    [Fact]
    public void ASessionResolvedAndNotKeptIsDropped()
    {
        var known = new[] { Session("session_a"), Session("session_b") };
        var fresh = new[] { Session("session_a") };

        var merged = ClaudeCloudRoster.Merge(known, fresh, new[] { "session_a", "session_b" });

        Assert.Equal("session_a", Assert.Single(merged).Id);
    }

    [Fact]
    public void AFreshSessionNobodyKnewAboutIsAdded()
    {
        var merged = ClaudeCloudRoster.Merge(
            Array.Empty<ClaudeCloudSessions.Session>(),
            new[] { Session("session_new") },
            new[] { "session_new" });

        Assert.Equal("session_new", Assert.Single(merged).Id);
    }

    [Fact]
    public void AMergeIsOrderedTheSameWayAReductionIs()
    {
        var merged = ClaudeCloudRoster.Merge(
            new[] { Session("session_old", new DateTime(2026, 9, 19, 8, 0, 0, DateTimeKind.Utc)) },
            new[] { Session("session_new", new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc)) },
            new[] { "session_new" });

        Assert.Equal(new[] { "session_new", "session_old" }, merged.Select(s => s.Id));
    }

    // --- the single-session read --------------------------------------------

    [Fact]
    public void ASingleSessionBodyParsesAsOneRow()
    {
        var row = ClaudeCloudRoster.ParseSession(Row("session_a", "anthropic_cloud", "running"));

        Assert.NotNull(row);
        Assert.Equal("session_a", row!.Id);
        Assert.Equal("running", row.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>")]
    [InlineData("""{"id":"session_a"}""")]
    [InlineData("""[]""")]
    public void AnUnusableSingleSessionBodyIsNoRowAtAll(string? body)
    {
        Assert.Null(ClaudeCloudRoster.ParseSession(body));
    }

    // --- the constants -------------------------------------------------------

    [Fact]
    public void TheKindsAreTheMeasuredStrings()
    {
        Assert.Equal("anthropic_cloud", ClaudeCloudRoster.CloudKind);
        Assert.Equal("bridge", ClaudeCloudRoster.BridgeKind);
        Assert.Equal("archived", ClaudeCloudRoster.ArchivedStatus);
        Assert.True(ClaudeCloudRoster.IsKnownKind("bridge"));
        Assert.True(ClaudeCloudRoster.IsKnownKind("anthropic_cloud"));
        Assert.False(ClaudeCloudRoster.IsKnownKind("managed_runtime"));
        Assert.False(ClaudeCloudRoster.IsKnownKind(null));
    }

    [Fact]
    public void AnOkShapeHasNothingToSay()
    {
        Assert.Equal("", ClaudeCloudRoster.DescribeShape(ClaudeCloudRoster.PageShape.Ok));
    }

    // A kept row becomes an orb row carrying everything the orb draws.
    [Fact]
    public void AKeptRowBecomesASessionTheOrbLayerCanDraw()
    {
        var row = ClaudeCloudRoster.ParseSession(Row("session_a", "anthropic_cloud", "running",
            bucket: "running", title: "a title", updated: "2026-09-19T10:00:00Z",
            extra: ",\"external_metadata\":{\"model\":\"claude-opus-5\","
                   + "\"post_turn_summary\":{\"needs_action\":false,"
                   + "\"status_detail\":\"running the tests\","
                   + "\"recent_action\":\"edited a file\"}}"))!;

        var session = ClaudeCloudRoster.ToSession(row);

        Assert.NotNull(session);
        Assert.Equal("session_a", session!.Id);
        Assert.Equal("a title", session.Title);
        Assert.Equal("generating", session.State);
        Assert.Equal("https://claude.ai/code/session_a", session.Url);
        Assert.Equal("running", session.StatusBucket);
        Assert.False(session.NeedsAction);
        Assert.Equal("claude-opus-5", session.Model);
        Assert.Equal("running the tests", session.StatusDetail);
        Assert.Equal("edited a file", session.RecentAction);
        Assert.Equal(new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc), session.LastActivity);
    }
}
