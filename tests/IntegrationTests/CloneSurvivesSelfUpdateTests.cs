using System;
using System.IO;
using Avalonia.Media;
using ClaudeBuddy;
using ClaudeBuddy.Tests;
using Xunit;

namespace ClaudeBuddy.IntegrationTests;

// The downgrade loop, pinned against real directories.
//
// BundleCacheLayoutTests covers PlanFor, which is the rule. This is the other
// half CLAUDE.md asks for, and here the two really do fail differently: the rule
// can be perfectly right while Ensure() reads the wrong facts off disk or acts
// on the answer by deleting the bundle anyway. What broke for three days was not
// a wrong rule — IsStaleVersion had a correct one and a passing test — it was
// that the wiring never asked it, because three conditions shared one `&&`.
//
// So the assertion here is deliberately about the *bundle on disk*, not about
// an enum: after Ensure(), a clone that Squirrel has self-updated still has the
// version Squirrel gave it. That is the sentence the user cares about, and it is
// the one no unit test can make.
//
// The bundles are stubs — an Info.plist and nothing else. That is enough,
// because the whole decision is CFBundleVersion plus the presence of "Icon\r",
// and it keeps this away from /bin/cp -Rc on a 753MB tree and away from
// NSWorkspace: with no .icns under Contents/Resources, ApplyTintedIcon returns
// before it shells out to sips or asks macOS to set an icon. A test that needed
// App Management consent to pass would be a test nobody could run.
//
// Own collection for the same reason OrphanedCloneLayoutTests has one: this
// moves CLAUDE_BUDDY_BUNDLE_ROOT, which is process-wide.
[Collection("BundleRoot")]
public class CloneSurvivesSelfUpdateTests : IDisposable
{
    private const string RootVariable = "CLAUDE_BUDDY_BUNDLE_ROOT";

    private readonly string? _before = Environment.GetEnvironmentVariable(RootVariable);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cb-selfupdate-" + Guid.NewGuid().ToString("N"));
    private readonly string _installed;

    public CloneSurvivesSelfUpdateTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(RootVariable, _root);

        // Stands in for /Applications/Claude.app: the bundle Squirrel never
        // updates, because the bundle that is running is always a clone.
        _installed = Path.Combine(_root, "installed", "Claude.app");
        WriteBundle(_installed, "1.37937.0");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RootVariable, _before);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // The regression. A clone Squirrel has just updated is newer than the
    // installed bundle and has lost its "Icon\r" to the bundle swap. Ensure()
    // used to answer that by deleting it and re-cloning from the installed
    // bundle, which is older — so launching a profile silently rolled Claude
    // Desktop back, the update downloaded again, and the loop ran for days.
    [MacFact]
    public void ASelfUpdatedCloneIsNotRolledBackToTheInstalledVersion()
    {
        var clone = ClaudeDesktopBundles.PathFor("Claude-Board");
        WriteBundle(clone, "1.40609.0");
        WriteMarker("Claude-Board", "#ff875fd7");
        // No Icon\r: Squirrel moved a fresh bundle into place over the one that
        // had it.

        ClaudeDesktopBundles.Ensure("Claude-Board", _installed, Color.Parse("#ff875fd7"));

        Assert.Equal("1.40609.0", VersionOf(clone));
    }

    // The same launch must still repair the icon, or the profile loses its
    // colour permanently the first time it updates itself.
    [MacFact]
    public void TheRepaintPathLeavesTheBundleInPlaceToBeRecoloured()
    {
        var clone = ClaudeDesktopBundles.PathFor("Claude-Board");
        WriteBundle(clone, "1.40609.0");
        WriteMarker("Claude-Board", "#ff875fd7");

        var result = ClaudeDesktopBundles.Ensure(
            "Claude-Board", _installed, Color.Parse("#ff875fd7"));

        // Repaint returns the clone rather than null, so the caller launches the
        // coloured bundle and not the installed one.
        Assert.Equal(clone, result);
        Assert.True(Directory.Exists(clone));
    }

    // The direction a rebuild IS right for: a clone genuinely behind the
    // installed bundle. Fixing the downgrade must not cost the upgrade — this is
    // the case IsStale was written for, and it still has to reach it.
    [MacFact]
    public void ACloneBehindTheInstalledBundleIsStillRebuilt()
    {
        var clone = ClaudeDesktopBundles.PathFor("Claude-Board");
        WriteBundle(clone, "1.34493.1");
        WriteMarker("Claude-Board", "#ff875fd7");

        ClaudeDesktopBundles.Ensure("Claude-Board", _installed, Color.Parse("#ff875fd7"));

        Assert.Equal("1.37937.0", VersionOf(clone));
    }

    // An up-to-date, correctly coloured clone is left completely alone — the
    // common case, and the one that must not start doing disk work every launch.
    [MacFact]
    public void AMatchingCloneIsUntouched()
    {
        var clone = ClaudeDesktopBundles.PathFor("Claude-Board");
        WriteBundle(clone, "1.37937.0");
        WriteMarker("Claude-Board", "#ff875fd7");
        File.WriteAllText(Path.Combine(clone, "Icon\r"), "");

        var stamp = File.GetLastWriteTimeUtc(Path.Combine(clone, "Contents", "Info.plist"));

        var result = ClaudeDesktopBundles.Ensure(
            "Claude-Board", _installed, Color.Parse("#ff875fd7"));

        Assert.Equal(clone, result);
        Assert.Equal(
            stamp, File.GetLastWriteTimeUtc(Path.Combine(clone, "Contents", "Info.plist")));
    }

    // plutil reads either form, so an XML plist is a fair stand-in for the
    // binary one a real bundle carries.
    private static void WriteBundle(string bundle, string version)
    {
        Directory.CreateDirectory(Path.Combine(bundle, "Contents"));
        File.WriteAllText(
            Path.Combine(bundle, "Contents", "Info.plist"),
            $"""
             <?xml version="1.0" encoding="UTF-8"?>
             <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
             <plist version="1.0">
             <dict>
               <key>CFBundleVersion</key>
               <string>{version}</string>
               <key>CFBundleShortVersionString</key>
               <string>{version}</string>
             </dict>
             </plist>
             """);
    }

    private static string? VersionOf(string bundle)
    {
        var text = File.ReadAllText(Path.Combine(bundle, "Contents", "Info.plist"));
        var open = text.IndexOf("<string>", StringComparison.Ordinal) + "<string>".Length;
        var close = text.IndexOf("</string>", open, StringComparison.Ordinal);
        return text[open..close];
    }

    private static void WriteMarker(string profileFolder, string colour)
    {
        var dir = ClaudeDesktopBundles.DirectoryFor(profileFolder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "icon-colour"), colour);
    }
}
