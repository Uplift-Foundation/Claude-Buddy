using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.Tests;

// The rule that decides which profile a `claude://` link belongs to, and the
// two command lines built from it.
//
// This is the whole fix for CB-4 expressed as a pure function, which is the
// point: the bug it replaces was invisible precisely because the decision was
// being made by LaunchServices from a bundle id that every profile shares.
// Every branch below is a way to open the wrong account.
public class ClaudeDesktopUrlRoutingTests
{
    private const string DefaultDirectory = "/Users/x/Library/Application Support/Claude";
    private const string BoardDirectory = "/Users/x/Library/Application Support/Claude-Board";
    private const string WorkDirectory = "/Users/x/Library/Application Support/Claude-Work";

    private const string InstalledBundle = "/Applications/Claude.app";
    private const string BoardBundle = "/Users/x/Library/Application Support/ClaudeBuddy/bundles/Claude-Board/Claude.app";
    private const string WorkBundle = "/Users/x/Library/Application Support/ClaudeBuddy/bundles/Claude-Work/Claude.app";

    private static UrlRouteCandidate Default(bool running = false, int pid = 0) =>
        new(DefaultDirectory, InstalledBundle, IsDefault: true, running, pid);

    private static UrlRouteCandidate Board(bool running = false, int pid = 0) =>
        new(BoardDirectory, BoardBundle, IsDefault: false, running, pid);

    private static UrlRouteCandidate Work(bool running = false, int pid = 0) =>
        new(WorkDirectory, WorkBundle, IsDefault: false, running, pid);

    [Fact]
    public void Choose_WithNoProfilesAtAll_IsNull()
    {
        Assert.Null(ClaudeDesktopUrlRouting.Choose(new List<UrlRouteCandidate>(), lastActivePid: 0));
    }

    [Fact]
    public void Choose_PrefersTheInstanceTheUserWasLastIn()
    {
        // The case the bug was reported as: signing in from Board while Default
        // is also up. Before this router, the callback went to Default every
        // time, because that is what /Applications/Claude.app resolves to.
        var route = ClaudeDesktopUrlRouting.Choose(
            new[] { Default(running: true, pid: 100), Board(running: true, pid: 200) },
            lastActivePid: 200);

        Assert.NotNull(route);
        Assert.Equal(BoardDirectory, route!.ProfileDirectory);
        Assert.Equal(BoardBundle, route.BundlePath);
        Assert.True(route.AlreadyRunning);
        Assert.Equal(200, route.Pid);
    }

    [Fact]
    public void Choose_IgnoresAHintForAProfileThatIsNotRunning()
    {
        // A remembered pid outlives the instance it named. Honouring it would
        // address a dead process; falling through to the single running
        // instance is the useful answer.
        var route = ClaudeDesktopUrlRouting.Choose(
            new[] { Default(), Board(running: true, pid: 200) },
            lastActivePid: 999);

        Assert.Equal(BoardDirectory, route!.ProfileDirectory);
    }

    [Fact]
    public void Choose_WithExactlyOneInstanceRunning_UsesItEvenWithNoHint()
    {
        // Covers a link arriving before any frontmost sample has been taken —
        // there is nothing ambiguous to resolve, so a hint is not needed.
        var route = ClaudeDesktopUrlRouting.Choose(
            new[] { Default(), Board(running: true, pid: 200), Work() },
            lastActivePid: 0);

        Assert.Equal(BoardDirectory, route!.ProfileDirectory);
        Assert.Equal(200, route.Pid);
    }

    [Fact]
    public void Choose_WithSeveralRunningAndNoHint_PrefersDefault()
    {
        // Least surprising of the running instances: it is what macOS would
        // have done on its own before this router existed.
        var route = ClaudeDesktopUrlRouting.Choose(
            new[] { Board(running: true, pid: 200), Default(running: true, pid: 100), Work(running: true, pid: 300) },
            lastActivePid: 0);

        Assert.Equal(DefaultDirectory, route!.ProfileDirectory);
        Assert.Null(route.UserDataDir);
    }

