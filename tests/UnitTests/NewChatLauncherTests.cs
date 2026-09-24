using Xunit;

namespace ClaudeBuddy.Tests
{
    // The parts of NewChatLauncher that don't touch a real terminal: the
    // display names shared with NewChatAvailability's reason text, and the
    // LaunchForTests seam a UI test uses instead of spawning a process.
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

        [Fact]
        public void LaunchUsesTheTestSeamWhenOneIsInstalled()
        {
            var expected = new LaunchResult(LaunchOutcome.Launched, "started");
            NewChatLauncher.LaunchForTests = (cli, cwd) => expected;

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

            NewChatLauncher.LaunchForTests = (cli, cwd) =>
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
    }
}
