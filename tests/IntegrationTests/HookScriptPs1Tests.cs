using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests;

// Exercises ClaudeBuddyHook.ps1 — the PowerShell twin of ClaudeBuddyHook.sh —
// as a real subprocess. Written by reading the script closely (there is no
// pwsh on the machine this was authored on, confirmed via `which pwsh`), so
// unlike HookScriptShTests this class could not be run locally while it was
// written; it is gated to skip everywhere except a real Windows runner,
// where it will run for the first time. Get the argv/JSON shape agreeing
// with the script exactly, because nothing here has been proven against a
// live pwsh.
//
// Argv shape, from the script's param() block: -State <idle|generating|
// waiting|ended> (mandatory), -Agent <claude|codex> (default claude),
// -TempDir <path> (default '' -> Path.GetTempPath()). The payload arrives on
// stdin as JSON, same as the bash twin.
public class HookScriptPs1Tests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string HookScript = Path.Combine(RepoRoot, "ClaudeBuddyHook.ps1");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ClaudeBuddyHook.ps1")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find ClaudeBuddyHook.ps1 by walking up from " + AppContext.BaseDirectory);
    }

    private sealed record HookResult(int ExitCode, string Stdout, string Stderr);

    private static HookResult RunHook(
        string agent,
        string state,
        string payloadJson,
        string tempDir,
        IDictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(HookScript);
        psi.ArgumentList.Add("-State");
        psi.ArgumentList.Add(state);
        psi.ArgumentList.Add("-Agent");
        psi.ArgumentList.Add(agent);
        psi.ArgumentList.Add("-TempDir");
        psi.ArgumentList.Add(tempDir);

        if (extraEnv is not null)
        {
            foreach (var (key, value) in extraEnv)
                psi.Environment[key] = value;
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pwsh");

        process.StandardInput.Write(payloadJson);
        process.StandardInput.Close();

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new HookResult(process.ExitCode, stdout, stderr);
    }

    // Same Codex-safety invariant as the bash twin's own header explains:
    // any stdout is invalid permission-request JSON to Codex, and exit code
    // 2 specifically means "deny". Repeated in full in every test method.
    private static void AssertSilentSuccess(HookResult result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    private static string StatusDir(string tempDir) => Path.Combine(tempDir, "claude_buddy");

    private static string StatusFile(string tempDir, string sessionId) =>
        Path.Combine(StatusDir(tempDir), sessionId + ".txt");

    private static string Payload(object fields) => JsonSerializer.Serialize(fields);

    // The Windows script computes its own CRC-32 (no `cksum` binary on
    // Windows) with the explicit goal — per its own comment — of agreeing
    // with the bash twin's `cksum` byte-for-byte, so the same project
    // directory gets the same colour on either platform. This is a direct
    // C# port of ClaudeBuddyHook.ps1's Get-CksumCrc, used to build the
    // golden expectation for the auto-color test below without guessing a
    // magic number.
    //
    // Cross-checked (see WindowsCrc32Port_AgreesWithThePosixCksumBinary,
    // a plain, always-runs [Fact] below) against the real `cksum` binary's
    // output for "/tmp/proj" — 591481296 — captured by hand on this machine.
    // That check runs on every OS and does not touch pwsh, so it validates
    // this port even though the pwsh-invoking tests around it cannot run
    // here.
    internal static uint WindowsCksumCrc(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        uint crc = 0;

        void Roll(byte value)
        {
            crc ^= (uint)value << 24;
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }

        foreach (var b in bytes) Roll(b);

        var len = bytes.Length;
        while (len > 0)
        {
            Roll((byte)(len & 0xFF));
            len /= 256;
        }

        return ~crc;
    }

    [Fact]
    public void WindowsCrc32Port_AgreesWithThePosixCksumBinary()
    {
        // Golden value from running the real `cksum` on this machine:
        //   printf '%s' '/tmp/proj' | cksum   =>   591481296 9
        // Verified by hand while writing HookScriptShTests's auto-color
        // test. If this ever fails, the ps1 script's derived colour has
        // silently stopped agreeing with the sh script's for the same
        // directory — exactly the cross-platform guarantee its own comment
        // claims.
        Assert.Equal(591481296u, WindowsCksumCrc("/tmp/proj"));
    }

    private static string ExpectedAutoColor(string cwd)
    {
        string[] palette =
        {
            "red", "orange", "yellow", "green", "teal", "cyan",
            "blue", "purple", "violet", "magenta", "pink"
        };
        var index = (int)(WindowsCksumCrc(cwd) % (uint)palette.Length);
        return palette[index];
    }

    [WindowsFact]
    public void HookAlwaysExitsZeroWithNoOutput_ForEveryLiveStateAndAgent()
    {
        foreach (var agent in new[] { "claude", "codex" })
        foreach (var state in new[] { "idle", "generating", "waiting" })
        {
            var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
            var payload = Payload(new { session_id = "s-" + agent + "-" + state, cwd = "C:\\proj", transcript_path = "" });
            var codexHome = Path.Combine(tempDir, "codex-home");
            Directory.CreateDirectory(codexHome);
            var env = new Dictionary<string, string> { ["CODEX_HOME"] = codexHome };

            var result = RunHook(agent, state, payload, tempDir, env);

            AssertSilentSuccess(result);
        }
    }

    [WindowsFact]
    public void EndedStateDeletesTheStatusFileAndExitsSilently()
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        Directory.CreateDirectory(StatusDir(tempDir));
        var file = StatusFile(tempDir, "s1");
        File.WriteAllText(file, "stale status");

        var payload = Payload(new { session_id = "s1", cwd = "C:\\proj", transcript_path = "" });
        var result = RunHook("claude", "ended", payload, tempDir);

        AssertSilentSuccess(result);
        Assert.False(File.Exists(file), "ended must delete the session's status file");
    }

    [WindowsFact]
    public void MissingSessionId_WritesTheStatusFileNamedUnknown()
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        var payloadJson = "{\"cwd\":\"C:\\\\proj\",\"transcript_path\":\"\"}";

        var result = RunHook("claude", "idle", payloadJson, tempDir);
        AssertSilentSuccess(result);

        Assert.True(File.Exists(StatusFile(tempDir, "unknown")));
    }

    [WindowsFact]
    public void CustomTitleWinsOverAiTitle_RegardlessOfWhichWasWrittenLast()
    {
        AssertCustomTitleWins(writeCustomFirst: true);
        AssertCustomTitleWins(writeCustomFirst: false);
    }

    private static void AssertCustomTitleWins(bool writeCustomFirst)
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        var projectDir = Directory.CreateTempSubdirectory("cb-hook-ps1-proj-").FullName;
        var transcript = Path.Combine(projectDir, "t.jsonl");

        var customLine = "{\"type\":\"custom-title\",\"customTitle\":\"my name\",\"sessionId\":\"s1\"}";
        var aiLine = "{\"type\":\"ai-title\",\"aiTitle\":\"auto name\",\"sessionId\":\"s1\"}";
        var lines = writeCustomFirst
            ? new[] { customLine, aiLine }
            : new[] { aiLine, customLine };
        File.WriteAllLines(transcript, lines);

        var payload = Payload(new { session_id = "s1", cwd = "C:\\proj", transcript_path = transcript });
        var result = RunHook("claude", "idle", payload, tempDir);
        AssertSilentSuccess(result);

        var status = File.ReadAllText(StatusFile(tempDir, "s1"));
        Assert.True(
            status.Contains("\"title\":\"my name\"", StringComparison.Ordinal),
            "custom-title should win over ai-title when the custom record was written " +
            (writeCustomFirst ? "first" : "last") + ". Status file was: " + status);
    }

    [WindowsFact]
    public void AutoColorMarker_AppendsAgentColorRecordMatchingThePortedCksumHash()
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        Directory.CreateDirectory(StatusDir(tempDir));
        File.WriteAllText(Path.Combine(StatusDir(tempDir), ".auto-color"), "");

        var projectDir = Directory.CreateTempSubdirectory("cb-hook-ps1-proj-").FullName;
        var transcript = Path.Combine(projectDir, "t.jsonl");
        File.WriteAllText(transcript, "");

        const string cwd = "C:\\some\\fixed\\project\\path";
        var payload = Payload(new { session_id = "s1", cwd, transcript_path = transcript });

        var result = RunHook("claude", "idle", payload, tempDir);
        AssertSilentSuccess(result);

        var expectedColor = ExpectedAutoColor(cwd);

        var status = File.ReadAllText(StatusFile(tempDir, "s1"));
        Assert.Contains($"\"color\":\"{expectedColor}\"", status);

        var transcriptContents = File.ReadAllText(transcript);
        Assert.Contains(
            $"{{\"type\":\"agent-color\",\"agentColor\":\"{expectedColor}\",\"sessionId\":\"s1\"}}",
            transcriptContents);
    }

    [WindowsFact]
    public void CodexRolloutFallback_FindsTheRolloutFile_WithoutCrashingOrPrinting()
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        var codexHome = Directory.CreateTempSubdirectory("cb-hook-ps1-codexhome-").FullName;
        var sessionId = "abc123-session";
        var rolloutDir = Path.Combine(codexHome, "sessions", "2026", "08", "21");
        Directory.CreateDirectory(rolloutDir);
        var rolloutFile = Path.Combine(rolloutDir, $"rollout-2026-08-21T00-00-00-{sessionId}.jsonl");
        File.WriteAllText(rolloutFile, "not a real codex rollout row\n");

        var payload = Payload(new { session_id = sessionId, cwd = "C:\\proj", transcript_path = "" });
        var env = new Dictionary<string, string> { ["CODEX_HOME"] = codexHome };

        var result = RunHook("codex", "idle", payload, tempDir, env);

        AssertSilentSuccess(result);

        var status = File.ReadAllText(StatusFile(tempDir, sessionId));
        Assert.Contains(rolloutFile.Replace("\\", "\\\\"), status);
    }
}
