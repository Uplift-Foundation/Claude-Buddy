using Xunit;
using static Orbweaver.MacOSLegacyBundle;

namespace Orbweaver.Tests;

// CB-255 §5: the stale-bundle cleanup's executor against real folders.
//
// Every case builds its own "Applications" folders and Trash under a fresh
// temp root, with a fake Info.plist, and hands Run those paths directly — so
// nothing here can reach a real /Applications or ~/.Trash, and the cases run
// on every platform (`onMac: true` is passed in rather than read off the OS).
// The failure arms — a move that refuses, a candidate list that throws, a log
// that cannot be written — are reached by injecting the delegates.
public class MacOSLegacyBundleTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "cb-legacy-bundle-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _log = [];
    private readonly List<string> _notices = [];

    public MacOSLegacyBundleTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private string Trash => Path.Combine(_scratch, ".Trash");

    private string Applications => Path.Combine(_scratch, "Applications", LegacyBundleName);

    private string HomeApplications => Path.Combine(_scratch, "home", "Applications", LegacyBundleName);

    private static readonly DateTimeOffset At = new(2026, 10, 8, 14, 3, 9, TimeSpan.Zero);

    // A bundle with an Info.plist naming this id and executable, and a file in
    // MacOS so a move can be told from a recreate.
    private static void Bundle(string app, string identifier = "io.github.wtvamp.claudebuddy", string executable = "ClaudeBuddy")
    {
        var contents = Path.Combine(app, "Contents");
        Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
        File.WriteAllText(Path.Combine(contents, "MacOS", executable), "binary");
        File.WriteAllText(Path.Combine(contents, "Info.plist"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>CFBundleIdentifier</key>        <string>{identifier}</string>
                <key>CFBundleExecutable</key>        <string>{executable}</string>
            </dict>
            </plist>
            """);
    }

    private void RunOn(
        IReadOnlyList<string> candidates,
        bool onMac = true,
        bool overrideSet = false,
        Action<string, string>? move = null) =>
        Run(onMac, overrideSet, () => candidates, () => Trash, () => At, move, _log.Add, _notices.Add);

    [Fact]
    public void Our_old_bundle_is_moved_to_the_trash_and_the_user_told_once()
    {
        Bundle(Applications);

        RunOn([Applications]);

        Assert.False(Directory.Exists(Applications));
        var trashed = Path.Combine(Trash, "Claude Buddy.app");
        Assert.True(File.Exists(Path.Combine(trashed, "Contents", "MacOS", "ClaudeBuddy")));
        Assert.Equal([NoticeText], _notices);
        Assert.Contains($"moved {Applications} to {trashed}", _log);
        Assert.Contains("notice: " + NoticeText, _log);
    }

    // Both locations at once: both go, the second beside the first rather than
    // over it, and still one notice — it is one thing for the user to do.
    [Fact]
    public void Both_old_bundles_are_moved_without_overwriting_and_with_one_notice()
    {
        Bundle(Applications);
        Bundle(HomeApplications);

        RunOn([Applications, HomeApplications]);

        Assert.False(Directory.Exists(Applications));
        Assert.False(Directory.Exists(HomeApplications));
        Assert.True(Directory.Exists(Path.Combine(Trash, "Claude Buddy.app")));
        Assert.True(Directory.Exists(Path.Combine(Trash, "Claude Buddy 20261008-140309.app")));
        Assert.Single(_notices);
    }

    // The negative control the design calls for: an app called Claude
    // Buddy.app that is somebody else's stays exactly where it is.
    [Fact]
    public void A_same_named_app_that_is_not_ours_is_left_where_it_is()
    {
        Bundle(Applications, identifier: "com.example.claudebuddy");

        RunOn([Applications]);

        Assert.True(File.Exists(Path.Combine(Applications, "Contents", "Info.plist")));
        Assert.False(Directory.Exists(Trash));
        Assert.Empty(_notices);
        Assert.Contains(_log, line => line.StartsWith($"left {Applications} alone") && line.Contains("com.example.claudebuddy"));
    }

    // A plist this cannot read — binary, or torn — proves nothing either, and
    // the log says so rather than naming values it never saw.
    [Fact]
    public void A_bundle_whose_plist_cannot_be_read_is_left_alone()
    {
        Bundle(Applications);
        File.WriteAllText(Path.Combine(Applications, "Contents", "Info.plist"), "bplist00\u0001\u0002");

        RunOn([Applications]);

        Assert.True(Directory.Exists(Applications));
        Assert.Empty(_notices);
        Assert.Contains($"left {Applications} alone: its Info.plist is not this app's " +
                        "(CFBundleIdentifier (none), CFBundleExecutable (none))", _log);
    }

    // A bundle with no Info.plist cannot prove anything, and the ordinary case
    // — no old bundle at all — is silent.
    [Fact]
    public void A_bundle_without_a_plist_and_a_missing_bundle_are_untouched_and_silent()
    {
        Directory.CreateDirectory(Path.Combine(Applications, "Contents"));

        RunOn([Applications, HomeApplications]);

        Assert.True(Directory.Exists(Applications));
        Assert.Empty(_log);
        Assert.Empty(_notices);
    }

    [Fact]
    public void Nothing_happens_off_macos()
    {
        Bundle(Applications);

        RunOn([Applications], onMac: false);

        Assert.True(Directory.Exists(Applications));
        Assert.Empty(_log);
    }

    // Every suite sets ORBWEAVER_SETTINGS_DIR; under it this must never
    // reach the developer's real /Applications.
    [Fact]
    public void Nothing_happens_under_the_test_overrides()
    {
        Bundle(Applications);

        RunOn([Applications], overrideSet: true);

        Assert.True(Directory.Exists(Applications));
        Assert.Empty(_log);
    }

    // The parameterless entry point, as Program.cs calls it, under this
    // suite's real environment: a no-op that does not throw.
    [Fact]
    public void The_real_entry_point_is_a_no_op_under_the_suite_environment()
    {
        Assert.True(DataDirMigration.OverrideSet(Environment.GetEnvironmentVariable));
        MacOSLegacyBundle.Run();
    }

    // A move that refuses — a root-owned bundle, another volume — leaves the
    // bundle in place, says why, and raises no notice about a move that did
    // not happen.
    [Fact]
    public void A_move_that_refuses_leaves_the_bundle_and_raises_no_notice()
    {
        Bundle(Applications);

        RunOn([Applications], move: (_, _) => throw new UnauthorizedAccessException("denied"));

        Assert.True(Directory.Exists(Applications));
        Assert.Empty(_notices);
        Assert.Contains(_log, line => line.StartsWith($"could not move {Applications}") && line.Contains("denied"));
    }

    [Fact]
    public void A_failure_finding_the_candidates_is_logged_and_never_thrown()
    {
        Run(true, false, () => throw new IOException("boom"), () => Trash, log: _log.Add, notice: _notices.Add);

        Assert.Contains(_log, line => line.StartsWith("legacy bundle cleanup stopped") && line.Contains("boom"));
        Assert.Empty(_notices);
    }

    [Fact]
    public void A_log_that_cannot_be_written_does_not_throw()
    {
        Bundle(Applications);

        Run(true, false, () => [Applications], () => Trash, log: _ => throw new IOException("disk full"));

        Assert.False(Directory.Exists(Applications));
    }

    // The defaults Program.cs gets: the real move, the real clock, and the log
    // in migration.log beside the crash log. A bundle already in the Trash
    // makes the clock matter.
    [Fact]
    public void The_default_sinks_write_migration_log_and_use_the_clock()
    {
        var logs = Path.Combine(_scratch, "Logs");
        Bundle(Applications);
        Directory.CreateDirectory(Path.Combine(Trash, "Claude Buddy.app"));

        using (CrashLog.ScopeForTests(logs))
            Run(true, false, () => [Applications], () => Trash);

        Assert.False(Directory.Exists(Applications));
        Assert.Equal(2, Directory.GetDirectories(Trash).Length);
        var text = File.ReadAllText(Path.Combine(logs, DataDirMigration.LogFile));
        Assert.Contains("notice: " + NoticeText, text);
    }
}
