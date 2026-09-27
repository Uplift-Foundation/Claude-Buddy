using System.Text.Json.Nodes;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-198's two size settings — the global orbSize and the per-orb orbSizes map
// — through a real settings file.
//
// Here as well as in the unit suite for ChatTextScaleSettingTests' reason: the
// rules in OrbSizing can be right while the key never reaches disk, is written
// under a name Load does not read, or round-trips through _unknownKeys as well
// as the model — the duplicate-key mistake that stops settings saving at all.
[Collection("Settings")]
public class OrbSizeSettingTests
{
    private static string NewSettingsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-orbsize-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PointSettingsAt(string dir)
    {
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
    }

    private static JsonObject ReadBack(string dir) =>
        (JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json"))) as JsonObject)!;

    [Fact]
    public void ItDefaultsToTheShippedSizeAndIsWrittenUnderItsOwnKey()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        Assert.Equal(OrbSizing.Default, ClaudeBuddySettings.OrbSize);

        ClaudeBuddySettings.OrbSize = 1.5;

        Assert.Equal(1.5, ReadBack(dir)["orbSize"]!.GetValue<double>(), 3);
    }

    [Fact]
    public void ASizeSurvivesBeingReloadedFromDisk()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.OrbSize = 1.25;

        PointSettingsAt(dir);
        Assert.Equal(1.25, ClaudeBuddySettings.OrbSize);
    }

    [Theory]
    [InlineData("40")]
    [InlineData("0.01")]
    [InlineData("-2")]
    [InlineData("\"big\"")]
    public void AHandEditedSizeIsPinnedIntoRangeAndABadOneCostsOnlyItself(string written)
    {
        // "big" is the case Number() exists for: GetValue<double>() on it would
        // throw into Load's one catch and replace the whole model, so the
        // unrelated profile name alongside proves nothing else was lost.
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            $"{{ \"version\": 1, \"orbSize\": {written}, \"chatTextScale\": 1.3 }}");

        PointSettingsAt(dir);

        Assert.InRange(ClaudeBuddySettings.OrbSize, OrbSizing.Min, OrbSizing.Max);
        Assert.Equal(1.3, ClaudeBuddySettings.ChatTextScale, 3);
    }

    [Fact]
    public void BothKeysAreKnownSoNeitherAlsoRoundTripsAsAnUnknownOne()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"version\": 1, \"orbSize\": 1.5, \"orbSizes\": { \"a\": 2.0 }, \"somethingFromANewerBuild\": \"keep me\" }");

        PointSettingsAt(dir);
        Assert.Equal(1.5, ClaudeBuddySettings.OrbSize);
        Assert.Equal(2.0, ClaudeBuddySettings.OrbSizeFor("a"));

        ClaudeBuddySettings.OrbSize = 0.75;

        var root = ReadBack(dir);
        Assert.Equal(0.75, root["orbSize"]!.GetValue<double>(), 3);
        Assert.Equal(2.0, root["orbSizes"]!["a"]!.GetValue<double>(), 3);
        Assert.Equal("keep me", root["somethingFromANewerBuild"]!.GetValue<string>());
    }

    [Fact]
    public void APerOrbSizeRoundTripsCaseInsensitivelyAndNullClearsIt()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        Assert.Null(ClaudeBuddySettings.OrbSizeFor("C:/Work|lead"));

        ClaudeBuddySettings.SetOrbSize("C:/Work|lead", 1.5);
        ClaudeBuddySettings.SetOrbSize("other", 99);   // clamped on the way in

        PointSettingsAt(dir);
        Assert.Equal(1.5, ClaudeBuddySettings.OrbSizeFor("c:/work|LEAD"));
        Assert.Equal(OrbSizing.Max, ClaudeBuddySettings.OrbSizeFor("other"));

        ClaudeBuddySettings.SetOrbSize("C:/Work|lead", null);

        PointSettingsAt(dir);
        Assert.Null(ClaudeBuddySettings.OrbSizeFor("C:/Work|lead"));
        Assert.False(ReadBack(dir)["orbSizes"]!.AsObject().ContainsKey("C:/Work|lead"));
    }

    [Fact]
    public void ABlankKeyIsNeverWrittenOrRead()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.SetOrbSize("", 1.5);

        Assert.Null(ClaudeBuddySettings.OrbSizeFor(""));
        Assert.False(File.Exists(Path.Combine(dir, "settings.json")) &&
                     ReadBack(dir)["orbSizes"] is JsonObject o && o.Count > 0);
    }

    [Fact]
    public void AGarbageEntryInTheMapCostsOnlyThatEntry()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"version\": 1, \"orbSizes\": { \"good\": 1.25, \"bad\": \"huge\", \"worse\": {} } }");

        PointSettingsAt(dir);

        Assert.Equal(1.25, ClaudeBuddySettings.OrbSizeFor("good"));
        Assert.Null(ClaudeBuddySettings.OrbSizeFor("bad"));
        Assert.Null(ClaudeBuddySettings.OrbSizeFor("worse"));
    }

    [Fact]
    public void AFileFromAnOlderBuildWithNeitherKeyLoadsAsTheDefaults()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"version\": 1, \"chatTextScale\": 1.3 }");

        PointSettingsAt(dir);

        Assert.Equal(OrbSizing.Default, ClaudeBuddySettings.OrbSize);
        Assert.Null(ClaudeBuddySettings.OrbSizeFor("anything"));
    }
}
