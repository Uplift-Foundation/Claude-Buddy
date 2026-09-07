using Xunit;

namespace ClaudeBuddy.Tests
{
    // Serialises every test class in this assembly whose assertions depend on
    // CLAUDE_CONFIG_DIR holding still — not just the ones that call
    // SetEnvironmentVariable, but any that read the variable and then observe
    // it again through a launched child process, expecting the two readings
    // to agree.
    //
    // Same hazard SettingsCollection documents for ClaudeBuddySettings, one
    // level down: the environment block is process-wide, not per-test, so a
    // class that sets CLAUDE_CONFIG_DIR to a sentinel while another class is
    // mid-assertion about "what this process has" would not fail loudly — it
    // would read the other class's sentinel and pass or fail for the wrong
    // reason. A *bare* read is harmless and can stay outside this collection.
    // But a read followed by launching a child that snapshots the variable a
    // moment later, asserting the two match, is not bare — it is a window
    // wide enough for a sibling test's write, or that write's later restore,
    // to land in between and flip either side of the comparison with no
    // exception and nothing to point at. See
    // tests/IntegrationTests/ConfigDirEnvCollection.cs for the real case this
    // was written for: AgentRosterEnvironmentTests reads-then-observes and
    // was flaky against this suite's own UsagePollerEnvironmentTests until it
    // joined the same collection.
    [CollectionDefinition("ConfigDirEnv")]
    public class ConfigDirEnvCollection
    {
    }
}
