using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers the JSON shape of SessionStatus (SessionManager.cs ~11-145) against
// the exact literal ClaudeBuddyHook.sh writes. Per this repo's fixture rule
// (CLAUDE.md, Testing section), a fixture should come from real output, not
// memory. There's no live hook run to capture here, so instead the fixture
// is derived from the hook script's own printf — the line that writes $FILE:
//
//   printf '{"state":"%s","cli":"%s","cwd":"%s","title":"%s","color":"%s",
//   "term_program":"%s","term_id":"%s","tty":"%s","tmux_socket":"%s",
//   "tmux_pane":"%s","tmux_bin":"%s","session_pid":%s,"transcript_path":"%s"}'
//
// (ClaudeBuddyHook.sh, near the end). That field list and order is the real,
// load-bearing contract — just as authoritative as a captured file, since it
// is literally what every macOS hook write produces.
public class SessionStatusJsonTests
{
    // Mirrors the printf's field order and quoting exactly. session_pid is
    // the one unquoted field, because the shell script substitutes a bare
    // number there.
    private const string HookProducedJson =
        "{\"state\":\"generating\",\"cli\":\"codex\",\"cwd\":\"/Users/warren/project\"," +
        "\"title\":\"Fix the login bug\",\"color\":\"green\"," +
        "\"term_program\":\"vscode\",\"term_id\":\"term-1\",\"tty\":\"/dev/ttys002\"," +
        "\"tmux_socket\":\"default\",\"tmux_pane\":\"%3\",\"tmux_bin\":\"/opt/homebrew/bin/tmux\"," +
        "\"session_pid\":12345,\"transcript_path\":\"/Users/warren/.codex/sessions/2026/08/21/rollout.jsonl\"}";

    [Fact]
    public void Deserialize_MapsEveryHookWrittenFieldOntoItsProperty()
    {
        var status = JsonSerializer.Deserialize<SessionStatus>(HookProducedJson);

        Assert.NotNull(status);
        Assert.Equal("generating", status!.State);
        Assert.Equal("codex", status.Cli);
        Assert.Equal("/Users/warren/project", status.Cwd);
        Assert.Equal("Fix the login bug", status.Title);
        Assert.Equal("green", status.Color);
        Assert.Equal("vscode", status.TermProgram);
        Assert.Equal("term-1", status.TermId);
        Assert.Equal("/dev/ttys002", status.Tty);
        Assert.Equal("default", status.TmuxSocket);
        Assert.Equal("%3", status.TmuxPane);
        Assert.Equal("/opt/homebrew/bin/tmux", status.TmuxBin);
        Assert.Equal(12345, status.SessionPid);
        Assert.Equal("/Users/warren/.codex/sessions/2026/08/21/rollout.jsonl", status.TranscriptPath);

        // Cli deserializes ("cli" is [JsonPropertyName], not [JsonIgnore]) but
        // Source does not derive itself — SourceOf is a separate step every
        // caller must remember to run. Deserializing alone leaves Source at
        // its default, even for a file that plainly says "codex".
        Assert.Equal(SessionSource.ClaudeCode, status.Source);

        // Never written by the hook, and [JsonIgnore] besides — these stay at
        // their defaults regardless of what's in the file.
        Assert.Equal("", status.Lead);
        Assert.Equal("", status.Agent);
        Assert.Equal(SessionKind.Unknown, status.Kind);
        Assert.False(status.IsRoom);
        // term_pid is Windows-hook-only and absent from this macOS-shaped
        // fixture; missing keys leave the property at its default.
        Assert.Equal(0, status.TermPid);
    }

    [Fact]
    public void Serialize_OmitsLeadAndAgentButKeepsCli()
    {
        var status = new SessionStatus
        {
            State = "idle",
            Cli = "codex",
            Lead = "lead-session-id",
            Agent = "MenuUX"
        };

        var json = JsonSerializer.Serialize(status);

        // [JsonIgnore] on Lead/Agent/Source/Kind/IsRoom means a round trip
        // through ResetSessionToIdle (read a status file, flip the state,
        // write the object back) can never leak an app-derived field into a
        // hook-owned file.
        Assert.DoesNotContain("\"Lead\"", json);
        Assert.DoesNotContain("lead-session-id", json);
        Assert.DoesNotContain("\"Agent\"", json);
        Assert.DoesNotContain("MenuUX", json);

        // Cli is the hook's own field and is not ignored.
        Assert.Contains("\"cli\":\"codex\"", json);
    }

    [Fact]
    public void Deserialize_ToleratesAnUnknownExtraKeyWithoutThrowing()
    {
        // Independent of ClaudeBuddySettings' own _unknownKeys mechanism —
        // this is System.Text.Json's default tolerance for a future hook
        // writing a key this build has never heard of.
        const string withExtraKey =
            "{\"state\":\"idle\",\"cli\":\"codex\",\"cwd\":\"/tmp\",\"a_future_field\":\"surprise\"}";

        var status = JsonSerializer.Deserialize<SessionStatus>(withExtraKey);

        Assert.NotNull(status);
        Assert.Equal("idle", status!.State);
        Assert.Equal("codex", status.Cli);
    }
}
