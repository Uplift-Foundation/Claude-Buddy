using System.Diagnostics;
using Xunit;

namespace ClaudeBuddy.Tests;

// tools/stop-installed-buddy.sh, which `build-macos-app.sh --install` runs
// before it replaces /Applications/Claude Buddy.app (CB-206). Once the
// single-instance mutex spans sessions, the copy launchd starts after an
// install finds the old one holding it and exits, so the old one has to be
// gone first or the install leaves a stale Buddy running.
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
    private string StandIn(string name)
    {
        var macOs = Path.Combine(_dir, name + ".app", "Contents", "MacOS");
        Directory.CreateDirectory(macOs);
        var exe = Path.Combine(macOs, "ClaudeBuddy");
        File.Copy("/bin/bash", exe);
        Sign(exe);
        return exe;
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
    private Process Launch(string exe, bool ignoreTerm = false)
    {
        var body = (ignoreTerm ? "trap '' TERM; " : "") + "while :; do read -r _ || :; done";
        var process = Process.Start(new ProcessStartInfo(exe)
        {
            ArgumentList = { "-c", body },
            RedirectStandardInput = true,
            UseShellExecute = false,
        })!;
        _started.Add(process);

        // Until `ps` can see it under the stand-in's path, the script could not
        // either, and a test that raced it would pass for the wrong reason.
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
    public void Kills_a_copy_that_ignores_sigterm_after_the_grace()
    {
        var exe = StandIn("Claude Buddy");
        var stubborn = Launch(exe, ignoreTerm: true);

        var result = Stop([exe], grace: "1");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { stubborn.Id }, Pids(result.Stdout));
        Assert.True(result.Took >= TimeSpan.FromSeconds(1), "SIGKILL came before the grace ran out");
        Assert.True(stubborn.WaitForExit(5000));
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
}
