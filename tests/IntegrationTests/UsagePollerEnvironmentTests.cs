using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace ClaudeBuddy.Tests;

// What the usage poll is actually launched with (CB-113).
//
// Same technique as AgentRosterEnvironmentTests, for the same reason: a unit
// test can assert that UsageProcess does or does not put CLAUDE_CONFIG_DIR in
// a dictionary, but the actual claim is about what a *child process* sees, and
// the whole bug was a wrong belief about that. So this runs a real
// subprocess — a stand-in `claude` that prints what it was given — and reads
// the answer back out of its stdout.
//
// Unlike AgentRoster's CB-42, the null case here is deliberately not "leave it
// alone" — it is "remove it". The first test below is the regression test for
// CB-113 itself: without setting a sentinel in *this* process first, a test
// asserting "the child sees nothing" would pass on a clean CI runner for the
// wrong reason, exactly the way the bug shipped unnoticed. Setting the
// sentinel is what makes this fail against the pre-fix code (which would
// leave the sentinel visible to the child) and pass against the fix.
[Collection("ConfigDirEnv")]
public class UsagePollerEnvironmentTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _previous;

    public UsagePollerEnvironmentTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-usage-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _previous);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ANullConfigDirLeavesTheChildSeeingNothingEvenWhenTheParentHasOne()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", "/Users/someone/.claude-sentinel");

        Assert.Equal(string.Empty, RunFakeClaude(configDir: null));
    }

    [Fact]
    public void ANamedAccountReachesTheChildProcess()
    {
        var seen = RunFakeClaude(configDir: "/Users/someone/.claude-board");

        Assert.Equal("/Users/someone/.claude-board", seen);
    }

    // Runs a stand-in `claude` through the real UsageProcess and returns what
    // it saw in CLAUDE_CONFIG_DIR.
    private string RunFakeClaude(string? configDir)
    {
        var script = WriteFakeClaude();
        var psi = UsagePoller.UsageProcess(script, configDir);

        using var process = Process.Start(psi);
        Assert.NotNull(process);

        // The real factory redirects stdin too, since RunOne writes the
        // control request to it. The stand-in never reads it, so it is
        // closed here rather than left open — an unclosed pipe the child
        // never drains is exactly the kind of thing that hangs a test.
        process!.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEnd();
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
