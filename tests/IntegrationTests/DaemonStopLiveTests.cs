using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests;

// Opt-in, for the reason LiveBridgeFact is: each case starts a **real background
// job** under the machine's own logged-in Claude Code account, and so spends a
// little of its quota. Run it deliberately:
//
//   CLAUDE_BUDDY_LIVE_DAEMON_TESTS=1 dotnet test tests/IntegrationTests/ClaudeBuddy.IntegrationTests.csproj
//
// Its own switch rather than LiveBridgeFact's, which also retags the Remote
// Control relay for the whole process — nothing these cases want.
public sealed class LiveDaemonFactAttribute : FactAttribute
{
    public LiveDaemonFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_BUDDY_LIVE_DAEMON_TESTS") != "1")
        {
            Skip = "opt-in: starts a real Claude Code background job and spends quota (set CLAUDE_BUDDY_LIVE_DAEMON_TESTS=1)";
            return;
        }

        if (ClaudeBinary.Path is null) Skip = "no claude CLI on this machine";
    }
}

// CB-264, against the real daemon. The unit and UI suites pin what End decides;
// these pin the two claims about the daemon that the decision rests on, which
// are about a system nobody here controls and so get measured rather than
// asserted in a comment:
//
//   1. Signalling a parked job's worker does not end the job — the daemon
//      resumes it on a new pid. The negative control: if this ever stops
//      holding, the routing is merely redundant, and this says so.
//   2. BackgroundJobs.Stop does end it, and it stays ended. The excluded half
//      of the fix, run for real.
//
// A job parks on a permission prompt: default permission mode and a command
// that needs approval, which is the cheapest reliable way to reach "blocked".
public class DaemonStopLiveTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "cb264-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private string? _jobId;

    [LiveDaemonFact]
    public void SignallingAParkedWorkerDoesNotEndTheJob()
    {
        var (jobId, _, pid) = StartParkedJob("cb264-signal");

        Process.GetProcessById(pid).Kill();

        // Back on a different pid, still a job. Measured at about nine seconds;
        // a minute leaves room for a slow machine without hiding a real change.
        var back = WaitFor(TimeSpan.FromSeconds(60), () =>
            Row(jobId) is { } row && row.TryGetProperty("pid", out var p) && p.GetInt32() != pid);

        Assert.True(back, "the daemon did not resume the signalled job — CB-264's premise no longer holds");
    }

    [LiveDaemonFact]
    public void StopEndsAParkedJobAndItStaysEnded()
    {
        var (jobId, sessionId, _) = StartParkedJob("cb264-stop");

        Assert.True(BackgroundJobs.Stop(sessionId, transcriptPath: null));

        // Gone, and still gone after longer than the respawn above took.
        Assert.True(WaitFor(TimeSpan.FromSeconds(15), () => Row(jobId) is null));
        Thread.Sleep(TimeSpan.FromSeconds(20));
        Assert.Null(Row(jobId));
    }

    private (string JobId, string SessionId, int Pid) StartParkedJob(string name)
    {
        var started = Claude(_dir, "--bg", "--model", "haiku", "--permission-mode", "default", "-n", name,
            "Use the Bash tool to run exactly: curl -s https://example.com -o /dev/null");
        Assert.Equal(0, started.ExitCode);

        JsonElement? parked = null;
        Assert.True(WaitFor(TimeSpan.FromSeconds(90), () =>
        {
            parked = Listing().Cast<JsonElement?>().FirstOrDefault(r =>
                Text(r!.Value, "name") == name && Text(r.Value, "state") == "blocked" && r.Value.TryGetProperty("pid", out _));
            return parked is not null;
        }), "the scratch job never parked");

        _jobId = Text(parked!.Value, "id");
        return (_jobId!, Text(parked.Value, "sessionId")!, parked.Value.GetProperty("pid").GetInt32());
    }

    private JsonElement? Row(string jobId) =>
        Listing().Cast<JsonElement?>().FirstOrDefault(r => Text(r!.Value, "id") == jobId);

    private List<JsonElement> Listing()
    {
        var (exit, stdout) = Claude(_dir, "agents", "--json");
        if (exit != 0) return new();
        using var document = JsonDocument.Parse(stdout);
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool WaitFor(TimeSpan limit, Func<bool> condition)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            Thread.Sleep(TimeSpan.FromSeconds(2));
        }
        return condition();
    }

    private static (int ExitCode, string Stdout) Claude(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo(ClaudeBinary.Path!)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(true); return (-1, ""); }
        return (process.ExitCode, stdout.Result);
    }

    // Whatever happened, the scratch job is stopped and deleted, and its
    // directory removed: a failed run must not leave a parked job on the
    // machine for the user's own orbs to show.
    public void Dispose()
    {
        if (_jobId is not null)
        {
            Claude(_dir, "stop", _jobId);
            Claude(_dir, "rm", _jobId);
        }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
