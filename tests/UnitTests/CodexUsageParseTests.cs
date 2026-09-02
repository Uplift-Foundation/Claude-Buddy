using System;
using System.IO;
using Xunit;

namespace ClaudeBuddy.UnitTests;

public class CodexUsageParseTests
{
    // Copied from a live token_count event on this machine, 31 Aug 2026,
    // rollout-2026-08-30T22-20-49-….jsonl, last rate_limits snapshot.
    private const string LiveBothWindows = """
    {
      "limit_id": "codex",
      "primary": {
        "used_percent": 6.0,
        "window_minutes": 300,
        "resets_at": 1788239518
      },
      "secondary": {
        "used_percent": 3.0,
        "window_minutes": 10080,
        "resets_at": 1788807866
      },
      "credits": {
        "has_credits": false,
        "unlimited": false,
        "balance": null
      },
      "plan_type": "team"
    }
    """;

    [Fact]
    public void ALiveSnapshotBecomesAFiveHourRingAndAWeeklyRing()
    {
        var usage = CodexUsageParse.FromRateLimits(
            LiveBothWindows, "/Users/w/.codex", "codex", DateTimeOffset.UtcNow);

        Assert.NotNull(usage);
        Assert.Equal(AccountUsageSource.Codex, usage!.Source);
        Assert.Equal("team", usage.SubscriptionType);
        Assert.NotNull(usage.Session);
        Assert.Equal(6.0, usage.Session!.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788239518), usage.Session.ResetsAt);
        Assert.NotNull(usage.Weekly);
        Assert.Equal(3.0, usage.Weekly!.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788807866), usage.Weekly.ResetsAt);
        Assert.NotNull(usage.Extra);
        Assert.False(usage.Extra!.Enabled);
        Assert.Equal("no_credits", usage.Extra.DisabledReason);
    }

    [Fact]
    public void WeeklyOnlyPrimaryIsNotDrawnAsAFiveHourRing()
    {
        var json = """
        {"primary":{"used_percent":92.0,"window_minutes":10080,"resets_at":1787763432},"plan_type":"team"}
        """;
        var usage = CodexUsageParse.FromRateLimits(json, null, "codex", DateTimeOffset.UtcNow);
        Assert.NotNull(usage);
        Assert.Null(usage!.Session);
        Assert.Equal(92.0, usage.Weekly!.Percent);
    }

    [Fact]
    public void ATokenCountEnvelopeIsUnwrapped()
    {
        var json = """
        {"timestamp":"2026-08-31T05:21:22.218Z","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":2.0,"window_minutes":10080}}}}
        """;
        var usage = CodexUsageParse.FromRateLimits(json, null, "c", DateTimeOffset.UtcNow);
        Assert.NotNull(usage);
        Assert.Equal(2.0, usage!.Weekly!.Percent);
    }

    [Fact]
    public void NestedRateLimitsPropertyIsUnwrapped()
    {
        var json = """{"rate_limits":{"primary":{"used_percent":11,"window_minutes":300}}}""";
        var usage = CodexUsageParse.FromRateLimits(json, null, "c", DateTimeOffset.UtcNow);
        Assert.NotNull(usage);
        Assert.Equal(11.0, usage!.Session!.Percent);
        Assert.Null(usage.Weekly);
    }

    [Fact]
    public void UnlimitedCreditsAreNotAGauge()
    {
        var json = """{"primary":{"used_percent":1,"window_minutes":300},"credits":{"unlimited":true,"has_credits":true}}""";
        var usage = CodexUsageParse.FromRateLimits(json, null, "c", DateTimeOffset.UtcNow);
        Assert.Equal("unlimited", usage!.Extra!.DisabledReason);
        Assert.False(usage.Extra.Enabled);
    }

    [Fact]
    public void ACreditBalanceWithoutACapIsNotDrawnAsAShare()
    {
        var json = """{"primary":{"used_percent":1,"window_minutes":300},"credits":{"has_credits":true,"balance":"2745.75"}}""";
        var usage = CodexUsageParse.FromRateLimits(json, null, "c", DateTimeOffset.UtcNow);
        Assert.Equal("credits_no_cap", usage!.Extra!.DisabledReason);
        Assert.Null(usage.Extra.Percent);
    }

    [Fact]
    public void GarbageAndEmptyAreNoReading()
    {
        Assert.Null(CodexUsageParse.FromRateLimits("not json", null, "c", DateTimeOffset.UtcNow));
        Assert.Null(CodexUsageParse.FromRateLimits("", null, "c", DateTimeOffset.UtcNow));
        Assert.Null(CodexUsageParse.FromRateLimits(null, null, "c", DateTimeOffset.UtcNow));
        Assert.Null(CodexUsageParse.FromRateLimits("[]", null, "c", DateTimeOffset.UtcNow));
        Assert.Null(CodexUsageParse.FromRateLimits("{}", null, "c", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void HomesAlwaysIncludesTheDefaultAndSkipsBlanksAndDuplicates()
    {
        var homes = CodexUsageAccounts.Homes("/Users/w", new[] { " ", ".codex", "work", "/abs/.codex-abs", "" });
        Assert.Equal(new[]
        {
            Path.Combine("/Users/w", ".codex"),
            Path.Combine("/Users/w", "work"),
            "/abs/.codex-abs",
        }, homes);
    }

    [Fact]
    public void FallbackLabelStripsTheCodexPrefix()
    {
        Assert.Equal("work", CodexUsageAccounts.FallbackLabel("/tmp/.codex-work"));
        Assert.Equal("codex", CodexUsageAccounts.FallbackLabel(null));
        Assert.Equal("codex", CodexUsageAccounts.FallbackLabel(""));
        Assert.Equal("codex", CodexUsageAccounts.FallbackLabel("/tmp/.codex"));
    }

    [Fact]
    public void LabelFromNeverReadsTheTokensObject()
    {
        var json = """{"tokens":{"email":"stolen@x.com","access_token":"secret"},"auth_mode":"chatgpt"}""";
        Assert.Equal("work", CodexUsageAccounts.LabelFrom(json, "/tmp/.codex-work"));
    }

    [Fact]
    public void LabelFromUsesATopLevelEmail()
    {
        Assert.Equal("warren", CodexUsageAccounts.LabelFrom(
            """{"email":"warren@example.com"}""", "/tmp/.codex"));
    }

    // The two filesystem tests that used to sit here — newest-rollout-wins and
    // the oversized-line skip — moved to tests/IntegrationTests as part of
    // CB-83. They were never unit tests: both wrote real files with real mtimes
    // to exercise a seam with the filesystem, and the bug they failed to catch
    // was in exactly the part of that seam a temp directory is needed to
    // reproduce. CodexUsageScanTests covers both cases and several more.

    // CB-83 ------------------------------------------------------------------
    //
    // Codex sends a snapshot with both windows null alongside a
    // rate_limit_reached_type, and sends it to every live session at once — so
    // on a busy machine the newest line on disk is reliably the empty one. The
    // poller has to be able to tell that line from a real reading before it
    // picks a file, which is what HasWindow is for. Both fixtures below are
    // copied from this machine's rollouts on 2 Sep 2026, 0.3 seconds apart.

    private const string LiveDepleted = """
    {"timestamp":"2026-09-02T18:57:26.078Z","ordinal":184,"type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"premium","limit_name":null,"primary":null,"secondary":null,"credits":{"has_credits":false,"unlimited":false,"balance":null},"individual_limit":null,"spend_control_reached":null,"plan_type":null,"rate_limit_reached_type":"workspace_owner_credits_depleted"}}}
    """;

    private const string LiveWindowed = """
    {"timestamp":"2026-09-02T18:57:25.767Z","ordinal":183,"type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":98.0,"window_minutes":300,"resets_at":1788391232},"secondary":{"used_percent":38.0,"window_minutes":10080,"resets_at":1788807866},"plan_type":"team"}}}
    """;

    [Fact]
    public void ADepletedCreditsSnapshotCarriesNoWindowAndIsNotAReading()
    {
        Assert.False(CodexUsageParse.HasWindow(LiveDepleted));

        // It still parses — it is a legible snapshot, just an empty one. What
        // must not happen is it being chosen over the line before it, which is
        // the poller's job and is covered in the integration suite.
        var usage = CodexUsageParse.FromRateLimits(LiveDepleted, null, "c", DateTimeOffset.UtcNow);
        Assert.NotNull(usage);
        Assert.False(usage!.Available);
    }

    [Fact]
    public void TheSnapshotBeforeItIsARealReading()
    {
        Assert.True(CodexUsageParse.HasWindow(LiveWindowed));

        var usage = CodexUsageParse.FromRateLimits(LiveWindowed, null, "c", DateTimeOffset.UtcNow);
        Assert.True(usage!.Available);
        Assert.Equal(98.0, usage.Session!.Percent);
        Assert.Equal(38.0, usage.Weekly!.Percent);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not json at all", false)]
    [InlineData("[1,2,3]", false)]
    [InlineData("""{"nothing":"here"}""", false)]
    [InlineData("""{"primary":null,"secondary":null}""", false)]
    [InlineData("""{"rate_limits":{"primary":"soon"}}""", false)]
    [InlineData("""{"rate_limits":{"primary":{"window_minutes":300}}}""", false)]
    [InlineData("""{"rate_limits":{"primary":{"used_percent":"lots"}}}""", false)]
    [InlineData("""{"primary":{"used_percent":0}}""", true)]
    [InlineData("""{"rate_limits":{"primary":null,"secondary":{"used_percent":4}}}""", true)]
    public void HasWindowIsTrueOnlyForASnapshotWithAPercentageInIt(string? json, bool expected)
    {
        Assert.Equal(expected, CodexUsageParse.HasWindow(json));
    }

    // The timestamp is what orders snapshots across files, and what tells the
    // card how old the number is. A missing or unreadable one is null rather
    // than a guess — see the comment on TimestampOf.
    [Fact]
    public void TheEnvelopeTimestampIsRead()
    {
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-02T18:57:25.767Z").ToUniversalTime(),
            CodexUsageParse.TimestampOf(LiveWindowed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("{ not json")]
    [InlineData("\"a string\"")]
    [InlineData("""{"payload":{}}""")]
    [InlineData("""{"timestamp":1788391232}""")]
    [InlineData("""{"timestamp":"the other day"}""")]
    public void ASnapshotThatDoesNotSayWhenItWasWrittenGetsNoGuess(string? json)
    {
        Assert.Null(CodexUsageParse.TimestampOf(json));
    }

    [Fact]
    public void TheSnapshotTimestampBecomesTheReadingsAge()
    {
        var writtenAt = DateTimeOffset.Parse("2026-09-01T05:05:24Z");
        var readAt = DateTimeOffset.Parse("2026-09-02T21:07:29Z");

        var usage = CodexUsageParse.FromRateLimits(
            LiveWindowed, "/Users/w/.codex", "codex", readAt, writtenAt);

        Assert.Equal(writtenAt, usage!.ObservedAt);
        Assert.Equal(writtenAt, usage.AsOf);
        Assert.True(usage.IsStale(readAt));
    }

    // Without a snapshot timestamp the reading falls back to the read, which is
    // the old behaviour and still the right one for a shape that says nothing
    // about when it was written.
    [Fact]
    public void WithNoSnapshotTimestampTheReadingIsDatedByTheRead()
    {
        var readAt = DateTimeOffset.Parse("2026-09-02T21:07:29Z");

        var usage = CodexUsageParse.FromRateLimits(LiveBothWindows, null, "c", readAt);

        Assert.Null(usage!.ObservedAt);
        Assert.Equal(readAt, usage.AsOf);
        Assert.False(usage.IsStale(readAt));
    }

    // A string where a number belongs used to take the whole reading down:
    // JsonElement.TryGetDouble throws InvalidOperationException rather than
    // returning false, and FromRateLimits catches only JsonException. Every
    // other surprise in this parser degrades to "no reading"; this one escaped
    // to CompositeUsageSource's catch-all and blanked the Codex source.
    [Fact]
    public void APercentageThatIsNotANumberIsIgnoredRatherThanThrown()
    {
        var json = """
        {"primary":{"used_percent":"lots","window_minutes":300},"secondary":{"used_percent":7,"window_minutes":10080}}
        """;

        var usage = CodexUsageParse.FromRateLimits(json, null, "c", DateTimeOffset.UtcNow);

        Assert.NotNull(usage);
        Assert.Null(usage!.Session);
        Assert.Equal(7.0, usage.Weekly!.Percent);
    }
}
