using System.IO;
using System.Reflection;
using Xunit;

namespace Orbweaver.Tests;

// CB-250 QA: BrandTests pins the constants and a handful of derived names.
// These pin the rest of the call sites that build a path, an asset name or a
// sentence out of them, as the full string that shipped before the constants
// existed. Each value was a literal on develop at 2f5c99ba; a test here going
// red means a call site now produces something a user, a disk or a release
// asset will see differently.
//
// Spelled out rather than rebuilt from Brand on purpose: an expectation
// written as $"{Brand.Slug}-..." agrees with any value Brand ever holds, which
// is the one thing these tests exist to not do.
//
// CB-255 moved the expectations to the Orbweaver names deliberately, as
// phase 2 of the rename; each one that names something already on a user's
// disk has a migration elsewhere in that change. The speech engine's two stay
// on the old stem, since that is a binary name and moves in phase 3.
public class BrandCallSiteTests
{
    // Temp folders. Each one is where a running copy looks for files an
    // earlier copy left, so a changed name orphans them silently.
    [Fact]
    public void TheChimeCacheIsTheShippedTempFolder()
    {
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "Orbweaver-chimes"),
            AudioVolume.ChimeCacheDirectory);
    }

    [Fact]
    public void TheGrokRefreshScratchIsTheShippedTempFolder()
    {
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "orbweaver-grok-refresh"),
            GrokUsageRefresher.ScratchDirectory);
    }

    // Private, so read by reflection: neither has a seam of its own, and a
    // rename that moved them would strand every pasted image or saved media
    // file a previous run wrote.
    [Theory]
    [InlineData(typeof(ChatAttachments), "orbweaver_pasted_images")]
    [InlineData(typeof(OpenClawMedia), "orbweaver_media")]
    public void PrivateTempFoldersAreTheShippedNames(Type owner, string folder)
    {
        var property = owner.GetProperty("Directory_", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMemberException(owner.Name, "Directory_");

        Assert.Equal(Path.Combine(Path.GetTempPath(), folder), (string)property.GetValue(null)!);
    }

    // The speech engine is a release asset: the URL names a file that exists on
    // GitHub, and the exe name is what the unzipped folder holds. Spelled out
    // as the full string rather than built from Brand, so a change to the stem
    // shows up here as the URL a user's machine will actually fetch.
    [Fact]
    public void TheSpeechEngineUrlNamesTheShippedAsset()
    {
        Assert.Equal(
            "https://github.com/Uplift-Foundation/Claude-Buddy/releases/download/"
            + $"v{NeuralSpeech.EngineVersion}/OrbweaverSpeech-{NeuralSpeech.EngineVersion}-{NeuralSpeech.EngineRid}.zip",
            NeuralSpeech.EngineUrl);
    }

    [Fact]
    public void TheSpeechEngineExeIsTheShippedName()
    {
        Assert.Equal(
            OperatingSystem.IsWindows() ? "OrbweaverSpeech.exe" : "OrbweaverSpeech",
            NeuralSpeech.EngineExeName);
    }

    // What every engine downloaded before phase 3 is called, which the
    // fallback still has to recognise on disk.
    [Fact]
    public void TheLegacySpeechEngineExeIsTheNameOldReleasesShipped()
    {
        Assert.Equal(
            OperatingSystem.IsWindows() ? "ClaudeBuddySpeech.exe" : "ClaudeBuddySpeech",
            NeuralSpeech.LegacyEngineExeName);
    }

    // The installed hook: the folder under ~/.claude and the script filename,
    // both written into the user's own CLI settings.
    [Fact]
    public void TheHookScriptIsTheShippedFilename()
    {
        Assert.Equal(
            OperatingSystem.IsWindows() ? "OrbweaverHook.ps1" : "OrbweaverHook.sh",
            NewChatHookState.HookScriptName);
    }

    // Both copies, spelled out per CLI on macOS: the new folder and script
    // first, the legacy pair second (CB-255 §1). Windows Claude Code has its
    // own pair under %LOCALAPPDATA% and is pinned in NewChatHookStateTests.
    [Theory]
    [InlineData("ClaudeCode", ".claude")]
    [InlineData("Codex", ".codex")]
    [InlineData("Grok", ".grok")]
    public void AnInstalledHookIsFoundUnderTheShippedFolder(string cliName, string home)
    {
        var root = Path.Combine(Path.GetTempPath(), "cb250-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(
            new[]
            {
                Path.Combine(root, home, "orbweaver", "OrbweaverHook.sh"),
                Path.Combine(root, home, "claude-buddy", "ClaudeBuddyHook.sh")
            },
            NewChatHookState.HookCopyCandidates(
                Enum.Parse<NewChatCli>(cliName), onWindows: false, root, "unused", _ => null));
    }

    // A distro is wired if its settings name either script: the new one, or
    // the legacy one an un-upgraded install wrote (CB-255 §1). Pure, so asked
    // on both legs rather than only on Windows.
    [Theory]
    [InlineData("\"command\": \"C:\\\\Users\\\\x\\\\.claude\\\\claude-buddy\\\\ClaudeBuddyHook.ps1\"", true)]
    [InlineData("\"command\": \"...\\\\claudebuddyhook.PS1\"", true)]
    [InlineData("\"command\": \"...\\\\OrbweaverHook.ps1\"", true)]
    [InlineData("\"command\": \"...\\\\orbweaverhook.PS1\"", true)]
    [InlineData("\"command\": \"...\\\\OrbweaverHook.sh\"", false)]
    public void WslSettingsRecogniseTheShippedHookName(string text, bool expected)
    {
        Assert.Equal(expected, WslIntegration.SettingsTextMentionsHook(text));
    }

    // Sentences a user reads, each with the name in a different position, and
    // two of them consts built by concatenating an interpolated const with a
    // plain one.
    [Fact]
    public void UserFacingSentencesAreTheShippedWording()
    {
        Assert.Equal(
            "Hello. This is how Orbweaver will sound when it reads a reply aloud.",
            VoicePreview.SampleText);
        Assert.Equal(
            "Orbweaver could not read what is running under this session, so it is not "
            + "offering to end it. Close this menu and open it again to retry.",
            SessionDependents.UnknownTip);
        Assert.Equal(
            "Not connected to Orbweaver on the other machine right now. Check the link in "
            + "Settings, then try again.",
            RemoteControlChatSession.NotConnectedNote);
        Assert.Equal(
            "coming over the link from Orbweaver on the other machine",
            RemoteControlChatSession.WaitHint);
        Assert.Equal(
            "Handed to job-hunter for its next turn. It arrives as a message from Orbweaver, "
            + "not keystrokes, so built-in slash commands won't run.",
            LocalCliChatSession.DeliveryNote(new DeliveryReceipt(DeliveryResult.Accepted, null), "job-hunter"));
    }
}
