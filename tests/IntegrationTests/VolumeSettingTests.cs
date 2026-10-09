using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-200's two volume keys — speechVolume and alertVolume — through a real
// settings file.
//
// Here as well as in the unit suite for OrbSizeSettingTests' reason: AudioVolume
// can clamp perfectly while a key never reaches disk, is written under a name
// Load does not read, or round-trips through _unknownKeys as well as the model
// (the duplicate-key mistake that stops settings saving at all).
[Collection("Settings")]
public class VolumeSettingTests
{
    private static string NewSettingsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-volume-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PointSettingsAt(string dir)
    {
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private static JsonObject ReadBack(string dir) =>
        (JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json"))) as JsonObject)!;

    [Fact]
    public void BothDefaultToFullAndAreWrittenUnderTheirOwnKeys()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        Assert.Equal(AudioVolume.Default, OrbweaverSettings.SpeechVolume);
        Assert.Equal(AudioVolume.Default, OrbweaverSettings.AlertVolume);

        OrbweaverSettings.SpeechVolume = 0.4;
        OrbweaverSettings.AlertVolume = 0.7;

        var root = ReadBack(dir);
        Assert.Equal(0.4, root["speechVolume"]!.GetValue<double>(), 3);
        Assert.Equal(0.7, root["alertVolume"]!.GetValue<double>(), 3);
    }

    [Fact]
    public void BothSurviveARestartIndependently()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.SpeechVolume = 0.25;
        OrbweaverSettings.AlertVolume = 0.85;

        PointSettingsAt(dir);   // a fresh Load from disk, as a relaunch does
        Assert.Equal(0.25, OrbweaverSettings.SpeechVolume, 3);
        Assert.Equal(0.85, OrbweaverSettings.AlertVolume, 3);
    }

    [Theory]
    [InlineData("40", 1.0)]
    [InlineData("-2", 0.0)]
    [InlineData("\"loud\"", 1.0)]
    [InlineData("null", 1.0)]
    [InlineData("{}", 1.0)]
    public void AHandEditedLevelIsPinnedIntoRangeAndABadOneCostsOnlyItself(string written, double expected)
    {
        // "loud" is Number()'s case: GetValue<double>() would throw into Load's
        // one catch and reset the whole file, so the unrelated key beside it
        // proves nothing else was lost.
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            $"{{ \"version\": 1, \"speechVolume\": {written}, \"alertVolume\": {written}, \"chatTextScale\": 1.3 }}");

        PointSettingsAt(dir);

        Assert.Equal(expected, OrbweaverSettings.SpeechVolume);
        Assert.Equal(expected, OrbweaverSettings.AlertVolume);
        Assert.Equal(1.3, OrbweaverSettings.ChatTextScale, 3);
    }

    [Fact]
    public void AnOutOfRangeWriteIsClampedBeforeItReachesDisk()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.SpeechVolume = 3;
        OrbweaverSettings.AlertVolume = -1;

        var root = ReadBack(dir);
        Assert.Equal(1.0, root["speechVolume"]!.GetValue<double>(), 3);
        Assert.Equal(0.0, root["alertVolume"]!.GetValue<double>(), 3);
    }

    // The ticket's round-trip rule, both directions.
    //
    // This build reading a file from a newer one: the new keys are known, so
    // they are read into the model and written back once — not carried in
    // _unknownKeys as well, which would make Save throw on a duplicate — while
    // a key this build does not know is still kept.
    [Fact]
    public void BothKeysAreKnownSoNeitherAlsoRoundTripsAsAnUnknownOne()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"version\": 1, \"speechVolume\": 0.3, \"alertVolume\": 0.6, \"somethingFromANewerBuild\": \"keep me\" }");

        PointSettingsAt(dir);
        Assert.Equal(0.3, OrbweaverSettings.SpeechVolume, 3);
        Assert.Equal(0.6, OrbweaverSettings.AlertVolume, 3);

        OrbweaverSettings.SpeechVolume = 0.5;   // any write makes Save run

        var root = ReadBack(dir);
        Assert.Equal(0.5, root["speechVolume"]!.GetValue<double>(), 3);
        Assert.Equal(0.6, root["alertVolume"]!.GetValue<double>(), 3);
        Assert.Equal("keep me", root["somethingFromANewerBuild"]!.GetValue<string>());
    }

    // An older build reading a file this one wrote: it has never heard of
    // either key, so they reach it exactly as somethingFromANewerBuild reached
    // this one above — and _unknownKeys puts them back. Simulated by the one
    // thing an older build's view differs in, the key's name: a volume key
    // spelled the way no build has ever known survives a save untouched, which
    // is the mechanism an older build relies on for the real ones.
    [Fact]
    public void AnOlderBuildWouldCarryTheLevelsThroughUntouched()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"version\": 1, \"speechVolumeFromTheFuture\": 0.3, \"alertVolumeFromTheFuture\": { \"nested\": 0.6 } }");

        PointSettingsAt(dir);
        OrbweaverSettings.OrbSize = 1.25;   // an unrelated save

        var root = ReadBack(dir);
        Assert.Equal(0.3, root["speechVolumeFromTheFuture"]!.GetValue<double>(), 3);
        Assert.Equal(0.6, root["alertVolumeFromTheFuture"]!["nested"]!.GetValue<double>(), 3);
    }

    [Fact]
    public void AFileFromAnOlderBuildWithNeitherKeyLoadsAtFullVolume()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"version\": 1, \"chatTextScale\": 1.3 }");

        PointSettingsAt(dir);

        Assert.Equal(AudioVolume.Default, OrbweaverSettings.SpeechVolume);
        Assert.Equal(AudioVolume.Default, OrbweaverSettings.AlertVolume);
    }
}
