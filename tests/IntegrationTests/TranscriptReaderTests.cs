using Xunit;

namespace ClaudeBuddy.Tests;

// TranscriptReader.cs reads only the last TailBytes (262144, private const —
// hardcoded here since InternalsVisibleTo does not reach private members)
// of a transcript, because real transcripts reach tens of MB and this runs
// on every hook call. MaxSpokenChars (1500, same visibility note) caps how
// much of a message it will hand back. Row shapes below are borrowed
// verbatim from tests/TranscriptTests/Program.cs's own already-validated
// fixtures, per this repo's fixture-provenance rule (write fixtures from
// real captures, not from memory).
// In the Settings collection since the multi-profile cases below repoint
// CLAUDE_BUDDY_SETTINGS_DIR and add a profile directory. Without it they race
// every other settings test in this assembly — and this branch has fixed that
// same ordering hazard five times, so adding a sixth would be careless.
[Collection("Settings")]
public class TranscriptReaderTests
{
    private const int TailBytes = 262144;
    private const int MaxSpokenChars = 1500;

    private const string UserSaid =
        """{"type":"user","uuid":"u1","timestamp":"2026-08-16T10:00:00Z","message":{"role":"user","content":"fix the arrangement test"}}""";

    private const string AssistantSaid =
        """{"type":"assistant","uuid":"a1","timestamp":"2026-08-16T10:00:09Z","message":{"role":"assistant","content":[{"type":"text","text":"Fixed the nested-team case."}]}}""";

    private const string CxSessionMeta =
        """{"timestamp":"2026-08-19T16:57:08.290Z","ordinal":0,"type":"session_meta","payload":{"session_id":"01a01af4","cwd":"/Users/w/Source/AIEA","originator":"codex-tui","cli_version":"0.148.0"}}""";

    private const string CxUser =
        """{"timestamp":"2026-08-19T16:57:08.663Z","ordinal":9,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","id":"01a01af4-8937","content":[{"type":"text","text":"convert this from a claude project to codex","text_elements":[]}]}}}""";

    private const string CxAgent =
        """{"timestamp":"2026-08-19T16:57:11.063Z","ordinal":12,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","id":"msg_000c72","content":[{"type":"Text","text":"I'll inventory the repository's Claude-specific files."}],"phase":"commentary"}}}""";

    private const string CxAgentFinal =
        """{"timestamp":"2026-08-19T17:03:05.443Z","ordinal":310,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","id":"msg_000c73","content":[{"type":"Text","text":"Converted the workspace from Claude to Codex."}],"phase":"final_answer"}}}""";

    [Fact]
    public void LatestAssistantText_HappyPath_ReturnsTheAssistantsText()
    {
        var path = WriteTempFile(UserSaid + "\n" + AssistantSaid + "\n");

        var result = TranscriptReader.LatestAssistantText(path, sessionId: null);

        Assert.Equal("Fixed the nested-team case.", result);
    }

    // The "drop the first partial line" rule: TailLines() seeks to
    // fs.Length - TailBytes and, if that seek point is past the start of the
    // file (start > 0), throws away everything up to and including the next
    // '\n' — because that first fragment is a line that was cut in half by
    // the seek, not a real row.
    //
    // Built so the seek point provably lands *inside* a padding line: that
    // line alone is longer than TailBytes, so start (= totalLength -
    // TailBytes) is guaranteed to fall before its end. What should survive
    // the drop is exactly the real assistant row that follows it intact.
    [Fact]
    public void LatestAssistantText_DropsAPartialLeadingLineInTheTailWindow_AndStillFindsTheRealRow()
    {
        var paddingLine = new string('x', TailBytes + 1000);
        var content = paddingLine + "\n" + AssistantSaid + "\n";
        var path = WriteTempFile(content);

        Assert.True(new FileInfo(path).Length > TailBytes,
            "fixture must exceed TailBytes for this test to exercise the tail-seek path at all");

        var result = TranscriptReader.LatestAssistantText(path, sessionId: null);

        Assert.Equal("Fixed the nested-team case.", result);
    }

