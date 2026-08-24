using System;
using System.IO;
using Avalonia.Media;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// ClaudeDesktopBundles' view of its own cache: where a profile's cloned
// Claude.app goes, whether one is there, and whether the icon colour on disk is
// still the colour that was asked for.
//
// The cloning itself is excluded — it copies a 753MB bundle with /bin/cp -Rc,
// shells out to codesign, reads a binary Info.plist through plutil and sends
// setIcon:forFile:options: to NSWorkspace. None of that is reachable from a
// headless runner, and none of it should be attempted on the machine running the
// tests. What IS testable is the layout and the staleness questions around it,
// which is where a mistake silently costs running-detection for every cloned
// instance (see the comment on the class: the bundle has to be named exactly
// "Claude.app" or MacOSProcessScan's path-suffix match stops recognising it).
//
// Own collection, not the Settings one: these tests move an environment variable
// rather than the settings model, so they must not run concurrently with each
// other, but they have no reason to serialise behind the settings lane.
[CollectionDefinition("BundleRoot")]
public sealed class BundleRootCollection
{
}

[Collection("BundleRoot")]
public class BundleCacheLayoutTests : IDisposable
{
    private const string Override = "CLAUDE_BUDDY_BUNDLE_ROOT";

    private readonly string? _previous = Environment.GetEnvironmentVariable(Override);
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "cb-bundles-" + Guid.NewGuid().ToString("n")[..12]);

    public BundleCacheLayoutTests()
    {
        Directory.CreateDirectory(_scratch);
        Environment.SetEnvironmentVariable(Override, _scratch);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Override, _previous);
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true);
    }

    // ---- the override itself --------------------------------------------

    // Without this the only place these tests could write is the real cache, on
    // the machine running them, holding real bundles whose icons a user is
    // looking at.
    [Fact]
    public void RootFollowsTheScratchOverride()
    {
        Assert.Equal(_scratch, ClaudeDesktopBundles.Root);
    }

    [Fact]
    public void RootFallsBackToApplicationSupportWhenNothingIsSet()
    {
        Environment.SetEnvironmentVariable(Override, null);

        Assert.Contains("ClaudeBuddy", ClaudeDesktopBundles.Root);
        Assert.EndsWith("bundles", ClaudeDesktopBundles.Root);
    }

    // An empty string is not an override — { Length: > 0 } — because an
    // exported-but-empty variable is a common shell accident and would otherwise
    // relocate the cache to the process's working directory.
    [Fact]
    public void AnEmptyOverrideIsIgnoredRatherThanUsed()
    {
        Environment.SetEnvironmentVariable(Override, "");

        Assert.NotEqual("", ClaudeDesktopBundles.Root);
        Assert.EndsWith("bundles", ClaudeDesktopBundles.Root);
    }

    // ---- layout ----------------------------------------------------------

    // The name is load-bearing. MacOSProcessScan matches a running instance on
    // the path suffix "/Claude.app/Contents/MacOS/Claude", so naming the clone
    // after the profile would leave every cloned instance looking like it was
    // not running at all.
    [Fact]
    public void TheCloneIsAlwaysNamedClaudeAppInsideAPerProfileDirectory()
    {
        var path = ClaudeDesktopBundles.PathFor("Claude-Profile-2");

        Assert.Equal("Claude.app", Path.GetFileName(path));
        Assert.Equal("Claude-Profile-2", Path.GetFileName(Path.GetDirectoryName(path)!));
        Assert.Equal(ClaudeDesktopBundles.DirectoryFor("Claude-Profile-2"),
            Path.GetDirectoryName(path));
    }

    [Fact]
    public void EachProfileGetsItsOwnDirectoryUnderRoot()
    {
        var one = ClaudeDesktopBundles.DirectoryFor("alpha");
        var two = ClaudeDesktopBundles.DirectoryFor("beta");

        Assert.NotEqual(one, two);
        Assert.StartsWith(ClaudeDesktopBundles.Root, one);
        Assert.StartsWith(ClaudeDesktopBundles.Root, two);
    }

    // ---- Exists ----------------------------------------------------------

    [Fact]
    public void NoCloneMeansItDoesNotExist()
    {
        Assert.False(ClaudeDesktopBundles.Exists("never-cloned"));
    }

    // Exists is gated on macOS as well as on the directory, because a cloned
    // bundle is a macOS concept — so on Windows the answer is false even with the
    // directory sitting right there. Asserted against the platform rather than a
    // literal, so this says the same thing on both CI runners.
    [Fact]
    public void ADirectoryThatIsThereCountsOnlyOnMacOS()
    {
        Directory.CreateDirectory(ClaudeDesktopBundles.PathFor("cloned"));

        Assert.Equal(OperatingSystem.IsMacOS(), ClaudeDesktopBundles.Exists("cloned"));
    }

    // ---- IsStaleFor ------------------------------------------------------

    // Short-circuits on Exists, which is what makes this callable at all from a
    // test: the other half reads a binary Info.plist by shelling out to plutil,
    // and a suite has no business spawning that.
    [Fact]
    public void AProfileWithNoCloneIsNotStale()
    {
        Assert.False(ClaudeDesktopBundles.IsStaleFor("never-cloned", "/Applications/Claude.app"));
    }

    // ---- ColourMatches ---------------------------------------------------

    [Fact]
    public void NoMarkerFileMeansTheColourDoesNotMatch()
    {
        Assert.False(ClaudeDesktopBundles.ColourMatches("alpha", Colors.Red));
    }

    [Fact]
    public void AMarkerHoldingTheSameColourMatches()
    {
        WriteMarker("alpha", Colors.Red.ToString());

        Assert.True(ClaudeDesktopBundles.ColourMatches("alpha", Colors.Red));
    }

    [Fact]
    public void AMarkerHoldingADifferentColourDoesNotMatch()
    {
        WriteMarker("alpha", Colors.Red.ToString());

        Assert.False(ClaudeDesktopBundles.ColourMatches("alpha", Colors.Blue));
    }

    // Trimmed, so a marker written with a trailing newline — which any text
    // editor or shell redirect will do — is not read as a different colour and
    // does not trigger a 753MB re-clone.
    [Fact]
    public void SurroundingWhitespaceInTheMarkerIsIgnored()
    {
        WriteMarker("alpha", "\n  " + Colors.Red + "  \n");

        Assert.True(ClaudeDesktopBundles.ColourMatches("alpha", Colors.Red));
    }

    // A directory standing where the marker file should be. Note this does NOT
    // reach the catch: File.Exists is false for a directory, so the && short
    // circuits. Kept because it is a real state — an interrupted clone can leave
    // one — and because it is the case the next person will assume covers the
    // catch. It does not; the test below does.
    [Fact]
    public void ADirectoryWhereTheMarkerShouldBeIsTreatedAsNotMatching()
    {
        Directory.CreateDirectory(
            Path.Combine(ClaudeDesktopBundles.DirectoryFor("alpha"), "icon-colour"));

        Assert.False(ClaudeDesktopBundles.ColourMatches("alpha", Colors.Red));
    }

    // The catch itself: a marker that exists and cannot be read. Without it, an
    // unreadable file in the cache would throw out of a launch path.
    //
    // Arranged per platform because there is no one portable way to make a file
    // unreadable — Unix has mode bits, Windows has share modes, and neither
    // works on the other. What is being asserted is the same on both.
    [Fact]
    public void AMarkerThatCannotBeReadIsTreatedAsNotMatching()
    {
        WriteMarker("alpha", Colors.Red.ToString());
        var marker = Path.Combine(ClaudeDesktopBundles.DirectoryFor("alpha"), "icon-colour");

        if (OperatingSystem.IsWindows())
        {
            // Hold the only handle with no sharing; ReadAllText then fails.
            using var exclusive = new FileStream(
                marker, FileMode.Open, FileAccess.Read, FileShare.None);

            Assert.False(ClaudeDesktopBundles.ColourMatches("alpha", Colors.Red));
        }
        else
        {
            File.SetUnixFileMode(marker, UnixFileMode.None);
            try
            {
                Assert.False(ClaudeDesktopBundles.ColourMatches("alpha", Colors.Red));
            }
            finally
            {
                // Readable again, or Dispose cannot delete the scratch directory.
                File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    // ---- IconApplied -----------------------------------------------------

    // Starts true and only goes false when macOS refuses an icon write, so a
    // machine that has never cloned anything does not show a warning.
    [Fact]
    public void IconAppliedStartsOutTrue()
    {
        Assert.True(ClaudeDesktopBundles.IconApplied);
    }

    private void WriteMarker(string profileFolder, string contents)
    {
        var dir = ClaudeDesktopBundles.DirectoryFor(profileFolder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "icon-colour"), contents);
    }
}
