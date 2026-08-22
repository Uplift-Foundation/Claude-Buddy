using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace ClaudeBuddy.Tests;

// Runs once, before any test in this assembly, no matter which test class
// happens to run first. ClaudeBuddySettings.Directory reads
// CLAUDE_BUDDY_SETTINGS_DIR when it's set and falls back to the developer's
// real %APPDATA%/~/Library/... path otherwise — any test that touches a
// ClaudeBuddySettings property (even indirectly, e.g. via OrbColors or
// ClaudeDesktopColors, both of which read through to settings) would
// otherwise read and possibly overwrite Warren's real settings.json.
internal static class TestBootstrap
{
    [ModuleInitializer]
    public static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-unittests-" + Guid.NewGuid());
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
    }
}
