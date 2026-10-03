using Xunit;

namespace ClaudeBuddy.Tests
{
    // The parts of NewChatLauncher that don't touch a real terminal: the
    // display names shared with NewChatAvailability's reason text, the
    // LaunchForTests seam a UI test uses instead of spawning a process, and
    // (below) Decide/ResolveDirectory — the decisions RealLaunch makes after
    // running its real I/O, pulled out so a swapped message or a flipped
    // tmux/window preference fails a test rather than only failing on the
    // machine that happens to have no tmux, or none of these CLIs installed.
    public class NewChatLauncherTests
    {
        // NewChatCli is internal, and a [Theory]'s InlineData parameters have
        // to be at least as accessible as the test method itself — so each
        // case is its own Fact rather than a Theory over the enum.
        [Fact]
        public void DisplayNameNamesClaudeCode()
        {
            Assert.Equal("Claude Code", NewChatLauncher.DisplayName(NewChatCli.ClaudeCode));
        }

        [Fact]
        public void DisplayNameNamesCodex()
        {
            Assert.Equal("Codex", NewChatLauncher.DisplayName(NewChatCli.Codex));
        }

        [Fact]
        public void DisplayNameNamesGrok()
        {
            Assert.Equal("Grok", NewChatLauncher.DisplayName(NewChatCli.Grok));
        }

        // The `_ => cli.ToString()` arm — unreachable through any of this
        // file's own callers (they only ever pass a defined NewChatCli), but
        // real code all the same, and a cast out of the defined range is
        // what reaches it. ToString() on an undefined enum value prints its
        // numeric value, which is exactly the fallback worth pinning: a
        // caller that somehow gets here still sees a number rather than a
        // blank label or a thrown exception.
        [Fact]
        public void DisplayNameFallsBackToToStringForAnUndefinedCli()
        {
            var undefined = (NewChatCli)99;

            Assert.Equal(undefined.ToString(), NewChatLauncher.DisplayName(undefined));
            Assert.Equal("99", NewChatLauncher.DisplayName(undefined));
        }

        [Fact]
        public void LaunchUsesTheTestSeamWhenOneIsInstalled()
        {
            var expected = new LaunchResult(LaunchOutcome.Launched, "started");
            NewChatLauncher.LaunchForTests = (cli, cwd, profileDir) => expected;

            try
            {
                var result = NewChatLauncher.Launch(NewChatCli.Grok, "/tmp/somewhere");
                Assert.Same(expected, result);
            }
            finally
            {
                NewChatLauncher.LaunchForTests = null;
            }
        }

        [Fact]
        public void LaunchPassesTheCliAndCwdThroughToTheSeam()
        {
            NewChatCli? seenCli = null;
            string? seenCwd = null;

            NewChatLauncher.LaunchForTests = (cli, cwd, profileDir) =>
            {
                seenCli = cli;
                seenCwd = cwd;
                return new LaunchResult(LaunchOutcome.Launched, "ok");
            };

            try
            {
                NewChatLauncher.Launch(NewChatCli.Codex, "/work/dir");

                Assert.Equal(NewChatCli.Codex, seenCli);
                Assert.Equal("/work/dir", seenCwd);
            }
            finally
            {
                NewChatLauncher.LaunchForTests = null;
            }
        }

        // The profile dir is the third leg of the same seam — a UI test can
        // assert Start actually passed the picker's choice through without
        // ever reaching RealLaunch (excluded from coverage; see its own
        // header).
        [Fact]
        public void LaunchPassesTheProfileDirThroughToTheSeam()
        {
            string? seenProfileDir = "not set yet";

            NewChatLauncher.LaunchForTests = (cli, cwd, profileDir) =>
            {
                seenProfileDir = profileDir;
                return new LaunchResult(LaunchOutcome.Launched, "ok");
            };

            try
            {
                NewChatLauncher.Launch(NewChatCli.ClaudeCode, "/work/dir", "/Users/me/.claude-work");

                Assert.Equal("/Users/me/.claude-work", seenProfileDir);
            }
            finally
            {
                NewChatLauncher.LaunchForTests = null;
            }
        }

        [Fact]
        public void LaunchWithNoProfileDirPassesNullThroughToTheSeam()
        {
            string? seenProfileDir = "not set yet";

            NewChatLauncher.LaunchForTests = (cli, cwd, profileDir) =>
            {
                seenProfileDir = profileDir;
                return new LaunchResult(LaunchOutcome.Launched, "ok");
            };

            try
            {
                NewChatLauncher.Launch(NewChatCli.ClaudeCode, "/work/dir");

                Assert.Null(seenProfileDir);
            }
            finally
            {
                NewChatLauncher.LaunchForTests = null;
            }
        }

        // --- ConfigDirFor: the one path to each CLI's account variable ------

