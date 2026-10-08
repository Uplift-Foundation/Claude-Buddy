using Xunit;

namespace ClaudeBuddy.Tests
{
    // Whether a CLI's hook is installed, for CB-168's new-chat dialog.
    //
    // In IntegrationTests rather than UnitTests because the answer is decided
    // by what is actually on disk — the point is the layout each installer
    // produces, and a fake filesystem would be testing a different function.
    // The candidate paths themselves are pure, and asked here for every CLI on
    // both platforms so neither CI leg only ever checks its own.
    //
    // Two copies count since CB-255 §1 — the Orbweaver one and the legacy
    // Claude Buddy one — and Windows Claude Code's copy lives under
    // %LOCALAPPDATA%, not ~/.claude. The second was a pre-existing bug: the
    // dialog warned "no orb until hooks are installed" on every wired Windows
    // machine. The cases marked CB-255 below are the ones that were wrong.
    public class NewChatHookStateTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "cb-newchathook-" + Guid.NewGuid().ToString("N"));

        private string Home => Path.Combine(_root, "home");
        private string Local => Path.Combine(_root, "local");

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        private static void Touch(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "#!/bin/sh\n");
        }

        private IReadOnlyList<string> Candidates(
            NewChatCli cli, bool onWindows, Func<string, string?>? env = null) =>
            NewChatHookState.HookCopyCandidates(cli, onWindows, Home, Local, env ?? (_ => null));

        // Theory rows name the CLI as a string: NewChatCli is internal, and a
        // public test method cannot take it as a parameter.
        private IReadOnlyList<string> Candidates(
            string cliName, bool onWindows, Func<string, string?>? env = null) =>
            Candidates(Enum.Parse<NewChatCli>(cliName), onWindows, env);

        // --- HookCopyCandidates: where each installer puts the copy -------

        // CB-255: the bug. install-windows-hooks.ps1 copies the script to
        // $env:LOCALAPPDATA\<data dir>, and nothing under ~/.claude.
        [Fact]
        public void WindowsClaudeCodeLooksUnderLocalAppDataNotDotClaude()
        {
            Assert.Equal(
                new[]
                {
                    Path.Combine(Local, "Orbweaver", "OrbweaverHook.ps1"),
                    Path.Combine(Local, "ClaudeBuddy", "ClaudeBuddyHook.ps1")
                },
                Candidates(NewChatCli.ClaudeCode, onWindows: true));
        }

        // Claude Code has no hook-install override: CLAUDE_CONFIG_DIR moves
        // the settings file, not the script copy, so it changes nothing here.
        [Fact]
        public void MacClaudeCodeLooksUnderDotClaudeAndIgnoresTheEnvironment()
        {
            Assert.Equal(
                new[]
                {
                    Path.Combine(Home, ".claude", "orbweaver", "OrbweaverHook.sh"),
                    Path.Combine(Home, ".claude", "claude-buddy", "ClaudeBuddyHook.sh")
                },
                Candidates(NewChatCli.ClaudeCode, onWindows: false, _ => Path.Combine(_root, "elsewhere")));
        }

        [Theory]
        [InlineData("Codex", ".codex", false, "OrbweaverHook.sh", "ClaudeBuddyHook.sh")]
        [InlineData("Codex", ".codex", true, "OrbweaverHook.ps1", "ClaudeBuddyHook.ps1")]
        [InlineData("Grok", ".grok", false, "OrbweaverHook.sh", "ClaudeBuddyHook.sh")]
        [InlineData("Grok", ".grok", true, "OrbweaverHook.ps1", "ClaudeBuddyHook.ps1")]
        public void CodexAndGrokLookUnderTheirOwnHomeOnBothPlatforms(
            string cliName, string home, bool onWindows, string script, string legacyScript)
        {
            Assert.Equal(
                new[]
                {
                    Path.Combine(Home, home, "orbweaver", script),
                    Path.Combine(Home, home, "claude-buddy", legacyScript)
                },
                Candidates(cliName, onWindows));
        }

        // CODEX_HOME and GROK_HOME can point a whole account elsewhere, and
        // the real installer wires whichever directory it names — this has to
        // agree, or the dialog would report the hook missing for an account
        // whose hook is actually installed. Each CLI reads only its own.
        [Theory]
        [InlineData("Codex", "CODEX_HOME", false)]
        [InlineData("Codex", "CODEX_HOME", true)]
        [InlineData("Grok", "GROK_HOME", false)]
        [InlineData("Grok", "GROK_HOME", true)]
        public void TheCliHomeOverrideMovesBothCandidates(string cliName, string variable, bool onWindows)
        {
            var elsewhere = Path.Combine(_root, "elsewhere");

            var found = Candidates(cliName, onWindows, name => name == variable ? elsewhere : null);

            Assert.All(found, path => Assert.StartsWith(elsewhere, path));
            Assert.Equal(Path.Combine(elsewhere, "orbweaver"), Path.GetDirectoryName(found[0]));
            Assert.Equal(Path.Combine(elsewhere, "claude-buddy"), Path.GetDirectoryName(found[1]));
        }

        // An empty override string is the same as no override — the installers'
        // own rule (`${CODEX_HOME:-…}`, `if ($env:CODEX_HOME)`).
        [Theory]
        [InlineData("Codex", ".codex")]
        [InlineData("Grok", ".grok")]
        public void AnEmptyOverrideIsTreatedAsNoOverride(string cliName, string home)
        {
            var found = Candidates(cliName, onWindows: false, _ => "");

            Assert.Equal(Path.Combine(Home, home, "orbweaver"), Path.GetDirectoryName(found[0]));
        }

        // The `_ => null` arm — unreachable through anything this file's own
        // callers pass (NewChatAvailability.AllClis only ever hands this the
        // three real values), but real code all the same, and a cast out of
        // the defined range is what reaches it. No candidates means not
        // installed rather than an exception.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnUndefinedCliHasNoCandidates(bool onWindows)
        {
            Assert.Empty(Candidates((NewChatCli)99, onWindows));
            Assert.False(NewChatHookState.AnyExists(Candidates((NewChatCli)99, onWindows)));
        }

        // --- AnyExists over real files: the copy that actually decides it --

        [Fact]
        public void TheNewCopyAloneMeansInstalled()
        {
            var found = Candidates(NewChatCli.Codex, onWindows: false);
            Touch(found[0]);

            Assert.True(NewChatHookState.AnyExists(found));
        }

        // CB-255: a machine whose installer has not re-wired since the upgrade
        // is still wired to the legacy script, and its hooks fire.
        [Fact]
        public void TheLegacyCopyAloneMeansInstalled()
        {
            var found = Candidates(NewChatCli.Grok, onWindows: false);
            Touch(found[1]);

            Assert.True(NewChatHookState.AnyExists(found));
        }

        // CB-255: the Windows box as it is on disk today — the legacy
        // Claude Code copy under %LOCALAPPDATA%\ClaudeBuddy and nothing under
        // ~/.claude — reads as installed. Before the fix it did not.
        [Fact]
        public void AWindowsClaudeCodeInstallUnderLocalAppDataMeansInstalled()
        {
            Touch(Path.Combine(Local, "ClaudeBuddy", "ClaudeBuddyHook.ps1"));

            Assert.True(NewChatHookState.AnyExists(Candidates(NewChatCli.ClaudeCode, onWindows: true)));
        }

        // The negative control for the one above: the same tree, asked as a
        // Mac would ask it, finds nothing — so the positive is the Windows
        // rule's doing and not a candidate that matches anything.
        [Fact]
        public void TheSameTreeAskedAsAMacIsNotInstalled()
        {
            Touch(Path.Combine(Local, "ClaudeBuddy", "ClaudeBuddyHook.ps1"));

            Assert.False(NewChatHookState.AnyExists(Candidates(NewChatCli.ClaudeCode, onWindows: false)));
        }

        [Fact]
        public void NoCopyAndNoDirectoryMeansNotInstalled()
        {
            Assert.False(NewChatHookState.AnyExists(Candidates(NewChatCli.Codex, onWindows: false)));
        }

        // The Windows and Unix hook filenames are different files, so the
        // right one for the platform has to be asked for — checking the wrong
        // one would report a real install as missing, or a foreign one as ours.
        [Fact]
        public void OnlyThePlatformsOwnScriptCounts()
        {
            Touch(Path.Combine(Home, ".grok", "claude-buddy", "ClaudeBuddyHook.ps1"));

            Assert.False(NewChatHookState.AnyExists(Candidates(NewChatCli.Grok, onWindows: false)));
            Assert.True(NewChatHookState.AnyExists(Candidates(NewChatCli.Grok, onWindows: true)));
        }

        // The folder without the script is not an install: an uninstall
        // leaves folders behind on purpose (install-macos-hooks.sh).
        [Fact]
        public void AnEmptyHookFolderIsNotInstalled()
        {
            Directory.CreateDirectory(Path.Combine(Home, ".codex", "orbweaver"));
            Directory.CreateDirectory(Path.Combine(Home, ".codex", "claude-buddy"));

            Assert.False(NewChatHookState.AnyExists(Candidates(NewChatCli.Codex, onWindows: false)));
        }

        // The seam the UI suite drives: `exists` is what is asked, and the
        // real filesystem is not.
        [Fact]
        public void AnyExistsAsksTheGivenPredicate()
        {
            var asked = new List<string>();

            Assert.True(NewChatHookState.AnyExists(new[] { "a", "b" }, p => { asked.Add(p); return p == "b"; }));
            Assert.Equal(new[] { "a", "b" }, asked);
        }

        [Fact]
        public void HookScriptNameMatchesThePlatform()
        {
            Assert.Equal(
                OperatingSystem.IsWindows() ? "OrbweaverHook.ps1" : "OrbweaverHook.sh",
                NewChatHookState.HookScriptName);
        }

        // --- CurrentlyInstalled: the halves wired together -----------------

        [Fact]
        public void CurrentlyInstalledAsksTheRealEnvironmentAndDoesNotThrow()
        {
            // Nothing is asserted about the answer — it depends on this
            // machine's real installs — only that composing HookCopyCandidates
            // with AnyExists through the real environment doesn't throw for
            // any of the three CLIs.
            foreach (var cli in NewChatAvailability.AllClis)
            {
                _ = NewChatHookState.CurrentlyInstalled(cli);
            }
        }
    }
}
