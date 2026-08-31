using System;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;

namespace ClaudeBuddy.Tests;

// The new settings key, through a real file on disk.
//
// Covered here rather than only as a property because the failure this guards
// against is a *file* failure and cannot be seen in memory. Every setting has to
// be listed in ClaudeBuddySettings.KnownKeys as well as being loaded and saved;
// miss that one line and the key round-trips through _unknownKeys *as well as*
// being written properly, which JsonObject rejects as a duplicate. The comment
// on _unknownKeys records what that class of mistake has already cost here — an
// older build silently erased three real settings from somebody's file.
//
// So the assertion that matters most below is not that the flag survives. It is
// that a settings file containing a key this build has never heard of still has
// that key afterwards.
// Joins the Settings collection, without which this class and
// SettingsRoundTripTests race for one process-wide CLAUDE_BUDDY_SETTINGS_DIR
// and both read whichever directory won. That is not theoretical: these tests
// passed alone and failed two-of-four inside `dotnet test tests/Tests.sln -c
// Release` until the attribute went on.
[Collection("Settings")]
public class AccountUsageSettingsTests
{
    private static string NewSettingsDir()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "cb-integrationtests-usage-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PointSettingsAt(string dir)
    {
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
    }

    [Fact]
    public void AccountUsageIsOffUntilSomebodyAsksForIt()
    {
        PointSettingsAt(NewSettingsDir());

        // The orbs are four more things on a desktop that already has orbs on
        // it, behind a poll that starts a process per account. Neither is
        // something to begin doing to someone who has not asked.
        Assert.False(ClaudeBuddySettings.AccountUsageEnabled);
    }

    [Fact]
    public void TheFlagReachesDiskImmediatelyAndComesBack()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.AccountUsageEnabled = true;

        var path = Path.Combine(dir, "settings.json");
        Assert.True(File.Exists(path));

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        Assert.NotNull(root);
        Assert.True(root!["accountUsageEnabled"]!.GetValue<bool>());

        // And survives a reload, which is what an app restart looks like.
        PointSettingsAt(dir);
        Assert.True(ClaudeBuddySettings.AccountUsageEnabled);

        ClaudeBuddySettings.AccountUsageEnabled = false;
        PointSettingsAt(dir);
        Assert.False(ClaudeBuddySettings.AccountUsageEnabled);
    }

    // The one that catches a missing KnownKeys entry.
    //
    // If "accountUsageEnabled" were absent from that list, this save would throw
    // on the duplicate key rather than quietly dropping the stranger — so a pass
    // here means both halves are right.
    [Fact]
    public void WritingTheFlagDoesNotDisturbAKeyThisBuildHasNeverHeardOf()
    {
        var dir = NewSettingsDir();
        var path = Path.Combine(dir, "settings.json");

        File.WriteAllText(path, """
        {
          "accountUsageEnabled": true,
          "somethingFromANewerBuild": {"nested": [1, 2, 3]},
          "aScalarFromTheFuture": "keep me"
        }
        """);

        PointSettingsAt(dir);

        Assert.True(ClaudeBuddySettings.AccountUsageEnabled);

        ClaudeBuddySettings.AccountUsageEnabled = false;

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        Assert.NotNull(root);

        Assert.False(root!["accountUsageEnabled"]!.GetValue<bool>());
        Assert.Equal("keep me", root["aScalarFromTheFuture"]!.GetValue<string>());
        Assert.NotNull(root["somethingFromANewerBuild"]);
        Assert.Equal(3, root["somethingFromANewerBuild"]!["nested"]!.AsArray().Count);
    }

    // Account orbs remember where they were put, in the store the session orbs
    // already use. The keys are namespaced so an account can never collide with
    // a session's — those are keyed by cwd, and a config directory is a path
    // too, so without the prefix an account at ~/x and a session in ~/x would be
    // the same entry.
    [Fact]
    public void AnAccountOrbsPositionIsKeptApartFromASessions()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        var accountKey = AccountOrbs.PositionKey("/Users/x/project");

        ClaudeBuddySettings.SetOrbPosition("/Users/x/project", 10, 20);
        ClaudeBuddySettings.SetOrbPosition(accountKey, 300, 400);

        PointSettingsAt(dir);

        var session = ClaudeBuddySettings.OrbPositionFor("/Users/x/project");
        var account = ClaudeBuddySettings.OrbPositionFor(accountKey);

        Assert.NotNull(session);
        Assert.NotNull(account);
        Assert.Equal(10, session!.X);
        Assert.Equal(300, account!.X);
    }
}
