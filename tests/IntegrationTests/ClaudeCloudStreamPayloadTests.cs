using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// A whole turn off the cloud event stream, end to end through the real line
// loop: bytes in UTF-8, read by a StreamReader exactly as HttpCloudEventStream
// reads the socket, framed, classified, and the resume point tracked.
//
// **The structure is captured; the values are invented.** CB-199's stream
// probe printed event names, ids, key names and order and never a body
// (2026-09-29, /tmp/cb199-stream3.txt for the cold-start turn and
// /tmp/cb199-stream2.txt for the interrupt, on the Mac they were taken on).
// The fixtures below follow those captures event for event, sequence numbers
// included, with every value made up.
//
// Three captured behaviours are pinned because a consumer has to survive
// them: the durable `assistant` arriving *before* the trailing
// `message_delta`/`message_stop`; a cold start's durable `env_manager_log`
// rows and a second `session_update` before anything else happens; and a turn
// full of durable rows that are not chat at all — turn_handoff_available,
// active_goal, autocompact_state, rate_limit_event, hook_started,
// hook_response, prompt_suggestion — every one carrying a sequence number.
public class ClaudeCloudStreamPayloadTests
{
    private const string Sid = "session_01StreamFixture";

    private static string Client(long seq, string eventType, string payload) =>
        "event: client_event\nid: " + seq + "\ndata: {\"event_id\":\"ev-" + seq + "\",\"sequence_num\":\""
        + seq + "\",\"event_type\":\"" + eventType + "\",\"source\":\"s\",\"payload\":" + payload
        + ",\"created_at\":\"2026-09-29T10:00:00Z\"}\n\n";

    private static string Ephemeral(string eventType, string payload) =>
        "event: ephemeral_event\ndata: {\"event_type\":\"" + eventType + "\",\"payload\":" + payload
        + ",\"timestamp\":\"t\",\"source\":\"s\"}\n\n";

    private static string Delta(string inner) =>
        Ephemeral("stream_event", "{\"type\":\"stream_event\",\"event\":" + inner
            + ",\"parent_tool_use_id\":null,\"session_id\":\"" + Sid + "\",\"uuid\":\"u-live\"}");

    private static string Delivery(string status) =>
        "event: delivery_update\ndata: {\"event_id\":\"uuid-ours\",\"status\":\"" + status
        + "\",\"timestamp\":\"t\"}\n\n";

    private static string Text(string text) =>
        Delta("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\""
              + text + "\"}}");

    private static string System(long seq, string subtype, string extra = "") =>
        Client(seq, "system", "{\"type\":\"system\",\"subtype\":\"" + subtype + "\",\"session_id\":\""
                              + Sid + "\",\"uuid\":\"u-" + seq + "\"" + extra + "}");

    private static string Noise(long seq, string type) =>
        Client(seq, type, "{\"type\":\"" + type + "\",\"session_id\":\"" + Sid + "\",\"uuid\":\"u-" + seq + "\"}");

    private static string Session(string status) =>
        "event: session_update\ndata: {\"connection_status\":\"" + status + "\"}\n\n";

