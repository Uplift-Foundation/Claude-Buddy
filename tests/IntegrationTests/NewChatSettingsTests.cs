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

        Assert.Null(ClaudeBuddySettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
    }

    [Fact]
    public void TheLastProfileSurvivesARestart()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR")!;

        ClaudeBuddySettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, ".claude-work");

        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();

        Assert.Equal(".claude-work", ClaudeBuddySettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
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

        ClaudeBuddySettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, ".claude-work");
        ClaudeBuddySettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, blank);

        Assert.Null(ClaudeBuddySettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
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

        ClaudeBuddySettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, ".claude-work");
        ClaudeBuddySettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, null);

        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();

        Assert.Null(ClaudeBuddySettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
    }

    // --- CB-203: one remembered account per CLI -------------------------

    // Each CLI's last account round-trips through a real file on its own key,
    // and setting one never disturbs another — a Codex pick must never come
    // back as Claude Code's (or Grok's) remembered account.
    [Fact]
    public void EachCliRemembersItsOwnLastAccountAcrossAReload()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR")!;

        ClaudeBuddySettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, ".claude-work");
        ClaudeBuddySettings.SetNewChatLastProfile(NewChatCli.Codex, ".codex-work");
        ClaudeBuddySettings.SetNewChatLastProfile(NewChatCli.Grok, ".grok-work");

        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();

        Assert.Equal(".claude-work", ClaudeBuddySettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
        Assert.Equal(".codex-work", ClaudeBuddySettings.NewChatLastProfileFor(NewChatCli.Codex));
        Assert.Equal(".grok-work", ClaudeBuddySettings.NewChatLastProfileFor(NewChatCli.Grok));

        var json = File.ReadAllText(Path.Combine(dir, "settings.json"));
        Assert.Contains("\"newChatLastProfile\": \".claude-work\"", json);
        Assert.Contains("\"newChatLastCodexProfile\": \".codex-work\"", json);
        Assert.Contains("\"newChatLastGrokProfile\": \".grok-work\"", json);
    }

    [Theory]
    [InlineData("Codex")]
    [InlineData("Grok")]
    public void ClearingOneCliLastAccountLeavesTheOthersAlone(string cliName)
    {
        var cli = Enum.Parse<NewChatCli>(cliName);
        FreshSettings();

        ClaudeBuddySettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, ".claude-work");
        ClaudeBuddySettings.SetNewChatLastProfile(cli, ".other-work");
        ClaudeBuddySettings.SetNewChatLastProfile(cli, "  ");

        Assert.Null(ClaudeBuddySettings.NewChatLastProfileFor(cli));
        Assert.Equal(".claude-work", ClaudeBuddySettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
    }

    // A settings file written before CB-203 has only newChatLastProfile. It
    // stays Claude Code's, and Codex and Grok start with nothing remembered
    // rather than inheriting it.
    [Fact]
    public void APreCb203FileKeepsItsAccountForClaudeCodeOnly()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR")!;
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"newChatLastProfile\": \".claude-board\" }");

        ClaudeBuddySettings.ReloadForTests();

        Assert.Equal(".claude-board", ClaudeBuddySettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
        Assert.Null(ClaudeBuddySettings.NewChatLastProfileFor(NewChatCli.Codex));
        Assert.Null(ClaudeBuddySettings.NewChatLastProfileFor(NewChatCli.Grok));
    }
}
