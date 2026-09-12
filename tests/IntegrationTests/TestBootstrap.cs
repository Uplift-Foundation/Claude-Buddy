using System.Runtime.CompilerServices;

namespace ClaudeBuddy.Tests;

// Runs once, before any test in this assembly, and before the first access
// to ClaudeBuddySettings from any of them. Points the settings store at a
// throwaway directory instead of the real
// %APPDATA%/ClaudeBuddy (Windows) or ~/Library/Application Support/ClaudeBuddy
// (macOS) — settings.json does not follow HOME on macOS, so without this an
// integration test run would read and could overwrite the developer's own
// settings.
//
// One directory for the whole assembly, not one per test. Most tests here
// never touch ClaudeBuddySettings at all (hook-script and TranscriptReader
// tests are pure file/process tests), and the ones that do are the P1
// SettingsRoundTripTests, which are collected under [Collection("Settings")]
// and repoint this env var themselves before calling ReloadForTests() — see
// that class for why sharing the static model safely needs both.
internal static class TestBootstrap
{
    [ModuleInitializer]
    public static void Init()
    {
        Environment.SetEnvironmentVariable(
            "CLAUDE_BUDDY_SETTINGS_DIR",
            Path.Combine(Path.GetTempPath(), "cb-integrationtests-" + Guid.NewGuid()));

        // StatusDirectory.Path() — where ClaudeBuddySettings.LogFailure writes
        // settings-errors.log — honors TMPDIR rather than
        // CLAUDE_BUDDY_SETTINGS_DIR (see StatusDirectory.Root's own comment:
        // it's the seam a test uses to get its own sandbox). Left unset, every
        // suite run appended Save/Load failure traces to the real
        // $TMPDIR/claude_buddy/settings-errors.log on the developer's machine —
        // a user-facing diagnostic file growing without bound from test noise
        // (CB-17).
        //
        // A short suffix, not the full settings-dir guid: SessionMessengerSocketTests
        // builds real AF_UNIX sockets under Path.GetTempPath(), which honors
        // TMPDIR too, and a socket path over 104 bytes throws. Reusing the
        // (much longer) settings scratch path here pushed that over the limit;
        // an 8-hex-char suffix, the same budget SessionMessengerSocketTests
        // already uses for its own directory, leaves it room.
        Environment.SetEnvironmentVariable(
            "TMPDIR",
            Path.Combine(Path.GetTempPath(), "cbt-" + Guid.NewGuid().ToString("N")[..8]));

        // ...and no test here may start a real relay by accident — a live Claude
        // Code session in tmux, on the developer's own account. Unless the
        // live-bridge tests were deliberately opted into, which is the one case
        // in this repository where starting one is the point: those are
        // [LiveBridgeFact], skipped unless CLAUDE_BUDDY_LIVE_BRIDGE_TESTS=1, and
        // they tag their relays to stay out of the installed app's way.
        //
        // See RemoteControlSessions.StartsBlocked for what this guards and why a
        // comment asking tests not to do it turned out not to be enough (CB-42).
        if (Environment.GetEnvironmentVariable("CLAUDE_BUDDY_LIVE_BRIDGE_TESTS") != "1")
        {
            Environment.SetEnvironmentVariable("CLAUDE_BUDDY_NO_RELAY", "1");
        }
    }
}
