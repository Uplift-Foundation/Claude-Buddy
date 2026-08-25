using Xunit;

namespace ClaudeBuddy.Tests
{
    // The package family name that goes into Claude Desktop's AUMID.
    //
    // The only pure function in WindowsAppLookup, and worth reaching because
    // getting it wrong fails silently: a malformed AUMID is not rejected with an
    // error, IApplicationActivationManager simply activates nothing.
    //
    // WindowsAppLookup carries [SupportedOSPlatform("windows")] at the class,
    // which is an analyser hint about the registry and manifest work the rest of
    // it does. This function is string splitting with no platform dependency of
    // its own, so it runs and is asserted on both CI legs rather than being
    // skipped on the one where a developer is most likely to break it — hence the
    // suppression below rather than a [WindowsFact], which would have left this
    // untested on macOS for no real reason.
#pragma warning disable CA1416
    public class WindowsAppLookupTests
    {
        // A real package full name: name, version, architecture, resource id and
        // publisher hash, joined by underscores. The family name is the first
        // and last of those and nothing in between — the middle parts change with
        // every update, which is the whole reason the family name exists.
        [Fact]
        public void TheFamilyNameIsTheNameAndThePublisherHash()
        {
            Assert.Equal(
                "Claude_8wekyb3d8bbwe",
                WindowsAppLookup.FamilyNameFromFullName("Claude_1.2.3.0_x64__8wekyb3d8bbwe"));
        }

        [Fact]
        public void EverythingBetweenTheEndsIsDropped()
        {
            Assert.Equal("A_E", WindowsAppLookup.FamilyNameFromFullName("A_B_C_D_E"));
        }

        // The empty resource-id segment in a real full name produces a double
        // underscore, and it must not be mistaken for the publisher hash.
        [Fact]
        public void AnEmptyResourceIdSegmentIsNotTheLastSegment()
        {
            Assert.Equal(
                "AnthropicClaude_4mzk8j1sv1ndm",
                WindowsAppLookup.FamilyNameFromFullName(
                    "AnthropicClaude_0.14.5.0_x64__4mzk8j1sv1ndm"));
        }

        // Two segments is the minimum that can produce a family name, pinned so
        // the boundary is not quietly moved to three.
        [Fact]
        public void TwoSegmentsAreEnough()
        {
            Assert.Equal("A_B", WindowsAppLookup.FamilyNameFromFullName("A_B"));
        }

        // Not a package full name at all. Null is the honest answer: composing
        // one anyway would produce an AUMID that activates nothing, which is
        // indistinguishable from the app not being installed.
        [Theory]
        [InlineData("OnlyOneSegment")]
        [InlineData("")]
        public void SomethingThatIsNotAPackageFullNameIsNull(string fullName)
        {
            Assert.Null(WindowsAppLookup.FamilyNameFromFullName(fullName));
        }
    }
#pragma warning restore CA1416
}
