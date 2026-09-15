using Xunit;

namespace ClaudeBuddy.Tests
{
    // Serialises every test class in this assembly whose assertions depend on
    // CLAUDE_CONFIG_DIR holding still — not only the ones that call
    // Environment.SetEnvironmentVariable, but any that read the variable and
    // then observe it again through a launched child process, expecting the
    // two readings to agree.
    //
    // The definition has to live here rather than be borrowed from
    // tests/UnitTests, which declares one under the same name: xUnit resolves a
    // collection definition per assembly, so a bare [Collection("ConfigDirEnv")]
    // in this assembly would still serialise correctly on the name alone, but
    // there would be nowhere to write down why, and nothing to stop the two
    // assemblies' idea of "ConfigDirEnv" drifting apart. Same argument
    // SettingsCollection.cs already makes for "Settings".
    //
    // The hazard itself: the environment block is process-wide, not per-test.
    // A class asserting "the child inherited what this process has" while a
    // sibling class is mid-way through setting a sentinel would read the
    // sentinel instead of its own value — a flake with no exception and no
    // obvious cause, the same shape CB-3/CB-6 already found in the settings
    // statics. A *bare* read of the variable is harmless and does not need
    // this collection. But AgentRosterEnvironmentTests'
    // The_default_account_leaves_the_child_with_what_this_process_has is not
    // bare: it reads CLAUDE_CONFIG_DIR, launches a child that snapshots it a
    // moment later, and asserts the two agree. That test carries no
    // SetEnvironmentVariable of its own, and was originally left out of this
    // collection on the reasoning that only writers needed it — CB-113's
    // UsagePollerEnvironmentTests, added alongside it in the same assembly and
    // sharing this collection, is exactly the sibling whose write could land
    // in that read-to-launch window and flip the comparison either way with
    // no exception and nothing to point at. It is a member here for that
    // reason, not because it writes anything.
    [CollectionDefinition("ConfigDirEnv")]
    public class ConfigDirEnvCollection
    {
    }
}
