using System;
using Xunit;

namespace ClaudeBuddy.UnitTests;

public class GrokUsageParseTests
{
    private const string LiveConfig = """
    {
      "ctx": {
        "config": {
          "creditUsagePercent": 14.0,
          "currentPeriod": {
            "type": "USAGE_PERIOD_TYPE_WEEKLY",
            "start": "2026-08-31T06:30:23.778489+00:00",
            "end": "2026-09-07T06:30:23.778489+00:00"
          },
          "onDemandCap": { "val": 0 },
          "onDemandUsed": { "val": 0 },
          "prepaidBalance": { "val": 0 },
          "isUnifiedBillingUser": true
        },
        "subscriptionTier": "SuperGrok"
      }
    }
    """;

    [Fact]
    public void ALiveCreditsLineBecomesAWeeklyWindowAndNoFiveHourRing()
    {
        var usage = GrokUsageParse.FromCreditsConfig(
            LiveConfig, "/Users/w/.grok", "warren", DateTimeOffset.Parse("2026-08-31T18:00:00Z"));

        Assert.NotNull(usage);
        Assert.Null(usage!.Session);
        Assert.NotNull(usage.Weekly);
        Assert.Equal(14.0, usage.Weekly!.Percent);
        Assert.Equal(DateTimeOffset.Parse("2026-09-07T06:30:23.778489+00:00"), usage.Weekly.ResetsAt);
        Assert.NotNull(usage.Extra);
        Assert.False(usage.Extra!.Enabled);
        Assert.Null(usage.Extra.Percent);
        Assert.Equal(AccountUsageSource.Grok, usage.Source);
    }

    [Fact]
    public void OnDemandCapTurnsTheInnerRingIntoAGauge()
    {
        var json = """
        {"creditUsagePercent": 40, "onDemandCap": {"val": 100}, "onDemandUsed": {"val": 25}}
        """;

        var usage = GrokUsageParse.FromCreditsConfig(json, null, "g", DateTimeOffset.UtcNow);
        Assert.NotNull(usage);
        Assert.True(usage!.Extra!.Enabled);
        Assert.Equal(25.0, usage.Extra.Percent);
    }