    // /tmp/cb199-stream3.txt, event for event: a turn on a cold session.
    private static string NormalTurn() =>
        ": keepalive\n"
        + Session("connected")
        + Client(103, "user", "{\"message\":{\"role\":\"user\",\"content\":\"hi\"},\"parent_tool_use_id\":null,"
                              + "\"server_received_wall_ms\":1,\"session_id\":\"" + Sid + "\",\"trace_context\":{},"
                              + "\"type\":\"user\",\"uuid\":\"uuid-ours\"}")
        + Session("connected")
        + string.Concat(Enumerable.Range(104, 6).Select(seq => Noise(seq, "env_manager_log")))
        + Delivery("DELIVERY_STATUS_RECEIVED")
        + System(110, "turn_handoff_available")
        + Noise(111, "active_goal")
        + Noise(112, "autocompact_state")
        + Ephemeral("system", "{\"commands\":[],\"session_id\":\"" + Sid + "\",\"subtype\":\"commands_changed\",\"type\":\"system\",\"uuid\":\"u-c\"}")
        + Delivery("DELIVERY_STATUS_PROCESSING")
        + System(113, "init")
        + System(114, "status", ",\"status\":\"requesting\"")
        + Delta("{\"message\":{},\"type\":\"message_start\"}")
        + Delta("{\"content_block\":{},\"index\":0,\"type\":\"content_block_start\"}")
        + Text("Hello, ")
        + Text("world \\\"quoted\\\"")
        + Delta("{\"index\":0,\"type\":\"content_block_stop\"}")
        + Client(115, "assistant", "{\"message\":{\"role\":\"assistant\",\"content\":[]},\"parent_tool_use_id\":null,"
                                   + "\"request_id\":\"r\",\"session_id\":\"" + Sid + "\",\"timestamp\":\"t\",\"type\":\"assistant\",\"uuid\":\"u-final\"}")
        + Delta("{\"context_management\":{},\"delta\":{},\"type\":\"message_delta\",\"usage\":{}}")
        + Delta("{\"type\":\"message_stop\"}")
        + Noise(116, "rate_limit_event")
        + Ephemeral("system", "{\"needs_action\":false,\"session_id\":\"" + Sid + "\",\"status_category\":\"c\",\"status_detail\":\"d\","
                              + "\"subtype\":\"post_turn_summary\",\"summarizes_uuid\":\"u-final\",\"type\":\"system\",\"uuid\":\"u-p\"}")
        + Delivery("DELIVERY_STATUS_PROCESSED")
        + System(117, "hook_started")
        + System(118, "hook_response")
        + Client(119, "result", "{\"is_error\":false,\"num_turns\":1,\"subtype\":\"success\",\"type\":\"result\",\"uuid\":\"u-res\"}")
        + ": keepalive\n"
        + Noise(120, "prompt_suggestion");

