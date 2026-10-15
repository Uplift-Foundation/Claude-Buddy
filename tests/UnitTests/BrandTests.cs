using Xunit;

namespace Orbweaver.Tests;

// CB-250: Brand holds every name the app goes by, and each one is pinned here
// to the string that has shipped. That is the point of the class rather than a
// side effect of it — a value changing is a folder moving on a user's disk, a
// hook path going stale in their Claude Code settings, or a second copy of the
// app running past the single-instance check, and every one of those needs a
// migration. A rename that comes with its migrations updates these
// expectations deliberately; one that doesn't fails here first.
public class BrandTests
{
    //
    // CB-255 (phase 2) is that deliberate update: the names a user's disk and
    // OS hold flip to Orbweaver, each with its migration. SpeechEngineName and
    // AssemblyName are binary names and flipped with their binaries in phase 3
    // (CB-256); MacBundleId never moves.
    [Theory]
    [InlineData(nameof(Brand.DisplayName), Brand.DisplayName, "Orbweaver")]
    [InlineData(nameof(Brand.ShortName), Brand.ShortName, "Orbweaver")]
    [InlineData(nameof(Brand.DataDirName), Brand.DataDirName, "Orbweaver")]
    [InlineData(nameof(Brand.StatusFolderName), Brand.StatusFolderName, "orbweaver")]
    [InlineData(nameof(Brand.Slug), Brand.Slug, "orbweaver")]
    [InlineData(nameof(Brand.HookScriptPowerShell), Brand.HookScriptPowerShell, "OrbweaverHook.ps1")]
    [InlineData(nameof(Brand.HookScriptShell), Brand.HookScriptShell, "OrbweaverHook.sh")]
    [InlineData(nameof(Brand.SingleInstanceMutexName), Brand.SingleInstanceMutexName, "Orbweaver_SingleInstance_Mutex")]
    [InlineData(nameof(Brand.SpeechEngineName), Brand.SpeechEngineName, "OrbweaverSpeech")]
    [InlineData(nameof(Brand.MacBundleId), Brand.MacBundleId, "io.github.wtvamp.claudebuddy")]
    [InlineData(nameof(Brand.HotkeyWindowClass), Brand.HotkeyWindowClass, "OrbweaverGlobalHotkeyWindow")]
    [InlineData(nameof(Brand.AssemblyName), Brand.AssemblyName, "Orbweaver")]
    public void EachNameIsTheShippedString(string member, string actual, string shipped)
    {
        Assert.True(actual == shipped, $"Brand.{member} is \"{actual}\", shipped as \"{shipped}\"");
    }

    // The names every migration recognises an old install by. Unlike the rows
    // above, these never get a deliberate update: they are what builds before
    // CB-255 wrote to users' disks, settings and mutex namespace, and a drift
    // here means the data-dir move, the legacy mutex claim, the legacy status
    // folder watch and the hook-entry strip all silently stop finding what
    // they exist to migrate.
    [Theory]
    [InlineData(nameof(Brand.Legacy.DisplayName), Brand.Legacy.DisplayName, "Claude Buddy")]
    [InlineData(nameof(Brand.Legacy.DataDirName), Brand.Legacy.DataDirName, "ClaudeBuddy")]
    [InlineData(nameof(Brand.Legacy.StatusFolderName), Brand.Legacy.StatusFolderName, "claude_buddy")]
    [InlineData(nameof(Brand.Legacy.Slug), Brand.Legacy.Slug, "claude-buddy")]
    [InlineData(nameof(Brand.Legacy.HookScriptPowerShell), Brand.Legacy.HookScriptPowerShell, "ClaudeBuddyHook.ps1")]
    [InlineData(nameof(Brand.Legacy.HookScriptShell), Brand.Legacy.HookScriptShell, "ClaudeBuddyHook.sh")]
    [InlineData(nameof(Brand.Legacy.SingleInstanceMutexName), Brand.Legacy.SingleInstanceMutexName, "ClaudeBuddy_SingleInstance_Mutex")]
    [InlineData(nameof(Brand.Legacy.Executable), Brand.Legacy.Executable, "ClaudeBuddy")]
    [InlineData(nameof(Brand.Legacy.SpeechEngineName), Brand.Legacy.SpeechEngineName, "ClaudeBuddySpeech")]
    public void EachLegacyNameIsTheStringOldBuildsShipped(string member, string actual, string shipped)
    {
        Assert.True(actual == shipped, $"Brand.Legacy.{member} is \"{actual}\", shipped as \"{shipped}\"");
    }

    // A Legacy member equal to its current twin would make a migration a
    // no-op that reads as working — "move ClaudeBuddy to ClaudeBuddy". Every
    // Legacy name is one that changed; that is the only reason it exists.
    [Theory]
    [InlineData(Brand.Legacy.DisplayName, Brand.DisplayName)]
    [InlineData(Brand.Legacy.DataDirName, Brand.DataDirName)]
    [InlineData(Brand.Legacy.StatusFolderName, Brand.StatusFolderName)]
    [InlineData(Brand.Legacy.Slug, Brand.Slug)]
    [InlineData(Brand.Legacy.HookScriptPowerShell, Brand.HookScriptPowerShell)]
    [InlineData(Brand.Legacy.HookScriptShell, Brand.HookScriptShell)]
    [InlineData(Brand.Legacy.SingleInstanceMutexName, Brand.SingleInstanceMutexName)]
    [InlineData(Brand.Legacy.SpeechEngineName, Brand.SpeechEngineName)]
    public void EachLegacyNameDiffersFromTheCurrentOne(string legacy, string current)
    {
        Assert.NotEqual(legacy, current);
    }

    // Every avares:// URI is built from Brand.AssemblyName, but the assembly is
    // named by <AssemblyName> in the csproj. If a rename moves one and not the
    // other, the tray icon and the settings window's styles resolve to nothing
    // — at runtime, with no build error. This is the build-time version.
    [Fact]
    public void AssemblyNameMatchesTheBuiltAssembly()
    {
        Assert.Equal(Brand.AssemblyName, typeof(Brand).Assembly.GetName().Name);
    }

    // The derived names the call sites build from Brand, spelled out once, so a
    // change to the stem shows up as the full strings a user would see change.
    [Fact]
    public void DerivedNamesAreTheShippedStrings()
    {
        Assert.Equal("Exit Orbweaver", OrbWindow.ExitMenuHeader);
        Assert.Equal("isn't a terminal Orbweaver can type into", TerminalTyping.CantTypePhrase);
        Assert.Equal("**(via Orbweaver)** ", OpenClawSender.MirrorPrefix);
        Assert.Equal("Orbweaver on mini", SessionMessenger.FromName("mini"));
        Assert.Equal(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":" +
            "{\"name\":\"orbweaver\",\"title\":\"Orbweaver\",\"version\":\"1\"}}}",
            CodexAppServerUsage.InitializeRequest);
        Assert.Equal("Orbweaver_SingleInstance_Mutex", SingleInstance.MutexName);
        Assert.Equal("ClaudeBuddy_SingleInstance_Mutex", SingleInstance.LegacyMutexName);
        Assert.Equal("orbweaver", StatusDirectory.FolderName);
    }
}
