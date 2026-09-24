using System;
using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.Tests;

// NewChatOrbWatch: the ~20s "did an orb actually appear" check the New chat
// dialog runs after a launch.
public class NewChatOrbWatchTests
{
    private static SessionStatus Local(string cwd, SessionSource source = SessionSource.ClaudeCode) =>
        new() { Cwd = cwd, Source = source };

    // --- CliOf ---

    // NewChatCli is internal, so it can't be a [Theory] parameter on a public
    // method (CS0051) — the mapping is exercised as one case per outcome
    // instead, inside the method body rather than via InlineData.
    [Fact]
    public void CliOfMapsEveryLocalCli()
    {
        Assert.Equal(NewChatCli.ClaudeCode, NewChatOrbWatch.CliOf(SessionSource.ClaudeCode));
        Assert.Equal(NewChatCli.Codex, NewChatOrbWatch.CliOf(SessionSource.Codex));
        Assert.Equal(NewChatCli.Grok, NewChatOrbWatch.CliOf(SessionSource.Grok));
    }

    [Theory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void CliOfIsNullForNonLaunchableSources(SessionSource source)
    {
        Assert.Null(NewChatOrbWatch.CliOf(source));
    }

    // --- IsNewMatch ---

    [Fact]
    public void ANewLocalSessionInTheRightFolderAndCliMatches()
    {
        var prior = new HashSet<string>();
        var status = Local("/repo/one");

        Assert.True(NewChatOrbWatch.IsNewMatch("id-1", status, prior, NewChatCli.ClaudeCode, "/repo/one"));
    }

    [Fact]
    public void ASessionThatAlreadyExistedBeforeTheLaunchDoesNotMatch()
    {
        var prior = new HashSet<string> { "id-1" };
        var status = Local("/repo/one");

        Assert.False(NewChatOrbWatch.IsNewMatch("id-1", status, prior, NewChatCli.ClaudeCode, "/repo/one"));
    }

    [Fact]
    public void ANonLocalSessionNeverMatches()
    {
        var prior = new HashSet<string>();
        var status = Local("/repo/one", SessionSource.OpenClaw);

        Assert.False(NewChatOrbWatch.IsNewMatch("id-1", status, prior, NewChatCli.ClaudeCode, "/repo/one"));
    }

    [Fact]
    public void TheWrongCliDoesNotMatch()
    {
        var prior = new HashSet<string>();
        var status = Local("/repo/one", SessionSource.Codex);

        Assert.False(NewChatOrbWatch.IsNewMatch("id-1", status, prior, NewChatCli.ClaudeCode, "/repo/one"));
    }

    [Fact]
    public void ADifferentFolderDoesNotMatch()
    {
        var prior = new HashSet<string>();
        var status = Local("/repo/other");

        Assert.False(NewChatOrbWatch.IsNewMatch("id-1", status, prior, NewChatCli.ClaudeCode, "/repo/one"));
    }

    [Fact]
    public void TrailingSeparatorsAreIgnored()
    {
        var prior = new HashSet<string>();
        var status = Local("/repo/one/");

        Assert.True(NewChatOrbWatch.IsNewMatch("id-1", status, prior, NewChatCli.ClaudeCode, "/repo/one"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ABlankCwdOnEitherSideNeverMatches(string? blank)
    {
        var prior = new HashSet<string>();
        var status = Local(blank ?? "");

        Assert.False(NewChatOrbWatch.IsNewMatch("id-1", status, prior, NewChatCli.ClaudeCode, "/repo/one"));
        Assert.False(NewChatOrbWatch.IsNewMatch("id-1", Local("/repo/one"), prior, NewChatCli.ClaudeCode, blank ?? ""));
    }

    [Fact]
    public void NullArgumentsThrow()
    {
        var prior = new HashSet<string>();
        Assert.Throws<ArgumentNullException>(() =>
            NewChatOrbWatch.IsNewMatch(null!, Local("/repo/one"), prior, NewChatCli.ClaudeCode, "/repo/one"));
        Assert.Throws<ArgumentNullException>(() =>
            NewChatOrbWatch.IsNewMatch("id-1", null!, prior, NewChatCli.ClaudeCode, "/repo/one"));
        Assert.Throws<ArgumentNullException>(() =>
            NewChatOrbWatch.IsNewMatch("id-1", Local("/repo/one"), null!, NewChatCli.ClaudeCode, "/repo/one"));
    }

    // --- FindMatch ---

    [Fact]
    public void FindMatchReturnsTheFirstMatchingId()
    {
        var current = new Dictionary<string, SessionStatus>
        {
            ["id-1"] = Local("/repo/other"),
            ["id-2"] = Local("/repo/one")
        };

        Assert.Equal("id-2", NewChatOrbWatch.FindMatch(current, new HashSet<string>(), NewChatCli.ClaudeCode, "/repo/one"));
    }

    [Fact]
    public void FindMatchReturnsNullWhenNothingMatches()
    {
        var current = new Dictionary<string, SessionStatus> { ["id-1"] = Local("/repo/other") };

        Assert.Null(NewChatOrbWatch.FindMatch(current, new HashSet<string>(), NewChatCli.ClaudeCode, "/repo/one"));
    }

    [Fact]
    public void FindMatchThrowsOnNullCurrent()
    {
        Assert.Throws<ArgumentNullException>(() =>
            NewChatOrbWatch.FindMatch(null!, new HashSet<string>(), NewChatCli.ClaudeCode, "/repo/one"));
    }
}
