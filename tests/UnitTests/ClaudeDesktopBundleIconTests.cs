using System.IO;
using Xunit;

namespace ClaudeBuddy.Tests;

// The evidence that a tinted Dock icon actually went on.
//
// This is the check that repairs CB-4's third root cause. The old code wrote
// its "this clone is colour X" marker *before* asking macOS to set the icon, so
// a refusal — the App Management privacy permission, which an ad-hoc rebuild
// invalidates — was recorded as a success. Ensure() then saw a matching marker
// for ever and never rebuilt, so tinting stayed broken even after the user
// granted permission. Found exactly that way on a real machine: a marker
// written minutes earlier beside a bundle with no Icon\r file at all.
public class ClaudeDesktopBundleIconTests
{
    [Fact]
    public void HasCustomIcon_IsFalseForABundleWithNoIconResource()
    {
        // The broken state: the clone exists, but nothing was ever written to
        // it. Reading this as "already the right colour" is the bug.
        var bundle = Path.Combine(Path.GetTempPath(), "cb-icon-" + Guid.NewGuid(), "Claude.app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents"));

        Assert.False(ClaudeDesktopBundles.HasCustomIcon(bundle));
    }

    [Fact]
    public void HasCustomIcon_IsTrueOnlyForTheCarriageReturnName()
    {
        var bundle = Path.Combine(Path.GetTempPath(), "cb-icon-" + Guid.NewGuid(), "Claude.app");
        Directory.CreateDirectory(bundle);

        // A plain "Icon" is not what macOS looks for, and treating it as one
        // would reintroduce the same false positive from a different direction.
        File.WriteAllText(Path.Combine(bundle, "Icon"), "");
        Assert.False(ClaudeDesktopBundles.HasCustomIcon(bundle));

        File.WriteAllText(Path.Combine(bundle, "Icon\r"), "");
        Assert.True(ClaudeDesktopBundles.HasCustomIcon(bundle));
    }

    [Fact]
    public void HasCustomIcon_IsFalseRatherThanThrowingForAPathThatIsNotThere()
    {
        // Called from ColourMatches, which is called from Ensure on a launch
        // path: an exception here would cost the launch, not just the colour.
        Assert.False(ClaudeDesktopBundles.HasCustomIcon(
            Path.Combine(Path.GetTempPath(), "cb-icon-missing-" + Guid.NewGuid(), "Claude.app")));
    }
}
