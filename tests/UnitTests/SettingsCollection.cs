using Xunit;

namespace ClaudeBuddy.Tests
{
    // Serialises every test class that touches ClaudeBuddySettings.
    //
    // Same reasoning as tests/IntegrationTests' collection of the same name, and
    // it arrived here the same way: by a test failing. ClaudeDesktopColorsTests
    // writes a profile colour and reads it back, and it failed exactly once in
    // five runs of the suite under the coverage collector while passing every
    // time under a plain `dotnet test`. Nothing about that test is wrong.
    //
    // ClaudeBuddySettings is a static class holding one model for the whole
    // process. xUnit runs test *classes* in parallel by default, and nine classes
    // in this assembly read or write that model — profile colours, voice choices,
    // gateway filters, reply toggles. Under load they interleave, and a
    // once-in-five failure with no reproduction is exactly the shape that has.
    //
    // Rather than hunt the specific interleaving, the whole class of race is
    // removed: everything that touches the shared model runs one at a time. That
    // is the same trade the integration suite already made, for the same reason,
    // and it costs a few seconds on a suite that runs in fifteen.
    [CollectionDefinition("Settings")]
    public class SettingsCollection
    {
    }
}