    [Fact]
    public void Choose_WithSeveralRunningAndNoDefaultRunning_IsStableOnLowestPid()
    {
        // Deliberately not list order. An answer that moves between scans would
        // send otherwise identical links to different accounts for no visible
        // reason, which is the failure mode being fixed rather than a new one.
        var candidates = new[] { Work(running: true, pid: 300), Board(running: true, pid: 200), Default() };

        var route = ClaudeDesktopUrlRouting.Choose(candidates, lastActivePid: 0);
        Assert.Equal(BoardDirectory, route!.ProfileDirectory);

        // Same set, opposite order in: same answer out.
        var reversed = new[] { Board(running: true, pid: 200), Work(running: true, pid: 300), Default() };
        Assert.Equal(BoardDirectory, ClaudeDesktopUrlRouting.Choose(reversed, 0)!.ProfileDirectory);
    }

    [Fact]
    public void Choose_WithNothingRunning_LaunchesDefault()
    {
        var route = ClaudeDesktopUrlRouting.Choose(
            new[] { Board(), Default(), Work() },
            lastActivePid: 0);

        Assert.Equal(DefaultDirectory, route!.ProfileDirectory);
        Assert.False(route.AlreadyRunning);
        Assert.Equal(0, route.Pid);
    }

    [Fact]
    public void Choose_WithNothingRunningAndNoDefaultProfile_TakesTheFirstByDirectory()
    {
        // A machine whose Default directory has been removed still has to have
        // an answer, and it has to be the same answer every time.
        var route = ClaudeDesktopUrlRouting.Choose(
            new[] { Work(), Board() },
            lastActivePid: 0);

        Assert.Equal(BoardDirectory, route!.ProfileDirectory);
    }

    [Fact]
    public void Choose_SetsUserDataDirForCreatedProfilesAndNeverForDefault()
    {
        // Mirrors LaunchMac's rule exactly: setting CLAUDE_USER_DATA_DIR on
        // Default suppresses the app's own sidecar-config resolution and can
        // re-trigger the deployment-mode chooser.
        var board = ClaudeDesktopUrlRouting.Choose(new[] { Board(running: true, pid: 200) }, 0);
        Assert.Equal(BoardDirectory, board!.UserDataDir);

        var fallback = ClaudeDesktopUrlRouting.Choose(new[] { Default(running: true, pid: 100) }, 0);
        Assert.Null(fallback!.UserDataDir);
    }

