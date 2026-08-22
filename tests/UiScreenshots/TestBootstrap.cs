using System.IO;
using System.Runtime.CompilerServices;

namespace ClaudeBuddy.Tests;

// Same seam as tests/UiTests/TestBootstrap.cs, for the same reason: OrbWindow
// and OrbFlyout read settings-backed colors the moment they're constructed
// (OrbWindow's _orbBrush field initializer, OrbColors.Idle), so this points
// CLAUDE_BUDDY_SETTINGS_DIR at a private scratch directory before that can
// happen, or this suite reads and writes the developer's real settings.json.
internal static class TestBootstrap
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "cb-uiscreenshots-" + Guid.NewGuid());
        Directory.CreateDirectory(scratch);

        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", scratch);

        ClaudeBuddySettings.ReloadForTests();
    }
}
