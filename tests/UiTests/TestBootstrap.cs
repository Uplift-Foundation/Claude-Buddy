using System.IO;
using System.Runtime.CompilerServices;

namespace ClaudeBuddy.Tests;

// Runs before any test in this assembly touches a single line of app code.
//
// ClaudeBuddySettings.Directory is a static property read lazily, but the
// model behind it (ClaudeBuddySettings.Load) is cached for the process once
// read — see ClaudeBuddySettings.ReloadForTests's own comment. The danger
// this heads off is real and cheap to hit by accident: OrbWindow's
// constructor has a field initializer,
// `private readonly SolidColorBrush _orbBrush = new(OrbColors.Idle);`,
// which reads ClaudeBuddySettings.IdleColor the moment an OrbWindow is
// constructed — before a test method's own body runs a single statement.
// Point CLAUDE_BUDDY_SETTINGS_DIR at a fresh, private scratch directory
// before that happens, or the very first OrbWindow built anywhere in this
// suite reads (and, on a save, writes) the developer's real settings.json.
//
// [ModuleInitializer] runs once per assembly load, ahead of any test
// collection, and ahead of the Avalonia headless bootstrap in
// TestAppBuilder — both of which is required, since AppBuilder.Configure
// touches ClaudeBuddySettings too (App.axaml.cs reads it while composing
// styles).
internal static class TestBootstrap
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "cb-uitests-" + Guid.NewGuid());
        Directory.CreateDirectory(scratch);

        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", scratch);

        ClaudeBuddySettings.ReloadForTests();
    }
}
