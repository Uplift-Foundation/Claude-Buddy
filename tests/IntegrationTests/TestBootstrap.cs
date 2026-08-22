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
    }
}
