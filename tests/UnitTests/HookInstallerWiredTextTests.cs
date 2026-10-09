using Xunit;

namespace Orbweaver.Tests
{
    // What "this profile is wired" means to the Settings card (HookInstaller),
    // as text, with no file behind it (CB-256).
    //
    // The bug this pins: the check looked only for "ClaudeBuddyHook", so once
    // the installers wrote OrbweaverHook.* a freshly wired profile read as
    // un-wired. And the trap next to the fix: WslIntegration has a two-name
    // check already, but it counts only the .ps1 on purpose (a WSL distro wired
    // for bash is not wired for the Windows hook), while a native profile on a
    // Mac carries the .sh. The last case here holds the two rules apart.
    public class HookInstallerWiredTextTests
    {
        [Theory]
        [InlineData("""{"hooks":{"Stop":[{"command":"/Users/x/.claude/orbweaver/OrbweaverHook.sh"}]}}""")]
        [InlineData("""{"hooks":{"Stop":[{"command":"pwsh -File C:/x/OrbweaverHook.ps1"}]}}""")]
        [InlineData("""{"hooks":{"Stop":[{"command":"/Users/x/.claude/claude-buddy/ClaudeBuddyHook.sh"}]}}""")]
        [InlineData("""{"hooks":{"Stop":[{"command":"pwsh -File C:/x/ClaudeBuddyHook.ps1"}]}}""")]
        [InlineData("""{"hooks":{"Stop":[{"command":"pwsh -File C:/x/orbweaverhook.PS1"}]}}""")]
        public void EitherScriptNameOnEitherPlatformCountsAsWired(string text)
        {
            Assert.True(HookInstaller.SettingsTextMentionsHook(text));
        }

        [Theory]
        [InlineData("")]
        [InlineData("""{"hooks":{}}""")]
        [InlineData("""{"hooks":{"Stop":[{"command":"/x/orbweaver/other-hook.sh"}]}}""")]
        public void SettingsWithoutEitherScriptAreNotWired(string text)
        {
            Assert.False(HookInstaller.SettingsTextMentionsHook(text));
        }

        [Fact]
        public void AMacProfileIsWiredHereEvenThoughTheWslRuleWouldSayNo()
        {
            const string mac = """{"hooks":{"Stop":[{"command":"/x/OrbweaverHook.sh"}]}}""";

            Assert.True(HookInstaller.SettingsTextMentionsHook(mac));
            Assert.False(WslIntegration.SettingsTextMentionsHook(mac));
        }
    }
}
