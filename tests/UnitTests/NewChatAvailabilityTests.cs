using Xunit;

namespace ClaudeBuddy.Tests
{
    // CB-168's new-chat dialog decides, for each CLI, whether it's enabled and
    // why not — or, if it is enabled, whether to warn that no orb will appear
    // without its hook installed. Evaluate is the pure heart of that decision,
    // taking Locate and hook-install state as functions so no real filesystem
    // or PATH is needed to check every branch.
    public class NewChatAvailabilityTests
    {
        private static IReadOnlyList<NewChatOption> Run(
            Func<NewChatCli, string?> locate, Func<NewChatCli, bool> hookInstalled) =>
            NewChatAvailability.Evaluate(locate, hookInstalled);

        [Fact]
        public void AllThreeClisAreReturnedInOrder()
        {
            var options = Run(_ => "/bin/found", _ => true);

            Assert.Equal(
                new[] { NewChatCli.ClaudeCode, NewChatCli.Codex, NewChatCli.Grok },
                options.Select(o => o.Cli).ToArray());
        }

        // Not found: disabled, named reason, no warning.
        [Fact]
        public void ACliNotOnDiskIsDisabledWithTheNotFoundReason()
        {
            var options = Run(_ => null, _ => true);

            foreach (var option in options)
            {
                Assert.False(option.Enabled);
                Assert.NotNull(option.Reason);
                Assert.Contains(NewChatAvailability.NotFoundReasonSuffix, option.Reason);
                Assert.Null(option.Warning);
            }
        }

        // Found, hook installed: enabled, no reason, no warning.
        [Fact]
        public void AFullyWiredCliIsEnabledWithNoReasonAndNoWarning()
        {
            var options = Run(_ => "/usr/local/bin/claude", _ => true);

            foreach (var option in options)
            {
                Assert.True(option.Enabled);
                Assert.Null(option.Reason);
                Assert.Null(option.Warning);
            }
        }

        // Found, hook missing: enabled (a terminal is still useful without an
        // orb), but warned.
        [Fact]
        public void AFoundCliWithNoHookIsEnabledWithAWarning()
        {
            var options = Run(_ => "/usr/local/bin/codex", _ => false);

            foreach (var option in options)
            {
                Assert.True(option.Enabled);
                Assert.Null(option.Reason);
                Assert.Equal(NewChatAvailability.HookMissingWarning, option.Warning);
            }
        }

        // hookInstalled is never asked about a CLI Locate could not find — a
        // CLI that isn't there has no hook state worth reporting, and asking
        // anyway would be an unmodeled fourth combination the dialog has no
        // row for.
        [Fact]
        public void HookStateIsNotConsultedWhenTheCliWasNotFound()
        {
            var asked = new List<NewChatCli>();

            Run(_ => null, cli => { asked.Add(cli); return true; });

            Assert.Empty(asked);
        }

        // Mixed rows: each CLI decided independently of the others.
        [Fact]
        public void EachCliIsDecidedIndependently()
        {
            var options = Run(
                cli => cli == NewChatCli.Codex ? null : "/bin/found",
                cli => cli == NewChatCli.Grok);

            var byCli = options.ToDictionary(o => o.Cli);

            Assert.True(byCli[NewChatCli.ClaudeCode].Enabled);
            Assert.Equal(NewChatAvailability.HookMissingWarning, byCli[NewChatCli.ClaudeCode].Warning);

            Assert.False(byCli[NewChatCli.Codex].Enabled);
            Assert.NotNull(byCli[NewChatCli.Codex].Reason);

            Assert.True(byCli[NewChatCli.Grok].Enabled);
            Assert.Null(byCli[NewChatCli.Grok].Warning);
        }

        // The reason text names the CLI, so three disabled rows don't read
        // identically to someone who can't yet tell them apart by icon alone.
        //
        // NewChatCli is internal, and a [Theory]'s InlineData parameters have
        // to be at least as accessible as the test method itself — so each
        // case is asserted directly rather than through a Theory over the enum.
        [Fact]
        public void TheReasonNamesEachSpecificCli()
        {
            var options = Run(_ => null, _ => true);
            var byCli = options.ToDictionary(o => o.Cli);

            Assert.StartsWith("Claude Code", byCli[NewChatCli.ClaudeCode].Reason);
            Assert.StartsWith("Codex", byCli[NewChatCli.Codex].Reason);
            Assert.StartsWith("Grok", byCli[NewChatCli.Grok].Reason);
        }

        // Current() falls back to the real Evaluate/Locate/hook-state path
        // when no test seam is installed, and honours the seam when one is.
        [Fact]
        public void CurrentUsesTheTestSeamWhenOneIsInstalled()
        {
            var fake = new[] { new NewChatOption(NewChatCli.ClaudeCode, true, null, null) };
            NewChatAvailability.CurrentForTests = () => fake;

            try
            {
                Assert.Same(fake, NewChatAvailability.Current());
            }
            finally
            {
                NewChatAvailability.CurrentForTests = null;
            }
        }

        [Fact]
        public void CurrentFallsBackToTheRealEvaluationWithNoSeamInstalled()
        {
            NewChatAvailability.CurrentForTests = null;

            var options = NewChatAvailability.Current();

            Assert.Equal(3, options.Count);
        }

        // RealLocate's own `_ => null` arm — unreachable through Current()
        // itself, since AllClis only ever hands it the three real values, but
        // real code all the same and worth pinning against a cast out of the
        // defined range rather than left uncovered.
        [Fact]
        public void RealLocateAnswersNullForAnUndefinedCli()
        {
            Assert.Null(NewChatAvailability.RealLocate((NewChatCli)99));
        }
    }
}
