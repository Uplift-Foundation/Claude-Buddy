using System.Collections.Generic;
using System.Threading;

namespace ClaudeBuddy
{
    // A cloud session's live event stream:
    // `GET /v1/code/sessions/{id}/events/stream`, server-sent events.
    //
    // **Measured on a real Mac on 2026-09-29** against a throwaway
    // `anthropic_cloud` session with the CLI's own login; the contract is in
    // CB-199's stream notes. What it gives the panel that polling cannot is the
    // text as it is written — `text_delta`s about every 0.2 s — plus a turn's
    // start and end as events rather than as a status somebody has to go and
    // read.
    //
    // Four SSE event names were seen, and each maps to one Kind:
    //
    //  * `client_event` — **durable**. Carries an SSE `id` equal to the event's
    //    `sequence_num`, and a payload shaped like a Claude Code transcript row
    //    (the same shape the /v2 feed and ChatTranscript already read).
    //  * `ephemeral_event` — no id, never replayed. The live deltas, and the
    //    post-turn summary.
    //  * `delivery_update` — no id. RECEIVED / PROCESSING / PROCESSED for a
    //    message we sent.
    //  * `session_update` — no id. Seen at connect, carrying connection_status.
    //
    // A `:` comment arrives every 12–15 s as a keepalive.
    internal enum CloudStreamEventKind
    {
        Durable,
        Ephemeral,
        Delivery,
        Session,
        Keepalive,

        // An SSE event name this version has not seen. Delivered rather than
        // dropped, so a new kind shows up in a trace instead of vanishing; the
        // panel ignores it.
        Other,

        // **The stream is over**, and Outcome says why. Always the last item an
        // OpenAsync enumeration yields, and yielded exactly once:
        //
        //  * a clean end of stream is `Ok` with the status that opened it;
        //  * a non-2xx answer is whatever CloudOutcomes.OutcomeFor makes of it
        //    — so a deleted session is SessionGone, a refused login
        //    TokenRefused, a 429 RateLimited with its Retry-After;
        //  * a transport failure mid-stream or before the headers is
        //    `Unavailable` with status 0.
        //
        // An enumeration that stopped because the caller cancelled yields
        // nothing further: cancelling is the caller's own decision and needs no
        // report.
        Ended,
    }

    // One event off the stream.
    //
    // PayloadJson is the raw `data:` JSON exactly as it arrived, so the panel
    // can hand a durable row to the transcript mapping without anything here
    // pretending to understand it. The fields after it are **hints** read out
    // of that JSON by ClaudeCloudStreamEvents.Classify — each is null when the
    // event does not carry it, and none is a promise that it will:
    //
    //  * PayloadType — the payload's `type`: user, assistant, system, result,
    //    control_request, control_response, stream_event, …
    //  * Subtype — the payload's `subtype`: init, status, success,
    //    error_during_execution, post_turn_summary, …
    //  * StatusValue — a `system subtype=status` event's `status`
    //    ("requesting"), or a session_update's `connection_status`.
    //  * TextDelta — the text of a `stream_event` whose inner event is a
    //    `content_block_delta` with a `text_delta`. Only text: a thinking or
    //    tool-input delta is not text the panel shows.
    //  * InnerType — a `stream_event`'s inner `event.type`: message_start,
    //    content_block_start, content_block_delta, content_block_stop,
    //    message_delta, message_stop.
    //  * DeliveryStatus — a delivery_update's `DELIVERY_STATUS_*`.
    //  * Uuid — the payload's `uuid`, which is how the echo of our own user
    //    turn reconciles with the bubble added after the send's 2xx.
    //
    // **No field ever holds the access token.** The token goes into the
    // request's Authorization header and nowhere else; the canary tests hold
    // every field here to that.
    internal sealed record CloudStreamEvent(
        CloudStreamEventKind Kind,
        string? Name,
        long? SequenceNum,
        string? PayloadJson,
        string? PayloadType = null,
        string? Subtype = null,
        string? StatusValue = null,
        string? TextDelta = null,
        string? InnerType = null,
        string? DeliveryStatus = null,
        string? Uuid = null,
        CloudOutcome? Outcome = null);

    // The stream, as an interface, so the chat session can be driven by a fake
    // that yields a scripted turn. Same argument as ICloudApi.
    //
    // **One call opens one connection.** The enumeration ends with exactly one
    // `Ended` event (see the Kind) and the connection is closed when the
    // enumeration is disposed or `ct` is cancelled. Reconnecting is the
    // caller's job, decided by ClaudeCloudStreamPolicy — the stream itself
    // never retries, for the reason CB-164 gave about retry loops against an
    // endpoint we are a guest on.
    //
    // `fromSequenceNum` is the last durable sequence number the caller has.
    // **Always pass one when you have one.** Measured: with it, the stream
    // starts live with no replay; without it, the server replays the whole
    // session history first (129 events in 0.4 s on a short session).
    internal interface ICloudEventStream
    {
        IAsyncEnumerable<CloudStreamEvent> OpenAsync(string accessToken, string sessionId,
            long? fromSequenceNum, CancellationToken ct);
    }
}
