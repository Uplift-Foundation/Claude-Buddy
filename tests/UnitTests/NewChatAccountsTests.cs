using Xunit;

namespace ClaudeBuddy.Tests
{
    // CB-201's Account picker rows — pure, so every branch (nothing
    // configured, a second spelling of the default account, a duplicate, a
    // blank) is a test rather than a real settings file and a real $HOME.
    public class NewChatAccountsTests
    {
        // Rooted for the platform the test is running on, the same reason
        // ClaudeProfileTests.Home is: the label comes from
        // ChatHeaderMeta.HomeRelative(resolved, homeResolved), and both sides
        // of that comparison go through Path.GetFullPath first. A Unix-shaped
        // constant here passed on macOS and failed on windows-latest —
        // Path.GetFullPath("/Users/me") on Windows resolves against the
        // current drive rather than staying "/Users/me", so it stopped
        // reading as "under home" at all. Caught by the CI leg that exists to
        // catch exactly this.
        private static readonly string Home =
            OperatingSystem.IsWindows() ? @"C:\Users\me" : "/Users/me";

        // A rooted path that is not under Home on either platform — a
        // different drive on Windows, a different top-level directory on
        // Unix — for the "outside home" cases.
        private static readonly string OutsideHome =
            OperatingSystem.IsWindows() ? @"D:\Backup\.claude-mobile" : "/Volumes/Backup/.claude-mobile";

        // The separator HomeRelative's own "~" + rest actually produces on
        // this platform, so a label assertion never hardcodes the other
        // platform's slash.
        private static string Tilde(string rest) => "~" + Path.DirectorySeparatorChar + rest;

        [Fact]
        public void WithNoExtrasOnlyDefaultIsOffered()
        {
            var choices = NewChatAccounts.Choices(NewChatCli.ClaudeCode, Home, Array.Empty<string>());

            var only = Assert.Single(choices);
            Assert.Equal(NewChatAccounts.DefaultLabelFor(NewChatCli.ClaudeCode), only.Label);
            Assert.Null(only.ProfileDir);
        }

        [Fact]
        public void ARealExtraIsOfferedWithATildeLabelAndItsRawProfileDir()
        {
            var choices = NewChatAccounts.Choices(NewChatCli.ClaudeCode, Home, new[] { ".claude-work" });

            Assert.Equal(2, choices.Count);
            Assert.Equal(Tilde(".claude-work"), choices[1].Label);
            Assert.Equal(".claude-work", choices[1].ProfileDir);
        }

        // --- Label built from the resolved path, not the raw string --------

        // A relative extra (the ordinary case — what BrowseForProfileDir in
        // SettingsWindow actually produces) labels as "~/<name>". Same
        // assertion as ARealExtraIsOfferedWithATildeLabelAndItsRawProfileDir
        // above, named for this group so the four cases QA asked for read
        // together.
        [Fact]
        public void ARelativeExtraLabelsAsATildePath()
        {
            var choices = NewChatAccounts.Choices(NewChatCli.ClaudeCode, Home, new[] { ".claude-work" });

            Assert.Equal(Tilde(".claude-work"), choices[1].Label);
        }

        // A literal leading "~/" in the raw setting is not tilde-expansion —
        // nothing in this app parses a tilde out of user input, only
        // ChatHeaderMeta.HomeRelative ever produces one, on the way out. So
        // "~/x" names a real subdirectory called "~", one level down from
        // that, and labels honestly as such rather than silently collapsing
        // the literal and the display convention into the same thing.
        [Fact]
        public void ALiteralLeadingTildeInTheRawSettingIsNotExpanded()
        {
            var choices = NewChatAccounts.Choices(NewChatCli.ClaudeCode, Home, new[] { "~/x" });

            Assert.Equal(Tilde("~" + Path.DirectorySeparatorChar + "x"), choices[1].Label);
            Assert.Equal("~/x", choices[1].ProfileDir);
        }

        // An absolute path that happens to sit under home labels exactly the
        // same as the relative spelling of the same directory — the label is
        // about where the directory actually is, not about how it was typed.
        [Fact]
        public void AnAbsoluteExtraUnderHomeLabelsAsATildePath()
        {
            var absolute = Path.Combine(Home, ".claude-work");
            var choices = NewChatAccounts.Choices(NewChatCli.ClaudeCode, Home, new[] { absolute });

            Assert.Equal(Tilde(".claude-work"), choices[1].Label);
            Assert.Equal(absolute, choices[1].ProfileDir);
        }

