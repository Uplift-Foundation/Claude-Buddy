using Xunit;

namespace ClaudeBuddy.Tests;

// CB-250: Brand holds every name the app goes by, and each one is pinned here
// to the string that has shipped. That is the point of the class rather than a
// side effect of it — a value changing is a folder moving on a user's disk, a
// hook path going stale in their Claude Code settings, or a second copy of the
// app running past the single-instance check, and every one of those needs a
// migration. A rename that comes with its migrations updates these
// expectations deliberately; one that doesn't fails here first.
public class BrandTests
{
    [Theory]
    [InlineData(nameof(Brand.DisplayName), Brand.DisplayName, "Claude Buddy")]
    [InlineData(nameof(Brand.ShortName), Brand.ShortName, "Buddy")]
    [InlineData(nameof(Brand.DataDirName), Brand.DataDirName, "ClaudeBuddy")]
    [InlineData(nameof(Brand.StatusFolderName), Brand.StatusFolderName, "claude_buddy")]
    [InlineData(nameof(Brand.Slug), Brand.Slug, "claude-buddy")]
    [InlineData(nameof(Brand.HookScriptPowerShell), Brand.HookScriptPowerShell, "ClaudeBuddyHook.ps1")]
    [InlineData(nameof(Brand.HookScriptShell), Brand.HookScriptShell, "ClaudeBuddyHook.sh")]
    [InlineData(nameof(Brand.SingleInstanceMutexName), Brand.SingleInstanceMutexName, "ClaudeBuddy_SingleInstance_Mutex")]
    [InlineData(nameof(Brand.SpeechEngineName), Brand.SpeechEngineName, "ClaudeBuddySpeech")]
    [InlineData(nameof(Brand.MacBundleId), Brand.MacBundleId, "io.github.wtvamp.claudebuddy")]
    [InlineData(nameof(Brand.HotkeyWindowClass), Brand.HotkeyWindowClass, "ClaudeBuddyGlobalHotkeyWindow")]
    [InlineData(nameof(Brand.AssemblyName), Brand.AssemblyName, "ClaudeBuddy")]
    public void EachNameIsTheShippedString(string member, string actual, string shipped)
    {
        Assert.True(actual == shipped, $"Brand.{member} is \"{actual}\", shipped as \"{shipped}\"");
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
        Assert.Equal("Exit Claude Buddy", OrbWindow.ExitMenuHeader);
        Assert.Equal("isn't a terminal Buddy can type into", TerminalTyping.CantTypePhrase);
        Assert.Equal("**(via Claude Buddy)** ", OpenClawSender.MirrorPrefix);
        Assert.Equal("Claude Buddy on mini", SessionMessenger.FromName("mini"));
        Assert.Equal(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":" +
            "{\"name\":\"claude-buddy\",\"title\":\"Claude Buddy\",\"version\":\"1\"}}}",
            CodexAppServerUsage.InitializeRequest);
        Assert.Equal("ClaudeBuddy_SingleInstance_Mutex", SingleInstance.MutexName);
        Assert.Equal("claude_buddy", StatusDirectory.FolderName);
    }
}
