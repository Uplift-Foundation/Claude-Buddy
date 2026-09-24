using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

// Every [AvaloniaFact] in this assembly shares one Avalonia application and
// one headless dispatcher (see TestAppBuilder) — there is no per-test
// instance to isolate them. xUnit's default is to run different test
// classes as separate collections in parallel, on separate threads, which
// against a single shared dispatcher means real concurrent construction of
// Window objects from multiple threads — a plausible contributor to the
// intermittent KeyNotFoundException for "fonts:SystemFonts" documented on
// TestAppBuilder.WarmUpFontManager.
//
// On its own, this setting did NOT stop that failure recurring in CI
// (confirmed by reproducing it on real hardware with this exact setting in
// place) — it is one piece of a defense-in-depth stack alongside the
// warm-up and the CI-level retry (see ci.yml), not a fix by itself. Kept
// because it can only reduce the odds of a documented, still-not-fully-
// understood upstream race, never increase them, and disabling
// parallelization costs nothing here — 108 tests across three suites run
// in under two seconds regardless.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

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

        // Where StatusDirectory.Path() puts settings-errors.log — left unset,
        // every suite run appends failure traces to the developer's real
        // $TMPDIR/claude_buddy/settings-errors.log (CB-17).
        //
        // CLAUDE_BUDDY_STATUS_ROOT, not TMPDIR: moving TMPDIR from inside the
        // test process breaks the coverage collector's IPC and cost this repo
        // its added-line coverage rule for a while. See IntegrationTests'
        // TestBootstrap for the full reasoning (CB-172).
        var statusRoot = Path.Combine(
            Path.GetTempPath(), "cbt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(statusRoot);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_STATUS_ROOT", statusRoot);

        // ...and no test in this assembly may start a real relay: that is a live
        // Claude Code session in tmux, on the developer's own account, holding a
        // relay name the installed app also wants. Set here rather than trusted
        // to call discipline because CB-42 proved the discipline was already
        // broken and nobody could tell — the call was dormant only because the
        // relay it started always failed. See RemoteControlSessions.StartsBlocked.
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_NO_RELAY", "1");

        // CB-168: no test in this assembly, including one nobody has
        // written yet, may reach a real speech engine or chime process — see
        // TextToSpeech.SilenceForTests and ChimePlayer.SilenceForTests for
        // why this has to be a process-wide guard rather than trusted to
        // every call site remembering its own seam. Set here because this is
        // the assembly where it was actually found: OrbWindowSpeakTests and
        // TurnSummarySpeechTests posted a real Speak call via
        // Dispatcher.UIThread.Post without ever awaiting it, and the posted
        // job ran on whichever later test next pumped the dispatcher.
        TextToSpeech.SilenceForTests = true;
        ChimePlayer.SilenceForTests = true;

        ClaudeBuddySettings.ReloadForTests();
    }
}
