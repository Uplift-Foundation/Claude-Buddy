using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests;

// Exercises ClaudeBuddyHook.sh — the bash half of the hook that Claude Code
// and Codex invoke on every tool call — as a real subprocess, the same way
// the CLIs themselves do: argv is `[claude|codex] <idle|generating|waiting|ended>`,
// the JSON payload arrives on stdin, and the only observable contract is the
// exit code, stdout, and stderr (see ClaudeBuddyHook.sh's own header comment)
// plus whatever it writes under $TMPDIR/claude_buddy.
//
// Every test point at its own fresh TMPDIR (Directory.CreateTempSubdirectory)
// so nothing here ever touches a real /tmp/claude_buddy or a developer's
// actual $HOME/.codex — except the path-traversal test below, which proves
// that isolation itself is not airtight.
public class HookScriptShTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string HookScript = Path.Combine(RepoRoot, "ClaudeBuddyHook.sh");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ClaudeBuddyHook.sh")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find ClaudeBuddyHook.sh by walking up from " + AppContext.BaseDirectory);
    }

    private sealed record HookResult(int ExitCode, string Stdout, string Stderr);

    // Runs the real script as a subprocess. extraEnv is layered on top of a
    // full copy of this process's environment (ProcessStartInfo.Environment
    // starts pre-populated with it) so PATH, HOME etc. still resolve — only
    // TMPDIR and whatever extraEnv names are overridden, exactly like a real
    // hook invocation only ever has TMPDIR (and sometimes CODEX_HOME)
    // pointed somewhere specific by its caller.
    private static HookResult RunHook(
        string agent,
        string state,
        string payloadJson,
        string tmpDir,
        IDictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(HookScript);
        psi.ArgumentList.Add(agent);
        psi.ArgumentList.Add(state);
        psi.Environment["TMPDIR"] = tmpDir;
        if (extraEnv is not null)
        {
            foreach (var (key, value) in extraEnv)
                psi.Environment[key] = value;
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start bash");

        // A broken pipe here is a pass, not a failure.
        //
        // The hook is allowed to exit before it ever reads stdin, and one test
        // below asserts precisely that: an unrecognised state hits `*) exit 0`
        // before `PAYLOAD=$(cat)`. When it wins that race the pipe is already
        // closed and this write throws IOException("Broken pipe") — so the
        // harness was failing the very behaviour it exists to verify. Locally
        // the process was usually still alive and it passed; under CI load it
        // was not, and it failed on both runners.
        //
        // Swallowed only around the write: the assertions on exit code, stdout
        // and stderr below are untouched, so a hook that genuinely misbehaves
        // still fails.
        try
        {
            process.StandardInput.Write(payloadJson);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Exited without reading its input, which some states do by design.
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new HookResult(process.ExitCode, stdout, stderr);
    }

    // The single most important invariant in the whole script (see its own
    // header comment): under Codex, any stdout at all is read as invalid
    // permission-request JSON, and exit code 2 specifically means "deny". A
    // hook that ever violates this would start silently refusing the user's
    // own tool approvals. Asserted in full in every single test method below
    // rather than factored into a helper that could be skipped by accident.
    private static void AssertSilentSuccess(HookResult result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    private static string StatusDir(string tmpDir) =>
        Path.Combine(tmpDir.TrimEnd('/'), "claude_buddy");

    private static string StatusFile(string tmpDir, string sessionId) =>
        Path.Combine(StatusDir(tmpDir), sessionId + ".txt");

    private static string Payload(object fields) => JsonSerializer.Serialize(fields);

    // A codex-agent test that doesn't care about the rollout fallback still
    // must not touch the developer's real ~/.codex (the script falls back to
    // $HOME/.codex whenever CODEX_HOME is unset), so every such test pins
    // CODEX_HOME at an empty scratch directory.
    private static string EmptyCodexHome(string tmpDir)
    {
        var dir = Path.Combine(tmpDir, "codex-home");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [UnixFact]
    public void HookAlwaysExitsZeroWithNoOutput_ForEveryLiveStateAndAgent()
    {
        foreach (var agent in new[] { "claude", "codex" })
        foreach (var state in new[] { "idle", "generating", "waiting" })
        {
            var tmp = Directory.CreateTempSubdirectory("cb-hook-");
            var payload = Payload(new { session_id = "s-" + agent + "-" + state, cwd = "/tmp/proj", transcript_path = "" });
            var env = new Dictionary<string, string> { ["CODEX_HOME"] = EmptyCodexHome(tmp.FullName) };

            var result = RunHook(agent, state, payload, tmp.FullName, env);

            AssertSilentSuccess(result);
        }
    }

    [UnixFact]
    public void HookIgnoresAnUnrecognisedState_SilentlyAndWithoutReadingStdin()
    {
        // ClaudeBuddyHook.sh checks $STATE against its four valid values
        // before it ever reads stdin (`case "$STATE" in ... *) exit 0 ;; esac`
        // comes before `PAYLOAD=$(cat)`), so a bogus state is a fast, silent
        // no-op rather than an attempt to parse whatever was piped in.
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        var payload = Payload(new { session_id = "s1", cwd = "/tmp/proj", transcript_path = "" });

        var result = RunHook("claude", "bogus-state", payload, tmp.FullName);

        AssertSilentSuccess(result);
        Assert.False(Directory.Exists(StatusDir(tmp.FullName)));
    }

    [UnixFact]
    public void EndedStateDeletesTheStatusFileAndExitsSilently()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        Directory.CreateDirectory(StatusDir(tmp.FullName));
        var file = StatusFile(tmp.FullName, "s1");
        File.WriteAllText(file, "stale status");

        var payload = Payload(new { session_id = "s1", cwd = "/tmp/proj", transcript_path = "" });
        var result = RunHook("claude", "ended", payload, tmp.FullName);

        AssertSilentSuccess(result);
        Assert.False(File.Exists(file), "ended must delete the session's status file");
    }

    [UnixFact]
    public void EndedStateWithNoExistingStatusFile_IsStillSilentAndSuccessful()
    {
        // `rm -f` on a file that was never written (Ctrl+C'd session, a
        // duplicate SessionEnd, etc.) must not turn into a noisy failure.
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        var payload = Payload(new { session_id = "never-existed", cwd = "/tmp/proj", transcript_path = "" });

        var result = RunHook("claude", "ended", payload, tmp.FullName);

        AssertSilentSuccess(result);
    }

    // *** Security finding ***
    //
    // FILE="$DIR/$SESSION_ID.txt" (ClaudeBuddyHook.sh, ~line 76) builds the
    // status file's path by direct string concatenation of the attacker-
    // controlled session_id straight out of the hook payload — no
    // sanitisation. The SAFE_ID scrubber that exists in the file
    // (`tr -cd '0-9a-fA-F-'`, ~line 223) is applied only to the value handed
    // to sqlite3 later on, never to $SESSION_ID before $FILE is built. A
    // session_id containing ".." therefore walks the resulting path out of
    // $DIR/claude_buddy entirely.
    //
    // This test proves the escape is real rather than theoretical: it hands
    // the hook `session_id: "../../evil"` and shows the JSON status blob
    // (cwd, tty, tmux socket, etc.) actually lands two directories above the
    // isolated temp dir, outside both the intended claude_buddy status
    // directory *and* the fresh TMPDIR the test set up for isolation.
    [UnixFact]
    public void SessionIdPathTraversal_EscapesTheStatusDirectory()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-traversal-");
        var maliciousSessionId = "../../evil";
        var payload = Payload(new { session_id = maliciousSessionId, cwd = "/tmp/proj", transcript_path = "" });

        // $DIR/$SESSION_ID.txt, normalised the way the shell's own path
        // resolution (and the filesystem) will normalise it.
        var escapedPath = Path.GetFullPath(
            Path.Combine(StatusDir(tmp.FullName), maliciousSessionId + ".txt"));

        try
        {
            var result = RunHook("claude", "idle", payload, tmp.FullName);

            // Even while writing outside its own sandbox, the hook still
            // honours the Codex-safety invariant — exit 0, nothing printed.
            AssertSilentSuccess(result);

            Assert.True(
                File.Exists(escapedPath),
                $"expected the path-traversal escape to actually write a file at {escapedPath} " +
                "— if this now fails, the script has been sanitising SESSION_ID before building " +
                "$FILE and this is no longer a real finding.");

            Assert.False(
                escapedPath.StartsWith(StatusDir(tmp.FullName), StringComparison.Ordinal),
                "the written file should be OUTSIDE the intended claude_buddy status directory " +
                "— that is the vulnerability being demonstrated.");

            // Confirms it escaped the test's own isolation, not just the
            // claude_buddy subfolder.
            Assert.False(
                escapedPath.StartsWith(tmp.FullName, StringComparison.Ordinal),
                "the written file escaped even the test's own temp sandbox, landing in a " +
                "directory the test does not own.");

            var written = File.ReadAllText(escapedPath);
            Assert.Contains("\"cwd\":\"/tmp/proj\"", written);
        }
        finally
        {
            // The whole point of this test is that the file lands outside
            // anything `using var tmp` will clean up, so it has to be swept
            // up by hand or it leaks a stray file into the shared temp tree
            // on every run.
            if (File.Exists(escapedPath)) File.Delete(escapedPath);
        }
    }

    [UnixFact]
    public void AutoColorMarker_AppendsAgentColorRecordMatchingTheScriptsOwnCksumHash()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        Directory.CreateDirectory(StatusDir(tmp.FullName));
        File.WriteAllText(Path.Combine(StatusDir(tmp.FullName), ".auto-color"), "");

        var projectDir = Directory.CreateTempSubdirectory("cb-hook-proj-").FullName;
        var transcript = Path.Combine(projectDir, "t.jsonl");
        File.WriteAllText(transcript, "");

        const string cwd = "/tmp/some-fixed-project-path";
        var payload = Payload(new { session_id = "s1", cwd, transcript_path = transcript });

        var result = RunHook("claude", "idle", payload, tmp.FullName);
        AssertSilentSuccess(result);

        // Golden value computed the same way the script computes it —
        // `cksum` of the cwd bytes, mod 11, 1-indexed into this exact list —
        // rather than a hardcoded magic number, so this test keeps meaning
        // if the algorithm (or the palette) ever changes.
        var expectedColor = ExpectedAutoColor(cwd);

        var status = File.ReadAllText(StatusFile(tmp.FullName, "s1"));
        Assert.Contains($"\"color\":\"{expectedColor}\"", status);

        var transcriptContents = File.ReadAllText(transcript);
        Assert.Contains(
            $"{{\"type\":\"agent-color\",\"agentColor\":\"{expectedColor}\",\"sessionId\":\"s1\"}}",
            transcriptContents);
    }

    [UnixFact]
    public void WithoutTheAutoColorMarker_NoColorRecordIsEverAppended()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        // No .auto-color marker file created — this is the off-by-default
        // state install-* leaves things in.
        var projectDir = Directory.CreateTempSubdirectory("cb-hook-proj-").FullName;
        var transcript = Path.Combine(projectDir, "t.jsonl");
        File.WriteAllText(transcript, "");

        var payload = Payload(new { session_id = "s1", cwd = "/tmp/some-project", transcript_path = transcript });
        var result = RunHook("claude", "idle", payload, tmp.FullName);
        AssertSilentSuccess(result);

        Assert.Equal("", File.ReadAllText(transcript));
        var status = File.ReadAllText(StatusFile(tmp.FullName, "s1"));
        Assert.Contains("\"color\":\"\"", status);
    }

    // Runs the real `cksum` binary the same way the script does, rather than
    // re-implementing the CRC in C#, so a change to the script's algorithm
    // (or to this repo's chosen palette) breaks this test instead of the
    // test quietly agreeing with a stale expectation.
    private static string ExpectedAutoColor(string cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cksum",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        process.StandardInput.Write(cwd);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        // "<checksum> <byte-count> [filename]"
        var hash = ulong.Parse(output.Split(' ')[0]);

        string[] palette =
        {
            "red", "orange", "yellow", "green", "teal", "cyan",
            "blue", "purple", "violet", "magenta", "pink"
        };
        var index = (int)(hash % (ulong)palette.Length);
        return palette[index];
    }

    [UnixFact]
    public void CustomTitleWinsOverAiTitle_RegardlessOfWhichWasWrittenLast()
    {
        AssertCustomTitleWins(writeCustomFirst: true);
        AssertCustomTitleWins(writeCustomFirst: false);
    }

    private static void AssertCustomTitleWins(bool writeCustomFirst)
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        var projectDir = Directory.CreateTempSubdirectory("cb-hook-proj-").FullName;
        var transcript = Path.Combine(projectDir, "t.jsonl");

        var customLine = "{\"type\":\"custom-title\",\"customTitle\":\"my name\",\"sessionId\":\"s1\"}";
        var aiLine = "{\"type\":\"ai-title\",\"aiTitle\":\"auto name\",\"sessionId\":\"s1\"}";
        var lines = writeCustomFirst
            ? new[] { customLine, aiLine }
            : new[] { aiLine, customLine };
        File.WriteAllLines(transcript, lines);

        var payload = Payload(new { session_id = "s1", cwd = "/tmp/proj", transcript_path = transcript });
        var result = RunHook("claude", "idle", payload, tmp.FullName);
        AssertSilentSuccess(result);

        var status = File.ReadAllText(StatusFile(tmp.FullName, "s1"));
        Assert.True(
            status.Contains("\"title\":\"my name\"", StringComparison.Ordinal),
            "custom-title should win over ai-title when the custom record was written " +
            (writeCustomFirst ? "first" : "last") + ". Status file was: " + status);
    }

    [UnixFact]
    public void FieldExtractsTheTopLevelCwd_NotANestedToolInputCwd()
    {
        // Regression test for exactly the bug field()'s own comment
        // describes: Codex payloads nest tool_input as arbitrary JSON that
        // can itself carry a "cwd" key (a command's own working directory),
        // which a naive last-match extraction would pick over the real
        // top-level cwd — silently renaming/repositioning the orb for the
        // wrong directory.
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        var payloadJson =
            "{\"session_id\":\"s2\",\"cwd\":\"/top/level\"," +
            "\"tool_input\":{\"cwd\":\"file:///nested/level\"}," +
            "\"transcript_path\":\"\"}";

        var result = RunHook("claude", "idle", payloadJson, tmp.FullName);
        AssertSilentSuccess(result);

        var status = File.ReadAllText(StatusFile(tmp.FullName, "s2"));
        Assert.Contains("\"cwd\":\"/top/level\"", status);
        Assert.DoesNotContain("/nested/level", status);
    }

    [UnixFact]
    public void CodexRolloutFallback_GlobsForTheSessionsRolloutFile_WithoutCrashingOrPrinting()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        var codexHome = Directory.CreateTempSubdirectory("cb-hook-codexhome-").FullName;
        var sessionId = "abc123-session";
        var rolloutDir = Path.Combine(codexHome, "sessions", "2026", "08", "21");
        Directory.CreateDirectory(rolloutDir);
        var rolloutFile = Path.Combine(rolloutDir, $"rollout-2026-08-21T00-00-00-{sessionId}.jsonl");
        // Content the hook "can't read meaningfully" — no session_meta, no
        // UserMessage row — which is exactly the point: this exercises the
        // glob resolution, not rollout parsing.
        File.WriteAllText(rolloutFile, "not a real codex rollout row\n");

        // transcript_path deliberately empty — that's what triggers the glob
        // fallback (ClaudeBuddyHook.sh, ~line 89-93).
        var payload = Payload(new { session_id = sessionId, cwd = "/tmp/proj", transcript_path = "" });
        var env = new Dictionary<string, string> { ["CODEX_HOME"] = codexHome };

        var result = RunHook("codex", "idle", payload, tmp.FullName, env);

        AssertSilentSuccess(result);

        var status = File.ReadAllText(StatusFile(tmp.FullName, sessionId));
        Assert.Contains(rolloutFile.Replace("\\", "\\\\"), status);
    }

    [UnixFact]
    public void MissingSessionId_WritesTheStatusFileNamedUnknown()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        var payloadJson = "{\"cwd\":\"/tmp/proj\",\"transcript_path\":\"\"}";

        var result = RunHook("claude", "idle", payloadJson, tmp.FullName);
        AssertSilentSuccess(result);

        Assert.True(File.Exists(StatusFile(tmp.FullName, "unknown")));
    }
}
