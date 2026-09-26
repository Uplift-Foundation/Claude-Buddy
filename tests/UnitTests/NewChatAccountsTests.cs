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
