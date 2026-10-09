using System.Diagnostics;
using Xunit;

namespace Orbweaver.Tests;

// tools/stop-installed-buddy.sh, which `build-macos-app.sh --install` runs
// before it replaces /Applications/Orbweaver.app (CB-206). Once the
// single-instance mutex spans sessions, the copy launchd starts after an
// install finds the old one holding it and exits, so the old one has to be
// gone first or the install leaves a stale Buddy running. Since CB-255 the
// install also removes the legacy /Applications/Claude Buddy.app, so the
// script takes every path to stop in one call.
//
// Driven against stand-ins rather than Buddy: an ad-hoc re-signed copy of
// /bin/bash at a scratch path with a space in it, as /Applications' has. The
// script is asked about that path and nothing else, so nothing real on the
// machine running the suite can be signalled.
public class StopInstalledBuddyScriptTests : IDisposable
{
    private static readonly string Script = Path.Combine(FindRepoRoot(), "tools", "stop-installed-buddy.sh");

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cb206 stop " + Guid.NewGuid().ToString("N"));

    private readonly List<Process> _started = new();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tools", "stop-installed-buddy.sh")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find tools/stop-installed-buddy.sh by walking up from " + AppContext.BaseDirectory);
    }

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            process.Dispose();
        }

        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // A stand-in executable at <scratch>/<name>.app/Contents/MacOS/ClaudeBuddy.
    //
    // **Written as plain bytes, not File.Copy'd (CB-245).** /bin/bash is stored
    // with APFS transparent compression (`ls -lO` says `compressed`), and
    // File.Copy keeps that: the copy is a decmpfs-compressed file. codesign -f
    // has to rewrite it in place, and under load that rewrite intermittently
    // fails with "Attribute not found" (ENOATTR). Measured on this Mac by
    // signing fresh copies 32 at a time: compressed copies failed 170 times in
    // 5,400, plain copies 0 in 3,900, and plain copies made read-only 0 in
    // 2,500, so it is the compression and not the mode or the provenance
    // attribute (which plain copies carry too). The retry in Sign could not
    // reach it: the run that found this failed all three attempts on the same
    // file. A plain write is never compressed, so the race has nothing to act
    // on — `Stand_ins_are_not_compressed` pins that rather than the timing.
    private string StandIn(string name)
    {
        var exe = UnsignedStandIn(name);
        Sign(exe);
        return exe;
    }

    // The copy before codesign sees it. Separate because signing rewrites the
    // file and clears its compression whichever way it was made, so a check
    // after Sign could not tell a plain write from a compressed clone.
    private string UnsignedStandIn(string name)
    {
        var macOs = Path.Combine(_dir, name + ".app", "Contents", "MacOS");
        Directory.CreateDirectory(macOs);
        var exe = Path.Combine(macOs, "ClaudeBuddy");
        File.WriteAllBytes(exe, File.ReadAllBytes("/bin/bash"));
        File.SetUnixFileMode(exe,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return exe;
    }

    // The file flags `ls -lO` prints, "-" when there are none.
    private static string FileFlags(string path)
    {
        using var stat = Process.Start(new ProcessStartInfo("/usr/bin/stat")
        {
            ArgumentList = { "-f", "%Sf", path },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var flags = stat.StandardOutput.ReadToEnd().Trim();
        stat.WaitForExit();
        return flags;
    }

    // CB-245's property, asserted directly: the stand-in codesign is handed is
    // not a compressed file. The control beside it is the source itself, which
    // is, so a pass here cannot come from `stat` reporting nothing at all. Read
    // before signing, for the reason UnsignedStandIn gives.
    [MacInstallFact]
    public void Stand_ins_are_not_compressed()
    {
        Assert.Contains("compressed", FileFlags("/bin/bash"));

        var exe = UnsignedStandIn("Claude Buddy");

        Assert.DoesNotContain("compressed", FileFlags(exe));
    }

    // Ad-hoc signs the copy. QA saw codesign exit 1 intermittently, only
    // when the whole integration suite ran in parallel under load, and never
    // with this class alone. So signing is serialised across the class, a
    // failure is retried twice, and a final failure carries codesign's own
    // stderr and exit code, which the first version threw away.
    private static readonly object SignGate = new();

    private static void Sign(string exe)
    {
        var failures = new List<string>();

        lock (SignGate)
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                using var sign = Process.Start(new ProcessStartInfo("/usr/bin/codesign")
                {
                    ArgumentList = { "-s", "-", "-f", exe },
                    RedirectStandardError = true,
                    UseShellExecute = false,
                })!;
                var stderr = sign.StandardError.ReadToEnd();
                sign.WaitForExit();
                if (sign.ExitCode == 0) return;

                failures.Add($"attempt {attempt}: exit {sign.ExitCode}: {stderr.Trim()}");
                Thread.Sleep(250 * attempt);
            }
        }

        Assert.Fail("codesign could not sign the stand-in:\n" + string.Join("\n", failures));
    }

    // Runs the stand-in as bash with a script, so `ps` reports the stand-in's
    // own path. ignoreTerm makes it survive SIGTERM, for the SIGKILL arm.
    //
    // It waits on the builtin `read` against a stdin pipe this test holds open,
    // and never forks. A first version looped on an external `sleep`, and every
    // fork is a child that carries the stand-in's path in `ps` until it execs —
    // so under load the script, or Running below, could catch a pid that was
    // gone a moment later, and one full run failed on exactly that.
    // `beforeTrap` is shell run ahead of the trap, for the case that pins the
    // race below; nothing else passes it.
    private Process Launch(string exe, bool ignoreTerm = false, string beforeTrap = "")
    {
        // A stand-in that ignores SIGTERM says so on stdout once the trap is in
        // place, and this waits for that line (CB-243). Waiting only for it to
        // appear in ps was not enough: ps lists it the moment it is exec'd, and
        // the `trap '' TERM` runs a moment later inside bash. A SIGTERM landing
        // in that gap killed the "stubborn" copy outright, the script saw it
        // gone and correctly stopped waiting, and the test reported "SIGKILL
        // came before the grace ran out" about a run in which no SIGKILL was
        // ever sent. Forced by sleeping a second before the trap: 3 of 3 failed
        // that way, the stand-in exiting 143 (SIGTERM) in 0.26 s, against 137
        // (SIGKILL) in 1.60 s when the trap was already set.
        var body = ignoreTerm
            ? beforeTrap + "trap '' TERM; echo ready; while :; do read -r _ || :; done"
            : "while :; do read -r _ || :; done";
        var process = Process.Start(new ProcessStartInfo(exe)
        {
            ArgumentList = { "-c", body },
            RedirectStandardInput = true,
            RedirectStandardOutput = ignoreTerm,
            UseShellExecute = false,
        })!;
        _started.Add(process);

        if (ignoreTerm)
        {
            // The signal itself, with a backstop only for a stand-in that never
            // gets as far as its trap.
            var ready = process.StandardOutput.ReadLineAsync();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "the stand-in never set its SIGTERM trap");
            Assert.Equal("ready", ready.Result);
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!Running(exe).Contains(process.Id))
        {
            Assert.True(DateTime.UtcNow < deadline, "the stand-in never showed up in ps");
            Thread.Sleep(50);
        }

        return process;
    }

    private static List<int> Running(string exe)
    {
        using var ps = Process.Start(new ProcessStartInfo("/bin/ps")
        {
            ArgumentList = { "-axo", "pid=,comm=" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();

        var pids = new List<int>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimStart();
            var space = trimmed.IndexOf(' ');
            if (space <= 0) continue;
            if (trimmed[(space + 1)..].TrimStart() == exe && int.TryParse(trimmed[..space], out var pid))
                pids.Add(pid);
        }

        return pids;
    }

    private sealed record Result(int ExitCode, string Stdout, string Stderr, TimeSpan Took);

    private static Result Stop(string[] args, string? grace = null)
    {
        var psi = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Script);
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (grace is not null) psi.Environment["CLAUDE_BUDDY_STOP_GRACE_SECONDS"] = grace;

        var clock = Stopwatch.StartNew();
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new Result(process.ExitCode, stdout, stderr, clock.Elapsed);
    }

    private static int[] Pids(string stdout) =>
        stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Order().ToArray();

    // Every copy running from the path is stopped and named — two, as the
    // 26 Sep machine had: one launched from a shell, one from launchd.
    [MacInstallFact]
    public void Stops_every_copy_running_from_the_path()
    {
        var exe = StandIn("Claude Buddy");
        var first = Launch(exe);
        var second = Launch(exe);

        var result = Stop([exe]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { first.Id, second.Id }.Order().ToArray(), Pids(result.Stdout));
        Assert.True(first.WaitForExit(5000));
        Assert.True(second.WaitForExit(5000));
        Assert.Empty(Running(exe));
    }

    // The negative control: a process running from any other path — another
    // build of Buddy in a worktree, a dist/ copy someone is trying — is left
    // alone, so a pass above cannot come from killing everything named
    // ClaudeBuddy.
    [MacInstallFact]
    public void Leaves_a_copy_at_another_path_alone()
    {
        var installed = StandIn("Claude Buddy");
        var elsewhere = StandIn("Claude Buddy dev");
        var target = Launch(installed);
        var bystander = Launch(elsewhere);

        var result = Stop([installed]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { target.Id }, Pids(result.Stdout));
        Assert.True(target.WaitForExit(5000));
        Assert.False(bystander.HasExited);
        Assert.Equal(new[] { bystander.Id }, Running(elsewhere).ToArray());
    }

    // A copy that ignores SIGTERM is still gone once the grace runs out —
    // without the SIGKILL arm, the install would go ahead with it running.
    [MacInstallFact]
    public void Kills_a_copy_that_ignores_sigterm_after_the_grace() => KillsAStubbornCopyAfterTheGrace();

    // The same, with the stand-in slow to set its trap (CB-243). A second
    // before `trap '' TERM` is the startup gap that flaked under load, widened
    // until it is certain; the old Launch failed this every time. Kept as well
    // as fixed, because it pins the property — wait for the trap, not for ps —
    // rather than how quickly bash happens to start.
    [MacInstallFact]
    public void Kills_a_copy_that_ignores_sigterm_even_when_it_is_slow_to_say_so() =>
        KillsAStubbornCopyAfterTheGrace(beforeTrap: "sleep 1; ");

    private void KillsAStubbornCopyAfterTheGrace(string beforeTrap = "")
    {
        var exe = StandIn("Claude Buddy");
        var stubborn = Launch(exe, ignoreTerm: true, beforeTrap: beforeTrap);

        var result = Stop([exe], grace: "1");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { stubborn.Id }, Pids(result.Stdout));
        Assert.True(stubborn.WaitForExit(5000));

        // How it died, not only when: 137 is 128 + SIGKILL. A copy that died
        // to the SIGTERM instead (143) never exercised the arm this is about,
        // which is exactly what the old timing-only assertion misreported.
        Assert.Equal(137, stubborn.ExitCode);
        Assert.True(result.Took >= TimeSpan.FromSeconds(1), "the script sent SIGKILL before the grace ran out");
        Assert.Empty(Running(exe));
    }

    // Nothing running from the path: nothing printed, nothing signalled, and
    // success — the ordinary first install.
    [MacInstallFact]
    public void Does_nothing_when_no_copy_is_running()
    {
        var exe = StandIn("Claude Buddy");

        var result = Stop([exe]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [MacInstallFact]
    public void Refuses_to_run_without_a_path()
    {
        var result = Stop([]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("usage", result.Stderr);
    }

    // CB-255: the new bundle and the legacy one in one call, as --install
    // makes it. Both are stopped and named; a third build at another path —
    // the negative control — is left running, so a pass cannot come from the
    // script stopping everything once it has more than one path.
    [MacInstallFact]
    public void Stops_copies_running_from_any_of_several_paths()
    {
        var current = StandIn("Orbweaver");
        var legacy = StandIn("Claude Buddy");
        var elsewhere = StandIn("Claude Buddy dev");
        var fromCurrent = Launch(current);
        var fromLegacy = Launch(legacy);
        var bystander = Launch(elsewhere);

        var result = Stop([current, legacy]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { fromCurrent.Id, fromLegacy.Id }.Order().ToArray(), Pids(result.Stdout));
        Assert.True(fromCurrent.WaitForExit(5000));
        Assert.True(fromLegacy.WaitForExit(5000));
        Assert.False(bystander.HasExited);
        Assert.Equal(new[] { bystander.Id }, Running(elsewhere).ToArray());
    }

    // One path running, one not — the ordinary upgrade from a machine where
    // only the legacy bundle was ever installed.
    [MacInstallFact]
    public void A_path_with_nothing_running_beside_one_that_has_a_copy_is_fine()
    {
        var current = StandIn("Orbweaver");
        var legacy = StandIn("Claude Buddy");
        var fromLegacy = Launch(legacy);

        var result = Stop([current, legacy]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { fromLegacy.Id }, Pids(result.Stdout));
        Assert.True(fromLegacy.WaitForExit(5000));
    }

    // An empty path anywhere in the list is a caller's mistake, refused
    // rather than read as "nothing was running there".
    [MacInstallFact]
    public void Refuses_an_empty_path_among_several()
    {
        var current = StandIn("Orbweaver");

        var result = Stop([current, ""]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("usage", result.Stderr);
    }
}
