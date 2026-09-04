using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// The parts of GrokUsageRefresh.cs that do not start a real process:
// GrokBinary.Locate, the ShouldRefresh gate, and the scheduler that holds the
// state ShouldRefresh needs across calls. GrokUsageRefresher.Refresh itself is
// excluded from coverage — it starts the user's real Grok Build application —
// and is verified in the PR body against a live run instead.
public class GrokUsageRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cb-grok-bin-" + Guid.NewGuid().ToString("N"));

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

    // ---- GrokBinary.Locate ----------------------------------------------

    // The real install shape on this machine: ~/.grok/bin/grok, a symlink to
    // ../downloads/grok-macos-aarch64. A plain file stands in for the symlink
    // here since File.Exists follows either.
    [Fact]
    public void TheStandaloneInstallIsFoundFirst()
    {
        var home = Path.Combine(_root, "home");
        var expected = Touch("home", ".grok", "bin", "grok");

        Assert.Equal(expected, GrokBinary.Locate(home, "", NoSystemInstalls));
    }

    [Fact]
    public void ASystemInstallIsFoundWhenTheHomeHasNothing()
    {
        var system = Touch("opt", "grok");

        Assert.Equal(
            system,
            GrokBinary.Locate(Path.Combine(_root, "home"), "", new[] { system }));
    }

    [Fact]
    public void PathIsTheLastResort()
    {
        var onPath = Touch("elsewhere", "grok");

        Assert.Equal(
            onPath,
            GrokBinary.Locate(
                Path.Combine(_root, "home"),
                Path.Combine(_root, "elsewhere"),
                NoSystemInstalls));
    }

    [Fact]
    public void NothingAnywhereIsNullRatherThanAThrow()
    {
        Assert.Null(GrokBinary.Locate(Path.Combine(_root, "home"), "", NoSystemInstalls));
    }

    // Same reasoning as CodexBinary's equivalent test: what a null searchPath
    // resolves to on this machine is not asserted, only that resolving it does
    // not throw — a real answer here depends on whether this developer's Mac
    // happens to have grok on PATH, and a CI runner does not.
    [Fact]
    public void ANullSearchPathFallsBackToTheEnvironmentWithoutThrowing()
    {
        var record = Record.Exception(
            () => GrokBinary.Locate(Path.Combine(_root, "home"), null, NoSystemInstalls));

        Assert.Null(record);
    }

    [Fact]
    public void AWindowsShimIsFoundByItsExtension()
    {
        var home = Path.Combine(_root, "home");
        var expected = Touch("home", ".grok", "bin", "grok.cmd");

        Assert.Null(GrokBinary.Locate(home, "", NoSystemInstalls, GrokBinary.UnixExtensions));
        Assert.Equal(
            expected,
            GrokBinary.Locate(home, "", NoSystemInstalls, GrokBinary.WindowsExtensions));
    }

    [Fact]
    public void ThePathPropertyIsLookedUpOnceAndCached()
    {
        var first = GrokBinary.Path;
        var second = GrokBinary.Path;

        Assert.Equal(first, second);
    }

    // ---- GrokUsageRefresher.ShouldRefresh --------------------------------

    private static readonly DateTimeOffset Now = new(2026, 9, 4, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BothSwitchesMustBeOn()
    {
        var longAgo = Now - GrokUsageRefresher.MinimumInterval;

        Assert.False(GrokUsageRefresher.ShouldRefresh(Now, longAgo, false, true));
        Assert.False(GrokUsageRefresher.ShouldRefresh(Now, longAgo, true, false));
        Assert.False(GrokUsageRefresher.ShouldRefresh(Now, longAgo, false, false));
        Assert.True(GrokUsageRefresher.ShouldRefresh(Now, longAgo, true, true));
    }

    [Fact]
    public void TheFloorIsTwentyMinutesNotFive()
    {
        var fiveMinutesAgo = Now - TimeSpan.FromMinutes(5);
        var twentyMinutesAgo = Now - TimeSpan.FromMinutes(20);

        Assert.False(GrokUsageRefresher.ShouldRefresh(Now, fiveMinutesAgo, true, true));
        Assert.True(GrokUsageRefresher.ShouldRefresh(Now, twentyMinutesAgo, true, true));
    }

    [Fact]
    public void NeverRefreshedMeansDueImmediately()
    {
        Assert.True(GrokUsageRefresher.ShouldRefresh(Now, DateTimeOffset.MinValue, true, true));
    }

    // ---- GrokUsageRefreshScheduler ---------------------------------------

    [Fact]
    public void TheFirstTickWithBothSwitchesOnSaysYes()
    {
        var scheduler = new GrokUsageRefreshScheduler();

        Assert.True(scheduler.Tick(Now, accountUsageEnabled: true, autoRefreshEnabled: true));
    }

    [Fact]
    public void EitherSwitchOffSaysNo()
    {
        var scheduler = new GrokUsageRefreshScheduler();

        Assert.False(scheduler.Tick(Now, accountUsageEnabled: false, autoRefreshEnabled: true));
        Assert.False(scheduler.Tick(Now, accountUsageEnabled: true, autoRefreshEnabled: false));
    }

    // The reason the scheduler exists rather than a bare call to ShouldRefresh
    // with a field SessionManager updates itself: a yes has to be remembered
    // immediately, before the caller has done anything about it, or a poll
    // timer ticking every two seconds would say yes again on the very next
    // tick while the first refresh is still holding Grok's pty open.
    [Fact]
    public void AYesIsNotRepeatedBeforeTheFloorElapsesAgain()
    {
        var scheduler = new GrokUsageRefreshScheduler();

        Assert.True(scheduler.Tick(Now, true, true));
        Assert.False(scheduler.Tick(Now.AddSeconds(2), true, true));
        Assert.False(scheduler.Tick(Now.AddMinutes(19), true, true));
        Assert.True(scheduler.Tick(Now.AddMinutes(20), true, true));
    }

    // A no is not remembered the same way — flipping the switch on later in
    // the same window must not have to wait out a floor that never actually
    // started counting down for a real refresh.
    [Fact]
    public void ASwitchThatWasOffDoesNotStartTheFloorRunning()
    {
        var scheduler = new GrokUsageRefreshScheduler();

        Assert.False(scheduler.Tick(Now, accountUsageEnabled: false, autoRefreshEnabled: true));
        Assert.True(scheduler.Tick(
            Now.AddSeconds(2), accountUsageEnabled: true, autoRefreshEnabled: true));
    }
}
