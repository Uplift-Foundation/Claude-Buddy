using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using Verdict = Orbweaver.LegacyHookCleanup.Verdict;

namespace Orbweaver.Tests;

// CB-255 §1: when a legacy hook-script folder may be deleted. The rule is
// pure and every arm is a case here; the IO half is driven against real temp
// folders with back-dated markers in tests/IntegrationTests.
public class LegacyHookCleanupTests
{
    private static readonly string[] JustOurs = { "ClaudeBuddyHook.sh", ".superseded" };

    // --- Decide ------------------------------------------------------------

    // No marker: the installer has not run since the upgrade, so the CLI's
    // settings still point here and the script is the live hook.
    [Fact]
    public void NoMarkerLeavesTheFolderHoweverItLooks()
    {
        Assert.Equal(Verdict.NoMarker, LegacyHookCleanup.Decide(null, JustOurs));
        Assert.Equal(Verdict.NoMarker, LegacyHookCleanup.Decide(null, new[] { "Logs" }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(13)]
    public void AMarkerYoungerThanFourteenDaysIsTooSoon(int days)
    {
        Assert.Equal(Verdict.TooSoon, LegacyHookCleanup.Decide(TimeSpan.FromDays(days), JustOurs));
    }

    // One second short of the line, and exactly on it: >= 14 days retires.
    [Fact]
    public void TheBoundaryIsFourteenDaysInclusive()
    {
        Assert.Equal(Verdict.TooSoon,
            LegacyHookCleanup.Decide(TimeSpan.FromDays(14) - TimeSpan.FromSeconds(1), JustOurs));
        Assert.Equal(Verdict.Retire, LegacyHookCleanup.Decide(TimeSpan.FromDays(14), JustOurs));
        Assert.Equal(TimeSpan.FromDays(14), LegacyHookCleanup.Grace);
    }

    // A marker stamped in the future by a clock that has since gone back is
    // not old enough, rather than infinitely old.
    [Fact]
    public void ANegativeAgeIsTooSoon()
    {
        Assert.Equal(Verdict.TooSoon, LegacyHookCleanup.Decide(TimeSpan.FromDays(-3), JustOurs));
    }

    [Theory]
    [InlineData(".superseded")]
    [InlineData("ClaudeBuddyHook.sh", ".superseded")]
    [InlineData("ClaudeBuddyHook.ps1", ".superseded")]
    [InlineData("ClaudeBuddyHook.sh", "ClaudeBuddyHook.ps1", ".superseded")]
    [InlineData("claudebuddyhook.PS1", ".SUPERSEDED")]
    public void AnOldMarkerOverOnlyOurFilesRetires(params string[] contents)
    {
        Assert.Equal(Verdict.Retire, LegacyHookCleanup.Decide(TimeSpan.FromDays(30), contents));
    }

    // Anything we did not put there refuses the whole folder: the Windows
    // Logs root the migration has not moved, a user's own file, or the new
    // script copied into the old folder by hand.
    [Theory]
    [InlineData("Logs")]
    [InlineData("notes.txt")]
    [InlineData("OrbweaverHook.sh")]
    [InlineData("ClaudeBuddyHook.sh.bak")]
    public void AnythingUnrecognisedRefusesTheFolder(string stranger)
    {
        Assert.Equal(Verdict.Unrecognised,
            LegacyHookCleanup.Decide(TimeSpan.FromDays(30), new[] { "ClaudeBuddyHook.sh", ".superseded", stranger }));
    }

    // --- LegacyFolders -----------------------------------------------------

    private const string Home = "/Users/me";
    private const string Local = "/local";

    [Fact]
    public void OnAMacEveryCliHomeIsListedAndLocalAppDataIsNot()
    {
        Assert.Equal(
            new[]
            {
                Path.Combine(Home, ".claude", "claude-buddy"),
                Path.Combine(Home, ".codex", "claude-buddy"),
                Path.Combine(Home, ".grok", "claude-buddy")
            },
            LegacyHookCleanup.LegacyFolders(onWindows: false, Home, Local, _ => null));
    }

    // install-windows-hooks.ps1's Claude Code copy: %LOCALAPPDATA%\ClaudeBuddy.
    [Fact]
    public void OnWindowsTheLocalAppDataFolderIsListedToo()
    {
        var folders = LegacyHookCleanup.LegacyFolders(onWindows: true, Home, Local, _ => null);

        Assert.Equal(4, folders.Count);
        Assert.Equal(Path.Combine(Local, "ClaudeBuddy"), folders[^1]);
    }

    // Each installer's own override, beside the default rather than instead
    // of it: the installer may have been run both ways.
    [Fact]
    public void CodexHomeAndGrokHomeAddTheirOwnFolders()
    {
        var env = new Dictionary<string, string?> { ["CODEX_HOME"] = "/work/codex", ["GROK_HOME"] = "/work/grok" };

        var folders = LegacyHookCleanup.LegacyFolders(false, Home, Local, name => env.GetValueOrDefault(name));

        Assert.Contains(Path.Combine("/work/codex", "claude-buddy"), folders);
        Assert.Contains(Path.Combine("/work/grok", "claude-buddy"), folders);
        Assert.Contains(Path.Combine(Home, ".codex", "claude-buddy"), folders);
        Assert.Equal(5, folders.Count);
    }

    [Fact]
    public void AnEmptyOverrideAddsNothing()
    {
        Assert.Equal(3, LegacyHookCleanup.LegacyFolders(false, Home, Local, _ => "").Count);
    }

    // An override that names the default home is one folder, not two —
    // case-insensitively on Windows, exactly on a Mac.
    [Fact]
    public void AnOverrideNamingTheDefaultIsListedOnce()
    {
        var sameHome = Path.Combine(Home, ".codex");
        Assert.Equal(3, LegacyHookCleanup.LegacyFolders(
            false, Home, Local, n => n == "CODEX_HOME" ? sameHome : null).Count);

        var shouted = Path.Combine(Home, ".CODEX");
        Assert.Equal(4, LegacyHookCleanup.LegacyFolders(
            true, Home, Local, n => n == "CODEX_HOME" ? shouted : null).Count);
        Assert.Equal(4, LegacyHookCleanup.LegacyFolders(
            false, Home, Local, n => n == "CODEX_HOME" ? shouted : null).Count);
    }

    // --- Skipped -----------------------------------------------------------

    // Either variable in either spelling (CB-256).
    [Theory]
    [InlineData("ORBWEAVER_SETTINGS_DIR")]
    [InlineData("ORBWEAVER_STATUS_ROOT")]
    [InlineData(BrandEnv.LegacyPrefix + BrandEnv.SettingsDir)]
    [InlineData(BrandEnv.LegacyPrefix + BrandEnv.StatusRoot)]
    public void EitherTestOverrideSkipsTheWholeStep(string variable)
    {
        Assert.True(LegacyHookCleanup.Skipped(name => name == variable ? "/sandbox" : null));
    }

    [Fact]
    public void NoOverrideOrAnEmptyOneDoesNotSkip()
    {
        Assert.False(LegacyHookCleanup.Skipped(_ => null));
        Assert.False(LegacyHookCleanup.Skipped(_ => ""));
    }

    // And this process *is* under them, which is what makes the real Run()
    // a no-op in every suite (StartupOrderTests calls it).
    [Fact]
    public void ThisSuiteIsUnderTheOverrides()
    {
        Assert.True(LegacyHookCleanup.Skipped(Environment.GetEnvironmentVariable));
    }
}
