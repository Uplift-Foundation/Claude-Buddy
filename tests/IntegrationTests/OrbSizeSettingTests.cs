using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

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
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private static JsonObject ReadBack(string dir) =>
        (JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json"))) as JsonObject)!;

    [Fact]
    public void ItDefaultsToTheShippedSizeAndIsWrittenUnderItsOwnKey()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        Assert.Equal(OrbSizing.Default, OrbweaverSettings.OrbSize);

        OrbweaverSettings.OrbSize = 1.5;

        Assert.Equal(1.5, ReadBack(dir)["orbSize"]!.GetValue<double>(), 3);
    }

    [Fact]
    public void ASizeSurvivesBeingReloadedFromDisk()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.OrbSize = 1.25;

        PointSettingsAt(dir);
        Assert.Equal(1.25, OrbweaverSettings.OrbSize);
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

        Assert.InRange(OrbweaverSettings.OrbSize, OrbSizing.Min, OrbSizing.Max);
        Assert.Equal(1.3, OrbweaverSettings.ChatTextScale, 3);
    }

    [Fact]
    public void BothKeysAreKnownSoNeitherAlsoRoundTripsAsAnUnknownOne()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"version\": 1, \"orbSize\": 1.5, \"orbSizes\": { \"a\": 2.0 }, \"somethingFromANewerBuild\": \"keep me\" }");

        PointSettingsAt(dir);
        Assert.Equal(1.5, OrbweaverSettings.OrbSize);
        Assert.Equal(2.0, OrbweaverSettings.OrbSizeFor("a"));

        OrbweaverSettings.OrbSize = 0.75;

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

        Assert.Null(OrbweaverSettings.OrbSizeFor("C:/Work|lead"));

        OrbweaverSettings.SetOrbSize("C:/Work|lead", 1.5);
        OrbweaverSettings.SetOrbSize("other", 99);   // clamped on the way in

        PointSettingsAt(dir);
        Assert.Equal(1.5, OrbweaverSettings.OrbSizeFor("c:/work|LEAD"));
        Assert.Equal(OrbSizing.Max, OrbweaverSettings.OrbSizeFor("other"));

        OrbweaverSettings.SetOrbSize("C:/Work|lead", null);

        PointSettingsAt(dir);
        Assert.Null(OrbweaverSettings.OrbSizeFor("C:/Work|lead"));
        Assert.False(ReadBack(dir)["orbSizes"]!.AsObject().ContainsKey("C:/Work|lead"));
    }

    [Fact]
    public void ABlankKeyIsNeverWrittenOrRead()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.SetOrbSize("", 1.5);

        Assert.Null(OrbweaverSettings.OrbSizeFor(""));
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

        Assert.Equal(1.25, OrbweaverSettings.OrbSizeFor("good"));
        Assert.Null(OrbweaverSettings.OrbSizeFor("bad"));
        Assert.Null(OrbweaverSettings.OrbSizeFor("worse"));
    }

    [Fact]
    public void AFileFromAnOlderBuildWithNeitherKeyLoadsAsTheDefaults()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"version\": 1, \"chatTextScale\": 1.3 }");

        PointSettingsAt(dir);

        Assert.Equal(OrbSizing.Default, OrbweaverSettings.OrbSize);
        Assert.Null(OrbweaverSettings.OrbSizeFor("anything"));
    }

    // --- CB-198: the size a spot was saved at ---------------------------------

    [Fact]
    public void ASavedSpotKeepsTheSizeItWasSavedAt()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.SetOrbPosition("spot", 300, 200, 2.0);

        var written = ReadBack(dir)["orbPositions"]!["spot"]!;
        Assert.Equal(2.0, written["size"]!.GetValue<double>(), 3);

        PointSettingsAt(dir);
        Assert.Equal(new OrbweaverSettings.OrbPlacement(300, 200, 2.0), OrbweaverSettings.OrbPositionFor("spot"));
    }

    [Fact]
    public void ASpotFromAnOlderBuildHasNoSizeAndIsWrittenBackWithoutOne()
    {
        // The legacy shape, exactly as a pre-CB-198 build wrote it.
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"version\": 1, \"orbPositions\": { \"old\": { \"x\": 10, \"y\": 20 } } }");

        PointSettingsAt(dir);
        Assert.Equal(new OrbweaverSettings.OrbPlacement(10, 20, null), OrbweaverSettings.OrbPositionFor("old"));

        // Some other write makes Save run; the old spot must round-trip as it was.
        OrbweaverSettings.OrbSize = 1.5;
        var old = ReadBack(dir)["orbPositions"]!["old"]!.AsObject();
        Assert.False(old.ContainsKey("size"));
        Assert.Equal(10, old["x"]!.GetValue<int>());
    }

    [Fact]
    public void ABadSavedSizeCostsTheSizeNotTheSpot()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"version\": 1, \"orbPositions\": { \"s\": { \"x\": 10, \"y\": 20, \"size\": \"huge\" } } }");

        PointSettingsAt(dir);

        Assert.Equal(new OrbweaverSettings.OrbPlacement(10, 20, null), OrbweaverSettings.OrbPositionFor("s"));
    }

    [Fact]
    public void ResavingTheSameCornerAtANewSizeIsAChange()
    {
        // SetOrbPosition skips a write that changes nothing; a size change at
        // the same corner is not nothing.
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.SetOrbPosition("spot", 5, 5, 1.0);
        OrbweaverSettings.SetOrbPosition("spot", 5, 5, 1.0);
        OrbweaverSettings.SetOrbPosition("spot", 5, 5, 2.0);

        PointSettingsAt(dir);
        Assert.Equal(2.0, OrbweaverSettings.OrbPositionFor("spot")!.Size);
    }
}
