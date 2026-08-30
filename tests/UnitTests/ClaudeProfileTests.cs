using Xunit;

namespace ClaudeBuddy.Tests;

// Which config directory a `claude` this app starts is told to use (CB-42).
//
// The case that matters is the first one, and it is the kind of assertion that
// looks like it is testing nothing: "the default account gets *no* variable".
// Setting it to the default path instead is a one-word difference in the code
// and the difference between a working relay and one that sits on a first-run
// wizard until somebody kills it, because Claude Code reads
// `$HOME/.claude/.claude.json` when told and `$HOME/.claude.json` when not.
public class ClaudeProfileTests
{
    private const string Home = "/Users/someone";

    [Fact]
    public void Names_no_context_for_the_default_account()
    {
        Assert.Null(ClaudeProfile.ConfigDirFor(Home, ".claude"));
    }

    [Fact]
    public void Names_the_directory_for_a_second_account()
    {
        // The case the variable exists for: without it, per-account relays are
        // impossible because every one of them would read the same registry.
        Assert.Equal(
            Path.Combine(Home, ".claude-board"),
            ClaudeProfile.ConfigDirFor(Home, ".claude-board"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Names_no_context_when_no_profile_was_given(string? profileDir)
    {
        // A blank entry is not "$HOME" — the settings list permits one and
        // resolving it to the home directory would name something that is not a
        // config directory at all.
        Assert.Null(ClaudeProfile.ConfigDirFor(Home, profileDir));
    }

    [Theory]
    [InlineData(".claude/")]
    [InlineData("./.claude")]
    [InlineData(".claude/.")]
    [InlineData("  .claude  ")]
    [InlineData(".CLAUDE")]
    public void Recognises_the_default_account_however_it_is_spelled(string profileDir)
    {
        // The settings UI takes free text, so these are all things someone can
        // type, and every one of them is the same directory. Compared by
        // resolved path rather than by name for exactly this, the way
        // BackgroundJobs.ExtraAccountDirs holds the default account out.
        Assert.Null(ClaudeProfile.ConfigDirFor(Home, profileDir));
    }

    [Fact]
    public void Recognises_the_default_account_written_as_an_absolute_path()
    {
        Assert.Null(ClaudeProfile.ConfigDirFor(Home, Path.Combine(Home, ".claude")));
    }

    [Fact]
    public void Keeps_an_absolute_path_that_is_a_different_account()
    {
        // Absolute wins over the home directory — Path.Combine's own rule — so
        // a config directory kept outside $HOME still works.
        var elsewhere = Path.Combine(Path.GetTempPath(), "claude-elsewhere");

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(elsewhere)),
            ClaudeProfile.ConfigDirFor(Home, elsewhere));
    }

    [Fact]
    public void Normalises_the_answer_it_does_give()
    {
        // Not cosmetic: the same account named two ways has to produce one
        // string, or two relays that are the same account look like two
        // accounts to everything downstream.
        Assert.Equal(
            ClaudeProfile.ConfigDirFor(Home, ".claude-board"),
            ClaudeProfile.ConfigDirFor(Home, "./.claude-board/"));
    }

    [Fact]
    public void Refuses_to_call_an_unresolvable_name_the_default_account()
    {
        // A name the platform cannot resolve at all. The answer that matters is
        // that it is *not* null: whatever this is, it is not the default
        // account, and quietly treating it as one would put a session that was
        // asked for by name into somebody else's context.
        var nonsense = ClaudeProfile.ConfigDirFor(Home, ".claude-\0-board");

        Assert.NotNull(nonsense);
        Assert.Contains("claude-", nonsense);
    }
}
