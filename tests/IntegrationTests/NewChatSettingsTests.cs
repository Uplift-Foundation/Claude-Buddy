using Xunit;

namespace ClaudeBuddy.Tests;

// The two new "New chat…" settings — the recent-folders list and the last
// CLI chosen — round-tripped through a real settings.json.
//
// Same collection and repointing dance as SettingsListsAndProfilesTests, for
// the reason its own header gives: ClaudeBuddySettings is one static model
// for the whole process.
[Collection("Settings")]
public class NewChatSettingsTests
{
    private static void FreshSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-settings-newchat-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
    }

    [Fact]
    public void WithNothingSavedTheRecentFolderListIsEmpty()
    {
        FreshSettings();

        Assert.Empty(ClaudeBuddySettings.NewChatRecentFolders);
    }

    [Fact]
    public void TheRecentFolderListSurvivesARestart()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR")!;

        ClaudeBuddySettings.SetNewChatRecentFolders(new[] { "/repo/one", "/repo/two" });

        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();

        Assert.Equal(new[] { "/repo/one", "/repo/two" }, ClaudeBuddySettings.NewChatRecentFolders);
    }

    // Blank entries are skipped rather than stored — the same rule the other
    // list settings in this file apply, so a caller that hands the setter a
    // half-formed value doesn't end up with an empty combo row.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankFoldersAreSkippedRatherThanStored(string blank)
    {
        FreshSettings();

        ClaudeBuddySettings.SetNewChatRecentFolders(new[] { "/repo/one", blank });

        Assert.Equal(new[] { "/repo/one" }, ClaudeBuddySettings.NewChatRecentFolders);
    }

    // Setting the list replaces it outright rather than merging — the caller
    // (RecentFolders.Merge) is what decides ordering/de-duplication/the cap,
    // so this setter has no rule of its own to apply beyond dropping blanks.
    [Fact]
    public void SettingTheListReplacesWhatWasThereBefore()
    {
        FreshSettings();

        ClaudeBuddySettings.SetNewChatRecentFolders(new[] { "/repo/one" });
        ClaudeBuddySettings.SetNewChatRecentFolders(new[] { "/repo/two", "/repo/three" });

        Assert.Equal(new[] { "/repo/two", "/repo/three" }, ClaudeBuddySettings.NewChatRecentFolders);
    }

    [Fact]
    public void WithNothingSavedTheLastCliIsNull()
    {
        FreshSettings();

        Assert.Null(ClaudeBuddySettings.NewChatLastCli);
    }

    [Fact]
    public void TheLastCliSurvivesARestart()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR")!;

        ClaudeBuddySettings.SetNewChatLastCli("codex");

        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();

        Assert.Equal("codex", ClaudeBuddySettings.NewChatLastCli);
    }

    // A blank value is not a choice — same rule NewChatRecentFolders applies,
    // restated for the scalar setting so a caller passing through an empty
    // combo selection doesn't store a value that reads as "chosen".
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankLastCliIsStoredAsNull(string? blank)
    {
        FreshSettings();

        ClaudeBuddySettings.SetNewChatLastCli("codex");
        ClaudeBuddySettings.SetNewChatLastCli(blank);

        Assert.Null(ClaudeBuddySettings.NewChatLastCli);
    }

    // --- NewChatLastProfile (CB-201's Account picker) --------------------

    [Fact]
    public void WithNothingSavedTheLastProfileIsNull()
    {
        FreshSettings();

        Assert.Null(ClaudeBuddySettings.NewChatLastProfile);
    }

    [Fact]
    public void TheLastProfileSurvivesARestart()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR")!;

        ClaudeBuddySettings.SetNewChatLastProfile(".claude-work");

        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();

        Assert.Equal(".claude-work", ClaudeBuddySettings.NewChatLastProfile);
    }

    // A blank value is not a choice — same rule ABlankLastCliIsStoredAsNull
    // applies to the CLI setting beside this one.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankLastProfileIsStoredAsNull(string? blank)
    {
        FreshSettings();

        ClaudeBuddySettings.SetNewChatLastProfile(".claude-work");
        ClaudeBuddySettings.SetNewChatLastProfile(blank);

        Assert.Null(ClaudeBuddySettings.NewChatLastProfile);
    }

    // Null means Default was chosen just as much as it means never chosen —
    // both read the same way, so setting it back to null after a real
    // profile is the ordinary "picked Default this time" case, not an edge
    // case of the blank rule above.
    [Fact]
    public void SettingTheLastProfileBackToNullReplacesTheSavedOne()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR")!;

        ClaudeBuddySettings.SetNewChatLastProfile(".claude-work");
        ClaudeBuddySettings.SetNewChatLastProfile(null);

        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();

        Assert.Null(ClaudeBuddySettings.NewChatLastProfile);
    }
}