        // The bug QA caught: an absolute path outside home must never be
        // trimmed down to something that reads as home-relative — a path
        // like that names something that does not exist. The label is the
        // resolved absolute path, unchanged.
        [Fact]
        public void AnAbsoluteExtraOutsideHomeLabelsAsTheAbsolutePathUnchanged()
        {
            var choices = NewChatAccounts.Choices(NewChatCli.ClaudeCode, Home, new[] { OutsideHome });

            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(OutsideHome)), choices[1].Label);
            Assert.Equal(OutsideHome, choices[1].ProfileDir);
        }

        // Pins the fix itself (Tamsin, CB-201): every Home constant above is
        // already canonical, so reverting NewChatAccounts.Choices back to
        // ChatHeaderMeta.HomeRelative(resolved, home) — comparing against
        // the raw, unresolved home instead of ClaudeProfile.Resolve(home,
        // "") — still passes every one of them. A redundant-but-equivalent
        // home (a trailing "\." segment) is the one shape that tells the two
        // apart: resolved collapses the redundancy away via
        // ClaudeProfile.Resolve's own Path.GetFullPath, but the raw home
        // string still carries it, so an un-resolved comparison stops
        // matching the boundary at all and falls through to the full
        // absolute path instead of "~\...".
        [Fact]
        public void ARedundantHomeSegmentStillLabelsAsHomeRelative()
        {
            var noncanonicalHome = Home + Path.DirectorySeparatorChar + ".";

            var choices = NewChatAccounts.Choices(NewChatCli.ClaudeCode, noncanonicalHome, new[] { ".claude-work" });

            Assert.Equal(Tilde(".claude-work"), choices[1].Label);
        }

        [Fact]
        public void SeveralRealExtrasAreOfferedInOrderAfterDefault()
        {
            var choices = NewChatAccounts.Choices(NewChatCli.ClaudeCode, Home, new[] { ".claude-work", ".claude-board" });

            Assert.Equal(
                new[] { NewChatAccounts.DefaultLabelFor(NewChatCli.ClaudeCode), Tilde(".claude-work"), Tilde(".claude-board") },
                choices.Select(c => c.Label).ToArray());
        }

        // Every spelling ClaudeProfile.ConfigDirFor collapses to the default
        // account (CB-42) is also not offered as a distinct row here — a
        // choice a user could pick that behaves exactly like Default would
        // be a confusing extra entry, not a real second account.
        [Theory]
        [InlineData(".claude")]
        [InlineData(".claude/")]
        public void AnExtraNamingTheDefaultAccountIsDropped(string profileDir)
        {
            var choices = NewChatAccounts.Choices(NewChatCli.ClaudeCode, Home, new[] { profileDir });

            var only = Assert.Single(choices);
            Assert.Null(only.ProfileDir);
        }

        // The absolute spelling of the same rule — its own case rather than
        // a [Theory] entry, since it needs Home rather than a compile-time
        // constant.
        [Fact]
        public void AnAbsoluteSpellingOfTheDefaultAccountIsDropped()
        {
            var choices = NewChatAccounts.Choices(NewChatCli.ClaudeCode, Home, new[] { Path.Combine(Home, ".claude") });

            var only = Assert.Single(choices);
            Assert.Null(only.ProfileDir);
        }

        [Fact]
        public void ADuplicateExtraIsOfferedOnlyOnce()
        {
            var choices = NewChatAccounts.Choices(NewChatCli.ClaudeCode, Home, new[] { ".claude-work", ".claude-work" });

            Assert.Equal(2, choices.Count);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankExtrasAreSkipped(string blank)
        {
            var choices = NewChatAccounts.Choices(NewChatCli.ClaudeCode, Home, new[] { blank, ".claude-work" });

            Assert.Equal(2, choices.Count);
            Assert.Equal(".claude-work", choices[1].ProfileDir);
        }

        // --- CB-203: Codex and Grok, each keyed on its own default home ----

        [Theory]
        [InlineData("ClaudeCode", ".claude")]
        [InlineData("Codex", ".codex")]
        [InlineData("Grok", ".grok")]
        public void TheDefaultRowNamesThatCliOwnDefaultDirectory(string cliName, string defaultDir)
        {
            var cli = Enum.Parse<NewChatCli>(cliName);
            var only = Assert.Single(NewChatAccounts.Choices(cli, Home, Array.Empty<string>()));

            Assert.Equal("Default (" + Tilde(defaultDir) + ")", only.Label);
            Assert.Equal(only.Label, NewChatAccounts.DefaultLabelFor(cli));
            Assert.Null(only.ProfileDir);
        }

        // Each CLI drops every spelling of *its own* default home — relative,
        // trailing separator, absolute — the CB-42 de-duplication rule moved
        // onto ".codex" and ".grok".
        [Theory]
        [InlineData("Codex", ".codex")]
        [InlineData("Codex", ".codex/")]
        [InlineData("Grok", ".grok")]
        [InlineData("Grok", ".grok/")]
        public void AnExtraNamingThatCliDefaultHomeIsDropped(string cliName, string profileDir)
        {
            var cli = Enum.Parse<NewChatCli>(cliName);
            var only = Assert.Single(NewChatAccounts.Choices(cli, Home, new[] { profileDir }));
            Assert.Null(only.ProfileDir);
        }

        [Theory]
        [InlineData("Codex", ".codex")]
        [InlineData("Grok", ".grok")]
        public void AnAbsoluteSpellingOfThatCliDefaultHomeIsDropped(string cliName, string defaultDir)
        {
            var cli = Enum.Parse<NewChatCli>(cliName);
            var only = Assert.Single(NewChatAccounts.Choices(cli, Home, new[] { Path.Combine(Home, defaultDir) }));
            Assert.Null(only.ProfileDir);
        }

        // The other way round: ".claude" is not Codex's default, so in a
        // Codex list it is a real (if odd) extra rather than being folded
        // into Default — the de-dup is keyed on the CLI being launched, not
        // on Claude Code's directory for everything.
        [Fact]
        public void AnotherCliDefaultDirectoryIsARealExtraForCodex()
        {
            var choices = NewChatAccounts.Choices(NewChatCli.Codex, Home, new[] { ".claude" });

            Assert.Equal(2, choices.Count);
            Assert.Equal(".claude", choices[1].ProfileDir);
        }

        [Theory]
        [InlineData("Codex", ".codex-work")]
        [InlineData("Grok", ".grok-work")]
        public void ARealExtraIsOfferedForCodexAndGrok(string cliName, string extra)
        {
            var cli = Enum.Parse<NewChatCli>(cliName);
            var choices = NewChatAccounts.Choices(cli, Home, new[] { extra, extra, " " });

            Assert.Equal(2, choices.Count);
            Assert.Equal(Tilde(extra), choices[1].Label);
            Assert.Equal(extra, choices[1].ProfileDir);
        }

        [Fact]
        public void AnAbsoluteGrokExtraOutsideHomeLabelsAsTheAbsolutePath()
        {
            var outside = OperatingSystem.IsWindows() ? @"D:\Backup\.grok-mobile" : "/Volumes/Backup/.grok-mobile";

            var choices = NewChatAccounts.Choices(NewChatCli.Grok, Home, new[] { outside });

            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(outside)), choices[1].Label);
            Assert.Equal(outside, choices[1].ProfileDir);
        }

        // The table itself: every CLI's default directory and variable, so a
        // swapped pair is a named failure rather than a launch under the
        // wrong account.
        [Theory]
        [InlineData("ClaudeCode", ".claude", "CLAUDE_CONFIG_DIR")]
        [InlineData("Codex", ".codex", "CODEX_HOME")]
        [InlineData("Grok", ".grok", "GROK_HOME")]
        public void EachCliNamesItsOwnDefaultDirectoryAndVariable(string cliName, string dir, string variable)
        {
            var cli = Enum.Parse<NewChatCli>(cliName);
            var home = NewChatAccountHome.For(cli);

            Assert.Equal(dir, home.DefaultDirName);
            Assert.Equal(variable, home.EnvVar);
        }

        // --- CB-203: NewChatAccountWarning ---------------------------------

        [Theory]
        [InlineData("ClaudeCode", ".claude-work", "Claude Code will start first-run setup there.")]
        [InlineData("Codex", ".codex-work", "Codex will refuse to start.")]
        [InlineData("Grok", ".grok-work", "Grok will create a fresh, logged-out account there.")]
        public void AMissingAccountDirectoryIsWarnedPerCli(string cliName, string profileDir, string consequence)
        {
            var cli = Enum.Parse<NewChatCli>(cliName);
            string? asked = null;

            var warning = NewChatAccountWarning.For(cli, Home, profileDir, dir => { asked = dir; return false; });

            Assert.Equal("This folder doesn't exist; " + consequence, warning);
            Assert.Equal(Path.Combine(Home, profileDir), asked);
        }

        [Fact]
        public void AnExistingAccountDirectoryIsNotWarned()
        {
            Assert.Null(NewChatAccountWarning.For(NewChatCli.Codex, Home, ".codex-work", _ => true));
        }

        // Default has no directory of ours to check — the filesystem is never
        // asked, so a missing ~/.codex is the CLI's business, not a warning.
        [Theory]
        [InlineData("Codex", null)]
        [InlineData("Codex", ".codex")]
        [InlineData("Grok", ".grok/")]
        public void TheDefaultAccountIsNeverWarned(string cliName, string? profileDir)
        {
            var asked = false;

            var warning = NewChatAccountWarning.For(
                Enum.Parse<NewChatCli>(cliName), Home, profileDir, _ => { asked = true; return false; });

            Assert.Null(warning);
            Assert.False(asked);
        }

        [Fact]
        public void ChoiceToStringIsItsLabel()
        {
            var choice = new NewChatAccounts.Choice("~/.claude-work", ".claude-work");

            Assert.Equal("~/.claude-work", choice.ToString());
        }
    }
}
