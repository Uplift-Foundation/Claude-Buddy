using System.Text.Json.Nodes;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-167's four keys through a real settings.json on disk.
//
// Unit tests cover what TurnSoundPolicy *decides*; this covers the seam the
// decision's inputs are actually read through, which fails differently — the
// parser gets a field wrong, or the round trip loses it entirely. Modelled
// on SpeakScopeSettingTests, including the unknown-key case: a downgrade
// once silently erased three speech settings from a real file, which is
// exactly what _unknownKeys exists to stop happening again to these four.
[Collection("Settings")]
public class TurnSoundSettingsTests
{
    private static string NewSettingsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-turnsounds-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PointSettingsAt(string dir)
    {
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
    }

    // --- turnSoundsEnabled ---

    [Fact]
    public void TurnSoundsEnabled_DefaultsToOn()
    {
        PointSettingsAt(NewSettingsDir());

        Assert.True(ClaudeBuddySettings.TurnSoundsEnabled);
    }

    [Fact]
    public void TurnSoundsEnabled_RoundTripsBothWaysThroughTheFile()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.TurnSoundsEnabled = false;

        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json"))) as JsonObject;
        Assert.False(root!["turnSoundsEnabled"]!.GetValue<bool>());

        PointSettingsAt(dir);
        Assert.False(ClaudeBuddySettings.TurnSoundsEnabled);

