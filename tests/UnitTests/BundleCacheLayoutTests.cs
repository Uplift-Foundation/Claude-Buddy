using System;
using System.IO;
using Avalonia.Media;
using ClaudeBuddy.Tests;
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

    // ---- IsStaleVersion --------------------------------------------------

    // The rule IsStale applies once plutil has read both Info.plists. Pure, so
    // it is testable without a bundle on disk — which is the whole reason it was
    // pulled out of IsStale rather than left inside the excluded half.

    [Fact]
    public void SameVersionIsNotStale()
    {
        Assert.False(ClaudeDesktopBundles.IsStaleVersion("1.34493.1", "1.34493.1"));
    }

    [Fact]
    public void AnInstalledBundleThatHasMovedAheadMakesTheCloneStale()
    {
        Assert.True(ClaudeDesktopBundles.IsStaleVersion("1.34493.1", "1.37937.0"));
    }

    // The case that made this a comparison rather than an inequality. Claude
    // Desktop's Squirrel updater updates whichever bundle is running, and the
    // one that is running is usually a clone — measured on a real machine with
    // /Applications at 1.34493.1 and the Claude-Board clone already at
    // 1.37937.0. Answering "stale" there rebuilds the clone from the older
    // installed bundle and downgrades that profile, then opens userData written
    // by a newer Chromium with an older one.
    [Fact]
    public void ACloneAheadOfTheInstalledBundleIsLeftAlone()
    {
        Assert.False(ClaudeDesktopBundles.IsStaleVersion("1.37937.0", "1.34493.1"));
    }

    // Ordering is numeric, not lexical. Every one of these reverses under a
    // string comparison, which is why the old rule could not simply have had a
    // `<` put in front of it.
    [Theory]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.34493.1", "1.134493.0")]
    [InlineData("2.0.0", "10.0.0")]
    public void AHigherComponentBeatsALongerString(string clone, string source)
    {
        Assert.True(ClaudeDesktopBundles.IsStaleVersion(clone, source));
        Assert.False(ClaudeDesktopBundles.IsStaleVersion(source, clone));
    }

    // A missing trailing component is zero, not a difference — so a dropped
    // ".0" does not cost a 753MB re-clone, in either direction.
    [Fact]
    public void MissingTrailingComponentsCountAsZero()
    {
        Assert.False(ClaudeDesktopBundles.IsStaleVersion("1.2", "1.2.0"));
        Assert.False(ClaudeDesktopBundles.IsStaleVersion("1.2.0", "1.2"));
        Assert.True(ClaudeDesktopBundles.IsStaleVersion("1.2", "1.2.1"));
        Assert.False(ClaudeDesktopBundles.IsStaleVersion("1.2.1", "1.2"));
    }

    // A clone whose Info.plist could not be read is not one worth keeping, and
    // neither is a clone whose source could not be read — that is a source path
    // the caller is wrong about, and rebuilding is the recoverable answer.
    [Theory]
    [InlineData(null, "1.34493.1")]
    [InlineData("1.34493.1", null)]
    [InlineData(null, null)]
    public void AVersionThatCouldNotBeReadIsStale(string? clone, string? source)
    {
        Assert.True(ClaudeDesktopBundles.IsStaleVersion(clone, source));
    }

    // With no ordering to appeal to, the old inequality rule is the honest
    // answer: different means stale, identical does not. It errs towards
    // rebuilding, which is the direction that undoes.
    [Theory]
    [InlineData("1.0.0-beta", "1.0.0")]
    [InlineData("1.0.0", "1.0.0-beta")]
    [InlineData("", "1.0.0")]
    [InlineData("1.-2.0", "1.2.0")]
    [InlineData("1. 2.0", "1.2.0")]
    public void AnUnparseableVersionFallsBackToPlainInequality(string clone, string source)
    {
        Assert.True(ClaudeDesktopBundles.IsStaleVersion(clone, source));
    }

    [Fact]
    public void TwoIdenticalUnparseableVersionsAreNotStale()
    {
        Assert.False(ClaudeDesktopBundles.IsStaleVersion("1.0.0-beta", "1.0.0-beta"));
    }

    // ---- ColourMatches ---------------------------------------------------

    [Fact]
    public void NoMarkerFileMeansTheColourDoesNotMatch()
    {
        Assert.False(ClaudeDesktopBundles.ColourMatches("alpha", Colors.Red));
    }

    // A matching marker is necessary and no longer sufficient. The marker says
    // which colour was *intended*; the Icon\r file is whether one actually went
    // on, and a clone left by the older ordering bug has the first without the
    // second — it recorded a refusal as a success, so Ensure() never rebuilt it
    // and the tint never retried even once App Management was granted.
    //
    // Cross-platform, because it is the absence of a file that is being
    // asserted and no fixture with an awkward name has to be created.
    [Fact]
    public void AMatchingMarkerWithNoIconOnDiskDoesNotMatch()
    {
        WriteMarker("alpha", Colors.Red.ToString());

        Assert.False(ClaudeDesktopBundles.ColourMatches("alpha", Colors.Red));
    }

    // macOS only, and not because the rule is: NTFS rejects a carriage return in
    // a filename outright, so this test cannot stage its own fixture on Windows
    // — the same reason ClaudeDesktopBundleIconTests splits its case in two, and
    // the same skip-rather-than-omit pattern.
    [MacOnlyFact]
    public void AMarkerHoldingTheSameColourMatchesOnceTheIconIsThere()
    {
        WriteMarker("alpha", Colors.Red.ToString());
        WriteIcon("alpha");

        Assert.True(ClaudeDesktopBundles.ColourMatches("alpha", Colors.Red));
    }

    // The icon is present here, so this is the colour check failing rather than
    // the icon check — which is the point of writing one.
    [MacOnlyFact]
    public void AMarkerHoldingADifferentColourDoesNotMatch()
    {
        WriteMarker("alpha", Colors.Red.ToString());
        WriteIcon("alpha");

        Assert.False(ClaudeDesktopBundles.ColourMatches("alpha", Colors.Blue));
    }

    // Trimmed, so a marker written with a trailing newline — which any text
    // editor or shell redirect will do — is not read as a different colour and
    // does not trigger a 753MB re-clone.
    [MacOnlyFact]
    public void SurroundingWhitespaceInTheMarkerIsIgnored()
    {
        WriteMarker("alpha", "\n  " + Colors.Red + "  \n");
        WriteIcon("alpha");

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

    // ---- PlanFor ---------------------------------------------------------
    //
    // The rule that decides what Ensure() does with a clone already on disk.
    // It used to be one `&&` over these same three facts, which meant a missing
    // icon was answered with a full re-clone from /Applications — and since
    // Squirrel updates the *clone* and never /Applications, that re-clone was a
    // downgrade. These cases exist to keep the three answers distinct.

    [Fact]
    public void NothingOnDiskIsRebuilt()
    {
        Assert.Equal(
            ClaudeDesktopBundles.CloneAction.Rebuild,
            ClaudeDesktopBundles.PlanFor(exists: false, stale: false, colourMatches: false));
    }

    [Fact]
    public void ACorrectCloneIsLeftAlone()
    {
        Assert.Equal(
            ClaudeDesktopBundles.CloneAction.Reuse,
            ClaudeDesktopBundles.PlanFor(exists: true, stale: false, colourMatches: true));
    }

    // A clone genuinely behind the installed bundle is the one case a rebuild
    // is right for: it is the only direction that is an upgrade.
    [Fact]
    public void ACloneBehindTheInstalledBundleIsRebuilt()
    {
        Assert.Equal(
            ClaudeDesktopBundles.CloneAction.Rebuild,
            ClaudeDesktopBundles.PlanFor(exists: true, stale: true, colourMatches: true));
    }

    // The regression this whole change exists for. A clone that Squirrel has
    // just self-updated is newer than /Applications and has lost its "Icon\r"
    // to the bundle swap — exists, not stale, colour does not match. Answering
    // Rebuild there threw away the update and pinned the user to the older
    // version; the app's own log recorded it as
    // "Version changed since last launch: 1.40609.0 -> 1.37937.0".
    [Fact]
    public void AnUpdatedCloneWithNoIconIsRepaintedNotRebuilt()
    {
        Assert.Equal(
            ClaudeDesktopBundles.CloneAction.Repaint,
            ClaudeDesktopBundles.PlanFor(exists: true, stale: false, colourMatches: false));
    }

    // Staleness outranks the colour: a clone that is both behind and wrongly
    // coloured needs the newer bundle, and rebuilding repaints it anyway.
    [Fact]
    public void StalenessOutranksTheColour()
    {
        Assert.Equal(
            ClaudeDesktopBundles.CloneAction.Rebuild,
            ClaudeDesktopBundles.PlanFor(exists: true, stale: true, colourMatches: false));
    }

    // The file macOS actually looks at for a custom Finder icon: "Icon"
    // followed by a carriage return, at the bundle root rather than inside
    // Contents/, which is what keeps the code signature intact.
    private static void WriteIcon(string profileFolder)
    {
        var bundle = ClaudeDesktopBundles.PathFor(profileFolder);
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "Icon\r"), "");
    }

    private void WriteMarker(string profileFolder, string contents)
    {
        var dir = ClaudeDesktopBundles.DirectoryFor(profileFolder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "icon-colour"), contents);
    }
}
