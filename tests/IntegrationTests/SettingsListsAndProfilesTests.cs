using Xunit;

namespace ClaudeBuddy.Tests;

// The settings that are lists and dictionaries rather than single values, and
// the per-profile store.
//
// Same collection and the same repointing dance as SettingsRoundTripTests, and
// for the reason its header gives: ClaudeBuddySettings is a static class with one
// model for the whole process, so two of these running at once would stomp each
// other's environment variable.
//
// These are worth covering separately from the scalar settings because their
// failure modes are not "the value came back wrong". A list that accumulates
// duplicates runs a second relay for one account; a fallback chain that reads the
// wrong key silently resets a choice somebody already made; and a store that
// hands out its own instance lets a caller change a saved profile without saving
// it, so the change survives until the next restart and then vanishes.
[Collection("Settings")]
public class SettingsListsAndProfilesTests
{
    private static void FreshSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-settings-lists-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
    }

    // --- Remote Control accounts: a list that replaced a single value ---

    // The fallback chain, and the reason it exists: turning one account into a
    // list must not quietly reset someone who had already chosen one.
    [Fact]
    public void TheOldSingleAccountKeyIsStillHonouredWhenNoListExists()
    {
        FreshSettings();

        ClaudeBuddySettings.RemoteControlProfileDir = ".claude-work";

        Assert.Equal(new[] { ".claude-work" }, ClaudeBuddySettings.RemoteControlProfileDirs);
    }

    [Fact]
    public void WithNeitherKeySetTheDefaultAccountIsUsed()
    {
        FreshSettings();

        Assert.Equal(
            new[] { ClaudeBuddySettings.DefaultRemoteControlProfileDir },
            ClaudeBuddySettings.RemoteControlProfileDirs);
    }

    // A blank single value is not a choice, so it falls through to the default
    // rather than producing a list with one empty account in it.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankSingleAccountFallsThroughToTheDefault(string value)
    {
        FreshSettings();

        ClaudeBuddySettings.RemoteControlProfileDir = value;

        Assert.Equal(
            new[] { ClaudeBuddySettings.DefaultRemoteControlProfileDir },
            ClaudeBuddySettings.RemoteControlProfileDirs);
    }

    [Fact]
    public void AListWinsOverTheOldSingleKey()
    {
        FreshSettings();

        ClaudeBuddySettings.RemoteControlProfileDir = ".claude-old";
        ClaudeBuddySettings.SetRemoteControlProfileDirs(new[] { ".claude", ".claude-work" });

        Assert.Equal(new[] { ".claude", ".claude-work" }, ClaudeBuddySettings.RemoteControlProfileDirs);
    }

    // The single key is cleared when a list is written, so the two cannot
    // disagree. A leftover value would be a second source of truth that only
    // surfaces if the list is emptied again — which is the next test.
    [Fact]
    public void WritingAListClearsTheOldSingleKeyForGood()
    {
        FreshSettings();

        ClaudeBuddySettings.RemoteControlProfileDir = ".claude-old";
        ClaudeBuddySettings.SetRemoteControlProfileDirs(new[] { ".claude-work" });
        ClaudeBuddySettings.SetRemoteControlProfileDirs(Array.Empty<string>());

        // Back to the default, not back to ".claude-old" — the old choice is
        // gone rather than lying in wait.
        Assert.Equal(
            new[] { ClaudeBuddySettings.DefaultRemoteControlProfileDir },
            ClaudeBuddySettings.RemoteControlProfileDirs);
    }

    // Duplicates are dropped. Two entries for one account would start two relays
    // for it, and the relay name is a machine-wide mutex per account — so they
    // would fight over it, which is a failure the repo has already seen from
    // another direction (see LiveBridgeFactAttribute's comment).
    [Fact]
    public void DuplicateAccountsAreDropped()
    {
        FreshSettings();

        ClaudeBuddySettings.SetRemoteControlProfileDirs(
            new[] { ".claude", ".claude-work", ".claude" });

        Assert.Equal(new[] { ".claude", ".claude-work" }, ClaudeBuddySettings.RemoteControlProfileDirs);
    }

    // Ordinal, so two accounts differing only in case stay two accounts — they
    // are directory names, and on a case-sensitive filesystem they really are
    // different.
    [Fact]
    public void AccountsDifferingOnlyInCaseAreBothKept()
    {
        FreshSettings();

        ClaudeBuddySettings.SetRemoteControlProfileDirs(new[] { ".claude", ".Claude" });

        Assert.Equal(2, ClaudeBuddySettings.RemoteControlProfileDirs.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankAccountsAreSkippedRatherThanStored(string blank)
    {
        FreshSettings();

        ClaudeBuddySettings.SetRemoteControlProfileDirs(new[] { ".claude", blank });

        Assert.Equal(new[] { ".claude" }, ClaudeBuddySettings.RemoteControlProfileDirs);
    }

    [Fact]
    public void TheAccountListSurvivesARestart()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR")!;

        ClaudeBuddySettings.SetRemoteControlProfileDirs(new[] { ".claude", ".claude-work" });

        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();

        Assert.Equal(new[] { ".claude", ".claude-work" }, ClaudeBuddySettings.RemoteControlProfileDirs);
    }

    // --- extra CLI profile directories ---

    [Fact]
    public void AnExtraClaudeCodeProfileIsAddedAndPersisted()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR")!;

        ClaudeBuddySettings.AddClaudeCodeProfileDir(".claude-work");

        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();

        Assert.Contains(".claude-work", ClaudeBuddySettings.ClaudeCodeProfileDirs);
    }

    // Adding the same one twice is one entry. Every extra profile costs a
    // directory walk on each scan, and TranscriptReader searches them in order,
    // so a duplicate is wasted work on every poll.
    [Fact]
    public void AddingTheSameProfileTwiceKeepsOneEntry()
    {
        FreshSettings();

        ClaudeBuddySettings.AddClaudeCodeProfileDir(".claude-work");
        ClaudeBuddySettings.AddClaudeCodeProfileDir(".claude-work");

        Assert.Single(ClaudeBuddySettings.ClaudeCodeProfileDirs, d => d == ".claude-work");
    }

    [Fact]
    public void AProfileCanBeRemoved()
    {
        FreshSettings();

        ClaudeBuddySettings.AddClaudeCodeProfileDir(".claude-work");
        ClaudeBuddySettings.RemoveClaudeCodeProfileDir(".claude-work");

        Assert.DoesNotContain(".claude-work", ClaudeBuddySettings.ClaudeCodeProfileDirs);
    }

    [Fact]
    public void RemovingAProfileThatWasNeverAddedIsHarmless()
    {
        FreshSettings();

        ClaudeBuddySettings.RemoveClaudeCodeProfileDir(".never-added");

        Assert.Empty(ClaudeBuddySettings.ClaudeCodeProfileDirs);
    }

    // The list is handed out as a copy, so a caller cannot change the store
    // without going through Add/Remove — which is what would make a change
    // survive in memory and then vanish at the next restart.
    [Fact]
    public void TheProfileListIsACopy()
    {
        FreshSettings();

        ClaudeBuddySettings.AddClaudeCodeProfileDir(".claude-work");
        var first = ClaudeBuddySettings.ClaudeCodeProfileDirs;

        Assert.NotSame(first, ClaudeBuddySettings.ClaudeCodeProfileDirs);
    }

    // --- Codex homes: the same rules, a different list ---

    [Fact]
    public void ACodexHomeIsAddedDeduplicatedAndRemoved()
    {
        FreshSettings();

        ClaudeBuddySettings.AddCodexHome(".codex-work");
        ClaudeBuddySettings.AddCodexHome(".codex-work");

        Assert.Single(ClaudeBuddySettings.CodexHomes, h => h == ".codex-work");

        ClaudeBuddySettings.RemoveCodexHome(".codex-work");

        Assert.DoesNotContain(".codex-work", ClaudeBuddySettings.CodexHomes);
    }

    [Fact]
    public void CodexHomesAndClaudeProfilesAreSeparateLists()
    {
        FreshSettings();

        ClaudeBuddySettings.AddClaudeCodeProfileDir(".claude-work");
        ClaudeBuddySettings.AddCodexHome(".codex-work");

        Assert.DoesNotContain(".codex-work", ClaudeBuddySettings.ClaudeCodeProfileDirs);
        Assert.DoesNotContain(".claude-work", ClaudeBuddySettings.CodexHomes);
    }

    // --- per-profile settings ---

    [Fact]
    public void AnUnknownProfileGetsBlankSettingsRatherThanNull()
    {
        FreshSettings();

        var settings = ClaudeBuddySettings.For("Claude-Profile-9");

        Assert.NotNull(settings);
        Assert.True(string.IsNullOrEmpty(settings.Name));
    }

    [Fact]
    public void AProfilesNameAndColourRoundTripThroughDisk()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR")!;

        ClaudeBuddySettings.Update("Claude-Profile-1", p =>
        {
            p.Name = "Work";
            p.Color = "green";
            p.ShowSwatch = true;
            p.TintDockIcon = true;
            p.TintWindow = false;
        });

        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();

        var saved = ClaudeBuddySettings.For("Claude-Profile-1");

        Assert.Equal("Work", saved.Name);
        Assert.Equal("green", saved.Color);
        Assert.True(saved.ShowSwatch);
        Assert.True(saved.TintDockIcon);
        Assert.False(saved.TintWindow);
    }

    // For hands out a copy, per its own comment. Mutating what it returned must
    // not change the store — otherwise a caller that edited it without calling
    // Update would see its change until the next restart and then lose it, which
    // is the most confusing shape a settings bug can take.
    [Fact]
    public void ProfileSettingsAreHandedOutAsACopy()
    {
        FreshSettings();

        ClaudeBuddySettings.Update("Claude-Profile-1", p => p.Name = "Work");

        var borrowed = ClaudeBuddySettings.For("Claude-Profile-1");
        borrowed.Name = "Scribbled over";

        Assert.Equal("Work", ClaudeBuddySettings.For("Claude-Profile-1").Name);
    }

    // Forgotten when the profile itself is gone, because a leftover entry is
    // inherited by the next profile that reuses the folder name — not far-fetched
    // when new ones are numbered Claude-Profile-1, -2 and the numbering reuses a
    // gap.
    [Fact]
    public void RemovingAProfileForgetsItsNameAndColour()
    {
        FreshSettings();

        ClaudeBuddySettings.Update("Claude-Profile-2", p => { p.Name = "Old"; p.Color = "red"; });
        ClaudeBuddySettings.RemoveProfile("Claude-Profile-2");

        var reused = ClaudeBuddySettings.For("Claude-Profile-2");

        Assert.True(string.IsNullOrEmpty(reused.Name));
        Assert.True(string.IsNullOrEmpty(reused.Color));
    }

    [Fact]
    public void RemovingAProfileThatWasNeverStoredIsHarmless()
    {
        FreshSettings();

        ClaudeBuddySettings.RemoveProfile("Claude-Profile-never");

        Assert.True(string.IsNullOrEmpty(ClaudeBuddySettings.For("Claude-Profile-never").Name));
    }

    [Fact]
    public void TwoProfilesKeepTheirOwnSettings()
    {
        FreshSettings();

        ClaudeBuddySettings.Update("Claude-Profile-1", p => { p.Name = "Work"; p.Color = "green"; });
        ClaudeBuddySettings.Update("Claude-Profile-2", p => { p.Name = "Home"; p.Color = "blue"; });

        Assert.Equal("Work", ClaudeBuddySettings.For("Claude-Profile-1").Name);
        Assert.Equal("Home", ClaudeBuddySettings.For("Claude-Profile-2").Name);
        Assert.Equal("green", ClaudeBuddySettings.For("Claude-Profile-1").Color);
        Assert.Equal("blue", ClaudeBuddySettings.For("Claude-Profile-2").Color);
    }

    // Update on an existing profile changes only what the callback touches, so a
    // caller setting a colour does not blank the name beside it.
    [Fact]
    public void UpdateLeavesTheFieldsItDidNotTouch()
    {
        FreshSettings();

        ClaudeBuddySettings.Update("Claude-Profile-1", p => { p.Name = "Work"; p.Color = "green"; });
        ClaudeBuddySettings.Update("Claude-Profile-1", p => p.Color = "red");

        var saved = ClaudeBuddySettings.For("Claude-Profile-1");

        Assert.Equal("Work", saved.Name);
        Assert.Equal("red", saved.Color);
    }
}