    [Fact]
    public void UnreadableJsonIsNoReading()
    {
        Assert.Null(GrokUsageParse.FromCreditsConfig("not json", null, "g", DateTimeOffset.UtcNow));
        Assert.Null(GrokUsageParse.FromCreditsConfig("", null, "g", DateTimeOffset.UtcNow));
        Assert.Null(GrokUsageParse.FromCreditsConfig(null, null, "g", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void LabelUsesTheEmailLocalPart()
    {
        var json = """{"https://auth.x.ai::id": {"email": "warren@example.com"}}""";
        Assert.Equal("warren", GrokUsageAccounts.LabelFrom(json, "/tmp/.grok-work"));
    }

    [Fact]
    public void LabelFallsBackToTheDirectoryName()
    {
        Assert.Equal("work", GrokUsageAccounts.FallbackLabel("/tmp/.grok-work"));
        Assert.Equal("grok", GrokUsageAccounts.FallbackLabel(null));
        Assert.Equal("grok", GrokUsageAccounts.FallbackLabel(""));
        Assert.Equal("grok", GrokUsageAccounts.FallbackLabel("/tmp/.grok"));
        Assert.Equal("grok-", GrokUsageAccounts.FallbackLabel("/tmp/grok-"));
        Assert.Equal("grok", GrokUsageAccounts.FallbackLabel("/tmp/."));
    }

    [Fact]
    public void LabelFallsBackWhenAuthJsonIsUnreadableOrHasNoEmail()
    {
        Assert.Equal("work", GrokUsageAccounts.LabelFrom("not json", "/tmp/.grok-work"));
        Assert.Equal("work", GrokUsageAccounts.LabelFrom("[]", "/tmp/.grok-work"));
        Assert.Equal("work", GrokUsageAccounts.LabelFrom("{\"acct\":{\"email\":\"\"}}", "/tmp/.grok-work"));
        Assert.Equal("local", GrokUsageAccounts.LabelFrom("{\"acct\":{\"email\":\"local\"}}", "/tmp/.grok-work"));
    }

    [Fact]
    public void HomesAlwaysIncludesTheDefaultAndSkipsBlanksAndDuplicates()
    {
        var homes = GrokUsageAccounts.Homes("/Users/w", new[] { " ", ".grok", "work", "/abs/.grok-abs", "" });
        Assert.Equal(new[]
        {
            Path.Combine("/Users/w", ".grok"),
            Path.Combine("/Users/w", "work"),
            "/abs/.grok-abs",
        }, homes);
    }

    [Fact]
    public void AConfigEnvelopeWithoutCtxIsStillRead()
    {
        var json = """{"config":{"creditUsagePercent":7},"subscriptionTier":"SuperGrok"}""";
        var usage = GrokUsageParse.FromCreditsConfig(json, null, "g", DateTimeOffset.UtcNow);
        Assert.NotNull(usage);
        Assert.Equal(7.0, usage!.Weekly!.Percent);
        Assert.Equal("SuperGrok", usage.SubscriptionType);
    }

    [Fact]
    public void ANonObjectRootIsNoReading()
    {
        Assert.Null(GrokUsageParse.FromCreditsConfig("[]", null, "g", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void APercentWithoutAPeriodHasNoReset()
    {
        var usage = GrokUsageParse.FromCreditsConfig(
            """{"creditUsagePercent":3}""", null, "g", DateTimeOffset.UtcNow);
        Assert.NotNull(usage);
        Assert.Equal(3.0, usage!.Weekly!.Percent);
        Assert.Null(usage.Weekly.ResetsAt);
    }

    [Fact]
    public void MissingPercentIsAReadingWithNoWeeklyRing()
    {
        var usage = GrokUsageParse.FromCreditsConfig(
            """{"onDemandCap":{"val":0}}""", null, "g", DateTimeOffset.UtcNow);
        Assert.NotNull(usage);
        Assert.Null(usage!.Weekly);
        Assert.False(usage.Available);
    }

    [Fact]
    public void PlainNumberOnDemandFieldsAreRead()
    {
        var usage = GrokUsageParse.FromCreditsConfig(
            """{"creditUsagePercent":10,"onDemandCap":50,"onDemandUsed":10}""",
            null, "g", DateTimeOffset.UtcNow);
        Assert.NotNull(usage);
        Assert.True(usage!.Extra!.Enabled);
        Assert.Equal(20.0, usage.Extra.Percent);
    }

    [Fact]
    public void CompositeMergesSourcesAndSurvivesOneFailure()
    {
        var good = new StubSource(new AccountUsage(null, "a", true, null, null, null, null, DateTimeOffset.UtcNow));
        var bad = new ThrowingSource();
        var merged = new CompositeUsageSource(bad, good).Read();
        Assert.Single(merged);
        Assert.Equal("a", merged[0].Label);
    }

    private sealed class StubSource : IUsageSource
    {
        private readonly AccountUsage _usage;
        public StubSource(AccountUsage usage) => _usage = usage;
        public System.Collections.Generic.IReadOnlyList<AccountUsage> Read() => new[] { _usage };
    }

    private sealed class ThrowingSource : IUsageSource
    {
        public System.Collections.Generic.IReadOnlyList<AccountUsage> Read() =>
            throw new InvalidOperationException("boom");
    }

    // CB-83 ------------------------------------------------------------------
    //
    // Verbatim from ~/.grok/logs/unified.jsonl on this machine — the whole
    // envelope this time, not just ctx.config, because the envelope is where
    // both of the things this ticket fixes actually live: `ts` (how old the
    // number is) and `subscriptionTier` (which sits beside `config` inside
    // `ctx`, not inside it, and so was never found).
    private const string LiveLogLine = """
    {"ts":"2026-09-01T05:05:24.996Z","src":"shell","pid":88551,"ver":"1.0.13","lvl":"info","msg":"billing: fetched credits config","ctx":{"config":{"creditUsagePercent":44.0,"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","start":"2026-08-31T06:30:23.778489+00:00","end":"2026-09-07T06:30:23.778489+00:00"},"onDemandCap":{"val":0},"onDemandUsed":{"val":0},"prepaidBalance":{"val":0},"isUnifiedBillingUser":true},"onDemandEnabled":null,"subscriptionTier":"SuperGrok"}}
    """;

    [Fact]
    public void TheTierIsFoundBesideTheConfigRatherThanInsideIt()
    {
        var usage = GrokUsageParse.FromCreditsConfig(
            LiveLogLine, "/Users/w/.grok", "warren", DateTimeOffset.Parse("2026-09-01T06:00:00Z"));

        Assert.Equal("SuperGrok", usage!.SubscriptionType);
        Assert.Equal(44.0, usage.Weekly!.Percent);
    }

    // Grok appends a credits config when it starts and never again until the
    // next run. A machine that last ran grok on Monday is holding Monday's
    // percentage, and the reading has to say so rather than be dated by the
    // moment this app happened to read the log.
    [Fact]
    public void TheLogTimestampIsTheReadingsAgeNotTheMomentItWasRead()
    {
        var readAt = DateTimeOffset.Parse("2026-09-02T21:07:29Z");

        var usage = GrokUsageParse.FromCreditsConfig(LiveLogLine, null, "g", readAt);

        Assert.Equal(DateTimeOffset.Parse("2026-09-01T05:05:24.996Z").ToUniversalTime(), usage!.AsOf);
        Assert.True(usage.IsStale(readAt));
        Assert.True(readAt - usage.AsOf > TimeSpan.FromHours(38));
    }

    [Theory]
    [InlineData("""{"creditUsagePercent":5}""")]
    [InlineData("""{"ts":42,"creditUsagePercent":5}""")]
    [InlineData("""{"ts":"whenever","creditUsagePercent":5}""")]
    public void ALineThatDoesNotSayWhenItWasWrittenIsDatedByTheRead(string json)
    {
        var readAt = DateTimeOffset.Parse("2026-09-02T21:07:29Z");

        var usage = GrokUsageParse.FromCreditsConfig(json, null, "g", readAt);

        Assert.Null(usage!.ObservedAt);
        Assert.Equal(readAt, usage.AsOf);
        Assert.False(usage.IsStale(readAt));
    }

    [Fact]
    public void ATierOnTheBareRootIsStillFound()
    {
        var json = """{"creditUsagePercent":5,"subscriptionTier":"SuperGrokHeavy"}""";

        var usage = GrokUsageParse.FromCreditsConfig(json, null, "g", DateTimeOffset.UtcNow);

        Assert.Equal("SuperGrokHeavy", usage!.SubscriptionType);
    }

    [Fact]
    public void ANonObjectCtxDoesNotStopTheParse()
    {
        var json = """{"ctx":"nope","creditUsagePercent":5}""";

        var usage = GrokUsageParse.FromCreditsConfig(json, null, "g", DateTimeOffset.UtcNow);

        Assert.Null(usage!.SubscriptionType);
        Assert.Equal(5.0, usage.Weekly!.Percent);
    }

    [Fact]
    public void ACreditPercentageThatIsNotANumberIsNoReadingRatherThanAThrow()
    {
        var usage = GrokUsageParse.FromCreditsConfig(
            """{"creditUsagePercent":"most of it"}""", null, "g", DateTimeOffset.UtcNow);

        Assert.NotNull(usage);
        Assert.False(usage!.Available);
        Assert.Null(usage.Weekly);
    }
}