    [Theory]
    [InlineData("claude://open/chat", true)]
    [InlineData("CLAUDE://open/chat", true)]
    [InlineData("msauth.com.anthropic.claudefordesktop://auth?code=1", true)]
    [InlineData("https://claude.ai", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("claude", false)]
    [InlineData(":no-scheme", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Handles_AcceptsOnlyTheTwoSchemesWeClaimed(string url, bool expected)
    {
        // We became the system handler for exactly two schemes, so we are
        // answerable for delivering exactly those and nothing else. Forwarding
        // anything more would be inventing behaviour for a URL somebody else
        // owns.
        Assert.Equal(expected, ClaudeDesktopUrlRouting.Handles(url));
    }

    [Fact]
    public void Schemes_CoverDeepLinksAndTheSignInCallback()
    {
        // The msauth one is not decoration: it is the Microsoft sign-in
        // callback, and its absence is why this bug reads as "I can't log in"
        // rather than only as a stray window.
        Assert.Contains("claude", ClaudeDesktopUrlRouting.Schemes);
        Assert.Contains("msauth.com.anthropic.claudefordesktop", ClaudeDesktopUrlRouting.Schemes);
    }

    // ---- the two command lines built from a route ------------------------

    [Fact]
    public void RouterArguments_NeverPassDashN()
    {
        // -n is right for a launch, which has just proved nothing is running on
        // the directory. Here it would start a second Chromium on a live
        // userData directory — the leveldb corruption the profile feature
        // exists to avoid, and a route straight back to a lost login.
        var route = new UrlRoute(BoardDirectory, BoardBundle, BoardDirectory, AlreadyRunning: true, Pid: 200);

        var arguments = ClaudeDesktopUrlRouter.Arguments(route, "claude://x");

        Assert.DoesNotContain("-n", arguments);
        Assert.Equal(
            new[]
            {
                "-a", BoardBundle,
                "--env", "CLAUDE_USER_DATA_DIR=" + BoardDirectory,
                "claude://x",
                "--args", "--user-data-dir=" + BoardDirectory
            },
            arguments);
    }

    [Fact]
    public void RouterArguments_PutTheUrlBeforeDashDashArgs()
    {
        // open(1) hands everything after --args to the application untouched,
        // so a URL written after it stops being an operand and is never
        // delivered — the link silently does nothing. The order is the whole
        // reason this assertion exists separately from the one above.
        var route = new UrlRoute(BoardDirectory, BoardBundle, BoardDirectory, AlreadyRunning: false, Pid: 0);

        var arguments = ClaudeDesktopUrlRouter.Arguments(route, "claude://x");

        Assert.True(
            Array.IndexOf(arguments, "claude://x") < Array.IndexOf(arguments, "--args"),
            "the URL must precede --args or open(1) will not deliver it");
    }

    [Fact]
    public void RouterArguments_CarryTheSwitchAsWellAsTheVariable()
    {
        // Claude Desktop 1.34493.1 ignores CLAUDE_USER_DATA_DIR, so a callback
        // that has to start the profile would start it on Default — the exact
        // failure this routing feature exists to stop, arriving by a different
        // road.
        var route = new UrlRoute(BoardDirectory, BoardBundle, BoardDirectory, AlreadyRunning: false, Pid: 0);

        var arguments = ClaudeDesktopUrlRouter.Arguments(route, "claude://x");

        Assert.Contains("--user-data-dir=" + BoardDirectory, arguments);
        Assert.Contains("CLAUDE_USER_DATA_DIR=" + BoardDirectory, arguments);
    }

    [Fact]
    public void RouterArguments_OmitTheEnvironmentForDefaultAndPutTheUrlLast()
    {
        var route = new UrlRoute(DefaultDirectory, InstalledBundle, null, AlreadyRunning: false, Pid: 0);

        var arguments = ClaudeDesktopUrlRouter.Arguments(route, "claude://y");

        Assert.Equal(new[] { "-a", InstalledBundle, "claude://y" }, arguments);
        Assert.DoesNotContain("--args", arguments);
    }

    [Fact]
    public void LaunchArguments_AddressTheCloneByPath()
    {
        var arguments = ClaudeDesktopManager.LaunchArguments(
            BoardBundle, InstalledBundle, isDefault: false, BoardDirectory);

        Assert.Equal(
            new[]
            {
                "-n", "-a", BoardBundle,
                "--env", "CLAUDE_USER_DATA_DIR=" + BoardDirectory,
                "--args", "--user-data-dir=" + BoardDirectory
            },
            arguments);
    }

    [Fact]
    public void LaunchArguments_SelectTheProfileWithChromiumsSwitchAsWellAsTheVariable()
    {
        // The variable stopped working: Claude Desktop 1.34493.1 sets its
        // userData from --user-data-dir and ignores CLAUDE_USER_DATA_DIR, so a
        // launch carrying only the variable opened a second window on Default.
        // Both are passed, so a build honouring either lands in the right
        // place; --args is last because open(1) gives everything after it to
        // the application.
        var arguments = ClaudeDesktopManager.LaunchArguments(
            BoardBundle, InstalledBundle, isDefault: false, BoardDirectory);

        Assert.Equal("--args", arguments[^2]);
        Assert.Equal("--user-data-dir=" + BoardDirectory, arguments[^1]);
    }

    [Fact]
    public void LaunchArguments_LeaveDefaultWithNeitherSelector()
    {
        // Pointing either selector at the app's own default directory is not
        // the same thing to Chromium as omitting it, and can re-trigger the
        // deployment-mode chooser on an already configured profile.
        var arguments = ClaudeDesktopManager.LaunchArguments(
            BoardBundle, InstalledBundle, isDefault: true, DefaultDirectory);

        Assert.DoesNotContain("--args", arguments);
        Assert.DoesNotContain("--user-data-dir=" + DefaultDirectory, arguments);
        Assert.DoesNotContain("--env", arguments);
    }

    [Fact]
    public void LaunchArguments_OmitBothSelectorsRatherThanEmitAnEmptyOne()
    {
        // A bare `--user-data-dir=` is not the same as no switch: Chromium reads
        // the empty value and falls back to the default directory, which is the
        // silent wrong-profile launch the switch was added to prevent. The
        // router's Arguments has guarded this since it was written; the launcher
        // did not, and an asymmetry between two functions that build the same
        // command line is the kind of thing that becomes a bug later.
        var arguments = ClaudeDesktopManager.LaunchArguments(
            BoardBundle, InstalledBundle, isDefault: false, directory: "");

        Assert.Equal(new[] { "-n", "-a", BoardBundle }, arguments);
        Assert.DoesNotContain("--args", arguments);
        Assert.DoesNotContain("--env", arguments);
    }

    [Fact]
    public void LaunchArguments_UseTheInstalledBundleByPathRatherThanAnAmbiguousBundleId()
    {
        // The bug this replaces: Default with no clone was launched with
        // `-b com.anthropic.claudefordesktop`, an id every clone also answers
        // to, so LaunchServices could start a clone instead — Default wearing
        // another profile's colour.
        var arguments = ClaudeDesktopManager.LaunchArguments(
            clone: null, InstalledBundle, isDefault: true, DefaultDirectory);

        Assert.Equal(new[] { "-n", "-a", InstalledBundle }, arguments);
        Assert.DoesNotContain("-b", arguments);
        Assert.DoesNotContain("--env", arguments);
    }

    [Fact]
    public void LaunchArguments_FallBackToTheBundleIdOnlyWhenNothingResolvesOnDisk()
    {
        // Strictly worse than a path, and strictly better than not launching.
        var arguments = ClaudeDesktopManager.LaunchArguments(
            clone: null, installedApp: null, isDefault: true, DefaultDirectory);

        Assert.Equal(new[] { "-n", "-b", "com.anthropic.claudefordesktop" }, arguments);
    }

    [Fact]
    public void LaunchArguments_StillCarryTheEnvironmentOnTheBundleIdFallback()
    {
        var arguments = ClaudeDesktopManager.LaunchArguments(
            clone: null, installedApp: null, isDefault: false, BoardDirectory);

        Assert.Equal(
            new[]
            {
                "-n", "-b", "com.anthropic.claudefordesktop",
                "--env", "CLAUDE_USER_DATA_DIR=" + BoardDirectory,
                "--args", "--user-data-dir=" + BoardDirectory
            },
            arguments);
    }

    // ---- the bundle a running instance belongs to -------------------------

    [Fact]
    public void BundleFromExecutable_TakesTheAppAndRejectsHelpers()
    {
        Assert.Equal(BoardBundle,
            MacOSProcessScan.BundleFromExecutable(BoardBundle + "/Contents/MacOS/Claude"));

        // Helpers live in Claude Helper.app and must not each count as an
        // instance — that would report one profile as several and defeat the
        // duplicate-instance warning.
        Assert.Null(MacOSProcessScan.BundleFromExecutable(
            BoardBundle + "/Contents/Frameworks/Claude Helper.app/Contents/MacOS/Claude Helper"));

        Assert.Null(MacOSProcessScan.BundleFromExecutable("/usr/bin/claude"));
        Assert.Null(MacOSProcessScan.BundleFromExecutable(""));
    }
}
