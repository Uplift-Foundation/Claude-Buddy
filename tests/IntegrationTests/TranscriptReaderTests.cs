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

    private static string WriteTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "cb-integrationtests-transcript-" + Guid.NewGuid() + ".jsonl");
        File.WriteAllText(path, content);
        return path;
    }
}
