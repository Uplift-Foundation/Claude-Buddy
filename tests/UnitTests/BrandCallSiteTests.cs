using System.IO;
using System.Reflection;
using Xunit;

namespace ClaudeBuddy.Tests;

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
public class BrandCallSiteTests
{
    // Temp folders. Each one is where a running copy looks for files an
    // earlier copy left, so a changed name orphans them silently.
    [Fact]
    public void TheChimeCacheIsTheShippedTempFolder()
    {
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "ClaudeBuddy-chimes"),
            AudioVolume.ChimeCacheDirectory);
    }

    [Fact]
    public void TheGrokRefreshScratchIsTheShippedTempFolder()
    {
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "claude-buddy-grok-refresh"),
            GrokUsageRefresher.ScratchDirectory);
    }

    // Private, so read by reflection: neither has a seam of its own, and a
    // rename that moved them would strand every pasted image or saved media
    // file a previous run wrote.
    [Theory]
    [InlineData(typeof(ChatAttachments), "claude_buddy_pasted_images")]
    [InlineData(typeof(OpenClawMedia), "claude_buddy_media")]
    public void PrivateTempFoldersAreTheShippedNames(Type owner, string folder)
    {
        var property = owner.GetProperty("Directory_", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMemberException(owner.Name, "Directory_");

        Assert.Equal(Path.Combine(Path.GetTempPath(), folder), (string)property.GetValue(null)!);
    }

    // The speech engine is a release asset: the URL names a file that exists on
    // GitHub, and the exe name is what the unzipped folder holds.
    [Fact]
    public void TheSpeechEngineUrlNamesTheShippedAsset()
    {
        Assert.Equal(
            "https://github.com/Uplift-Foundation/Claude-Buddy/releases/download/"
            + $"v{NeuralSpeech.EngineVersion}/ClaudeBuddySpeech-{NeuralSpeech.EngineVersion}-{NeuralSpeech.EngineRid}.zip",
            NeuralSpeech.EngineUrl);
    }

    [Fact]
    public void TheSpeechEngineExeIsTheShippedName()
    {
        Assert.Equal(
            OperatingSystem.IsWindows() ? "ClaudeBuddySpeech.exe" : "ClaudeBuddySpeech",
            NeuralSpeech.EngineExeName);
    }

    // The installed hook: the folder under ~/.claude and the script filename,
    // both written into the user's own CLI settings.
    [Fact]
    public void TheHookScriptIsTheShippedFilename()
    {
        Assert.Equal(
            OperatingSystem.IsWindows() ? "ClaudeBuddyHook.ps1" : "ClaudeBuddyHook.sh",
            NewChatHookState.HookScriptName);
    }

    [Fact]
    public void AnInstalledHookIsFoundUnderTheShippedFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "cb250-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "claude-buddy"));
            File.WriteAllText(Path.Combine(root, "claude-buddy", "ClaudeBuddyHook.sh"), "");

            Assert.True(NewChatHookState.IsInstalled(root, "ClaudeBuddyHook.sh"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("\"command\": \"C:\\\\Users\\\\x\\\\.claude\\\\claude-buddy\\\\ClaudeBuddyHook.ps1\"", true)]
    [InlineData("\"command\": \"...\\\\claudebuddyhook.PS1\"", true)]
    [InlineData("\"command\": \"...\\\\OrbweaverHook.ps1\"", false)]
    public void WslSettingsRecogniseTheShippedHookName(string text, bool expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(expected, WslIntegration.SettingsTextMentionsHook(text));
    }

    // Sentences a user reads, each with the name in a different position, and
    // two of them consts built by concatenating an interpolated const with a
    // plain one.
    [Fact]
    public void UserFacingSentencesAreTheShippedWording()
    {
        Assert.Equal(
            "Hello. This is how Claude Buddy will sound when it reads a reply aloud.",
            VoicePreview.SampleText);
        Assert.Equal(
            "Claude Buddy could not read what is running under this session, so it is not "
            + "offering to end it. Close this menu and open it again to retry.",
            SessionDependents.UnknownTip);
        Assert.Equal(
            "Not connected to Claude Buddy on the other machine right now. Check the link in "
            + "Settings, then try again.",
            RemoteControlChatSession.NotConnectedNote);
        Assert.Equal(
            "coming over the link from Claude Buddy on the other machine",
            RemoteControlChatSession.WaitHint);
        Assert.Equal(
            "Handed to job-hunter for its next turn. It arrives as a message from Claude Buddy, "
            + "not keystrokes, so built-in slash commands won't run.",
            LocalCliChatSession.DeliveryNote(new DeliveryReceipt(DeliveryResult.Accepted, null), "job-hunter"));
    }
}
