using System;
using System.IO;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// NeuralSpeech's view of what is on disk: which engine binary it would run, and
// the three flags that decide whether the feature is offered, downloaded or used.
//
// All of it is File.Exists over paths under ClaudeBuddySettings.Directory, which
// CLAUDE_BUDDY_SETTINGS_DIR redirects — so these are real files in a temp
// directory rather than a mocked filesystem. Nothing here starts the engine; the
// process launch and the 300MB download are excluded, and separate.
//
// Worth testing rather than counting, because Installed / Usable / NeedsUpdate
// are three near-identical booleans whose differences only show up in states a
// user reaches once — mid-download, and just after a version bump.
//
// In the Settings collection: Directory comes from the process-wide settings
// model, and Available reads NeuralVoiceEnabled from it.
[Collection("Settings")]
public class NeuralSpeechLayoutTests : IDisposable
{
    private readonly string _root;

    public NeuralSpeechLayoutTests()
    {
        _root = NeuralSpeech.Root;
        Wipe();
    }

    public void Dispose() => Wipe();

    private void Wipe()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void PlaceEngine(string version)
    {
        var dir = Path.Combine(_root, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, NeuralSpeech.EngineExeName), "not really an engine");
    }

    private void PlaceModel()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(NeuralSpeech.ModelPath, "not really a model");
    }

    // ---- what the paths are ---------------------------------------------

    // The user's own voices live deliberately OUTSIDE Root, because an engine
    // upgrade deletes and replaces the whole versioned directory. A voice file
    // dropped in beside the bundled ones would vanish at the next release, and
    // this is the assertion that says so.
    [Fact]
    public void UserVoicesLiveOutsideTheDirectoryAnUpgradeDeletes()
    {
        Assert.False(
            NeuralSpeech.UserVoicesDirectory.StartsWith(_root + Path.DirectorySeparatorChar,
                StringComparison.Ordinal),
            $"user voices at {NeuralSpeech.UserVoicesDirectory} are inside {_root}, "
            + "which an engine upgrade deletes wholesale");
    }

    [Fact]
    public void TheEngineBinaryIsNamedForThePlatform()
    {
        var expected = OperatingSystem.IsWindows() ? "ClaudeBuddySpeech.exe" : "ClaudeBuddySpeech";
        Assert.Equal(expected, NeuralSpeech.EngineExeName);
    }

    // Derived from the platform rather than hardcoded, because asserting a
    // literal here would assert which machine the suite is running on.
    [Fact]
    public void TheEngineRidNamesThisBuildsPlatformAndArchitecture()
    {
        var rid = NeuralSpeech.EngineRid;

        if (OperatingSystem.IsMacOS())
            Assert.True(rid is "osx-arm64" or "osx-x64", rid);
        else
            Assert.Equal("win-x64", rid);
    }

    // The download URL has to name the same version the path does, or a build
    // fetches an engine it will then not look for.
    [Fact]
    public void TheDownloadUrlNamesTheVersionTheEnginePathExpects()
    {
        Assert.Contains($"v{NeuralSpeech.EngineVersion}/", NeuralSpeech.EngineUrl);
        Assert.Contains(NeuralSpeech.EngineRid, NeuralSpeech.EngineUrl);
        Assert.Contains(NeuralSpeech.EngineVersion, NeuralSpeech.EnginePath);
    }

    // ---- finding a fallback engine --------------------------------------

    [Fact]
    public void NoFallbackWhenTheEngineDirectoryDoesNotExistAtAll()
    {
        Assert.Null(NeuralSpeech.NewestOtherEngine());
    }

    [Fact]
    public void NoFallbackWhenAVersionDirectoryHoldsNoBinary()
    {
        Directory.CreateDirectory(Path.Combine(_root, "0.1.0-beta"));

        Assert.Null(NeuralSpeech.NewestOtherEngine());
    }

    // The dormant bug the VersionOrder comparer exists to prevent, asserted end
    // to end rather than only on the comparer: an ordinal sort puts 0.10.0-beta
    // below 0.2.0-beta, so the first release to reach a double-digit minor
    // version would silently start running a year-old engine.
    [Fact]
    public void PicksTheNewestFallbackByVersionNotByString()
    {
        PlaceEngine("0.2.0-beta");
        PlaceEngine("0.10.0-beta");

        var chosen = NeuralSpeech.NewestOtherEngine();

        Assert.NotNull(chosen);
        Assert.Contains("0.10.0-beta", chosen!);
    }

    // A directory name that is not a version at all sorts as 0.0 rather than
    // throwing, so it loses to anything real but is still usable alone.
    [Fact]
    public void AnUnparseableDirectoryNameLosesToARealVersion()
    {
        PlaceEngine("not-a-version");
        PlaceEngine("0.1.0-beta");

        Assert.Contains("0.1.0-beta", NeuralSpeech.NewestOtherEngine()!);
    }

    [Fact]
    public void PrefersTheExactVersionOverAnyFallback()
    {
        PlaceEngine("99.0.0");
        PlaceEngine(NeuralSpeech.EngineVersion);

        Assert.Equal(NeuralSpeech.EnginePath, NeuralSpeech.UsableEnginePath);
    }

    [Fact]
    public void FallsBackWhenThisBuildsOwnEngineIsMissing()
    {
        PlaceEngine("0.1.0-beta");

        var usable = NeuralSpeech.UsableEnginePath;

        Assert.NotNull(usable);
        Assert.NotEqual(NeuralSpeech.EnginePath, usable);
    }

    // ---- the three flags ------------------------------------------------

    // A download interrupted between the engine and the model must not read as
    // ready: they are ~150MB and ~156MB and arrive separately.
    [Fact]
    public void AnEngineWithNoModelIsNeitherInstalledNorUsable()
    {
        PlaceEngine(NeuralSpeech.EngineVersion);

        Assert.False(NeuralSpeech.Installed);
        Assert.False(NeuralSpeech.Usable);
    }

    [Fact]
    public void AModelWithNoEngineIsNeitherInstalledNorUsable()
    {
        PlaceModel();

        Assert.False(NeuralSpeech.Installed);
        Assert.False(NeuralSpeech.Usable);
    }

    [Fact]
    public void BothHalvesPresentMeansInstalledAndUsable()
    {
        PlaceEngine(NeuralSpeech.EngineVersion);
        PlaceModel();

        Assert.True(NeuralSpeech.Installed);
        Assert.True(NeuralSpeech.Usable);
        Assert.False(NeuralSpeech.NeedsUpdate);
    }

    // The state just after a version bump, and the reason Installed and Usable
    // are two questions: an older engine can still speak while the right one is
    // fetched, so the user loses nothing while the download runs.
    [Fact]
    public void AnOlderEngineIsUsableAndAlsoNeedsUpdating()
    {
        PlaceEngine("0.1.0-beta");
        PlaceModel();

        Assert.False(NeuralSpeech.Installed);
        Assert.True(NeuralSpeech.Usable);
        Assert.True(NeuralSpeech.NeedsUpdate);
    }

    // NeedsUpdate must be false on a machine that has never enabled the feature,
    // or it would start a 300MB download nobody asked for.
    [Fact]
    public void NothingOnDiskDoesNotNeedUpdating()
    {
        Assert.False(NeuralSpeech.Installed);
        Assert.False(NeuralSpeech.Usable);
        Assert.False(NeuralSpeech.NeedsUpdate);
    }

    // Available is the routing question, and it is Usable AND the setting — an
    // installed engine with the setting off must not be spoken through.
    [Fact]
    public void AvailableTracksBothTheDiskAndTheSetting()
    {
        PlaceEngine(NeuralSpeech.EngineVersion);
        PlaceModel();

        var original = ClaudeBuddySettings.NeuralVoiceEnabled;
        try
        {
            ClaudeBuddySettings.NeuralVoiceEnabled = false;
            Assert.False(NeuralSpeech.Available);

            ClaudeBuddySettings.NeuralVoiceEnabled = true;
            Assert.True(NeuralSpeech.Available);
        }
        finally
        {
            ClaudeBuddySettings.NeuralVoiceEnabled = original;
        }
    }

    [Fact]
    public void TheDefaultVoiceIsAnAmericanFemaleKokoroVoice()
    {
        // af_ is the prefix the engine files under American English female; a
        // default with an unrecognised prefix would fall through to that list
        // anyway, but a zf_ one would be hidden under Mandarin.
        Assert.StartsWith("af_", NeuralSpeech.DefaultVoiceName);
    }

    // The catch in NewestOtherEngine: a directory that cannot be enumerated is
    // the same answer as no fallback, because speaking should degrade rather than
    // fail.
    //
    // Reachable on Unix by taking the mode bits off Root. There is no equivalent
    // that is worth writing on Windows — its ACL APIs are Windows-only and this
    // is a defensive catch, not a rule — so on Windows this asserts the same
    // outcome by the ordinary route and the catch itself is covered on macOS
    // only. Said out loud rather than left as a mystery uncovered line.
    [Fact]
    public void AnUnreadableEngineDirectoryMeansNoFallbackRatherThanAThrow()
    {
        PlaceEngine("0.1.0-beta");

        if (OperatingSystem.IsWindows())
        {
            Wipe();
            Assert.Null(NeuralSpeech.NewestOtherEngine());
            return;
        }

        File.SetUnixFileMode(_root, UnixFileMode.None);
        try
        {
            Assert.Null(NeuralSpeech.NewestOtherEngine());
        }
        finally
        {
            // Restored, or Dispose cannot delete it.
            File.SetUnixFileMode(_root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
