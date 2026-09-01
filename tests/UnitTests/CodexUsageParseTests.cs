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

    [Fact]
    public void NewestRolloutWinsEvenIfAnOlderFileAlsoHasASnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "cb-codex-usage-" + Guid.NewGuid().ToString("n"));
        var older = Path.Combine(root, "2026", "08", "20");
        var newer = Path.Combine(root, "2026", "08", "31");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newer);

        File.WriteAllText(Path.Combine(older, "rollout-old.jsonl"),
            """{"payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":99,"window_minutes":10080}}}}""" + "\n");
        var newFile = Path.Combine(newer, "rollout-new.jsonl");
        File.WriteAllText(newFile,
            """{"payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":4,"window_minutes":300}}}}""" + "\n");
        File.SetLastWriteTimeUtc(Path.Combine(older, "rollout-old.jsonl"), DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow);

        var json = CodexUsagePoller.LatestRateLimitsJsonFrom(root);
        var usage = CodexUsageParse.FromRateLimits(json, root, "c", DateTimeOffset.UtcNow);
        Assert.Equal(4.0, usage!.Session!.Percent);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void AOversizedLineIsSkippedRatherThanParsed()
    {
        var root = Path.Combine(Path.GetTempPath(), "cb-codex-usage-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "rollout-big.jsonl");
        var huge = "{\"payload\":{\"type\":\"token_count\",\"rate_limits\":{\"primary\":{\"used_percent\":1}}}}"
                   + new string('x', CodexUsagePoller.MaxLineBytes);
        File.WriteAllText(path, huge + "\n"
            + """{"payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":8,"window_minutes":300}}}}""" + "\n");

        var line = CodexUsagePoller.LatestTokenCountLine(path);
        var usage = CodexUsageParse.FromRateLimits(line, root, "c", DateTimeOffset.UtcNow);
        Assert.Equal(8.0, usage!.Session!.Percent);

        Directory.Delete(root, recursive: true);
    }
}
