using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace ClaudeBuddy.Tests;

// What `claude agents --json` is actually launched with (CB-42).
//
// A unit test can assert that `AgentsProcess` does or does not put
// CLAUDE_CONFIG_DIR in a dictionary. That is not quite the claim: the claim is
// about the environment a *child process* ends up with, and the whole bug was a
// wrong belief about how one value in that environment behaves. So these run a
// real subprocess — a stand-in `claude` that prints what it was given — and read
// the answer back out of its stdout.
//
// The stand-in is a script rather than a mock because the seam being tested is
// the process boundary itself. Nothing here needs the real CLI, which is the
// point: this asserts the launch, and ParseAgentsJson asserts the answer.
public class AgentRosterEnvironmentTests : IDisposable
{
    private readonly string _dir;

    public AgentRosterEnvironmentTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-roster-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void A_named_account_reaches_the_child_process()
    {
        var seen = RunFakeClaude(configDir: "/Users/someone/.claude-board");

        Assert.Equal("/Users/someone/.claude-board", seen);
    }

    [Fact]
    public void The_default_account_leaves_the_child_with_what_this_process_has()
    {
        // The fix, from the far side of the process boundary: no variable of
        // this app's invention, so the child sees whatever Buddy itself has —
        // which for a Buddy launched from Finder or a login item is nothing,
        // and is the context an ordinary `claude` in a terminal would use.
        //
        // Asserted against this process's own value rather than against ""
        // precisely because inheritance is the behaviour being claimed: a test
        // that only passed on a machine with the variable unset would be a test
        // that passes for the wrong reason on CI.
        var inherited = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? string.Empty;

        Assert.Equal(inherited, RunFakeClaude(configDir: null));
    }

    [Fact]
    public void The_arguments_are_the_ones_the_registry_answers_to()
    {
        // Cheap, and it is the other half of "what was this launched with" —
        // a psi that carried the right environment and the wrong verb would
        // pass every assertion above.
        var psi = AgentRoster.AgentsProcess("/usr/local/bin/claude", null);

        Assert.Equal(new[] { "agents", "--json" }, psi.ArgumentList);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardOutput);
    }

    // Runs a stand-in `claude` through the real AgentsProcess and returns what
    // it saw in CLAUDE_CONFIG_DIR.
    private string RunFakeClaude(string? configDir)
    {
        var script = WriteFakeClaude();
        var psi = AgentRoster.AgentsProcess(script, configDir);

        using var process = Process.Start(psi);
        Assert.NotNull(process);

        var stdout = process!.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(10_000), "the stand-in claude did not exit");

        return stdout.Trim();
    }

    private string WriteFakeClaude()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var cmd = Path.Combine(_dir, "claude.cmd");

            // `echo.%VAR%` rather than `echo %VAR%` so an unset variable prints
            // an empty line instead of the literal name or "ECHO is off".
            File.WriteAllText(cmd, "@echo off\r\necho.%CLAUDE_CONFIG_DIR%\r\n");
            return cmd;
        }

        var sh = Path.Combine(_dir, "claude");

        File.WriteAllText(sh, "#!/bin/sh\nprintf '%s\\n' \"$CLAUDE_CONFIG_DIR\"\n");
        File.SetUnixFileMode(
            sh,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return sh;
    }
}