    [Fact]
    public void LatestAssistantText_LongMessageIsTruncatedToExactly1500CharsPlusEllipsis()
    {
        var longText = new string('a', 2000);
        var rowPrefix = "{\"type\":\"assistant\",\"uuid\":\"a9\",\"message\":{\"role\":\"assistant\"," +
                         "\"content\":[{\"type\":\"text\",\"text\":\"";
        var rowSuffix = "\"}]}}";
        var row = rowPrefix + longText + rowSuffix;
        var path = WriteTempFile(row + "\n");

        var result = TranscriptReader.LatestAssistantText(path, sessionId: null);

        Assert.NotNull(result);
        Assert.Equal(MaxSpokenChars + 1, result!.Length); // 1500 chars + the trailing "…"
        Assert.EndsWith("…", result);
        Assert.Equal(longText[..MaxSpokenChars] + "…", result);
    }

    [Fact]
    public void LatestAssistantText_MissingFile_ReturnsNullWithoutThrowing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "cb-integrationtests-missing-" + Guid.NewGuid() + ".jsonl");

        var result = TranscriptReader.LatestAssistantText(missingPath, sessionId: "whatever");

        Assert.Null(result);
    }

    [Fact]
    public void LatestCodexAgentText_ReturnsTheLastAgentMessagesText()
    {
        var content = string.Join('\n', new[] { CxSessionMeta, CxUser, CxAgent, CxAgentFinal }) + "\n";
        var path = WriteTempFile(content);

        var result = TranscriptReader.LatestCodexAgentText(path);

        Assert.Equal("Converted the workspace from Claude to Codex.", result);
    }

    [Fact]
    public void LatestCodexAgentText_MissingFile_ReturnsNullWithoutThrowing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "cb-integrationtests-missing-" + Guid.NewGuid() + ".jsonl");

        var result = TranscriptReader.LatestCodexAgentText(missingPath);

        Assert.Null(result);
    }

    // ---- the arms nothing reached ---------------------------------------

    // A transcript with turns but no assistant row at all. Distinct from a
    // missing file: the tail is read and every line walked, and the loop simply
    // finds nothing — which is also what exercises the `continue` for a
    // non-assistant row.
    [Fact]
    public void LatestAssistantText_NoAssistantRowAtAll_ReturnsNull()
    {
        var path = Rows(UserSaid, UserSaid, UserSaid);

        Assert.Null(TranscriptReader.LatestAssistantText(path));
    }

    // An assistant row whose text is blank is skipped rather than returned, so a
    // tool-only turn does not make the app speak an empty string — the reader
    // keeps walking backwards to the last real message.
    [Fact]
    public void LatestAssistantText_SkipsABlankAssistantRowAndKeepsLooking()
    {
        const string blank =
            """{"type":"assistant","uuid":"a0","timestamp":"2026-08-16T10:00:05Z","message":{"role":"assistant","content":[{"type":"text","text":"   "}]}}""";

        var path = Rows(AssistantSaid, blank);

        Assert.Equal("Fixed the nested-team case.", TranscriptReader.LatestAssistantText(path));
    }

    // A row that claims to be an assistant turn but is not valid JSON. The catch
    // is there because the file is appended to live, so a half-written line is a
    // real state — and this runs inside a hook call, where a throw is not free.
    [Fact]
    public void LatestAssistantText_MalformedAssistantRow_ReturnsNullRatherThanThrowing()
    {
        var path = Rows("""{"type":"assistant","message":{"content":[{"type":"text","text":oops}]}}""");

        Assert.Null(TranscriptReader.LatestAssistantText(path));
    }

    // Neither a path nor a session id: nothing to look up, and no exception.
    [Fact]
    public void LatestAssistantText_NothingToGoOn_ReturnsNull()
    {
        Assert.Null(TranscriptReader.LatestAssistantText(null));
        Assert.Null(TranscriptReader.LatestAssistantText(""));
        Assert.Null(TranscriptReader.LatestAssistantText("", ""));
    }

    // A session id matching no transcript anywhere: falls through to
    // FindTranscript and gets nothing back. Worth covering because the
    // alternative to returning null here is an empty path reaching File.Exists.
    [Fact]
    public void LatestAssistantText_UnknownSessionId_ReturnsNull()
    {
        Assert.Null(TranscriptReader.LatestAssistantText(null, "no-such-session-" + Guid.NewGuid()));
    }

    // Discovery by session id, end to end through a fake ~/.claude/projects
    // tree — the path a hook takes when it knows the session but not where its
    // transcript landed. `home` is a parameter precisely so this is testable
    // without touching the real one.
    [Fact]
    public void FindTranscriptFor_LocatesATranscriptBySessionIdUnderAFakeHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "cb-fakehome-" + Guid.NewGuid());
        var project = Path.Combine(home, ".claude", "projects", "-Users-w-Source-Thing");
        Directory.CreateDirectory(project);

        const string sessionId = "11111111-2222-3333-4444-555555555555";
        var transcript = Path.Combine(project, sessionId + ".jsonl");
        File.WriteAllText(transcript, AssistantSaid + "\n");

        try
        {
            Assert.Equal(transcript, TranscriptReader.FindTranscriptFor(sessionId, home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void FindTranscriptFor_ReturnsNullWhenTheHomeHasNoProjectsAtAll()
    {
        var home = Path.Combine(Path.GetTempPath(), "cb-emptyhome-" + Guid.NewGuid());
        Directory.CreateDirectory(home);

        try
        {
            Assert.Null(TranscriptReader.FindTranscriptFor("anything", home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // ---- the Codex reader's own empty cases ------------------------------

    [Fact]
    public void LatestCodexAgentText_NoAgentMessageAtAll_ReturnsNull()
    {
        var path = Rows(CxSessionMeta, CxUser);

        Assert.Null(TranscriptReader.LatestCodexAgentText(path));
    }

    [Fact]
    public void LatestCodexAgentText_MalformedRow_ReturnsNullRatherThanThrowing()
    {
        var path = Rows("""{"type":"event_msg","payload":{"type":"item_completed","item":oops}}""");

        Assert.Null(TranscriptReader.LatestCodexAgentText(path));
    }

    [Fact]
    public void LatestCodexAgentText_NoPath_ReturnsNull()
    {
        Assert.Null(TranscriptReader.LatestCodexAgentText(null));
        Assert.Null(TranscriptReader.LatestCodexAgentText(""));
    }

    private static string Rows(params string[] rows) =>
        WriteTempFile(string.Join('\n', rows) + "\n");

    // The scan finds candidate rows with a substring check for
    // "type":"assistant" before parsing anything, because parsing every row of
    // a 262144-byte window to find the last one is most of the work for none of
    // the answer. That shortcut can be wrong in exactly one direction: a row
    // that CONTAINS that text without being an assistant row.
    //
    // Which is what the parse behind it is for, and it is the parse that has the
    // final say — the substring only decides which rows are worth looking at.
    //
    // Fixture provenance, stated plainly: the row below is constructed rather
    // than captured. What it stands for is a shape, not a particular producer —
    // any row that nests one row's JSON inside another repeats the marker in the
    // raw line, and both CLIs write rows that carry other rows (a summary, a
    // subagent's output, a tool result quoting a transcript). Removing the parse
    // would make every one of those speak as though the assistant had said it.
    private const string NestsTheMarkerWithoutBeingOne =
        """{"type":"summary","uuid":"s1","timestamp":"2026-08-16T10:00:20Z","summary":"discussed the parser","echoes":{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"quoted, not said"}]}}}""";

    [Fact]
    public void ARowThatMerelyContainsTheAssistantMarkerIsNotMistakenForOne()
    {
        var path = WriteTempFile(AssistantSaid + "\n" + NestsTheMarkerWithoutBeingOne + "\n");

        // The real assistant row above it is what comes back — the nesting row
        // is passed over rather than returned as the newest thing said.
        Assert.Equal("Fixed the nested-team case.",
            TranscriptReader.LatestAssistantText(path, sessionId: null));
    }

    // And with nothing but that row, there is no assistant text at all rather
    // than the text it happens to be quoting.
    [Fact]
    public void ANestingRowOnItsOwnYieldsNoAssistantText()
    {
        Assert.Null(TranscriptReader.LatestAssistantText(
            WriteTempFile(NestsTheMarkerWithoutBeingOne + "\n"), sessionId: null));
    }

    private static string WriteTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "cb-integrationtests-transcript-" + Guid.NewGuid() + ".jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    // ---- rows that look assistant-shaped and are not -----------------------

    // The reader pre-filters on the raw substring "type":"assistant" before
    // parsing, because parsing every row of a 30MB transcript on every hook call
    // is not free. So the four shapes below all reach the parser and all have to
    // be refused there instead — a cheap filter has to be backed by a real check
    // or the cheapness is just a bug.

    // Contains the marker inside its own text, but is a user row. This is the
    // case the pre-filter cannot tell apart and the parser must.
    [Fact]
    public void ARowQuotingTheAssistantMarkerIsNotAnAssistantRow()
    {
        var path = Rows(
            """{"type":"user","message":{"role":"user","content":"grep for \"type\":\"assistant\" please"}}""");

        Assert.Null(TranscriptReader.LatestAssistantText(path));
    }

    [Fact]
    public void AnAssistantRowWithNoMessageIsRefused()
    {
        Assert.Null(TranscriptReader.LatestAssistantText(Rows("""{"type":"assistant"}""")));
    }

    [Fact]
    public void AnAssistantRowWithNoContentIsRefused()
    {
        Assert.Null(TranscriptReader.LatestAssistantText(
            Rows("""{"type":"assistant","message":{"role":"assistant"}}""")));
    }

    // Content as a bare string rather than the array of blocks this format uses.
    [Fact]
    public void AnAssistantRowWhoseContentIsNotAnArrayIsRefused()
    {
        Assert.Null(TranscriptReader.LatestAssistantText(
            Rows("""{"type":"assistant","message":{"content":"just a string"}}""")));
    }

    // And a refused row does not stop the reader finding a real one below it.
    [Fact]
    public void ARefusedRowDoesNotHideTheRealOneBeforeIt()
    {
        var path = Rows(AssistantSaid, """{"type":"assistant","message":{}}""");

        Assert.Equal("Fixed the nested-team case.", TranscriptReader.LatestAssistantText(path));
    }

    // ---- extra profile directories ----------------------------------------

    // Claude Code can be run against more than one config directory, and the
    // transcript for a session lives under whichever one it was started with. So
    // the search covers ~/.claude plus every configured profile — a session
    // started under a second profile is otherwise invisible to the reader, and
    // the symptom is an orb that never learns what its session said.
    [Fact]
    public void ATranscriptUnderAnExtraProfileDirectoryIsFound()
    {
        var home = Path.Combine(Path.GetTempPath(), "cb-multihome-" + Guid.NewGuid());
        var project = Path.Combine(home, ".claude-work", "projects", "-Users-w-Source-Thing");
        Directory.CreateDirectory(project);

        const string sessionId = "22222222-3333-4444-5555-666666666666";
        File.WriteAllText(Path.Combine(project, sessionId + ".jsonl"), AssistantSaid + "\n");

        var dir = Path.Combine(Path.GetTempPath(), "cb-multihome-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.AddClaudeCodeProfileDir(".claude-work");

        try
        {
            Assert.NotNull(TranscriptReader.FindTranscriptFor(sessionId, home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // The default directory still wins when both hold something, because it is
    // searched first and the newest write is what the reader is after.
    [Fact]
    public void TheDefaultDirectoryIsSearchedAlongsideTheExtras()
    {
        var home = Path.Combine(Path.GetTempPath(), "cb-multihome2-" + Guid.NewGuid());
        var project = Path.Combine(home, ".claude", "projects", "-Users-w-Source-Thing");
        Directory.CreateDirectory(project);

        const string sessionId = "33333333-4444-5555-6666-777777777777";
        var expected = Path.Combine(project, sessionId + ".jsonl");
        File.WriteAllText(expected, AssistantSaid + "\n");

        var dir = Path.Combine(Path.GetTempPath(), "cb-multihome2-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.AddClaudeCodeProfileDir(".claude-work");

        try
        {
            Assert.Equal(expected, TranscriptReader.FindTranscriptFor(sessionId, home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
