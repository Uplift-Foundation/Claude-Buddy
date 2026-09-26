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
            var choices = NewChatAccounts.Choices(Home, Array.Empty<string>());

            var only = Assert.Single(choices);
            Assert.Equal(NewChatAccounts.DefaultLabel, only.Label);
            Assert.Null(only.ProfileDir);
        }

        [Fact]
        public void ARealExtraIsOfferedWithATildeLabelAndItsRawProfileDir()
        {
            var choices = NewChatAccounts.Choices(Home, new[] { ".claude-work" });

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
            var choices = NewChatAccounts.Choices(Home, new[] { ".claude-work" });

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
            var choices = NewChatAccounts.Choices(Home, new[] { "~/x" });

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
            var choices = NewChatAccounts.Choices(Home, new[] { absolute });

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
            var choices = NewChatAccounts.Choices(Home, new[] { OutsideHome });

            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(OutsideHome)), choices[1].Label);
            Assert.Equal(OutsideHome, choices[1].ProfileDir);
        }

        [Fact]
        public void SeveralRealExtrasAreOfferedInOrderAfterDefault()
        {
            var choices = NewChatAccounts.Choices(Home, new[] { ".claude-work", ".claude-board" });

            Assert.Equal(
                new[] { NewChatAccounts.DefaultLabel, Tilde(".claude-work"), Tilde(".claude-board") },
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
            var choices = NewChatAccounts.Choices(Home, new[] { profileDir });

            var only = Assert.Single(choices);
            Assert.Null(only.ProfileDir);
        }

        // The absolute spelling of the same rule — its own case rather than
        // a [Theory] entry, since it needs Home rather than a compile-time
        // constant.
        [Fact]
        public void AnAbsoluteSpellingOfTheDefaultAccountIsDropped()
        {
            var choices = NewChatAccounts.Choices(Home, new[] { Path.Combine(Home, ".claude") });

            var only = Assert.Single(choices);
            Assert.Null(only.ProfileDir);
        }

        [Fact]
        public void ADuplicateExtraIsOfferedOnlyOnce()
        {
            var choices = NewChatAccounts.Choices(Home, new[] { ".claude-work", ".claude-work" });

            Assert.Equal(2, choices.Count);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankExtrasAreSkipped(string blank)
        {
            var choices = NewChatAccounts.Choices(Home, new[] { blank, ".claude-work" });

            Assert.Equal(2, choices.Count);
            Assert.Equal(".claude-work", choices[1].ProfileDir);
        }

        [Fact]
        public void ChoiceToStringIsItsLabel()
        {
            var choice = new NewChatAccounts.Choice("~/.claude-work", ".claude-work");

            Assert.Equal("~/.claude-work", choice.ToString());
        }
    }
}
