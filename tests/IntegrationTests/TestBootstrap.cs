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

        // No test in this assembly asks the OS for a credential. On macOS the
        // cloud arm's credential lives in the login Keychain, and reading it from
        // another application raises a consent dialog — which, headless, nobody
        // answers: the read waits out its forty-five-second budget, leaks the pool
        // thread parked inside Security.framework, and repeats for the next test
        // that gets there. It is invisible in CI, where no such Keychain item
        // exists and the query fails fast, and it only bites on a machine where
        // somebody has actually logged in. Set here with the settings seam above,
        // before any static constructor can run.
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_NO_CREDENTIAL_STORE", "1");

        // The floor under CrashLog.Directory, and the reason forgetting to
        // isolate it is now harmless rather than a race.
        //
        // Any test that refuses a persona picture writes a line about it, two
        // calls removed and without naming the variable: PersonaFiles.Reject →
        // PersonaLog.Record → Directory.CreateDirectory(CrashLog.Directory).
        // Dozens of cases here do that incidentally while asserting something
        // else entirely. Left unset, every one of them wrote into the
        // developer's real ~/Library/Logs/ClaudeBuddy unless some *other*
        // class happened to have the variable pointed elsewhere at that moment
        // — and when one did, the write landed in that class's scratch
        // directory instead, which is how CrashLogFileTests' assertion that its
        // own directory does not exist yet could be falsified by a test that
        // has nothing to do with crash logs.
        //
        // One directory for the assembly, and deliberately not per-class: the
        // classes that assert on what is *in* the log take a CrashLog
        // .ScopeForTests instead, which is AsyncLocal and therefore invisible
        // to everyone else. This is only somewhere harmless for the writes
        // nobody is looking at. Nothing asserts about it, so nothing races over
        // it.
        Environment.SetEnvironmentVariable(
            "CLAUDE_BUDDY_LOG_DIR",
            Path.Combine(Path.GetTempPath(), "cb-integrationtests-log-" + Guid.NewGuid()));

        // Where StatusDirectory.Path() puts settings-errors.log. Left unset,
        // every suite run appended Save/Load failure traces to the real
        // $TMPDIR/claude_buddy/settings-errors.log on the developer's machine —
        // a user-facing diagnostic file growing without bound from test noise
        // (CB-17).
        //
        // CLAUDE_BUDDY_STATUS_ROOT, not TMPDIR. This used to move TMPDIR, which
        // reached far further than the one directory it was aiming at: TMPDIR
        // is process-wide, and the Microsoft.Testing.Platform coverage
        // collector puts its IPC socket under it. The collector's server end
        // had already computed that path from the *original* TMPDIR before this
        // assembly was loaded; the client end, running after this initializer,
        // computed a different one — so `tools/coverage.sh` never got a
        // cobertura report out of either MTP suite, and the whole 100%-of-added-
        // lines rule in CLAUDE.md was unenforceable while that was true.
        //
        // It surfaced as two unrelated-looking errors, which is why it took
        // three attempts to pin down. MTP runs this executable twice (a test
        // host controller and the test host under it), so the initializer fired
        // twice and nested a second cbt- directory inside the first: on macOS
        // that put the socket path at 117 bytes against a 104-byte sun_path cap
        // and threw ArgumentOutOfRangeException. Shorten TMPDIR and the length
        // check passes, the client still looks in the wrong place, and you get
        // a TimeoutException instead. One cause, two faces.
        //
        // Measured, not assumed: a one-test project with nothing in it but this
        // module initializer reproduces both in about four seconds, and a
        // five-minute test without one passes clean — so neither suite duration
        // nor Avalonia was ever involved.
        var statusRoot = Path.Combine(
            Path.GetTempPath(), "cbt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(statusRoot);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_STATUS_ROOT", statusRoot);

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

        // CB-168: no test in this assembly, including one nobody has
        // written yet, may reach a real speech engine or chime process — see
        // TextToSpeech.SilenceForTests and ChimePlayer.SilenceForTests.
        TextToSpeech.SilenceForTests = true;
        ChimePlayer.SilenceForTests = true;
    }
}
