using Xunit;

namespace ClaudeBuddy.Tests
{
    // CB-201's Account picker rows — pure, so every branch (nothing
    // configured, a second spelling of the default account, a duplicate, a
    // blank) is a test rather than a real settings file and a real $HOME.
    public class NewChatAccountsTests
    {
        [Fact]
        public void WithNoExtrasOnlyDefaultIsOffered()
        {
            var choices = NewChatAccounts.Choices("/Users/me", Array.Empty<string>());

            var only = Assert.Single(choices);
            Assert.Equal(NewChatAccounts.DefaultLabel, only.Label);
            Assert.Null(only.ProfileDir);
        }

        [Fact]
        public void ARealExtraIsOfferedWithATildeLabelAndItsRawProfileDir()
        {
            var choices = NewChatAccounts.Choices("/Users/me", new[] { ".claude-work" });

            Assert.Equal(2, choices.Count);
            Assert.Equal("~/.claude-work", choices[1].Label);
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
            var choices = NewChatAccounts.Choices("/Users/me", new[] { ".claude-work" });

            Assert.Equal("~/.claude-work", choices[1].Label);
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
            var choices = NewChatAccounts.Choices("/Users/me", new[] { "~/x" });

            Assert.Equal("~/~/x", choices[1].Label);
            Assert.Equal("~/x", choices[1].ProfileDir);
        }

        // An absolute path that happens to sit under home labels exactly the
        // same as the relative spelling of the same directory — the label is
        // about where the directory actually is, not about how it was typed.
        [Fact]
        public void AnAbsoluteExtraUnderHomeLabelsAsATildePath()
        {
            var choices = NewChatAccounts.Choices("/Users/me", new[] { "/Users/me/.claude-work" });

            Assert.Equal("~/.claude-work", choices[1].Label);
            Assert.Equal("/Users/me/.claude-work", choices[1].ProfileDir);
        }

        // The bug QA caught: an absolute path outside home must never be
        // trimmed down to something that reads as home-relative — "~/Volumes/
        // Backup/.claude-mobile" names a path that does not exist. The label
        // is the resolved absolute path, unchanged.
        [Fact]
        public void AnAbsoluteExtraOutsideHomeLabelsAsTheAbsolutePathUnchanged()
        {
            var choices = NewChatAccounts.Choices("/Users/me", new[] { "/Volumes/Backup/.claude-mobile" });

            Assert.Equal("/Volumes/Backup/.claude-mobile", choices[1].Label);
            Assert.Equal("/Volumes/Backup/.claude-mobile", choices[1].ProfileDir);
        }

        [Fact]
        public void SeveralRealExtrasAreOfferedInOrderAfterDefault()
        {
            var choices = NewChatAccounts.Choices("/Users/me", new[] { ".claude-work", ".claude-board" });

            Assert.Equal(
                new[] { NewChatAccounts.DefaultLabel, "~/.claude-work", "~/.claude-board" },
                choices.Select(c => c.Label).ToArray());
        }

        // Every spelling ClaudeProfile.ConfigDirFor collapses to the default
        // account (CB-42) is also not offered as a distinct row here — a
        // choice a user could pick that behaves exactly like Default would
        // be a confusing extra entry, not a real second account.
        [Theory]
        [InlineData(".claude")]
        [InlineData(".claude/")]
        [InlineData("/Users/me/.claude")]
        public void AnExtraNamingTheDefaultAccountIsDropped(string profileDir)
        {
            var choices = NewChatAccounts.Choices("/Users/me", new[] { profileDir });

            var only = Assert.Single(choices);
            Assert.Null(only.ProfileDir);
        }

        [Fact]
        public void ADuplicateExtraIsOfferedOnlyOnce()
        {
            var choices = NewChatAccounts.Choices("/Users/me", new[] { ".claude-work", ".claude-work" });

            Assert.Equal(2, choices.Count);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankExtrasAreSkipped(string blank)
        {
            var choices = NewChatAccounts.Choices("/Users/me", new[] { blank, ".claude-work" });

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
