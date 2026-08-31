using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers which account a usage reading belongs to, and what it is called.
//
// The file this exists for is AccountFilePath, and the trap is worth stating
// because it is the kind that produces no error at all. Claude Code describes
// the account it runs under in `~/.claude.json` — a *sibling* of the config
// directory — while an extra account under CLAUDE_CONFIG_DIR is described by
// `<dir>/.claude.json`, inside it. A reasonable-looking
// Path.Combine(configDir, ".claude.json") for the default account therefore
// lands on `~/.claude/.claude.json`, which exists, parses, is a genuinely
// different and older file, and has no oauthAccount in it whatsoever. The
// result is not a crash: it is every default account quietly labelled by its
// directory name, forever, on every machine.
//
// Verified against this machine before the rule was written: ~/.claude.json has
// an oauthAccount, ~/.claude/.claude.json does not, and
// ~/.claude-board/.claude.json does.
public class UsageAccountsTests
{
    private static string Home => Path.Combine("/Users", "someone");

    [Fact]
    public void TheDefaultAccountIsDescribedBesideItsDirectoryNotInsideIt()
    {
        Assert.Equal(
            Path.Combine(Home, ".claude.json"),
            UsageAccounts.AccountFilePath(Home, null));
    }

    [Fact]
    public void AnExtraAccountIsDescribedInsideItsOwnDirectory()
    {
        var dir = Path.Combine(Home, ".claude-work");

        Assert.Equal(
            Path.Combine(dir, ".claude.json"),
            UsageAccounts.AccountFilePath(Home, dir));
    }

    // The name a person actually typed to log in, which is the only thing that
    // reliably tells two accounts at one organisation apart.
    [Fact]
    public void AnAccountIsNamedByTheLocalPartOfItsEmail()
    {
        const string json = """
        {"oauthAccount":{"emailAddress":"board@uplifttech.org",
                         "displayName":"Warren Thompson",
                         "organizationName":"The UPLIFT Foundation"}}
        """;

        Assert.Equal("board", UsageAccounts.LabelFrom(json, null));
    }

    [Fact]
    public void TheDisplayNameIsTheSecondChoice()
    {
        const string json = """
        {"oauthAccount":{"displayName":"Warren Thompson"}}
        """;

        Assert.Equal("Warren Thompson", UsageAccounts.LabelFrom(json, null));
    }

    // An account that has never been logged in has no oauthAccount at all. That
    // is an ordinary state — ~/.claude-work on this machine is exactly it — and
    // has to produce a usable name rather than a blank orb.
    [Theory]
    [InlineData("""{"numStartups":41,"installMethod":"native"}""")]
    [InlineData("""{"oauthAccount":null}""")]
    [InlineData("""{"oauthAccount":{"emailAddress":""}}""")]
    [InlineData("not json")]
    [InlineData(null)]
    public void AnAccountWithNoIdentityFallsBackToItsDirectory(string? json)
    {
        Assert.Equal("work", UsageAccounts.LabelFrom(json, "/Users/someone/.claude-work"));
    }

    // The leading dot and the "claude-" every one of these directories starts
    // with say nothing the orbs' own presence does not.
    [Theory]
    [InlineData("/Users/someone/.claude-work", "work")]
    [InlineData("/Users/someone/.claude-board", "board")]
    [InlineData("/Users/someone/.claude", "claude")]
    [InlineData("/Users/someone/scratch", "scratch")]
    [InlineData("/Users/someone/.claude-", "claude-")]
    [InlineData(null, "default")]
    [InlineData("", "default")]
    public void TheFallbackNameIsTheDirectoryWithoutItsCeremony(string? dir, string expected)
    {
        Assert.Equal(expected, UsageAccounts.FallbackLabel(dir));
    }

    [Fact]
    public void ATrailingSeparatorDoesNotSwallowTheName()
    {
        Assert.Equal("work", UsageAccounts.FallbackLabel("/Users/someone/.claude-work/"));
    }

    [Fact]
    public void TheFullEmailIsAvailableForTheCard()
    {
        const string json = """
        {"oauthAccount":{"emailAddress":"board@uplifttech.org"}}
        """;

        Assert.Equal("board@uplifttech.org", UsageAccounts.EmailFrom(json));
        Assert.Null(UsageAccounts.EmailFrom("""{"numStartups":1}"""));
        Assert.Null(UsageAccounts.EmailFrom("not json"));
        Assert.Null(UsageAccounts.EmailFrom(null));
    }

    // This app's own account leads, spelled null, meaning "leave the environment
    // alone" — the same convention BackgroundJobs.ReadOne uses. The rest come
    // from ExtraAccountDirs rather than being derived again here, which is what
    // stops a settings list naming ".claude" explicitly from asking the same
    // account twice.
    [Fact]
    public void TheOwnAccountLeadsAndIsNotAskedTwice()
    {
        var dirs = UsageAccounts.ConfigDirs(
            Home, new List<string> { ".claude", ".claude-work", ".claude-board" });

        Assert.Null(dirs[0]);
        Assert.Equal(3, dirs.Count);
        Assert.Equal(Path.Combine(Home, ".claude-work"), dirs[1]);
        Assert.Equal(Path.Combine(Home, ".claude-board"), dirs[2]);
    }

    [Fact]
    public void NoExtraAccountsMeansJustTheOwnOne()
    {
        var dirs = UsageAccounts.ConfigDirs(Home, Array.Empty<string>());

        Assert.Single(dirs);
        Assert.Null(dirs[0]);
    }
}
