using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// Driven through GrokTranscript.Map against shapes copied from a real
// grok 1.0.13 updates.jsonl (31 Aug 2026). See docs/grok-findings.md.
public class GrokTranscriptTests
{
    private static string Envelope(string update, long timestamp = 1788198313) =>
        "{\"timestamp\":" + timestamp + ",\"method\":\"session/update\","
        + "\"params\":{\"sessionId\":\"s1\",\"update\":" + update + "}}";

    private static List<ChatTranscript.Row> Map(params string[] updates) =>
        GrokTranscript.Map(updates.Select(u => Envelope(u)));

    [Fact]
    public void ConsecutiveUserChunksBecomeOneUserTurn()
    {
        var rows = Map(
            "{\"sessionUpdate\":\"user_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"Hello \"}}",
            "{\"sessionUpdate\":\"user_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"world\"}}");

        var row = Assert.Single(rows);
        Assert.Equal(ChatRole.User, row.Turn.Role);
        Assert.Equal("Hello world", row.Turn.Text);
    }

    [Fact]
    public void ConsecutiveAssistantChunksBecomeOneAssistantTurn()
    {
        var rows = Map(
            "{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"I'll \"}}",
            "{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"look.\"}}");

        var row = Assert.Single(rows);
        Assert.Equal(ChatRole.Assistant, row.Turn.Role);
        Assert.Equal("I'll look.", row.Turn.Text);
    }

    [Fact]
    public void ThoughtChunksAreASystemTurnAndDoNotMixWithTheReply()
    {
        var rows = Map(
            "{\"sessionUpdate\":\"agent_thought_chunk\",\"content\":{\"type\":\"text\",\"text\":\"thinking\"}}",
            "{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"answer\"}}");

        Assert.Equal(2, rows.Count);
        Assert.Equal(ChatRole.System, rows[0].Turn.Role);
        Assert.Equal("thinking", rows[0].Turn.Text);
        Assert.Equal(ChatRole.Assistant, rows[1].Turn.Role);
        Assert.Equal("answer", rows[1].Turn.Text);
    }

    [Fact]
    public void AToolCallBecomesASystemLineNamedForItsTitle()
    {
        var rows = Map(
            "{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"t1\",\"title\":\"grep\",\"kind\":\"search\"}");

        var row = Assert.Single(rows);
        Assert.Equal(ChatRole.System, row.Turn.Role);
        Assert.Equal("· grep", row.Turn.Text);
        Assert.Equal("tool:t1", row.Uuid);
    }

    [Fact]
    public void ToolCallUpdatesAreNotASecondTurn()
    {
        var rows = Map(
            "{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"t1\",\"title\":\"grep\",\"kind\":\"search\"}",
            "{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"t1\",\"status\":\"completed\",\"title\":\"grep\"}");

        Assert.Single(rows);
    }

    [Fact]
    public void HookExecutionAndModeUpdatesAreIgnored()
    {
        var rows = Map(
            "{\"sessionUpdate\":\"hook_execution\",\"event_name\":\"session_start\"}",
            "{\"sessionUpdate\":\"current_mode_update\",\"mode\":\"plan\"}",
            "{\"sessionUpdate\":\"turn_completed\",\"prompt_id\":\"p1\"}");

        Assert.Empty(rows);
    }

    [Fact]
    public void AUserThenAToolThenAReplyAreThreeTurns()
    {
        var rows = Map(
            "{\"sessionUpdate\":\"user_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"go\"}}",
            "{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"t1\",\"title\":\"read_file\"}",
            "{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"done\"}}");

        Assert.Equal(3, rows.Count);
        Assert.Equal(ChatRole.User, rows[0].Turn.Role);
        Assert.Equal(ChatRole.System, rows[1].Turn.Role);
        Assert.Equal(ChatRole.Assistant, rows[2].Turn.Role);
    }

    [Fact]
    public void IsInterestingRejectsUnrelatedRows()
    {
        Assert.False(GrokTranscript.IsInteresting("{\"sessionUpdate\":\"hook_execution\"}"));
        Assert.True(GrokTranscript.IsInteresting("{\"sessionUpdate\":\"user_message_chunk\"}"));
        Assert.True(GrokTranscript.IsInteresting("{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"x\"}"));
    }

    [Fact]
    public void ABareUpdateWithoutTheEnvelopeStillMaps()
    {
        var rows = GrokTranscript.Map(new[]
        {
            "{\"update\":{\"sessionUpdate\":\"user_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"hi\"}}}"
        });

        Assert.Equal("hi", Assert.Single(rows).Turn.Text);
    }

    [Fact]
    public void UnparseableAndEmptyLinesAreSkipped()
    {
        var rows = GrokTranscript.Map(new[]
        {
            "",
            "not json",
            "[",
            "{not json",
            Envelope("{\"sessionUpdate\":\"user_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"ok\"}}")
        });

        Assert.Equal("ok", Assert.Single(rows).Turn.Text);
    }

    [Fact]
    public void EmptyChunkContentIsNotATurn()
    {
        var rows = Map(
            "{\"sessionUpdate\":\"user_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"\"}}",
            "{\"sessionUpdate\":\"agent_message_chunk\"}",
            "{\"sessionUpdate\":\"agent_thought_chunk\",\"content\":\"not-an-object\"}");

        Assert.Empty(rows);
    }

    [Fact]
    public void AToolCallWithoutATitleUsesItsKind()
    {
        var rows = Map("{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"t1\",\"kind\":\"search\"}");

        Assert.Equal("· search", Assert.Single(rows).Turn.Text);
    }

    [Fact]
    public void AToolCallWithoutTitleOrKindIsJustTool()
    {
        var rows = Map("{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"t1\"}");

        Assert.Equal("· tool", Assert.Single(rows).Turn.Text);
    }

    [Fact]
    public void ARowWithNoUpdateObjectIsSkipped()
    {
        var rows = GrokTranscript.Map(new[] { "{\"params\":{\"sessionId\":\"s1\"}}" });
        Assert.Empty(rows);
    }

    [Fact]
    public void UnixTimestampsInSecondsAndMillisecondsAreRead()
    {
        var seconds = GrokTranscript.Map(new[]
        {
            Envelope("{\"sessionUpdate\":\"user_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"s\"}}",
                timestamp: 1_700_000_000)
        });
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), Assert.Single(seconds).Turn.At);

        var millis = GrokTranscript.Map(new[]
        {
            "{\"timestamp\":1700000000000,\"method\":\"session/update\","
            + "\"params\":{\"sessionId\":\"s1\",\"update\":{\"sessionUpdate\":\"user_message_chunk\","
            + "\"content\":{\"type\":\"text\",\"text\":\"m\"}}}}"
        });
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), Assert.Single(millis).Turn.At);
    }

    [Fact]
    public void AStringTimestampIsParsed()
    {
        var rows = GrokTranscript.Map(new[]
        {
            "{\"timestamp\":\"2026-08-31T12:00:00Z\",\"update\":{\"sessionUpdate\":\"user_message_chunk\","
            + "\"content\":{\"type\":\"text\",\"text\":\"hi\"}}}"
        });

        Assert.Equal(DateTimeOffset.Parse("2026-08-31T12:00:00Z"), Assert.Single(rows).Turn.At);
    }
}
