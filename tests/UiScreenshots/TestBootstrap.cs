using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

// Same reasoning as tests/UiTests/TestBootstrap.cs's identical line: every
// [AvaloniaFact] here shares one Avalonia application and one headless
// dispatcher, and xUnit's default of running different test classes as
// parallel collections means real concurrent Window construction against
// that one shared dispatcher — a plausible contributor to the FontManager
// race TestAppBuilder's warm-up also guards against. One piece of several,
// not a fix by itself; costs nothing given how fast this suite runs.
//
// Pinned to xunit.v3 3.2.2 (see the csproj's own comment on why — Avalonia.
// Headless.XUnit 12.1.1 depends on xunit.v3.extensibility.core 3.2.2
// specifically, and a newer 4.0.0 causes a binary-incompatible
// MissingMethodException at test discovery). DisableTestParallelization is
// only obsolete starting in xunit.v3 4.0.0's Xunit.v3.ParallelizationAttribute
// — not available yet at 3.2.2 — so this stays on the older spelling.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

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

        // ...and no test in this assembly may start a real relay: that is a live
        // Claude Code session in tmux, on the developer's own account, holding a
        // relay name the installed app also wants. Set here rather than trusted
        // to call discipline because CB-42 proved the discipline was already
        // broken and nobody could tell — the call was dormant only because the
        // relay it started always failed. See RemoteControlSessions.StartsBlocked.
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_NO_RELAY", "1");

        ClaudeBuddySettings.ReloadForTests();
    }
}
