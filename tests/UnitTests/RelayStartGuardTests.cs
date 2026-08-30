using Xunit;

namespace ClaudeBuddy.Tests;

// The stop that keeps a test process from starting a real relay (CB-42).
//
// Worth a test of its own because it is load-bearing in an unusual direction:
// every other guard here protects the app from a mistake, and this one protects
// the developer's machine and account from the suite. It is also the only guard
// whose failure mode is invisible on CI — a GitHub runner has no `claude` to
// start, so a broken block passes there and starts a live Claude Code session on
// the next person's laptop instead.
public class RelayStartGuardTests
{
    [Fact]
    public void Is_on_for_this_suite()
    {
        // Set by TestBootstrap before any test in the assembly runs. Asserted
        // rather than assumed: if the module initializer ever stops setting it,
        // this says so in one line instead of the suite quietly starting relays.
        Assert.True(RemoteControlSessions.StartsBlocked);
    }

    [Fact]
    public void Reads_the_variable_every_time_it_is_asked()
    {
        // Not cached, so the opt-in live-bridge tests can clear it for their own
        // duration — which is the one case in this repository where starting a
        // real relay is the point.
        var was = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_NO_RELAY");

        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_BUDDY_NO_RELAY", null);
            Assert.False(RemoteControlSessions.StartsBlocked);

            Environment.SetEnvironmentVariable("CLAUDE_BUDDY_NO_RELAY", "1");
            Assert.True(RemoteControlSessions.StartsBlocked);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_BUDDY_NO_RELAY", was);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("yes")]
    public void Blocks_on_exactly_one_value_and_not_on_lookalikes(string value)
    {
        // Deliberately not "any non-empty value". This variable exists to be set
        // by a bootstrap, not typed by a person, and a narrow rule means a shell
        // that exports it as "0" gets what "0" plainly says rather than the
        // opposite.
        var was = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_NO_RELAY");

        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_BUDDY_NO_RELAY", value);
            Assert.False(RemoteControlSessions.StartsBlocked);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_BUDDY_NO_RELAY", was);
        }
    }
}
