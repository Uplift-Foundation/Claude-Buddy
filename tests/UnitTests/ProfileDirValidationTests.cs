using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// Whether a folder someone picked is somewhere a Claude Code profile may live.
//
// The rule is "a direct child of a home directory", and getting it wrong is not
// cosmetic: a profile directory that is not one resolves to the wrong place on
// native Windows, in WSL and on macOS alike. It is the same rule the macOS
// installers apply when they refuse a profile name containing a slash.
//
// The WSL half matters too — a profile can be WSL-only, with no Windows-side
// counterpart at all, so a folder inside a distribution's home has to validate
// exactly like a native one rather than being rejected for not sitting under
// C:\Users\.
//
// Separated from the folder picker that calls it, which opens an OS dialog and
// waits for a human. This is the part with a decision in it.
public class ProfileDirValidationTests
{
    private static readonly string[] NoWsl = Array.Empty<string>();

    // Paths are built from the platform's own separator rather than written as
    // literals: hardcoding what an OS-dependent function produces is asserting
    // the platform instead of the rule, which has bitten this branch twice.
    private static string Under(string root, params string[] parts) =>
        Path.Combine(new[] { root }.Concat(parts).ToArray());

    private static readonly string Home =
        Path.Combine(Path.GetTempPath(), "cb-home");

    [Fact]
    public void ADirectChildOfHomeIsAccepted()
    {
        Assert.True(SettingsWindow.IsDirectChildOfAHome(
            Under(Home, ".claude-work"), Home, NoWsl));
    }

    // A nested folder is the case the rule exists to refuse.
    [Fact]
    public void ANestedFolderIsRefused()
    {
        Assert.False(SettingsWindow.IsDirectChildOfAHome(
            Under(Home, "projects", ".claude-work"), Home, NoWsl));
    }

    // Home itself is not a child of home.
    [Fact]
    public void HomeItselfIsRefused()
    {
        Assert.False(SettingsWindow.IsDirectChildOfAHome(Home, Home, NoWsl));
    }

    [Fact]
    public void SomewhereElseEntirelyIsRefused()
    {
        Assert.False(SettingsWindow.IsDirectChildOfAHome(
            Path.Combine(Path.GetTempPath(), "elsewhere", ".claude"), Home, NoWsl));
    }

    // A trailing separator is what a folder picker commonly hands back, and it
    // must not change the answer — otherwise the same folder is accepted or
    // refused depending on how it was selected.
    [Fact]
    public void ATrailingSeparatorDoesNotChangeTheAnswer()
    {
        var picked = Under(Home, ".claude-work") + Path.DirectorySeparatorChar;

        Assert.True(SettingsWindow.IsDirectChildOfAHome(picked, Home, NoWsl));
    }

    [Fact]
    public void ATrailingSeparatorOnTheHomeDoesNotChangeTheAnswerEither()
    {
        Assert.True(SettingsWindow.IsDirectChildOfAHome(
            Under(Home, ".claude-work"), Home + Path.DirectorySeparatorChar, NoWsl));
    }

    // ---- WSL homes ---------------------------------------------------------

    // These three run on Windows only, and not out of caution: the rule compares
    // a picked path against its parent via Path.GetDirectoryName, which splits on
    // backslashes on Windows and does not on macOS. A UNC path is Windows syntax,
    // so off Windows these cases would be asserting what the runtime does with a
    // string it has no reason to understand rather than what the rule decides.
    //
    // The behaviour they cover is Windows-only anyway — GetWslHomeUncPaths returns
    // an empty list everywhere else, which the last test in this file asserts from
    // the other side and does so on both platforms.

    [Fact]
    public void ADirectChildOfAWslHomeIsAcceptedToo()
    {
        if (!OperatingSystem.IsWindows()) return;

        var wsl = @"\\wsl.localhost\Ubuntu\home\warren";

        Assert.True(SettingsWindow.IsDirectChildOfAHome(
            wsl + @"\.claude", Home, new[] { wsl }));
    }

    // And nesting is refused there for the same reason it is on the native side.
    [Fact]
    public void ANestedFolderInsideAWslHomeIsRefused()
    {
        if (!OperatingSystem.IsWindows()) return;

        var wsl = @"\\wsl.localhost\Ubuntu\home\warren";

        Assert.False(SettingsWindow.IsDirectChildOfAHome(
            wsl + @"\projects\.claude", Home, new[] { wsl }));
    }

    // Several distributions, and a folder in any of their homes is fine.
    [Fact]
    public void AnyOfSeveralWslHomesWillDo()
    {
        if (!OperatingSystem.IsWindows()) return;

        var ubuntu = @"\\wsl.localhost\Ubuntu\home\warren";
        var debian = @"\\wsl.localhost\Debian\home\warren";

        Assert.True(SettingsWindow.IsDirectChildOfAHome(
            debian + @"\.claude", Home, new[] { ubuntu, debian }));
    }

    // Case-insensitive, because these are Windows paths and a picker may hand
    // back a different case from the one the distribution reported.
    [Fact]
    public void TheComparisonIsCaseInsensitive()
    {
        if (!OperatingSystem.IsWindows()) return;

        var wsl = @"\\wsl.localhost\Ubuntu\home\warren";

        Assert.True(SettingsWindow.IsDirectChildOfAHome(
            @"\\WSL.LOCALHOST\Ubuntu\home\warren\.claude", Home, new[] { wsl }));
    }

    // With no distributions installed the rule reduces to "a direct child of
    // $HOME", which is exactly what it should be off Windows — GetWslHomeUncPaths
    // returns an empty list there.
    [Fact]
    public void WithNoWslHomesTheRuleIsJustTheNativeOne()
    {
        Assert.True(SettingsWindow.IsDirectChildOfAHome(
            Under(Home, ".claude"), Home, NoWsl));

        // A WSL path with no WSL homes offered is refused wherever this runs: on
        // Windows because its parent is not $HOME, and elsewhere because the
        // whole string is one path segment with no parent at all.
        Assert.False(SettingsWindow.IsDirectChildOfAHome(
            @"\\wsl.localhost\Ubuntu\home\warren\.claude", Home, NoWsl));
    }
}