        // Rooted for the platform the test runs on, the same reason
        // ClaudeProfileTests.Home is — ConfigDirFor's answer is a resolved
        // path (Path.GetFullPath under the hood), and "/Users/me" resolves
        // against the current drive rather than staying itself on Windows.
        // A hardcoded Unix expectation here passed on macOS and failed on
        // windows-latest until this was made platform-aware.
        private static readonly string Home =
            OperatingSystem.IsWindows() ? @"C:\Users\me" : "/Users/me";

        [Fact]
        public void ConfigDirForDelegatesToClaudeProfileForClaudeCode()
        {
            var result = NewChatLauncher.ConfigDirFor(NewChatCli.ClaudeCode, Home, ".claude-work");

            Assert.Equal(Path.Combine(Home, ".claude-work"), result);
        }

        // CB-203: Codex and Grok resolve a named home the same way, through
        // the same normaliser, rather than being ignored as they were under
        // CB-201's Claude Code-only picker.
        [Theory]
        [InlineData("Codex", ".codex-work")]
        [InlineData("Grok", ".grok-work")]
        public void ConfigDirForResolvesANamedHomeForCodexAndGrok(string cliName, string profileDir)
        {
            var cli = Enum.Parse<NewChatCli>(cliName);
            Assert.Equal(Path.Combine(Home, profileDir), NewChatLauncher.ConfigDirFor(cli, Home, profileDir));
        }

        // Each CLI's own default home is "set nothing" — measured harmless to
        // set for Codex and Grok (CB-203), but null keeps one rule for all
        // three and lets an inherited CODEX_HOME/GROK_HOME through.
        [Theory]
        [InlineData("Codex", ".codex")]
        [InlineData("Codex", ".codex/")]
        [InlineData("Grok", ".grok")]
        [InlineData("Grok", null)]
        public void ConfigDirForIsNullForCodexAndGrokDefaultHome(string cliName, string? profileDir)
        {
            var cli = Enum.Parse<NewChatCli>(cliName);
            Assert.Null(NewChatLauncher.ConfigDirFor(cli, Home, profileDir));
        }

        [Theory]
        [InlineData("Codex", ".codex")]
        [InlineData("Grok", ".grok")]
        public void ConfigDirForIsNullForAnAbsoluteSpellingOfTheDefaultHome(string cliName, string dir)
        {
            var cli = Enum.Parse<NewChatCli>(cliName);
            Assert.Null(NewChatLauncher.ConfigDirFor(cli, Home, Path.Combine(Home, dir)));
        }

        // The de-dup is per CLI: Claude Code's default directory, handed to
        // Codex, is a real directory to point CODEX_HOME at, not Default.
        [Fact]
        public void ConfigDirForTreatsClaudeDefaultAsARealHomeForCodex()
        {
            Assert.Equal(Path.Combine(Home, ".claude"), NewChatLauncher.ConfigDirFor(NewChatCli.Codex, Home, ".claude"));
        }

        // CB-42's own case, reached through this seam: a profile dir that
        // names the default account, in each spelling ClaudeProfile.ConfigDirFor
        // already collapses, still resolves to null through ConfigDirFor —
        // this is the one path there is, so it inherits that behaviour rather
        // than needing its own copy of it.
        [Theory]
        [InlineData(".claude")]
        [InlineData(".claude/")]
        [InlineData("/Users/me/.claude")]
        public void ConfigDirForCollapsesEverySpellingOfTheDefaultAccountToNull(string profileDir)
        {
            Assert.Null(NewChatLauncher.ConfigDirFor(NewChatCli.ClaudeCode, "/Users/me", profileDir));
        }

        [Fact]
        public void ConfigDirForIsNullForClaudeCodeWithNoProfileDir()
        {
            Assert.Null(NewChatLauncher.ConfigDirFor(NewChatCli.ClaudeCode, "/Users/me", null));
        }

        // --- ResolveDirectory ------------------------------------------------

        [Fact]
        public void AnExistingCwdIsUsedAsIs()
        {
            Assert.Equal(
                "/work/dir", NewChatLauncher.ResolveDirectory("/work/dir", cwdExists: true, "/fallback"));
        }

        [Fact]
        public void AMissingCwdFallsBackToTheCurrentDirectory()
        {
            Assert.Equal(
                "/fallback", NewChatLauncher.ResolveDirectory("/gone", cwdExists: false, "/fallback"));
        }

        // --- Decide: not found, on every platform ----------------------------

        [Fact]
        public void ANullBinaryIsAlwaysNotFoundRegardlessOfPlatform()
        {
            var result = NewChatLauncher.Decide(
                "Codex", null, "/work", NewChatPlatform.MacOS, null, "%1", true);

            Assert.Equal(LaunchOutcome.NotFound, result.Outcome);
            Assert.StartsWith("Codex", result.Message);
            Assert.Contains(NewChatAvailability.NotFoundReasonSuffix, result.Message);
        }

        // --- Decide: Windows --------------------------------------------------

