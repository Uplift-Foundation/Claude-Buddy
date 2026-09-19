using System.Text.Json.Nodes;
using Xunit;

namespace ClaudeBuddy.Tests;

// The speakScope setting through a real file on disk.
//
// Unit tests cover what the mode *decides*; this covers the seam the decision
// is read through, which fails differently: the parser gets a field wrong, the
// round trip loses the field entirely. The unknown-key case is the one with
// history behind it — a downgrade once silently erased three speech settings
// from a real file, which is the whole reason _unknownKeys exists.
[Collection("Settings")]
public class SpeakScopeSettingTests
{
    private static string NewSettingsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-speakscope-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PointSettingsAt(string dir)
    {
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
    }

    // The default is load-bearing rather than incidental: everyone who has ever
    // pressed the speaker button has heard the whole reply, and an upgrade that
    // silently started reading them a summary instead would read as the feature
    // having broken rather than as a new setting having a default.
    [Fact]
    public void SpeakScope_DefaultsToTheFullResponse()
    {
        PointSettingsAt(NewSettingsDir());

        Assert.Equal(SpeakScope.Full, ClaudeBuddySettings.SpeakScope);
    }

    [Fact]
    public void SpeakScope_RoundTripsBothWaysThroughTheFile()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.SpeakScope = SpeakScope.Summary;

        var settingsPath = Path.Combine(dir, "settings.json");
        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.Equal("summary", root!["speakScope"]!.GetValue<string>());

        PointSettingsAt(dir);
        Assert.Equal(SpeakScope.Summary, ClaudeBuddySettings.SpeakScope);

        ClaudeBuddySettings.SpeakScope = SpeakScope.Full;
        PointSettingsAt(dir);
        Assert.Equal(SpeakScope.Full, ClaudeBuddySettings.SpeakScope);
    }

    // A value no build has ever written — a newer mode, or a hand edit. It
    // degrades to the behaviour nobody had to ask for rather than to silence,
    // which is the only safe direction for a setting that decides whether the
    // machine speaks at all.
    [Theory]
    [InlineData("\"vibe\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("42")]
    public void AnUnrecognisedValueReadsAsTheFullResponse(string json)
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"speakScope\": " + json + "}");

        PointSettingsAt(dir);

        Assert.Equal(SpeakScope.Full, ClaudeBuddySettings.SpeakScope);
    }

    // Case is not the user's problem when they have edited the file by hand.
    [Fact]
    public void TheStoredValueIsReadCaseInsensitively()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"speakScope\": \"Summary\"}");

        PointSettingsAt(dir);

        Assert.Equal(SpeakScope.Summary, ClaudeBuddySettings.SpeakScope);
    }

    // The failure this key is most likely to meet in the wild: an older build,
    // which has never heard of speakScope, launched once and saving for an
    // unrelated reason. _unknownKeys is what stops that erasing the choice.
    [Fact]
    public void SpeakScope_SurvivesASaveByABuildThatOnlyKnowsOtherKeys()
    {
        var dir = NewSettingsDir();

        // Written directly rather than through the setter, because the point is
        // a key the saving build does not have a model property for.
        File.WriteAllText(
            Path.Combine(dir, "settings.json"),
            "{\"speakScope\": \"summary\", \"aKeyFromTheFuture\": \"kept\"}");

        PointSettingsAt(dir);
        ClaudeBuddySettings.TwoLetterGlyphs = true;
        ClaudeBuddySettings.FlushPendingSave();

        PointSettingsAt(dir);
        Assert.Equal(SpeakScope.Summary, ClaudeBuddySettings.SpeakScope);

        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json"))) as JsonObject;
        Assert.Equal("kept", root!["aKeyFromTheFuture"]!.GetValue<string>());
    }

    // speakScope is in KnownKeys, so Save writes it once. Miss that list and it
    // round-trips through _unknownKeys as well as being written properly, which
    // JsonObject rejects as a duplicate — the failure is a throw on save, not a
    // wrong value, so it needs its own assertion.
    [Fact]
    public void SavingTwiceWithTheKeyPresentDoesNotThrowOnADuplicate()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.SpeakScope = SpeakScope.Summary;
        PointSettingsAt(dir);
        ClaudeBuddySettings.TwoLetterGlyphs = true;
        ClaudeBuddySettings.FlushPendingSave();

        PointSettingsAt(dir);
        Assert.Equal(SpeakScope.Summary, ClaudeBuddySettings.SpeakScope);
    }
}
