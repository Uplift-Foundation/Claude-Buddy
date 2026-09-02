using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// The live-usage path added by CB-85: finding the `codex` binary, and picking
// the one line worth reading out of the app-server's stream.
//
// Everything here is pure or filesystem-only. The subprocess itself —
// CodexAppServerUsage.Ask — is excluded from coverage and named in the PR, the
// same split UsagePoller.RunOne and BackgroundJobs.ReadOne already use: the
// launch is untestable, the JSON it prints is not.
public class CodexAppServerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cb-codex-bin-" + Guid.NewGuid().ToString("N"));

    private static readonly string[] NoSystemInstalls = Array.Empty<string>();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n");
        return path;
    }

    // ---- Locate --------------------------------------------------------

    [Fact]
    public void TheSymlinkInLocalBinIsFoundFirst()
    {
        var home = Path.Combine(_root, "home");
        var expected = Touch("home", ".local", "bin", "codex");
        Touch("home", ".codex", "packages", "standalone", "current", "bin", "codex");

        Assert.Equal(expected, CodexBinary.Locate(home, "", NoSystemInstalls));
    }

    // The standalone package is where ~/.local/bin/codex points. Worth finding
    // on its own, because a broken or removed symlink leaves a perfectly good
    // binary sitting there.
    [Fact]
    public void TheStandalonePackageIsFoundWhenTheSymlinkIsGone()
    {
        var home = Path.Combine(_root, "home");
        var expected = Touch(
            "home", ".codex", "packages", "standalone", "current", "bin", "codex");

        Assert.Equal(expected, CodexBinary.Locate(home, "", NoSystemInstalls));
    }

    [Fact]
    public void ASystemInstallIsFoundWhenTheHomeHasNothing()
    {
        var system = Touch("opt", "codex");

        Assert.Equal(
            system,
            CodexBinary.Locate(Path.Combine(_root, "home"), "", new[] { system }));
    }

    [Fact]
    public void PathIsTheLastResort()
    {
        var onPath = Touch("elsewhere", "codex");

        Assert.Equal(
            onPath,
            CodexBinary.Locate(
                Path.Combine(_root, "home"),
                Path.Combine(_root, "elsewhere"),
                NoSystemInstalls));
    }

    [Fact]
    public void NothingAnywhereIsNullRatherThanAThrow()
    {
        Assert.Null(CodexBinary.Locate(Path.Combine(_root, "home"), "", NoSystemInstalls));
    }

    // A null searchPath means "consult the real PATH", which is the default and
    // has to keep working. What it *finds* is deliberately not asserted: this
    // developer's Mac has a real codex on PATH and a CI runner does not, so an
    // assertion either way would be a test that passes for two different
    // reasons — the hazard ClaudeBinary.Locate's own comment exists to name.
    // That it answers at all, without throwing, is the part that is about the
    // code rather than about the machine.
    [Fact]
    public void ANullSearchPathFallsBackToTheEnvironmentWithoutThrowing()
    {
        var record = Record.Exception(
            () => CodexBinary.Locate(Path.Combine(_root, "home"), null, NoSystemInstalls));

        Assert.Null(record);
    }

    // On Windows an npm-installed CLI is `codex.cmd` and a bare "codex" exists
    // nowhere on disk, so a search that only tried the bare name found nothing —
    // silently, which is the whole failure class CodexBinary exists to avoid.
    // Driven with the extension list as an argument so this runs on every
    // platform rather than only on the Windows CI leg.
    [Fact]
    public void AWindowsShimIsFoundByItsExtension()
    {
        var home = Path.Combine(_root, "home");
        var expected = Touch("home", ".local", "bin", "codex.cmd");

        Assert.Null(CodexBinary.Locate(home, "", NoSystemInstalls, CodexBinary.UnixExtensions));
        Assert.Equal(
            expected,
            CodexBinary.Locate(home, "", NoSystemInstalls, CodexBinary.WindowsExtensions));
    }

    // The empty extension leads, so a real extensionless binary still wins on a
    // Windows machine that has one.
    [Fact]
    public void AnExtensionlessBinaryStillWinsUnderTheWindowsList()
    {
        var home = Path.Combine(_root, "home");
        var bare = Touch("home", ".local", "bin", "codex");
        Touch("home", ".local", "bin", "codex.cmd");

        Assert.Equal(
            bare,
            CodexBinary.Locate(home, "", NoSystemInstalls, CodexBinary.WindowsExtensions));
    }

    [Fact]
    public void ExtensionsApplyToThePathFallbackToo()
    {
        var expected = Touch("elsewhere", "codex.exe");

        Assert.Equal(
            expected,
            CodexBinary.Locate(
                Path.Combine(_root, "home"),
                Path.Combine(_root, "elsewhere"),
                NoSystemInstalls,
                CodexBinary.WindowsExtensions));
    }

    // ---- ResultFrom ----------------------------------------------------

    // Verbatim from a real `codex app-server` exchange on this machine,
    // 2 Sep 2026, codex-cli 0.151.0 — the response line plus the notification
    // rows that arrive around it.
    private const string LiveStream = """
    {"jsonrpc":"2.0","method":"account/updated","params":{"account":{"type":"chatgpt","email":null,"planType":"team"}}}
    {"id":1,"result":{"userAgent":"codex_cli_rs/0.151.0"}}
    {"id":2,"result":{"rateLimits":{"limitId":"codex","limitName":null,"primary":{"usedPercent":100,"windowDurationMins":300,"resetsAt":1788391232},"secondary":{"usedPercent":38,"windowDurationMins":10080,"resetsAt":1788807866},"credits":{"hasCredits":false,"unlimited":false,"balance":null},"individualLimit":null,"spendControlReached":false,"planType":"team","rateLimitReachedType":"workspace_owner_credits_depleted"},"rateLimitsByLimitId":{"codex":{"limitId":"codex","primary":{"usedPercent":100,"windowDurationMins":300,"resetsAt":1788391232}}}}}
    """;

    [Fact]
    public void TheAnswerIsPickedOutOfAStreamCarryingOtherRows()
    {
        var result = CodexAppServerUsage.ResultFrom(LiveStream);

        Assert.NotNull(result);
        Assert.Contains("\"rateLimits\"", result);
        Assert.DoesNotContain("userAgent", result);
    }

    [Fact]
    public void TheLiveAnswerBecomesAFiveHourRingAndAWeeklyRing()
    {
        var usage = CodexUsageParse.FromRateLimits(
            CodexAppServerUsage.ResultFrom(LiveStream), "/Users/w/.codex", "codex",
            DateTimeOffset.Parse("2026-09-02T22:30:00Z"));

        Assert.True(usage!.Available);
        Assert.Equal("team", usage.SubscriptionType);
        Assert.Equal(100.0, usage.Session!.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788391232), usage.Session.ResetsAt);
        Assert.Equal(38.0, usage.Weekly!.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788807866), usage.Weekly.ResetsAt);

        // Credits are read under their camelCase spelling too — `hasCredits`
        // false here, which is "no extra usage" rather than "unknown".
        Assert.Equal("no_credits", usage.Extra!.DisabledReason);
    }

    // A live answer is dated by the read, not by a snapshot timestamp it does
    // not have. That is CB-83's rule pointing the other way: the number is
    // current, so the orb must not dim and the card must not age it.
    [Fact]
    public void ALiveAnswerIsNotStaleAndCarriesNoObservedAt()
    {
        var readAt = DateTimeOffset.Parse("2026-09-02T22:30:00Z");

        var usage = CodexUsageParse.FromRateLimits(
            CodexAppServerUsage.ResultFrom(LiveStream), null, "codex", readAt);

        Assert.Null(usage!.ObservedAt);
        Assert.Equal(readAt, usage.AsOf);
        Assert.False(usage.IsStale(readAt));
    }

    [Fact]
    public void AnErrorResponseIsNoReadingRatherThanAnEmptyOne()
    {
        var stream = """
        {"id":2,"error":{"code":-32601,"message":"Method not found"}}
        """;

        Assert.Null(CodexAppServerUsage.ResultFrom(stream));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("""{"id":1,"result":{"userAgent":"x"}}""")]          // the wrong request
    [InlineData("""{"id":"2","result":{"rateLimits":{}}}""")]        // id as a string
    [InlineData("""{"result":{"rateLimits":{}}}""")]                 // no id
    [InlineData("""{"id":2,"result":"nope"}""")]                     // result not an object
    [InlineData("""{"id":2,"result":{"rateLimits":{}}""")]           // truncated line
    public void AnythingThatIsNotTheAnswerIsSkipped(string? stdout)
    {
        Assert.Null(CodexAppServerUsage.ResultFrom(stdout));
    }

    // The request ids have to agree with what ResultFrom looks for, or the
    // read silently finds nothing forever.
    [Fact]
    public void TheRequestAsksForTheIdTheReaderMatchesOn()
    {
        Assert.Contains($"\"id\":{CodexAppServerUsage.RequestId}",
                        CodexAppServerUsage.RateLimitsRequest);
        Assert.Contains("account/rateLimits/read", CodexAppServerUsage.RateLimitsRequest);
        Assert.Contains("\"method\":\"initialize\"", CodexAppServerUsage.InitializeRequest);
        Assert.DoesNotContain($"\"id\":{CodexAppServerUsage.RequestId}",
                              CodexAppServerUsage.InitializeRequest);
    }
}
