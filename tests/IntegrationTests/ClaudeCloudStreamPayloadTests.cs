using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.Tests;

// A whole turn off the cloud event stream, end to end through the real line
// loop: bytes in UTF-8, read by a StreamReader exactly as HttpCloudEventStream
// reads the socket, framed, classified, and the resume point tracked.
//
// **Hand-written from CB-199's measured stream contract, not captured.** The
// probe printed structure and key names only, never a body, so the event
// order, the event names and the wrapper keys below are the measured ones and
// every value is invented. Two measured behaviours are pinned here because a
// reducer built on this stream has to survive them: the durable `assistant`
// arriving *before* the trailing `message_delta`/`message_stop`, and a cold
// start's `env_manager_log` noise before the first delta.
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

    // The measured order of a normal turn, including the out-of-order tail.
    private static string NormalTurn() =>
        ": keepalive\n"
        + "event: session_update\ndata: {\"connection_status\":\"connected\"}\n\n"
        + Client(101, "user", "{\"type\":\"user\",\"uuid\":\"uuid-ours\",\"session_id\":\"" + Sid
                              + "\",\"message\":{\"role\":\"user\",\"content\":\"hi\"},\"parent_tool_use_id\":null}")
        + Delivery("DELIVERY_STATUS_RECEIVED")
        + Delivery("DELIVERY_STATUS_PROCESSING")
        + Ephemeral("env_manager_log", "{\"type\":\"env_manager_log\"}")
        + Client(102, "system", "{\"type\":\"system\",\"subtype\":\"init\",\"uuid\":\"u-init\"}")
        + Client(103, "system", "{\"type\":\"system\",\"subtype\":\"status\",\"status\":\"requesting\",\"uuid\":\"u-st\"}")
        + Delta("{\"type\":\"message_start\",\"message\":{}}")
        + Delta("{\"type\":\"content_block_start\",\"index\":0}")
        + Text("Hello, ")
        + Text("world \\\"quoted\\\"")
        + Delta("{\"type\":\"content_block_stop\",\"index\":0}")
        + Client(104, "assistant", "{\"type\":\"assistant\",\"uuid\":\"u-final\",\"message\":{\"role\":\"assistant\",\"content\":[]}}")
        + Delta("{\"type\":\"message_delta\",\"delta\":{},\"usage\":{}}")
        + Delta("{\"type\":\"message_stop\"}")
        + Ephemeral("system", "{\"type\":\"system\",\"subtype\":\"post_turn_summary\",\"needs_action\":false}")
        + Client(105, "result", "{\"type\":\"result\",\"subtype\":\"success\",\"uuid\":\"u-res\"}")
        + "event: prompt_suggestion\ndata: {\"x\":1}\n\n";

    private static async Task<List<CloudStreamEvent>> Read(string body, bool crlf = false)
    {
        if (crlf) body = body.Replace("\n", "\r\n", StringComparison.Ordinal);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var events = new List<CloudStreamEvent>();
        await foreach (var ev in ClaudeCloudStreamEvents.ReadAsync(reader, 200, CancellationToken.None))
        {
            events.Add(ev);
        }
        return events;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANormalTurnReadsInTheMeasuredOrder(bool crlf)
    {
        var events = await Read(NormalTurn(), crlf);

        Assert.Equal(new[]
        {
            CloudStreamEventKind.Keepalive, CloudStreamEventKind.Session,
            CloudStreamEventKind.Durable, CloudStreamEventKind.Delivery, CloudStreamEventKind.Delivery,
            CloudStreamEventKind.Ephemeral,
            CloudStreamEventKind.Durable, CloudStreamEventKind.Durable,
            CloudStreamEventKind.Ephemeral, CloudStreamEventKind.Ephemeral,
            CloudStreamEventKind.Ephemeral, CloudStreamEventKind.Ephemeral,
            CloudStreamEventKind.Ephemeral,
            CloudStreamEventKind.Durable,
            CloudStreamEventKind.Ephemeral, CloudStreamEventKind.Ephemeral,
            CloudStreamEventKind.Ephemeral,
            CloudStreamEventKind.Durable,
            CloudStreamEventKind.Other,
            CloudStreamEventKind.Ended,
        }, events.Select(e => e.Kind));

        // Our echo reconciles by uuid, and so does the delivery (presumed).
        Assert.Equal("uuid-ours", events[2].Uuid);
        Assert.Equal("user", events[2].PayloadType);
        Assert.Equal(new[] { "DELIVERY_STATUS_RECEIVED", "DELIVERY_STATUS_PROCESSING" },
            events.Where(e => e.Kind == CloudStreamEventKind.Delivery).Select(e => e.DeliveryStatus));

        // Busy markers, then the text, then the stored message and the end.
        Assert.Equal("init", events[6].Subtype);
        Assert.Equal("requesting", events[7].StatusValue);
        Assert.Equal("Hello, world \"quoted\"",
            string.Concat(events.Select(e => e.TextDelta).Where(t => t is not null)));
        Assert.Equal("assistant", events[13].PayloadType);
        Assert.Equal("success", events[17].Subtype);
        Assert.Equal("result", events[17].PayloadType);

        // Measured: the stored assistant arrives before the trailing deltas.
        var assistant = events.FindIndex(e => e.PayloadType == "assistant");
        var stop = events.FindIndex(e => e.InnerType == "message_stop");
        Assert.True(assistant < stop);

        Assert.Equal(CloudOutcomeKind.Ok, events[^1].Outcome!.Kind);
    }

    // The resume point is the last durable sequence number, and ephemeral
    // events in between never move it.
    [Fact]
    public async Task TheResumePointIsTheLastDurableEvent()
    {
        long? resume = 100;
        foreach (var ev in await Read(NormalTurn()))
        {
            resume = ClaudeCloudStreamPolicy.ResumeFrom(resume, ev);
        }

        Assert.Equal(105, resume);

        // …and it is exactly the path a reconnect asks for.
        Assert.Equal("/v1/code/sessions/session_01StreamFixture/events/stream?from_sequence_num=105",
            ClaudeCloudStreamRequest.StreamPath(Sid, resume));
    }

    // The measured interrupt: control_request, control_response, the partial
    // assistant (with `aborted`), a user row, result error_during_execution,
    // and a PROCESSED delivery.
    [Fact]
    public async Task AnInterruptedTurnEndsWithAnErrorResult()
    {
        var body =
            Text("partial")
            + Client(201, "control_request", "{\"type\":\"control_request\",\"request_id\":\"r\",\"request\":{\"subtype\":\"interrupt\"}}")
            + Client(202, "control_response", "{\"type\":\"control_response\"}")
            + Client(203, "assistant", "{\"type\":\"assistant\",\"uuid\":\"u-part\",\"aborted\":true,\"message\":{}}")
            + Client(204, "user", "{\"type\":\"user\",\"uuid\":\"u-int\",\"message\":{}}")
            + Client(205, "result", "{\"type\":\"result\",\"subtype\":\"error_during_execution\"}")
            + Delivery("DELIVERY_STATUS_PROCESSED");

        var events = await Read(body);

        Assert.Equal(new[] { "stream_event", "control_request", "control_response", "assistant", "user", "result", null, null },
            events.Select(e => e.PayloadType));
        Assert.Equal("error_during_execution", events[5].Subtype);
        Assert.Equal("DELIVERY_STATUS_PROCESSED", events[6].DeliveryStatus);
        Assert.Equal(new long?[] { null, 201, 202, 203, 204, 205, null, null }, events.Select(e => e.SequenceNum));
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
