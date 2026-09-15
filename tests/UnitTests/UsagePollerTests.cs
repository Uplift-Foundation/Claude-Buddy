using System.Diagnostics;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers UsagePoller.UsageProcess — the seam CB-113 was missing.
//
// The bug: a null configDir left CLAUDE_CONFIG_DIR untouched in the child's
// environment, so the default account's poll silently inherited whatever the
// *app process* happened to be started under, while its label was read
// straight off ~/.claude.json regardless. One orb, two accounts. See the long
// comment on UsageProcess in UsagePoller.cs for why this is the right fix and
// not a repeat of CB-42's "leave it alone" rule — this is a report about a
// specific named account, not a session launched on the user's behalf.
[Collection("ConfigDirEnv")]
public class UsagePollerTests : IDisposable
{
    private readonly string? _previous;

    public UsagePollerTests()
    {
        _previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
    }

    public void Dispose()
    {
        // Restore "was not set" as null rather than "", the same distinction
        // the test below exists to make.
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _previous);
    }

    [Fact]
    public void ANullConfigDirRemovesTheVariableEvenWhenTheParentHasOne()
    {
        // The regression this whole ticket is about: proving the null case
        // merely leaves the dictionary untouched is not enough, because
        // ProcessStartInfo.Environment starts out seeded from this process's
        // own environment. Setting the sentinel here is what makes this test
        // fail against the old code and pass against the fix — without it,
        // the assertion below would pass by accident on any machine where
        // CLAUDE_CONFIG_DIR was never set at all.
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", "/Users/someone/.claude-board");

        var psi = UsagePoller.UsageProcess("/usr/local/bin/claude", null);

        Assert.False(psi.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
    }

    [Fact]
    public void ANamedConfigDirIsSetToExactlyThatValue()
    {
        var psi = UsagePoller.UsageProcess("/usr/local/bin/claude", "/Users/someone/.claude-board");

        Assert.Equal("/Users/someone/.claude-board", psi.Environment["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public void TheArgumentsAreTheOnesTheControlProtocolAnswersTo()
    {
        // Cheap, and it is the other half of "what was this launched with" —
        // a psi that carried the right environment and the wrong verbs would
        // pass every assertion above. Mirrors
        // AgentRosterEnvironmentTests.The_arguments_are_the_ones_the_registry_answers_to.
        var psi = UsagePoller.UsageProcess("/usr/local/bin/claude", null);

        Assert.Equal(
            new[]
            {
                "-p", "--verbose", "--no-session-persistence",
                "--settings", "{\"disableAllHooks\":true}",
                "--input-format", "stream-json",
                "--output-format", "stream-json"
            },
            psi.ArgumentList);

        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardInput);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
    }
}
