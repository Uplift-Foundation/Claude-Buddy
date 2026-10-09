using System.Diagnostics;
using Xunit;
using static Orbweaver.KeepAliveRepair;

namespace Orbweaver.Tests;

// CB-256 §2: the keep-alive repair's executor against real folders.
//
// Every case builds a fake LaunchAgent plist and a fake bundle under a fresh
// temp root and hands Run those paths directly, so nothing here can reach a
// real ~/Library/LaunchAgents or run a real install-hooks.sh — and the cases
// run on every platform (`onMac: true` is passed in rather than read off the
// OS). The script itself is a delegate that records what it was asked to run;
// the few cases that start a real process are UnixFact and use a stand-in
// script written into the scratch folder.
public class KeepAliveRepairTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "cb-keepalive-repair-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _log = [];
    private readonly List<(string Script, string Argument)> _ran = [];

    public KeepAliveRepairTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private string PlistPath => Path.Combine(_scratch, "LaunchAgents", "io.github.wtvamp.claudebuddy.plist");

    // The new bundle this process "runs from": Contents/MacOS/Orbweaver, and
    // the bundled script beside it unless a case says otherwise.
    private string Bundle => Path.Combine(_scratch, "Applications", "Orbweaver.app");
    private string OwnExe => Path.Combine(Bundle, "Contents", "MacOS", "Orbweaver");
    private string BundledScriptPath => Path.Combine(Bundle, "Contents", "Resources", "install-hooks.sh");

    // What a phase-2 install's plist names, and a drag-install removed.
    private string GoneExe => Path.Combine(Bundle, "Contents", "MacOS", "ClaudeBuddy");

    private void WritePlist(string program, string label = "io.github.wtvamp.claudebuddy")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        File.WriteAllText(PlistPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{label}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{program}</string>
                </array>
            </dict>
            </plist>
            """);
    }

    private void WriteBundle(bool withScript = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OwnExe)!);
        File.WriteAllText(OwnExe, "binary");
        if (!withScript) return;
        Directory.CreateDirectory(Path.GetDirectoryName(BundledScriptPath)!);
        File.WriteAllText(BundledScriptPath, "#!/bin/sh\n");
    }

    private (int, string) Script(string script, string argument, int exitCode = 0, string output = "")
    {
        _ran.Add((script, argument));
        return (exitCode, output);
    }

    private void RunOn(
        bool onMac = true,
        bool overrideSet = false,
        Func<string, string, (int, string)>? exec = null,
        string? ownExe = null) =>
        Run(onMac, overrideSet, () => PlistPath, () => ownExe ?? OwnExe,
            exec: exec ?? ((script, argument) => Script(script, argument)), log: _log.Add);

    // ---- the repair ---------------------------------------------------------

    // The drag-install case end to end: the plist names the phase-2
    // executable, which is gone; the bundled script is run with
    // --keepalive-only, once, and what it printed lands in the log.
    [Fact]
    public void A_dead_keepalive_runs_the_bundled_script_and_logs_what_it_said()
    {
        WriteBundle();
        WritePlist(GoneExe);

        RunOn(exec: (script, argument) =>
            Script(script, argument, output: "=== Crash keep-alive: registered (x.plist)\n"));

        Assert.Equal(new[] { (BundledScriptPath, "--keepalive-only") }, _ran);
        Assert.Equal(
            [
                $"keep-alive {PlistPath} names {GoneExe}, which is gone",
                "  === Crash keep-alive: registered (x.plist)",
                $"keep-alive repaired by {BundledScriptPath} --keepalive-only",
            ],
            _log);
    }

    // A script that fails is reported with its exit code, not as a repair.
    [Fact]
    public void A_script_that_fails_is_logged_with_its_exit_code()
    {
        WriteBundle();
        WritePlist(GoneExe);

        RunOn(exec: (script, argument) => Script(script, argument, exitCode: 3, output: "boom\n\n"));

        Assert.Single(_ran);
        Assert.Contains("  boom", _log);
        Assert.Contains($"keep-alive repair: {BundledScriptPath} --keepalive-only exited 3", _log);
        Assert.DoesNotContain(_log, line => line.StartsWith("keep-alive repaired", StringComparison.Ordinal));
    }

    // A bundle without the script — a hand-assembled one — cannot be repaired
    // from. Said, and nothing run.
    [Fact]
    public void A_bundle_without_the_script_is_logged_and_nothing_run()
    {
        WriteBundle(withScript: false);
        WritePlist(GoneExe);

        RunOn();

        Assert.Empty(_ran);
        Assert.Contains($"keep-alive not repaired: no {BundledScriptPath} to run", _log);
    }

    // ---- what is left alone, silently ---------------------------------------
    //
    // The ordinary launch: no repair, and no line in migration.log either,
    // since every launch after the first would otherwise add one.

    [Fact]
    public void A_keepalive_whose_program_exists_is_left_alone()
    {
        WriteBundle();
        WritePlist(OwnExe);

        RunOn();

        Assert.Empty(_ran);
        Assert.Empty(_log);
    }

    [Fact]
    public void No_plist_is_left_alone()
    {
        WriteBundle();

        RunOn();

        Assert.Empty(_ran);
        Assert.Empty(_log);
    }

    // Somebody else's job at our file name is not ours to rewrite.
    [Fact]
    public void A_plist_with_another_label_is_left_alone()
    {
        WriteBundle();
        WritePlist(GoneExe, label: "com.example.agent");

        RunOn();

        Assert.Empty(_ran);
        Assert.Empty(_log);
    }

    // `dotnet run`: no bundle, so nothing to repair from and nothing launchd
    // should point at.
    [Fact]
    public void A_process_outside_a_bundle_is_left_alone()
    {
        WriteBundle();
        WritePlist(GoneExe);

        RunOn(ownExe: Path.Combine(_scratch, "bin", "Orbweaver"));

        Assert.Empty(_ran);
        Assert.Empty(_log);
    }

    // ---- the gate -----------------------------------------------------------

    // Off macOS, and under the test overrides every suite sets, Run does not
    // so much as resolve the plist path.
    [Fact]
    public void Off_macos_nothing_is_even_resolved()
    {
        var asked = false;

        Run(false, false, () => { asked = true; return PlistPath; }, () => { asked = true; return OwnExe; },
            exec: (script, argument) => Script(script, argument), log: _log.Add);

        Assert.False(asked);
        Assert.Empty(_ran);
        Assert.Empty(_log);
    }

    [Fact]
    public void Under_the_test_overrides_nothing_is_even_resolved()
    {
        var asked = false;

        Run(true, true, () => { asked = true; return PlistPath; }, () => { asked = true; return OwnExe; },
            exec: (script, argument) => Script(script, argument), log: _log.Add);

        Assert.False(asked);
        Assert.Empty(_ran);
    }

    // ---- never throws -------------------------------------------------------

    [Fact]
    public void A_script_that_throws_is_logged_and_swallowed()
    {
        WriteBundle();
        WritePlist(GoneExe);

        RunOn(exec: (_, _) => throw new InvalidOperationException("no shell"));

        Assert.Contains("keep-alive repair stopped: InvalidOperationException: no shell", _log);
    }

    [Fact]
    public void A_plist_path_that_throws_is_logged_and_swallowed()
    {
        Run(true, false, () => throw new IOException("no home"), () => OwnExe, log: _log.Add);

        Assert.Equal(["keep-alive repair stopped: IOException: no home"], _log);
    }

    [Fact]
    public void A_log_that_throws_is_swallowed()
    {
        WriteBundle();
        WritePlist(GoneExe);

        Run(true, false, () => PlistPath, () => OwnExe,
            exec: (script, argument) => Script(script, argument),
            log: _ => throw new IOException("disk full"));

        Assert.Single(_ran);
    }

    // ---- the defaults Program.cs gets ---------------------------------------

    // The real existence check and the log in migration.log beside the crash
    // log. The script is still a delegate: running a real one is the UnixFact
    // cases' job.
    [Fact]
    public void The_default_log_and_existence_check_write_migration_log()
    {
        var logs = Path.Combine(_scratch, "Logs");
        WriteBundle();
        WritePlist(GoneExe);

        using (CrashLog.ScopeForTests(logs))
            Run(true, false, () => PlistPath, () => OwnExe, exec: (script, argument) => Script(script, argument));

        Assert.Single(_ran);
        var text = File.ReadAllText(Path.Combine(logs, DataDirMigration.LogFile));
        Assert.Contains($"keep-alive repaired by {BundledScriptPath} --keepalive-only", text);
    }

    // The default executor, against a stand-in bundled script that echoes
    // what it was given: the real Process path, argument and output.
    [UnixFact]
    public void The_default_executor_runs_the_bundled_script_with_keepalive_only()
    {
        WriteBundle();
        WriteExecutable(BundledScriptPath, "#!/bin/sh\necho \"ran with $1\"\necho \"to stderr\" >&2\n");
        WritePlist(GoneExe);

        Run(true, false, () => PlistPath, () => OwnExe, log: _log.Add);

        Assert.Contains("  ran with --keepalive-only", _log);
        Assert.Contains("  to stderr", _log);
        Assert.Contains($"keep-alive repaired by {BundledScriptPath} --keepalive-only", _log);
    }

    // The real seam: install-hooks.sh's own --print-keepalive-plist output is
    // what ReadProgram has to understand, so it is fed straight from the
    // script rather than from a copy of its heredoc that could drift.
    [UnixFact]
    public void ReadProgram_understands_the_plist_install_hooks_writes()
    {
        var script = Path.Combine(FindRepoRoot(), "tools", "install-hooks.sh");
        using var bash = Process.Start(new ProcessStartInfo("/bin/bash")
        {
            ArgumentList = { script, "--print-keepalive-plist", GoneExe },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var plist = bash.StandardOutput.ReadToEnd();
        bash.WaitForExit();

        Assert.Equal(0, bash.ExitCode);
        Assert.Equal(GoneExe, ReadProgram(plist));
    }

    // ---- Exec ---------------------------------------------------------------

    [UnixFact]
    public void Exec_hands_back_the_exit_code_and_both_streams()
    {
        var script = Path.Combine(_scratch, "say.sh");
        WriteExecutable(script, "#!/bin/sh\necho \"out $1\"\necho err >&2\nexit 3\n");

        var (exitCode, output) = Exec(script, "--keepalive-only", TimeSpan.FromSeconds(30));

        Assert.Equal(3, exitCode);
        Assert.Equal("out --keepalive-only\nerr\n", output);
    }

    // A hung script is killed at the timeout and reported, so startup waits a
    // bounded time rather than forever.
    [UnixFact]
    public void Exec_kills_a_script_that_outlives_the_timeout()
    {
        var script = Path.Combine(_scratch, "hang.sh");
        WriteExecutable(script, "#!/bin/sh\necho started\nexec sleep 60\n");
        var clock = Stopwatch.StartNew();

        var (exitCode, output) = Exec(script, "--keepalive-only", TimeSpan.FromMilliseconds(500));

        Assert.Equal(-1, exitCode);
        Assert.StartsWith("started\n", output);
        Assert.EndsWith("timed out after 0.5 s", output);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}: the script was not killed");
    }

    private static void WriteExecutable(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tools", "install-hooks.sh")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find tools/install-hooks.sh above " + AppContext.BaseDirectory);
    }
}
