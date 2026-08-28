using System;
using System.IO;
using ClaudeBuddy;
using Xunit;

namespace ClaudeBuddy.IntegrationTests;

// CB-7's rule reads a path that another part of this app writes, and the two
// have to agree about a layout neither of them states in one place.
// ClaudeDesktopBundles decides where a clone lives; OrphanedCloneFolder parses
// that path back into a profile name off a *running process*, where the string
// arrives from proc_pidpath rather than from the function that built it.
//
// The unit tests cover the parse against hand-written paths, which is where the
// awkward cases live. This is the other half CLAUDE.md asks for: the same rule
// against a real directory tree, created the way the app creates one, through
// the CLAUDE_BUDDY_BUNDLE_ROOT seam. The two fail differently — a parser gets an
// edge case wrong, a seam gets the whole layout wrong — and a layout change in
// ClaudeDesktopBundles would leave every unit test green while every real
// instance stopped being recognised.
// Own collection, mirroring BundleRootCollection in tests/UnitTests and for the
// same reason: this class moves CLAUDE_BUDDY_BUNDLE_ROOT, which is process-wide,
// and xUnit runs collections in parallel within one assembly. Anything else
// reading ClaudeDesktopBundles.Root while this class has it pointed at a temp
// directory would see a root that vanishes from under it when Dispose deletes
// the tree — a failure that appears in a full-suite run and never in a filtered
// one, which is the shape of every flake this repo has had to chase.
//
// A separate definition rather than a shared one because collections do not
// cross assemblies; the name matches the unit-test side so the two read as the
// same lane even though xUnit treats them as unrelated.
[CollectionDefinition("BundleRoot")]
public sealed class IntegrationBundleRootCollection
{
}

[Collection("BundleRoot")]
public class OrphanedCloneLayoutTests : IDisposable
{
    private const string RootVariable = "CLAUDE_BUDDY_BUNDLE_ROOT";

    private readonly string? _before = Environment.GetEnvironmentVariable(RootVariable);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cb-orphan-" + Guid.NewGuid().ToString("N"));

    public OrphanedCloneLayoutTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(RootVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RootVariable, _before);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // The round trip that matters: the path ClaudeDesktopBundles would put a
    // clone at, parsed back into the profile it belongs to.
    [Fact]
    public void ThePathBundlesWritesIsThePathTheRuleReadsBack()
    {
        var clone = ClaudeDesktopBundles.PathFor("Claude-Board");
        Directory.CreateDirectory(clone);

        Assert.Equal(
            "Claude-Board",
            ClaudeDesktopManager.OrphanedCloneFolder(
                new ClaudeInstance(26126, null, clone), ClaudeDesktopBundles.Root, "Claude"));
    }

    // Default's clone lives in the same place and must still come back as not
    // stranded — this is the false positive that would have reported the most
    // common configuration on a real machine as broken.
    [Fact]
    public void DefaultsCloneInTheSameTreeIsStillNotStranded()
    {
        var clone = ClaudeDesktopBundles.PathFor("Claude");
        Directory.CreateDirectory(clone);

        Assert.Null(ClaudeDesktopManager.OrphanedCloneFolder(
            new ClaudeInstance(451, null, clone), ClaudeDesktopBundles.Root, "Claude"));
    }

    // A profile whose name contains a space, which the profile scanner allows
    // because profiles are folders the user can create by hand. The bundle root
    // itself already contains one on every real machine — "Application Support".
    [Fact]
    public void AProfileNameWithASpaceSurvivesTheRoundTrip()
    {
        var clone = ClaudeDesktopBundles.PathFor("Claude-two words");
        Directory.CreateDirectory(clone);

        Assert.Equal(
            "Claude-two words",
            ClaudeDesktopManager.OrphanedCloneFolder(
                new ClaudeInstance(7, null, clone), ClaudeDesktopBundles.Root, "Claude"));
    }

    // Exists() is how the URL router decides a profile has a clone at all, so if
    // it and the rule ever disagreed about the layout, a link would be addressed
    // to a bundle nothing is recognised as running from.
    [Fact]
    public void ExistsAgreesWithWhatTheRuleParses()
    {
        Assert.False(ClaudeDesktopBundles.Exists("Claude-Board"));

        Directory.CreateDirectory(ClaudeDesktopBundles.PathFor("Claude-Board"));

        // Exists is macOS-gated; the parse is not, and does not need to be.
        if (OperatingSystem.IsMacOS()) Assert.True(ClaudeDesktopBundles.Exists("Claude-Board"));

        Assert.Equal(
            "Claude-Board",
            ClaudeDesktopManager.OrphanedCloneFolder(
                new ClaudeInstance(1, null, ClaudeDesktopBundles.PathFor("Claude-Board")),
                ClaudeDesktopBundles.Root,
                "Claude"));
    }
}