        ClaudeBuddySettings.TurnSoundsEnabled = true;
        PointSettingsAt(dir);
        Assert.True(ClaudeBuddySettings.TurnSoundsEnabled);
    }

    // QA (CB-167): root["turnSoundsEnabled"]?.GetValue<bool>() reaching a
    // hand-edited non-bool value threw straight into Load's one catch that
    // replaces the *entire* model with defaults — a garbage value in this
    // one new key was costing every other setting in the file, speakScope
    // and turnFinishedSound included, the exact hole Text() and Number()
    // already exist to close for every other type. Two shapes of garbage: a
    // JSON string, and a JSON number, neither of which TryGetValue<bool>
    // may throw on.
    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    public void AGarbageTurnSoundsEnabledCostsOnlyItselfNotEveryOtherSetting(string garbage)
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"speakScope\": \"summary\", \"turnFinishedSound\": \"Hero\", \"turnSoundsEnabled\": "
                + garbage + " }");

        PointSettingsAt(dir);

        Assert.Equal(SpeakScope.Summary, ClaudeBuddySettings.SpeakScope);
        Assert.Equal("Hero", ClaudeBuddySettings.TurnFinishedSound);
        Assert.True(ClaudeBuddySettings.TurnSoundsEnabled);
    }

    // --- turnFinishedSound / needsAttentionSound ---

    [Fact]
    public void TurnFinishedAndNeedsAttentionSound_DefaultToNull()
    {
        PointSettingsAt(NewSettingsDir());

        Assert.Null(ClaudeBuddySettings.TurnFinishedSound);
        Assert.Null(ClaudeBuddySettings.NeedsAttentionSound);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("summary")]
    [InlineData("Glass")]
    [InlineData("/Users/user/Sounds/custom.mp3")]
    public void TurnFinishedSound_RoundTripsAnyRecognisedShapeOfValue(string value)
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.TurnFinishedSound = value;
        PointSettingsAt(dir);

        Assert.Equal(value, ClaudeBuddySettings.TurnFinishedSound);
    }

    [Fact]
    public void NeedsAttentionSound_RoundTripsThroughTheFile()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.NeedsAttentionSound = "Ping";
        PointSettingsAt(dir);

        Assert.Equal("Ping", ClaudeBuddySettings.NeedsAttentionSound);
    }

    // Setting back to null (the platform default) has to actually clear the
    // key on disk, not leave the last real value sitting there unread.
    [Fact]
    public void TurnFinishedSound_CanBeClearedBackToNull()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.TurnFinishedSound = "Glass";
        ClaudeBuddySettings.TurnFinishedSound = null;
        PointSettingsAt(dir);

        Assert.Null(ClaudeBuddySettings.TurnFinishedSound);
    }

    // --- orbTurnSounds ---

    [Fact]
    public void OrbTurnSoundFor_IsNullForAKeyNeverOverridden()
    {
        PointSettingsAt(NewSettingsDir());

        Assert.Null(ClaudeBuddySettings.OrbTurnSoundFor("never-touched"));
    }

    // QA (CB-167): SoundKeyFor now genuinely produces "" for a local
    // session with no cwd (see SessionScanRulesTests), which makes this an
    // arm a real scan can reach rather than a purely defensive one.
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void OrbTurnSoundFor_IsNullForAnEmptyKey(string? key)
    {
        PointSettingsAt(NewSettingsDir());

        Assert.Null(ClaudeBuddySettings.OrbTurnSoundFor(key!));
    }

    [Fact]
    public void SetOrbTurnSound_RoundTripsBothFieldsThroughTheFile()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.SetOrbTurnSound("some/project\nbuild", finished: "off", attention: "Ping");

        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json"))) as JsonObject;
        var entry = root!["orbTurnSounds"]!["some/project\nbuild"] as JsonObject;
        Assert.Equal("off", entry!["finished"]!.GetValue<string>());
        Assert.Equal("Ping", entry["attention"]!.GetValue<string>());

        PointSettingsAt(dir);
        var over = ClaudeBuddySettings.OrbTurnSoundFor("some/project\nbuild");
        Assert.NotNull(over);
        Assert.Equal("off", over!.Finished);
        Assert.Equal("Ping", over.Attention);
    }

    // Both fields null is not an override worth keeping — the entry is
    // removed rather than stored as an empty shell that would otherwise
    // shadow the global default forever for no reason.
    [Fact]
    public void SetOrbTurnSound_WithBothFieldsNullRemovesTheEntry()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.SetOrbTurnSound("key", finished: "off", attention: "Ping");
        ClaudeBuddySettings.SetOrbTurnSound("key", finished: null, attention: null);

        Assert.Null(ClaudeBuddySettings.OrbTurnSoundFor("key"));

        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json"))) as JsonObject;
        Assert.Null(root!["orbTurnSounds"]!["key"]);
    }

    [Fact]
    public void ClearOrbTurnSound_RemovesAnExistingOverride()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.SetOrbTurnSound("key", finished: "Glass", attention: null);
        ClaudeBuddySettings.ClearOrbTurnSound("key");

        Assert.Null(ClaudeBuddySettings.OrbTurnSoundFor("key"));
    }

    // A hand-edited entry with both sides absent is read the same way
    // SetOrbTurnSound(key, null, null) writes it — dropped rather than kept
    // as a pointless empty override.
    [Fact]
    public void AHandEditedEntryWithBothFieldsAbsentIsNotLoaded()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(
            Path.Combine(dir, "settings.json"),
            "{\"orbTurnSounds\": {\"key\": {}}}");

        PointSettingsAt(dir);

        Assert.Null(ClaudeBuddySettings.OrbTurnSoundFor("key"));
    }

    // Keys are looked up case-insensitively, matching ChatPanelSizes and
    // OrbPositions — the same Windows-path reasoning applies to a key that
    // is itself built from a Windows cwd.
    [Fact]
    public void OrbTurnSoundKeysAreCaseInsensitive()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.SetOrbTurnSound("MyKey", finished: "Glass", attention: null);

        Assert.NotNull(ClaudeBuddySettings.OrbTurnSoundFor("mykey"));
    }

    // --- surviving a save by a build that only knows other keys ---

    [Fact]
    public void TheFourKeys_SurviveASaveByABuildThatOnlyKnowsOtherKeys()
    {
        var dir = NewSettingsDir();

        File.WriteAllText(
            Path.Combine(dir, "settings.json"),
            "{\"turnSoundsEnabled\": false, \"turnFinishedSound\": \"summary\", "
                + "\"needsAttentionSound\": \"Ping\", "
                + "\"orbTurnSounds\": {\"key\": {\"finished\": \"off\", \"attention\": null}}, "
                + "\"aKeyFromTheFuture\": \"kept\"}");

        PointSettingsAt(dir);
        ClaudeBuddySettings.TwoLetterGlyphs = true; // an unrelated write
        ClaudeBuddySettings.FlushPendingSave();

        PointSettingsAt(dir);
        Assert.False(ClaudeBuddySettings.TurnSoundsEnabled);
        Assert.Equal("summary", ClaudeBuddySettings.TurnFinishedSound);
        Assert.Equal("Ping", ClaudeBuddySettings.NeedsAttentionSound);
        Assert.Equal("off", ClaudeBuddySettings.OrbTurnSoundFor("key")!.Finished);

        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json"))) as JsonObject;
        Assert.Equal("kept", root!["aKeyFromTheFuture"]!.GetValue<string>());
    }

    // The four keys are in KnownKeys, so Save writes each once. Miss one and
    // it round-trips through _unknownKeys as well as being written properly,
    // which JsonObject rejects as a duplicate key — a throw on save, not a
    // wrong value, so it needs its own assertion the way SpeakScope's does.
    [Fact]
    public void SavingTwiceWithAllFourKeysPresentDoesNotThrowOnADuplicate()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.TurnSoundsEnabled = false;
        ClaudeBuddySettings.TurnFinishedSound = "summary";
        ClaudeBuddySettings.NeedsAttentionSound = "Ping";
        ClaudeBuddySettings.SetOrbTurnSound("key", "off", null);

        PointSettingsAt(dir);
        ClaudeBuddySettings.TwoLetterGlyphs = true;
        ClaudeBuddySettings.FlushPendingSave();

        PointSettingsAt(dir);
        Assert.False(ClaudeBuddySettings.TurnSoundsEnabled);
        Assert.Equal("summary", ClaudeBuddySettings.TurnFinishedSound);
    }
}
