using System.Text.Json.Nodes;
using Xunit;

namespace ClaudeBuddy.Tests;

// The chat panel's text size through a real settings file.
//
// Covered here as well as in the unit suite because the two fail differently:
// ChatZoom can be perfectly correct about what 1.15 means while the key never
// reaches disk, is written under a name Load does not read, or is round-tripped
// through _unknownKeys as well as being written properly — which is the
// duplicate-key mistake KnownKeys exists to prevent, and which only a real
// Save/Load shows.
[Collection("Settings")]
public class ChatTextScaleSettingTests
{
    private static string NewSettingsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-chatscale-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PointSettingsAt(string dir)
    {
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
    }

    [Fact]
    public void ItDefaultsToTheShippedSizeAndIsWrittenUnderItsOwnKey()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        Assert.Equal(ChatZoom.Default, ClaudeBuddySettings.ChatTextScale, 3);

        ClaudeBuddySettings.ChatTextScale = 1.5;

        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json"))) as JsonObject;
        Assert.Equal(1.5, root!["chatTextScale"]!.GetValue<double>(), 3);
    }

    [Fact]
    public void ASizeSurvivesBeingReloadedFromDisk()
    {
        // The whole promise of the setting: the panel you open tomorrow is the
        // size you left it at today.
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.ChatTextScale = 1.75;

        PointSettingsAt(dir);
        Assert.Equal(1.75, ClaudeBuddySettings.ChatTextScale, 3);
    }

    [Theory]
    [InlineData(40.0)]
    [InlineData(0.01)]
    [InlineData(-2.0)]
    public void AHandEditedSizeOutsideTheLadderIsPinnedRatherThanObeyed(double written)
    {
        // settings.json is a plain file someone can open, and this is the one
        // setting whose broken value hides the settings window that would fix
        // it. Clamped on read, so even a file that already holds 40 opens a
        // readable panel.
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            $"{{ \"version\": 1, \"chatTextScale\": {written} }}");

        PointSettingsAt(dir);

        var scale = ClaudeBuddySettings.ChatTextScale;
        Assert.InRange(scale, ChatZoom.Min, ChatZoom.Max);
    }

    [Fact]
    public void TheKeyIsKnownSoItDoesNotAlsoRoundTripAsAnUnknownOne()
    {
        // A setting written by Save but missing from KnownKeys comes back
        // through _unknownKeys as well, and the JsonObject Save builds rejects
        // the duplicate — so the symptom is not a wrong size, it is settings
        // that stop saving at all. Writing an unrelated unknown key alongside
        // proves the round-trip still works with both in play.
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"version\": 1, \"chatTextScale\": 1.3, \"somethingFromANewerBuild\": \"keep me\" }");

        PointSettingsAt(dir);
        Assert.Equal(1.3, ClaudeBuddySettings.ChatTextScale, 3);

        ClaudeBuddySettings.ChatTextScale = 0.9;

        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json"))) as JsonObject;
        Assert.Equal(0.9, root!["chatTextScale"]!.GetValue<double>(), 3);
        Assert.Equal("keep me", root["somethingFromANewerBuild"]!.GetValue<string>());
    }
}