        [Fact]
        public void WindowsLaunchedTrueIsLaunchedAndNamesTheDirectory()
        {
            var result = NewChatLauncher.Decide(
                "Claude Code", "claude.exe", @"C:\work", NewChatPlatform.Windows,
                windowsLaunched: true, macTmuxPane: null, macTerminalLaunched: null);

            Assert.Equal(LaunchOutcome.Launched, result.Outcome);
            Assert.Contains("Claude Code", result.Message);
            Assert.Contains(@"C:\work", result.Message);
        }

        [Fact]
        public void WindowsLaunchedFalseIsSpawnFailed()
        {
            var result = NewChatLauncher.Decide(
                "Grok", "grok.exe", @"C:\work", NewChatPlatform.Windows,
                windowsLaunched: false, macTmuxPane: null, macTerminalLaunched: null);

            Assert.Equal(LaunchOutcome.SpawnFailed, result.Outcome);
            Assert.Contains("Grok", result.Message);
        }

        [Fact]
        public void WindowsLaunchedNullIsAlsoSpawnFailed()
        {
            // Decide never receives a genuine null from RealLaunch on the
            // Windows arm (StartWindowsProcess always returns a real bool),
            // but the parameter is nullable to share a signature across
            // platforms, and null must not be misread as success.
            var result = NewChatLauncher.Decide(
                "Codex", "codex.exe", @"C:\work", NewChatPlatform.Windows,
                windowsLaunched: null, macTmuxPane: null, macTerminalLaunched: null);

            Assert.Equal(LaunchOutcome.SpawnFailed, result.Outcome);
        }

        // --- Decide: macOS, tmux preferred over a fresh window ----------------

        [Fact]
        public void ANonEmptyTmuxPaneIsLaunchedEvenIfTerminalLaunchWasNeverTried()
        {
            var result = NewChatLauncher.Decide(
                "Claude Code", "/usr/local/bin/claude", "/work", NewChatPlatform.MacOS,
                windowsLaunched: null, macTmuxPane: "%3", macTerminalLaunched: null);

            Assert.Equal(LaunchOutcome.Launched, result.Outcome);
            Assert.Contains("/work", result.Message);
        }

        [Fact]
        public void AnEmptyTmuxPaneFallsBackToTheTerminalLaunchResult()
        {
            var launched = NewChatLauncher.Decide(
                "Codex", "/usr/local/bin/codex", "/work", NewChatPlatform.MacOS,
                windowsLaunched: null, macTmuxPane: "", macTerminalLaunched: true);

            Assert.Equal(LaunchOutcome.Launched, launched.Outcome);

            var failed = NewChatLauncher.Decide(
                "Codex", "/usr/local/bin/codex", "/work", NewChatPlatform.MacOS,
                windowsLaunched: null, macTmuxPane: "", macTerminalLaunched: false);

            Assert.Equal(LaunchOutcome.SpawnFailed, failed.Outcome);
        }

        [Fact]
        public void ANullTmuxPaneIsTreatedTheSameAsAnEmptyOne()
        {
            var result = NewChatLauncher.Decide(
                "Grok", "/usr/local/bin/grok", "/work", NewChatPlatform.MacOS,
                windowsLaunched: null, macTmuxPane: null, macTerminalLaunched: true);

            Assert.Equal(LaunchOutcome.Launched, result.Outcome);
        }

        [Fact]
        public void NoTmuxAndNoTerminalLaunchIsSpawnFailed()
        {
            var result = NewChatLauncher.Decide(
                "Grok", "/usr/local/bin/grok", "/work", NewChatPlatform.MacOS,
                windowsLaunched: null, macTmuxPane: null, macTerminalLaunched: null);

            Assert.Equal(LaunchOutcome.SpawnFailed, result.Outcome);
            Assert.Contains("Grok", result.Message);
        }

        // --- Decide: every other platform --------------------------------------

        [Fact]
        public void AnUnsupportedPlatformIsSpawnFailedWithItsOwnMessage()
        {
            var result = NewChatLauncher.Decide(
                "Claude Code", "/usr/local/bin/claude", "/work", NewChatPlatform.Other,
                windowsLaunched: null, macTmuxPane: null, macTerminalLaunched: null);

            Assert.Equal(LaunchOutcome.SpawnFailed, result.Outcome);
            Assert.Equal("Starting a new chat isn't supported on this platform yet.", result.Message);
        }

        // --- The three outcomes never share a message ---------------------------

        [Fact]
        public void NotFoundSpawnFailedAndLaunchedAllReadDifferently()
        {
            var notFound = NewChatLauncher.Decide(
                "Codex", null, "/work", NewChatPlatform.MacOS, null, null, null);
            var spawnFailed = NewChatLauncher.Decide(
                "Codex", "/usr/local/bin/codex", "/work", NewChatPlatform.MacOS, null, null, null);
            var launched = NewChatLauncher.Decide(
                "Codex", "/usr/local/bin/codex", "/work", NewChatPlatform.MacOS, null, "%1", null);

            Assert.NotEqual(notFound.Message, spawnFailed.Message);
            Assert.NotEqual(spawnFailed.Message, launched.Message);
            Assert.NotEqual(notFound.Message, launched.Message);
        }
    }
}
