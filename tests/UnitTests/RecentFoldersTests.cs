using System;
using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.Tests;

// RecentFolders.Merge: the "New chat…" dialog's folder combo. Live local-CLI
// cwds first, then whatever was saved before, de-duplicated and capped.
public class RecentFoldersTests
{
    private static SessionStatus Local(string cwd, SessionSource source = SessionSource.ClaudeCode) =>
        new() { Cwd = cwd, Source = source };

    [Fact]
    public void WithNothingAtAllTheListIsEmpty()
    {
        var result = RecentFolders.Merge(Array.Empty<SessionStatus>(), Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void LiveLocalCwdsComeBeforeSavedFolders()
    {
        var live = new[] { Local("/repo/live") };
        var saved = new[] { "/repo/saved" };

        var result = RecentFolders.Merge(live, saved);

        Assert.Equal(new[] { "/repo/live", "/repo/saved" }, result);
    }

    // OpenClaw and remote-control sessions have no local folder to offer —
    // IsLocalCli is exactly the line the rest of the app already draws for
    // "has a terminal you can be sent to".
    [Theory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void NonLocalSessionsAreNotOffered(SessionSource source)
    {
        var live = new[] { Local("/repo/remote", source) };

        var result = RecentFolders.Merge(live, Array.Empty<string>());

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(SessionSource.ClaudeCode)]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.Grok)]
    public void EveryLocalCliCountsAsLive(SessionSource source)
    {
        var live = new[] { Local("/repo/live", source) };

        var result = RecentFolders.Merge(live, Array.Empty<string>());

        Assert.Equal(new[] { "/repo/live" }, result);
    }

    // A folder that's both live and previously saved appears once, keeping
    // the live copy's earlier position.
    [Fact]
    public void ALiveFolderThatWasAlsoSavedAppearsOnlyOnce()
    {
        var live = new[] { Local("/repo/one") };
        var saved = new[] { "/repo/one", "/repo/two" };

        var result = RecentFolders.Merge(live, saved);

        Assert.Equal(new[] { "/repo/one", "/repo/two" }, result);
    }

    [Fact]
    public void DuplicatesWithinSavedAreDroppedToo()
    {
        var saved = new[] { "/repo/one", "/repo/one" };

        var result = RecentFolders.Merge(Array.Empty<SessionStatus>(), saved);

        Assert.Equal(new[] { "/repo/one" }, result);
    }

    [Fact]
    public void TheResultIsCappedAtMax()
    {
        var saved = new[] { "/a", "/b", "/c", "/d", "/e" };

        var result = RecentFolders.Merge(Array.Empty<SessionStatus>(), saved, max: 3);

        Assert.Equal(new[] { "/a", "/b", "/c" }, result);
    }

    [Fact]
    public void ADefaultMaxOfTenIsAppliedWhenNoneIsGiven()
    {
        var saved = new List<string>();
        for (var i = 0; i < 15; i++) saved.Add($"/repo/{i}");

        var result = RecentFolders.Merge(Array.Empty<SessionStatus>(), saved);

        Assert.Equal(RecentFolders.DefaultMax, result.Count);
    }

    [Fact]
    public void AZeroOrNegativeMaxProducesAnEmptyList()
    {
        var saved = new[] { "/repo/one" };

        Assert.Empty(RecentFolders.Merge(Array.Empty<SessionStatus>(), saved, max: 0));
        Assert.Empty(RecentFolders.Merge(Array.Empty<SessionStatus>(), saved, max: -1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankCwdsAndSavedEntriesAreSkipped(string blank)
    {
        var live = new[] { Local(blank) };
        var saved = new[] { blank, "/repo/real" };

        var result = RecentFolders.Merge(live, saved);

        Assert.Equal(new[] { "/repo/real" }, result);
    }

    [Fact]
    public void ANullSessionInTheLiveListIsSkippedRatherThanThrowing()
    {
        var live = new SessionStatus?[] { null, Local("/repo/one") };

        var result = RecentFolders.Merge(live!, Array.Empty<string>());

        Assert.Equal(new[] { "/repo/one" }, result);
    }

    [Fact]
    public void NullLiveOrSavedThrows()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RecentFolders.Merge(null!, Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() =>
            RecentFolders.Merge(Array.Empty<SessionStatus>(), null!));
    }
}