    private static async Task<List<CloudStreamEvent>> Read(string body, bool crlf = false)
    {
        if (crlf) body = body.Replace("\n", "\r\n", StringComparison.Ordinal);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var events = new List<CloudStreamEvent>();
        await foreach (var ev in ClaudeCloudStreamEvents.ReadAsync(reader, 200, "text/event-stream", CancellationToken.None))
        {
            events.Add(ev);
        }
        return events;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACapturedColdStartTurnReadsInOrder(bool crlf)
    {
        var events = await Read(NormalTurn(), crlf);
        var kinds = events.Select(e => e.Kind).ToList();

        Assert.Equal(CloudStreamEventKind.Keepalive, kinds[0]);
        Assert.Equal(2, kinds.Count(k => k == CloudStreamEventKind.Session));
        Assert.Equal(3, kinds.Count(k => k == CloudStreamEventKind.Delivery));
        Assert.Equal(2, kinds.Count(k => k == CloudStreamEventKind.Keepalive));
        Assert.Equal(CloudStreamEventKind.Ended, kinds[^1]);
        Assert.Equal(CloudOutcomeKind.Ok, events[^1].Outcome!.Kind);

        // Every durable row is here, in order, with its captured sequence number.
        var durable = events.Where(e => e.Kind == CloudStreamEventKind.Durable).ToList();
        Assert.Equal(Enumerable.Range(103, 18).Select(n => (long?)n), durable.Select(e => e.SequenceNum));

        // Our echo reconciles by uuid; its row is the transcript row itself.
        var echo = durable[0];
        Assert.Equal("user", echo.PayloadType);
        Assert.Equal("uuid-ours", echo.Uuid);
        Assert.Contains("\"trace_context\"", echo.RowJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"event_type\"", echo.RowJson!, StringComparison.Ordinal);

        // Most durable rows are not chat — the mapping has to decide.
        Assert.Equal(new[] { "user", "env_manager_log", "env_manager_log", "env_manager_log",
                "env_manager_log", "env_manager_log", "env_manager_log", "system", "active_goal",
                "autocompact_state", "system", "system", "assistant", "rate_limit_event", "system",
                "system", "result", "prompt_suggestion" },
            durable.Select(e => e.PayloadType));

        // Busy markers, the text, the stored message, the end.
        Assert.Equal("init", durable.Single(e => e.SequenceNum == 113).Subtype);
        Assert.Equal("requesting", durable.Single(e => e.SequenceNum == 114).StatusValue);
        Assert.Equal("Hello, world \"quoted\"",
            string.Concat(events.Select(e => e.TextDelta).Where(t => t is not null)));
        Assert.Equal("success", durable.Single(e => e.PayloadType == "result").Subtype);
        Assert.Equal(new[] { "DELIVERY_STATUS_RECEIVED", "DELIVERY_STATUS_PROCESSING", "DELIVERY_STATUS_PROCESSED" },
            events.Where(e => e.Kind == CloudStreamEventKind.Delivery).Select(e => e.DeliveryStatus));

        // Captured: the stored assistant arrives before the trailing deltas.
        var assistant = events.FindIndex(e => e.PayloadType == "assistant");
        Assert.True(assistant < events.FindIndex(e => e.InnerType == "message_delta"));
        Assert.True(assistant < events.FindIndex(e => e.InnerType == "message_stop"));

        // Ephemeral system rows: commands_changed and the post-turn summary.
        Assert.Equal(new[] { "commands_changed", "post_turn_summary" },
            events.Where(e => e.Kind == CloudStreamEventKind.Ephemeral && e.PayloadType == "system")
                .Select(e => e.Subtype));
    }

    // The resume point is the last durable sequence number — here a
    // prompt_suggestion, which is not chat but is durable — and no ephemeral
    // event in between moves it.
    [Fact]
    public async Task TheResumePointIsTheLastDurableEvent()
    {
        long? resume = 102;
        foreach (var ev in await Read(NormalTurn()))
        {
            resume = ClaudeCloudStreamPolicy.ResumeFrom(resume, ev);
        }

        Assert.Equal(120, resume);
        Assert.Equal("/v1/code/sessions/session_01StreamFixture/events/stream?from_sequence_num=120",
            ClaudeCloudStreamRequest.StreamPath(Sid, resume));
    }

    // /tmp/cb199-stream2.txt from the interrupt on: control_request (96),
    // a late delta, a RECEIVED delivery, control_response (97), the partial
    // assistant carrying `aborted` (98), a user row (99), result
    // error_during_execution (100), two PROCESSED deliveries, then
    // rate_limit_event and prompt_suggestion.
    [Fact]
    public async Task ACapturedInterruptEndsWithAnErrorResult()
    {
        var body =
            Text("partial")
            + Client(96, "control_request", "{\"request\":{\"subtype\":\"interrupt\"},\"request_id\":\"r\",\"type\":\"control_request\",\"uuid\":\"u-96\"}")
            + Text(" more")
            + Delivery("DELIVERY_STATUS_RECEIVED")
            + Client(97, "control_response", "{\"response\":{},\"type\":\"control_response\",\"uuid\":\"u-97\"}")
            + Client(98, "assistant", "{\"aborted\":true,\"message\":{},\"parent_tool_use_id\":null,\"request_id\":\"r\",\"session_id\":\"" + Sid + "\",\"timestamp\":\"t\",\"type\":\"assistant\",\"uuid\":\"u-98\"}")
            + Client(99, "user", "{\"message\":{},\"parent_tool_use_id\":null,\"session_id\":\"" + Sid + "\",\"timestamp\":\"t\",\"type\":\"user\",\"uuid\":\"u-99\"}")
            + Client(100, "result", "{\"is_error\":true,\"subtype\":\"error_during_execution\",\"type\":\"result\",\"uuid\":\"u-100\"}")
            + Delivery("DELIVERY_STATUS_PROCESSED")
            + Delivery("DELIVERY_STATUS_PROCESSED")
            + Noise(101, "rate_limit_event")
            + Noise(102, "prompt_suggestion");

        var events = await Read(body);

        Assert.Equal(new[] { "stream_event", "control_request", "stream_event", null, "control_response",
                "assistant", "user", "result", null, null, "rate_limit_event", "prompt_suggestion", null },
            events.Select(e => e.PayloadType));
        Assert.Equal(new long?[] { null, 96, null, null, 97, 98, 99, 100, null, null, 101, 102, null },
            events.Select(e => e.SequenceNum));

        // A delta after the interrupt went out is still text; stopping it is
        // the consumer's call on the result, not the parser's.
        Assert.Equal(" more", events[2].TextDelta);
        Assert.Contains("\"aborted\":true", events[5].RowJson!, StringComparison.Ordinal);
        Assert.Equal("error_during_execution", events[7].Subtype);
        Assert.Equal(CloudStreamEventKind.Ended, events[^1].Kind);
    }

    // Multi-byte text split across nothing in particular still reads whole:
    // the reader decodes UTF-8 before framing, so a delta is never cut inside
    // a character.
    [Fact]
    public async Task NonAsciiTextSurvivesTheReader()
    {
        var events = await Read(Text("naïve café 🎉"));

        Assert.Equal("naïve café 🎉", events[0].TextDelta);
    }
}
