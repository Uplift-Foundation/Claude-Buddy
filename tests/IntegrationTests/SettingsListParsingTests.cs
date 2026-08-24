using Xunit;

namespace ClaudeBuddy.Tests;

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
        ClaudeBuddySettings.ReloadForTests();
    }

    // ---- speakCommandArgs / speakVoicesCommandArgs ----------------------

    [Fact]
    public void SpeakCommandArgumentsAreReadInOrder()
    {
        Stage("""
        { "speakCommand": "say", "speakCommandArgs": ["-v", "Daniel", "-r", "200"] }
        """);

        Assert.Equal(new[] { "-v", "Daniel", "-r", "200" }, ClaudeBuddySettings.SpeakCommandArgs);
    }

    [Fact]
    public void VoicesCommandAndItsArgumentsAreReadTogether()
    {
        Stage("""
        { "speakVoicesCommand": "say", "speakVoicesCommandArgs": ["-v", "?"] }
        """);

        Assert.Equal("say", ClaudeBuddySettings.SpeakVoicesCommand);
        Assert.Equal(new[] { "-v", "?" }, ClaudeBuddySettings.SpeakVoicesCommandArgs);
    }

    // An older file has neither key. It must load with empty lists rather than
    // throwing, which is the whole reason each block is guarded by an `is
    // JsonArray` pattern rather than indexed directly.
    [Fact]
    public void AFileWithNoArgumentKeysLoadsWithEmptyLists()
    {
        Stage("""{ "speakCommand": "say" }""");

        Assert.Empty(ClaudeBuddySettings.SpeakCommandArgs);
        Assert.Empty(ClaudeBuddySettings.SpeakVoicesCommandArgs);
    }

    // A key holding the wrong shape is the same as absent, not a failure: the
    // pattern match simply does not bind.
    [Fact]
    public void AnArgumentKeyOfTheWrongTypeIsIgnored()
    {
        Stage("""{ "speakCommandArgs": "not an array" }""");

        Assert.Empty(ClaudeBuddySettings.SpeakCommandArgs);
    }

    // Blank and null entries are dropped rather than passed to a process as
    // empty arguments, which some commands treat as a positional argument.
    [Fact]
    public void EmptyAndNullArgumentsAreDropped()
    {
        Stage("""{ "speakCommandArgs": ["-v", "", null, "Daniel"] }""");

        Assert.Equal(new[] { "-v", "Daniel" }, ClaudeBuddySettings.SpeakCommandArgs);
    }

    // The getter hands out a copy, so a caller mutating what it got back cannot
    // reach into the shared model — the same guarantee ProfileSettings gets from
    // For().
    [Fact]
    public void TheArgumentListHandedOutIsACopy()
    {
        Stage("""{ "speakCommandArgs": ["-v"] }""");

        ClaudeBuddySettings.SpeakCommandArgs.Add("injected");

        Assert.Equal(new[] { "-v" }, ClaudeBuddySettings.SpeakCommandArgs);
    }

    // ---- codexHomes -----------------------------------------------------

    [Fact]
    public void CodexHomesAreRead()
    {
        Stage("""{ "codexHomes": ["work", "personal"] }""");

        Assert.Contains("work", ClaudeBuddySettings.CodexHomes);
        Assert.Contains("personal", ClaudeBuddySettings.CodexHomes);
    }

    // { Length: > 0 } rather than a null check, so an empty string does not
    // become a directory name that matches everything.
    [Fact]
    public void BlankAndNullCodexHomesAreDropped()
    {
        Stage("""{ "codexHomes": ["work", "", null] }""");

        Assert.Equal(new[] { "work" }, ClaudeBuddySettings.CodexHomes);
    }

    [Fact]
    public void AFileWithNoCodexHomesLoadsEmpty()
    {
        Stage("{}");

        Assert.Empty(ClaudeBuddySettings.CodexHomes);
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

        Assert.Equal(8317, ClaudeBuddySettings.OpenClawPort);
        Assert.Equal("ab:cd:ef", ClaudeBuddySettings.OpenClawFingerprint);
    }

    // The trap the case above describes, made explicit: a camelCased key is not
    // recognised, so the port falls back to its default. If the on-disk format is
    // ever tidied up, this is the test that should start failing and force a
    // migration to be written.
    [Fact]
    public void ACamelCasedOpenClawKeyIsNotRecognised()
    {
        Stage("""{ "openClawPort": 8317 }""");

        Assert.Equal(ClaudeBuddySettings.DefaultOpenClawPort, ClaudeBuddySettings.OpenClawPort);
    }

    // The fingerprint is coalesced to "" rather than left null, because every
    // caller compares it against a string.
    [Fact]
    public void AMissingFingerprintReadsAsEmptyRatherThanNull()
    {
        Stage("{}");

        Assert.Equal("", ClaudeBuddySettings.OpenClawFingerprint);
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
        var log = Path.Combine(Path.GetTempPath(), "claude_buddy", "settings-errors.log");
        var before = File.Exists(log) ? new FileInfo(log).Length : 0;

        Stage("{ this is not json");

        // Defaults, not a throw.
        Assert.Empty(ClaudeBuddySettings.CodexHomes);
        Assert.Empty(ClaudeBuddySettings.SpeakCommandArgs);

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

        Assert.Empty(ClaudeBuddySettings.CodexHomes);
    }

    // ---- the setters nothing else exercises -------------------------------

    // Both write straight through to disk rather than deferring, so a change is
    // durable the moment the setter returns. Worth one case each: an unwritten
    // preference is indistinguishable from one that never took.
    [Fact]
    public void TheVoicesCommandRoundTripsThroughDisk()
    {
        Stage("{}");

        ClaudeBuddySettings.SpeakVoicesCommand = "say";

        ClaudeBuddySettings.ReloadForTests();
        Assert.Equal("say", ClaudeBuddySettings.SpeakVoicesCommand);
    }

    [Fact]
    public void TheGatewayPortRoundTripsThroughDisk()
    {
        Stage("{}");

        ClaudeBuddySettings.OpenClawPort = 9999;

        ClaudeBuddySettings.ReloadForTests();
        Assert.Equal(9999, ClaudeBuddySettings.OpenClawPort);
    }
}
