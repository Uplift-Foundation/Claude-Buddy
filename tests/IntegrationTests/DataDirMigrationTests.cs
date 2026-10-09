using Xunit;
using static Orbweaver.DataDirMigration;

namespace Orbweaver.Tests;

// CB-255 §2: the data-dir migration's executor against real folders.
//
// Every case builds its own legacy and new folders under a fresh temp root and
// hands Run the roots directly, so nothing here can reach the developer's real
// %APPDATA% or ~/Library. The failure arms — a move that refuses, a copy that
// dies on the Nth file — are reached by injecting the move and copy delegates,
// because forcing a real cross-volume move is not portable. The chime sweep is
// always injected as a no-op except in the cases about it, which aim it at a
// scratch folder.
//
// In [Collection("Settings")] because one case round-trips settings.json
// through ClaudeBuddySettings, which moves the process-wide
// CLAUDE_BUDDY_SETTINGS_DIR.
[Collection("Settings")]
public class DataDirMigrationTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "cb-datadir-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _log = [];

    public DataDirMigrationTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private Root DataRoot => new(RootKind.Data, Path.Combine(_scratch, "ClaudeBuddy"), Path.Combine(_scratch, "Orbweaver"));

    private Root LogsRoot => new(RootKind.Logs, Path.Combine(_scratch, "LogsOld"), Path.Combine(_scratch, "LogsNew"));

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // A legacy data folder shaped like a real one.
    private static void SeedLegacyData(string legacy)
    {
        Write(Path.Combine(legacy, SettingsFile), """{"speechVolume": 0.4}""");
        Write(Path.Combine(legacy, PeerIdentityFile), """{"id": "abc"}""");
        Write(Path.Combine(legacy, "pair-open"), "");
        Write(Path.Combine(legacy, "speech-engine", "1.2.3", "engine.bin"), "engine");
        Directory.CreateDirectory(Path.Combine(legacy, "voices"));
    }

    private void RunOn(params Root[] roots) =>
        Run(false, () => roots, log: _log.Add, sweepChimes: () => { });

    private static string[] Listing(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void A_move_takes_everything_and_leaves_a_snapshot_behind()
    {
        var root = DataRoot;
        SeedLegacyData(root.Legacy);

        RunOn(root);

        // Everything arrived, empty folder included.
        Assert.Equal("""{"speechVolume": 0.4}""", File.ReadAllText(Path.Combine(root.New, SettingsFile)));
        Assert.Equal("engine", File.ReadAllText(Path.Combine(root.New, "speech-engine", "1.2.3", "engine.bin")));
        Assert.True(Directory.Exists(Path.Combine(root.New, "voices")));
        Assert.True(File.Exists(Path.Combine(root.New, "pair-open")));
        Assert.False(File.Exists(Path.Combine(root.New, MarkerFile)));

        // The legacy folder is exactly the snapshot: the two state files and
        // the note, and nothing big.
        Assert.Equal(new[] { MarkerFile, PeerIdentityFile, SettingsFile }, Listing(root.Legacy));
        Assert.Equal("""{"speechVolume": 0.4}""", File.ReadAllText(Path.Combine(root.Legacy, SettingsFile)));
        Assert.Equal("""{"id": "abc"}""", File.ReadAllText(Path.Combine(root.Legacy, PeerIdentityFile)));
        Assert.Contains(root.New, File.ReadAllText(Path.Combine(root.Legacy, MarkerFile)));

        Assert.Contains(_log, line => line.StartsWith("moved "));
        Assert.Contains(_log, line => line.StartsWith("left a snapshot"));
    }

    // A legacy folder without the state files still moves; the snapshot
    // carries only what exists.
    [Fact]
    public void A_snapshot_carries_only_the_state_files_that_exist()
    {
        var root = DataRoot;
        Write(Path.Combine(root.Legacy, "voices", "a.onnx"), "v");

        RunOn(root);

        Assert.Equal(new[] { MarkerFile }, Listing(root.Legacy));
        Assert.True(File.Exists(Path.Combine(root.New, "voices", "a.onnx")));
    }

    // The second launch after a successful move: new has settings.json, the
    // legacy folder is the snapshot. Nothing moves, in either direction.
    [Fact]
    public void A_second_run_after_a_move_changes_nothing()
    {
        var root = DataRoot;
        SeedLegacyData(root.Legacy);
        RunOn(root);
        var newBefore = Listing(root.New);
        var legacyBefore = Listing(root.Legacy);
        _log.Clear();

        RunOn(root);

        Assert.Equal(newBefore, Listing(root.New));
        Assert.Equal(legacyBefore, Listing(root.Legacy));
        Assert.Empty(_log);
    }

    // The forced copy path: the move refuses the way a cross-volume move or a
    // held file does. The legacy folder is left whole, no snapshot is taken,
    // the bundle cache stays behind, and settings.json is the last file copied.
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void A_move_that_refuses_falls_back_to_a_copy_with_settings_last(Type refusal)
    {
        var root = DataRoot;
        SeedLegacyData(root.Legacy);
        Write(Path.Combine(root.Legacy, "bundles", "Default", "Claude.app", "Info.plist"), "plist");
        var legacyBefore = Listing(root.Legacy);
        var copied = new List<string>();

        Run(false, () => [root],
            move: (_, _) => throw (Exception)Activator.CreateInstance(refusal, "refused")!,
            copy: (from, to) => { copied.Add(Path.GetRelativePath(root.Legacy, from)); File.Copy(from, to, true); },
            log: _log.Add,
            sweepChimes: () => { });

        Assert.Equal(SettingsFile, copied[^1]);
        Assert.Equal(4, copied.Count);
        Assert.Equal("engine", File.ReadAllText(Path.Combine(root.New, "speech-engine", "1.2.3", "engine.bin")));
        Assert.True(Directory.Exists(Path.Combine(root.New, "voices")));
        Assert.False(Directory.Exists(Path.Combine(root.New, "bundles")));
        Assert.Empty(Directory.EnumerateFiles(root.New, "*" + PartialSuffix, SearchOption.AllDirectories));

        Assert.Equal(legacyBefore, Listing(root.Legacy));
        Assert.False(File.Exists(Path.Combine(root.Legacy, MarkerFile)));

        Assert.Contains(_log, line => line.StartsWith("could not move ") && line.Contains(refusal.Name));
        Assert.Contains(_log, line => line.StartsWith("copied "));
    }

    // A crash mid-copy, simulated by a copy that dies on its second file. The
    // first run leaves "new without settings.json" — the state the next
    // launch recognises — and the second run, with a working copy, finishes.
    [Fact]
    public void A_copy_that_dies_part_way_is_finished_by_the_next_run()
    {
        var root = DataRoot;
        SeedLegacyData(root.Legacy);
        var calls = 0;

        Run(false, () => [root],
            move: (_, _) => throw new IOException("other volume"),
            copy: (from, to) =>
            {
                if (++calls == 2)
                {
                    File.WriteAllText(to, "torn");   // what a crash mid-file leaves
                    throw new IOException("disk went away");
                }

                File.Copy(from, to, true);
            },
            log: _log.Add,
            sweepChimes: () => { });

        Assert.False(File.Exists(Path.Combine(root.New, SettingsFile)));
        Assert.Contains(_log, line => line.StartsWith("migrating ") && line.Contains("disk went away"));

        // Next launch: the move refuses again, so it is the merge arm that
        // resumes — new exists without settings.json.
        Run(false, () => [root], move: (_, _) => throw new IOException("other volume"), log: _log.Add, sweepChimes: () => { });

        Assert.Equal("""{"speechVolume": 0.4}""", File.ReadAllText(Path.Combine(root.New, SettingsFile)));
        Assert.Equal("""{"id": "abc"}""", File.ReadAllText(Path.Combine(root.New, PeerIdentityFile)));
        Assert.Equal("engine", File.ReadAllText(Path.Combine(root.New, "speech-engine", "1.2.3", "engine.bin")));

        // The torn file was a .partial, never the real name, so nothing torn
        // survives — the resume rewrote it and renamed it into place.
        foreach (var file in Directory.EnumerateFiles(root.New, "*", SearchOption.AllDirectories))
            Assert.NotEqual("torn", File.ReadAllText(file));
        Assert.Empty(Directory.EnumerateFiles(root.New, "*" + PartialSuffix, SearchOption.AllDirectories));
        Assert.Contains(_log, line => line.StartsWith("merged "));
    }

    // Both exist and new has settings.json: new wins, and the legacy folder
    // is not touched either — this is also the dev-build-ran-first case.
    [Fact]
    public void Both_exist_and_new_has_settings_new_wins_and_neither_changes()
    {
        var root = DataRoot;
        SeedLegacyData(root.Legacy);
        Write(Path.Combine(root.New, SettingsFile), """{"speechVolume": 0.9}""");
        var legacyBefore = Listing(root.Legacy);

        RunOn(root);

        Assert.Equal(new[] { SettingsFile }, Listing(root.New));
        Assert.Equal("""{"speechVolume": 0.9}""", File.ReadAllText(Path.Combine(root.New, SettingsFile)));
        Assert.Equal(legacyBefore, Listing(root.Legacy));
        Assert.Empty(_log);
    }

    // Both exist and new has no settings.json: what new lacks is copied, what
    // it has is kept (new's version), and settings.json lands.
    [Fact]
    public void Both_exist_and_new_has_no_settings_the_legacy_folder_is_merged_in()
    {
        var root = DataRoot;
        SeedLegacyData(root.Legacy);
        Write(Path.Combine(root.New, PeerIdentityFile), """{"id": "new"}""");
        var legacyBefore = Listing(root.Legacy);

        RunOn(root);

        Assert.Equal("""{"id": "new"}""", File.ReadAllText(Path.Combine(root.New, PeerIdentityFile)));
        Assert.Equal("""{"speechVolume": 0.4}""", File.ReadAllText(Path.Combine(root.New, SettingsFile)));
        Assert.True(File.Exists(Path.Combine(root.New, "speech-engine", "1.2.3", "engine.bin")));
        Assert.Equal(legacyBefore, Listing(root.Legacy));
        Assert.Contains(_log, line => line.StartsWith("merged "));
    }

    // Logs merge: CrashLog may have made the new folder already. A crash.log in
    // both is decided by age, in both directions; one only in legacy is copied.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_logs_merge_keeps_the_newer_crash_log(bool legacyIsNewer)
    {
        var root = LogsRoot;
        Write(Path.Combine(root.Legacy, "crash.log"), "legacy crash");
        Write(Path.Combine(root.Legacy, "hotkeys.log"), "hotkeys");
        Write(Path.Combine(root.New, "crash.log"), "new crash");

        var older = DateTime.UtcNow.AddDays(-3);
        var newer = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(Path.Combine(root.Legacy, "crash.log"), legacyIsNewer ? newer : older);
        File.SetLastWriteTimeUtc(Path.Combine(root.New, "crash.log"), legacyIsNewer ? older : newer);

        RunOn(root);

        Assert.Equal(legacyIsNewer ? "legacy crash" : "new crash", File.ReadAllText(Path.Combine(root.New, "crash.log")));
        Assert.Equal("hotkeys", File.ReadAllText(Path.Combine(root.New, "hotkeys.log")));
        Assert.False(File.Exists(Path.Combine(root.Legacy, MarkerFile)));
    }

    // The Windows arm, through the real Roots rule on temp folders standing in
    // for %APPDATA%, %LOCALAPPDATA% and the profile: only Logs leaves
    // %LOCALAPPDATA%\ClaudeBuddy, and the hook script stays where running
    // Claude Code sessions still call it.
    [Fact]
    public void On_Windows_only_the_Logs_subfolder_of_local_app_data_moves()
    {
        var roaming = Path.Combine(_scratch, "Roaming");
        var local = Path.Combine(_scratch, "Local");
        SeedLegacyData(Path.Combine(roaming, "ClaudeBuddy"));
        Write(Path.Combine(local, "ClaudeBuddy", "ClaudeBuddyHook.ps1"), "hook");
        Write(Path.Combine(local, "ClaudeBuddy", "Logs", "crash.log"), "crash");

        Run(false, () => Roots(onWindows: true, roaming, local, Path.Combine(_scratch, "Home")),
            log: _log.Add, sweepChimes: () => { });

        Assert.Equal("crash", File.ReadAllText(Path.Combine(local, "Orbweaver", "Logs", "crash.log")));
        Assert.Equal(new[] { "Logs", Path.Combine("Logs", "crash.log") }, Listing(Path.Combine(local, "Orbweaver")));
        Assert.Equal(new[] { "ClaudeBuddyHook.ps1" }, Listing(Path.Combine(local, "ClaudeBuddy")));

        Assert.True(File.Exists(Path.Combine(roaming, "Orbweaver", SettingsFile)));
        Assert.True(File.Exists(Path.Combine(roaming, "ClaudeBuddy", MarkerFile)));
    }

    // The macOS arm's shape, on temp folders. Run here on whatever machine the
    // suite is on: it proves the rule moves ~/Library/Logs/ClaudeBuddy whole,
    // not that macOS's real folders are where Roots says they are.
    [Fact]
    public void Off_Windows_the_whole_Library_Logs_folder_moves()
    {
        var home = Path.Combine(_scratch, "Home");
        var support = Path.Combine(home, "Library", "Application Support");
        SeedLegacyData(Path.Combine(support, "ClaudeBuddy"));
        Write(Path.Combine(home, "Library", "Logs", "ClaudeBuddy", "keepalive.log"), "alive");

        Run(false, () => Roots(onWindows: false, support, Path.Combine(_scratch, "Local"), home),
            log: _log.Add, sweepChimes: () => { });

        Assert.Equal("alive", File.ReadAllText(Path.Combine(home, "Library", "Logs", "Orbweaver", "keepalive.log")));
        Assert.False(Directory.Exists(Path.Combine(home, "Library", "Logs", "ClaudeBuddy")));
        Assert.True(File.Exists(Path.Combine(support, "Orbweaver", SettingsFile)));
    }

    // Settings round-trip through the moved file: written by ClaudeBuddySettings
    // into the legacy folder, read back by ClaudeBuddySettings from the new one
    // — and the snapshot left behind reads back too, which is the downgrade
    // story.
    [Fact]
    public void Settings_written_under_the_legacy_name_read_back_from_the_new_one()
    {
        var root = DataRoot;
        Directory.CreateDirectory(root.Legacy);
        var previous = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR");

        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", root.Legacy);
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.SpeechVolume = 0.35;
            OrbweaverSettings.FlushPendingSave();
            Assert.True(File.Exists(Path.Combine(root.Legacy, SettingsFile)));

            RunOn(root);

            Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", root.New);
            OrbweaverSettings.ReloadForTests();
            Assert.Equal(0.35, OrbweaverSettings.SpeechVolume);

            Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", root.Legacy);
            OrbweaverSettings.ReloadForTests();
            Assert.Equal(0.35, OrbweaverSettings.SpeechVolume);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", previous);
            OrbweaverSettings.ReloadForTests();
        }
    }

    // Under the overrides the whole thing is off: nothing moves, the chime
    // sweep does not run, nothing is logged.
    [Fact]
    public void With_an_override_set_nothing_happens_at_all()
    {
        var root = DataRoot;
        SeedLegacyData(root.Legacy);
        var legacyBefore = Listing(root.Legacy);
        var swept = false;

        Run(true, () => [root], log: _log.Add, sweepChimes: () => swept = true);

        Assert.Equal(legacyBefore, Listing(root.Legacy));
        Assert.False(Directory.Exists(root.New));
        Assert.False(swept);
        Assert.Empty(_log);
    }

    // The real entry point under this suite's environment (TestBootstrap sets
    // CLAUDE_BUDDY_SETTINGS_DIR): a no-op that does not throw.
    [Fact]
    public void The_real_entry_point_is_a_no_op_under_the_test_environment()
    {
        DataDirMigration.Run();
    }

    // Never throws out of Run: not on a failure the move fallback does not
    // cover, not when the roots cannot even be computed, not when the log
    // itself throws.
    [Fact]
    public void Nothing_escapes_Run()
    {
        var root = DataRoot;
        SeedLegacyData(root.Legacy);

        Run(false, () => [root], move: (_, _) => throw new InvalidOperationException("odd"), log: _log.Add, sweepChimes: () => { });
        Assert.Contains(_log, line => line.StartsWith("migrating ") && line.Contains("odd"));
        Assert.True(File.Exists(Path.Combine(root.Legacy, SettingsFile)));

        Run(false, () => throw new InvalidOperationException("no roots"), log: _log.Add, sweepChimes: () => { });
        Assert.Contains(_log, line => line.StartsWith("migration stopped") && line.Contains("no roots"));

        Run(false, () => [root], move: (_, _) => throw new InvalidOperationException("odd"),
            log: _ => throw new IOException("log is gone"), sweepChimes: () => { });
    }

    // The default log sink: migration.log beside the crash log.
    [Fact]
    public void Without_a_log_sink_the_lines_go_to_migration_log_in_the_crash_log_folder()
    {
        var logs = Path.Combine(_scratch, "crashlog");
        var root = DataRoot;
        SeedLegacyData(root.Legacy);

        using (CrashLog.ScopeForTests(logs))
            Run(false, () => [root], sweepChimes: () => { });

        var text = File.ReadAllText(Path.Combine(logs, LogFile));
        Assert.Contains("moved " + root.Legacy, text);
        Assert.Contains("left a snapshot", text);
    }

    [Fact]
    public void Appending_to_an_unwritable_log_folder_is_swallowed()
    {
        var blocker = Path.Combine(_scratch, "a-file");
        File.WriteAllText(blocker, "");

        AppendLog(Path.Combine(blocker, "under-a-file"), "line");   // CreateDirectory throws; nothing escapes
    }

    // ---- the legacy chime cache -----------------------------------------

    [Fact]
    public void The_legacy_chime_cache_is_swept_and_a_missing_one_is_fine()
    {
        var cache = Path.Combine(_scratch, "ClaudeBuddy-chimes");
        Write(Path.Combine(cache, "chime-50.wav"), "wav");

        AudioVolume.SweepLegacyChimeCache(cache);
        Assert.False(Directory.Exists(cache));

        AudioVolume.SweepLegacyChimeCache(cache);   // already gone: no-op
    }

    [Fact]
    public void The_legacy_chime_cache_lives_under_temp_with_the_old_name()
    {
        Assert.Equal(Path.Combine(Path.GetTempPath(), "ClaudeBuddy-chimes"), AudioVolume.LegacyChimeCacheDirectory);
        Assert.NotEqual(AudioVolume.ChimeCacheDirectory, AudioVolume.LegacyChimeCacheDirectory);
    }

    // A chime still open (the old build mid-play on Windows) stops the delete;
    // the sweep gives up quietly. Only Windows refuses to delete an open file,
    // so elsewhere this asserts just that nothing escaped.
    [Fact]
    public void A_held_chime_leaves_the_sweep_for_next_time()
    {
        var cache = Path.Combine(_scratch, "ClaudeBuddy-chimes");
        var chime = Path.Combine(cache, "chime-50.wav");
        Write(chime, "wav");

        using (new FileStream(chime, FileMode.Open, FileAccess.Read, FileShare.None))
            AudioVolume.SweepLegacyChimeCache(cache);

        if (OperatingSystem.IsWindows()) Assert.True(File.Exists(chime));
    }

    // Run's own sweep step runs when no override is set.
    [Fact]
    public void Run_sweeps_the_chime_cache_when_not_overridden()
    {
        var swept = false;

        Run(false, () => [], log: _log.Add, sweepChimes: () => swept = true);

        Assert.True(swept);
    }
}
