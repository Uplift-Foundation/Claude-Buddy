using Xunit;

namespace ClaudeBuddy.Tests
{
    // Whether a CLI's Buddy hook is installed, for CB-168's new-chat dialog.
    //
    // In IntegrationTests rather than UnitTests because the answer is decided
    // by what is actually on disk — the point is the layout each installer
    // produces (a copy of the hook script at
    // "<cli home>/claude-buddy/ClaudeBuddyHook.{sh,ps1}"), and a fake
    // filesystem would be testing a different function.
    public class NewChatHookStateTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "cb-newchathook-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        private string Touch(params string[] parts)
        {
            var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "#!/bin/sh\n");
            return path;
        }

        // --- BaseDirectoryFor: which home each CLI actually uses ----------

        [Fact]
        public void ClaudeCodeAlwaysUsesDotClaudeUnderTheHomeDirectory()
        {
            var home = Path.Combine(_root, "home");

            var dir = NewChatHookState.BaseDirectoryFor(
                NewChatCli.ClaudeCode, home, _ => "should not matter");

            Assert.Equal(Path.Combine(home, ".claude"), dir);
        }

        [Fact]
        public void CodexFallsBackToDotCodexUnderHomeWithNoOverride()
        {
            var home = Path.Combine(_root, "home");

            var dir = NewChatHookState.BaseDirectoryFor(NewChatCli.Codex, home, _ => null);

            Assert.Equal(Path.Combine(home, ".codex"), dir);
        }

        // CODEX_HOME can point a whole account elsewhere, and the real
        // installer wires whichever directory it names — this has to agree,
        // or the dialog would report the hook missing for an account whose
        // hook is actually installed.
        [Fact]
        public void CodexHomeOverridesTheDefaultCodexDirectory()
        {
            var codexHome = Path.Combine(_root, "elsewhere-codex");

            var dir = NewChatHookState.BaseDirectoryFor(
                NewChatCli.Codex, Path.Combine(_root, "home"),
                name => name == "CODEX_HOME" ? codexHome : null);

            Assert.Equal(codexHome, dir);
        }

        [Fact]
        public void GrokFallsBackToDotGrokUnderHomeWithNoOverride()
        {
            var home = Path.Combine(_root, "home");

            var dir = NewChatHookState.BaseDirectoryFor(NewChatCli.Grok, home, _ => null);

            Assert.Equal(Path.Combine(home, ".grok"), dir);
        }

        [Fact]
        public void GrokHomeOverridesTheDefaultGrokDirectory()
        {
            var grokHome = Path.Combine(_root, "elsewhere-grok");

            var dir = NewChatHookState.BaseDirectoryFor(
                NewChatCli.Grok, Path.Combine(_root, "home"),
                name => name == "GROK_HOME" ? grokHome : null);

            Assert.Equal(grokHome, dir);
        }

        // An empty override string is the same as no override — the pattern
        // ClaudeBinary/CodexBinary/GrokBinary.Locate already use for PATH.
        [Fact]
        public void AnEmptyOverrideIsTreatedAsNoOverride()
        {
            var home = Path.Combine(_root, "home");

            var dir = NewChatHookState.BaseDirectoryFor(NewChatCli.Codex, home, _ => "");

            Assert.Equal(Path.Combine(home, ".codex"), dir);
        }

        // --- IsInstalled: the file that actually decides it ---------------

        [Fact]
        public void TheHookCopyBeingPresentMeansInstalled()
        {
            var baseDir = Path.Combine(_root, "home", ".claude");
            Touch("home", ".claude", "claude-buddy", "ClaudeBuddyHook.sh");

            Assert.True(NewChatHookState.IsInstalled(baseDir, "ClaudeBuddyHook.sh"));
        }

        [Fact]
        public void NoHookCopyMeansNotInstalled()
        {
            var baseDir = Path.Combine(_root, "home", ".codex");
            Directory.CreateDirectory(baseDir);

            Assert.False(NewChatHookState.IsInstalled(baseDir, "ClaudeBuddyHook.sh"));
        }

        // A base directory that does not exist at all is just as much "not
        // installed" as one that exists but is empty — neither is an error.
        [Fact]
        public void AMissingBaseDirectoryMeansNotInstalled()
        {
            Assert.False(NewChatHookState.IsInstalled(
                Path.Combine(_root, "never-created"), "ClaudeBuddyHook.sh"));
        }

        // The Windows and Unix hook filenames are different files, so the
        // right one for the platform has to be asked for — checking the wrong
        // one would report a real Windows install as missing.
        [Fact]
        public void OnlyTheNamedScriptCounts()
        {
            var baseDir = Path.Combine(_root, "home", ".grok");
            Touch("home", ".grok", "claude-buddy", "ClaudeBuddyHook.ps1");

            Assert.False(NewChatHookState.IsInstalled(baseDir, "ClaudeBuddyHook.sh"));
            Assert.True(NewChatHookState.IsInstalled(baseDir, "ClaudeBuddyHook.ps1"));
        }

        [Fact]
        public void HookScriptNameMatchesThePlatform()
        {
            Assert.Equal(
                OperatingSystem.IsWindows() ? "ClaudeBuddyHook.ps1" : "ClaudeBuddyHook.sh",
                NewChatHookState.HookScriptName);
        }

        // --- CurrentlyInstalled: the two halves wired together -------------

        [Fact]
        public void CurrentlyInstalledAsksTheRealEnvironmentAndDoesNotThrow()
        {
            // Nothing is asserted about the answer — it depends on this
            // machine's real installs — only that composing BaseDirectoryFor
            // with IsInstalled through the real environment doesn't throw for
            // any of the three CLIs.
            foreach (var cli in NewChatAvailability.AllClis)
            {
                _ = NewChatHookState.CurrentlyInstalled(cli);
            }
        }
    }
}
