using Xunit;

namespace Orbweaver.Tests;

// The three list-valued keys in settings.json — speakCommandArgs,
// speakVoicesCommandArgs and codexHomes — read through a real file, plus what
// happens when the file itself cannot be read at all.
//
// These parse loops are the part of Load() that an older settings file exercises:
// every one of them is written to be absent-tolerant, so a file from a previous
// version has no arguments rather than failing to load. That tolerance is only
// worth anything if it is checked, and it is exactly what a downgrade breaks —
// see the comment on _unknownKeys for the time three settings were silently
// erased from a real file.
[Collection("Settings")]
public class SettingsListParsingTests
{
    private static string NewSettingsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-listparse-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Stage(string json)
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), json);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    // ---- speakCommandArgs / speakVoicesCommandArgs ----------------------

    [Fact]
    public void SpeakCommandArgumentsAreReadInOrder()
    {
        Stage("""
        { "speakCommand": "say", "speakCommandArgs": ["-v", "Daniel", "-r", "200"] }
        """);

        Assert.Equal(new[] { "-v", "Daniel", "-r", "200" }, OrbweaverSettings.SpeakCommandArgs);
    }

    // CB-200: a custom speak command launched from a real settings file gets
    // its own arguments in order and the saved Speech level in its
    // environment — the file-to-process seam, where the unit tests only see
    // the builder.
    [Fact]
    public void ACustomCommandIsStartedWithItsArgumentsAndTheSavedLevel()
    {
        Stage("""
        { "speakCommand": "/usr/local/bin/my-tts", "speakCommandArgs": ["--model", "f5"],
          "speakCommandVoice": "female_03", "speechVolume": 0.25 }
        """);

        var startInfo = TextToSpeech.CustomCommandStartInfo(OrbweaverSettings.SpeakCommand!, voice: null);

        Assert.Equal("/usr/local/bin/my-tts", startInfo.FileName);
        Assert.Equal(new[] { "--model", "f5" }, startInfo.ArgumentList);
        Assert.Equal("female_03", startInfo.Environment["CLAUDEBUDDY_VOICE"]);
        Assert.Equal("0.25", startInfo.Environment[SpeechEngineContract.VolumeEnvVar]);
    }

    [Fact]
    public void VoicesCommandAndItsArgumentsAreReadTogether()
    {
        Stage("""
        { "speakVoicesCommand": "say", "speakVoicesCommandArgs": ["-v", "?"] }
        """);

        Assert.Equal("say", OrbweaverSettings.SpeakVoicesCommand);
        Assert.Equal(new[] { "-v", "?" }, OrbweaverSettings.SpeakVoicesCommandArgs);
    }

    // An older file has neither key. It must load with empty lists rather than
    // throwing, which is the whole reason each block is guarded by an `is
    // JsonArray` pattern rather than indexed directly.
    [Fact]
    public void AFileWithNoArgumentKeysLoadsWithEmptyLists()
    {
        Stage("""{ "speakCommand": "say" }""");

        Assert.Empty(OrbweaverSettings.SpeakCommandArgs);
        Assert.Empty(OrbweaverSettings.SpeakVoicesCommandArgs);
    }

    // A key holding the wrong shape is the same as absent, not a failure: the
    // pattern match simply does not bind.
    [Fact]
    public void AnArgumentKeyOfTheWrongTypeIsIgnored()
    {
        Stage("""{ "speakCommandArgs": "not an array" }""");

        Assert.Empty(OrbweaverSettings.SpeakCommandArgs);
    }

    // Blank and null entries are dropped rather than passed to a process as
    // empty arguments, which some commands treat as a positional argument.
    [Fact]
    public void EmptyAndNullArgumentsAreDropped()
    {
        Stage("""{ "speakCommandArgs": ["-v", "", null, "Daniel"] }""");

        Assert.Equal(new[] { "-v", "Daniel" }, OrbweaverSettings.SpeakCommandArgs);
    }

    // The getter hands out a copy, so a caller mutating what it got back cannot
    // reach into the shared model — the same guarantee ProfileSettings gets from
    // For().
    [Fact]
    public void TheArgumentListHandedOutIsACopy()
    {
        Stage("""{ "speakCommandArgs": ["-v"] }""");

        OrbweaverSettings.SpeakCommandArgs.Add("injected");

        Assert.Equal(new[] { "-v" }, OrbweaverSettings.SpeakCommandArgs);
    }

    // ---- codexHomes -----------------------------------------------------

    [Fact]
    public void CodexHomesAreRead()
    {
        Stage("""{ "codexHomes": ["work", "personal"] }""");

        Assert.Contains("work", OrbweaverSettings.CodexHomes);
        Assert.Contains("personal", OrbweaverSettings.CodexHomes);
    }

    // { Length: > 0 } rather than a null check, so an empty string does not
    // become a directory name that matches everything.
    [Fact]
    public void BlankAndNullCodexHomesAreDropped()
    {
        Stage("""{ "codexHomes": ["work", "", null] }""");

        Assert.Equal(new[] { "work" }, OrbweaverSettings.CodexHomes);
    }

    [Fact]
    public void AFileWithNoCodexHomesLoadsEmpty()
    {
        Stage("{}");

        Assert.Empty(OrbweaverSettings.CodexHomes);
    }

    [Fact]
    public void GrokHomesAreRead()
    {
        Stage("""{ "grokHomes": ["work", "personal"] }""");

        Assert.Contains("work", OrbweaverSettings.GrokHomes);
        Assert.Contains("personal", OrbweaverSettings.GrokHomes);
    }

    [Fact]
    public void BlankAndNullGrokHomesAreDropped()
    {
        Stage("""{ "grokHomes": ["work", "", null] }""");

        Assert.Equal(new[] { "work" }, OrbweaverSettings.GrokHomes);
    }

    [Fact]
    public void AFileWithNoGrokHomesLoadsEmpty()
    {
        Stage("{}");

        Assert.Empty(OrbweaverSettings.GrokHomes);
    }

    // ---- OpenClaw scalars ------------------------------------------------

    // Note the key spelling: "openclawPort", not "openClawPort". Every OpenClaw
    // key on disk lowercases the c while the rest of the file is camelCase
    // ("speakCommandArgs", "codexHomes"). Asserted as it actually is rather than
    // as it reads — this is a format users already have on disk, so the
    // inconsistency is not fixable without silently dropping their settings, and
    // a test written from the property name instead of the file would pass
    // against a default and prove nothing.
    [Fact]
    public void TheOpenClawPortAndFingerprintAreRead()
    {
        Stage("""{ "openclawPort": 8317, "openclawFingerprint": "ab:cd:ef" }""");

        Assert.Equal(8317, OrbweaverSettings.OpenClawPort);
        Assert.Equal("ab:cd:ef", OrbweaverSettings.OpenClawFingerprint);
    }

    // The trap the case above describes, made explicit: a camelCased key is not
    // recognised, so the port falls back to its default. If the on-disk format is
    // ever tidied up, this is the test that should start failing and force a
    // migration to be written.
    [Fact]
    public void ACamelCasedOpenClawKeyIsNotRecognised()
    {
        Stage("""{ "openClawPort": 8317 }""");

        Assert.Equal(OrbweaverSettings.DefaultOpenClawPort, OrbweaverSettings.OpenClawPort);
    }

    // The fingerprint is coalesced to "" rather than left null, because every
    // caller compares it against a string.
    [Fact]
    public void AMissingFingerprintReadsAsEmptyRatherThanNull()
    {
        Stage("{}");

        Assert.Equal("", OrbweaverSettings.OpenClawFingerprint);
    }

    // ---- a file that cannot be parsed ------------------------------------

    // Load's catch, and the LogFailure inside it. A settings file that is not
    // JSON must leave the app running on defaults: the alternative is that one
    // bad character in a preferences file stops the app from starting, and the
    // failure is written to a log rather than swallowed because there is
    // otherwise no way to tell why every setting reverted.
    [Fact]
    public void AMalformedSettingsFileLoadsDefaultsAndLogsWhy()
    {
        var log = Path.Combine(StatusDirectory.Path(), "settings-errors.log");
        var before = File.Exists(log) ? new FileInfo(log).Length : 0;

        Stage("{ this is not json");

        // Defaults, not a throw.
        Assert.Empty(OrbweaverSettings.CodexHomes);
        Assert.Empty(OrbweaverSettings.SpeakCommandArgs);

        Assert.True(File.Exists(log), $"expected a failure log at {log}");
        Assert.True(new FileInfo(log).Length > before,
            "expected the load failure to be appended to the log");
        Assert.Contains("Load failed", File.ReadAllText(log));
    }

    // Valid JSON that is not an object at all — a bare array — takes the same
    // route. Worth its own case because it parses successfully and then fails
    // the cast, which is a different arm.
    [Fact]
    public void ASettingsFileHoldingAnArrayLoadsDefaults()
    {
        Stage("[1, 2, 3]");

        Assert.Empty(OrbweaverSettings.CodexHomes);
    }

    // ---- the setters nothing else exercises -------------------------------

    // Both write straight through to disk rather than deferring, so a change is
    // durable the moment the setter returns. Worth one case each: an unwritten
    // preference is indistinguishable from one that never took.
    [Fact]
    public void TheVoicesCommandRoundTripsThroughDisk()
    {
        Stage("{}");

        OrbweaverSettings.SpeakVoicesCommand = "say";

        OrbweaverSettings.ReloadForTests();
        Assert.Equal("say", OrbweaverSettings.SpeakVoicesCommand);
    }

    [Fact]
    public void TheGatewayPortRoundTripsThroughDisk()
    {
        Stage("{}");

        OrbweaverSettings.OpenClawPort = 9999;

        OrbweaverSettings.ReloadForTests();
        Assert.Equal(9999, OrbweaverSettings.OpenClawPort);
    }
}
