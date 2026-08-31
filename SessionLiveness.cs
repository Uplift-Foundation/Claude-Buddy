using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ClaudeBuddy
{
    // Whether a session is a conversation somebody is having, or a terminal
    // somebody walked away from.
    //
    // **Every cheap way of asking this returns the wrong answer.** On the Mac
    // mini, a `job-hunter-mac-mini` session abandoned at a prompt on the 29th
    // was still drawing an orb a day later, beside an identically-named live
    // one — and each of the obvious checks agreed it was alive:
    //
    // - its **process** was running, because nobody had closed the window;
    // - `claude agents --json` **listed** it, for the same reason;
    // - its transcript's **mtime** was six minutes old.
    //
    // That last one is the trap worth naming, because it is the check anyone
    // would reach for first. The file really had just been written — with a
    // `bridge-session` row carrying no timestamp at all. Remote Control's
    // bridge keeps poking the transcript of a session it is attached to, so
    // mtime measures the bridge's health and not the conversation's.
    //
    // What does distinguish them is the newest row that represents somebody
    // saying something: `user` or `assistant`, both of which carry a
    // timestamp. On that measure the dead session last spoke 23 hours ago and
    // the live one 8 seconds ago, which is not a close call.
    //
    // **This is deliberately not the rule CB-74 removed.** That one keyed off
    // the status file's heartbeat, which stops updating for a session that is
    // merely idle — so it hid sessions that were alive and waiting, which is
    // exactly the mistake being avoided here. A session reported `idle` this
    // instant is kept, provided the conversation itself is recent.
    internal static class SessionLiveness
    {
        // How long a conversation stays interesting after its last turn.
        //
        // Long enough to survive a meal, a meeting or a school run — walking
        // away from a session for an hour is not abandoning it, and an orb
        // that vanishes over lunch is a worse bug than the one this fixes.
        // Short enough that yesterday's parked terminal is gone today, which
        // is the whole complaint.
        //
        // Nothing subtler is warranted: the two cases observed on real
        // machines were 8 seconds and 23 hours, and no plausible boundary
        // between them separates those differently.
        internal static readonly TimeSpan StaysInterestingFor = TimeSpan.FromHours(2);

        // The rows that mean somebody said something.
        //
        // Everything else a transcript holds is bookkeeping — `mode`,
        // `agent-name`, `queue-operation`, `file-history-snapshot`,
        // `bridge-session` — written by tooling rather than by a turn, and
        // several of them are written to a session nobody is using.
        private static bool IsATurn(string? type) =>
            type is "user" or "assistant";

        // Whether this session earns a roster entry.
        //
        // Pure, and takes `now` rather than reading a clock, so the boundary
        // can be asserted from both sides without waiting two hours.
        internal static bool WorthShowing(
            string? state, DateTime? lastTurnUtc, DateTime nowUtc, TimeSpan window)
        {
            // A session that is generating or waiting is being used right now,
            // whatever its transcript last recorded. A long tool call writes
            // nothing for minutes, and a permission prompt can sit unanswered
            // far longer than that — neither is abandoned, and both are the
            // moments a user most wants the orb.
            if (state is "generating" or "waiting") return true;

            // No turn found at all. That is a transcript this process could
            // not read, or one holding nothing but bookkeeping — and a session
            // whose liveness cannot be established is shown rather than
            // hidden. Hiding on a failed read would make an unreadable file
            // look exactly like an abandoned session, which is the confusion
            // this whole class exists to end.
            if (lastTurnUtc is not { } last) return true;

            return nowUtc - last < window;
        }

        internal static bool WorthShowing(string? state, DateTime? lastTurnUtc, DateTime nowUtc) =>
            WorthShowing(state, lastTurnUtc, nowUtc, StaysInterestingFor);

        // The newest turn in these lines, or nothing.
        //
        // Takes lines rather than a path so the rule is testable without a
        // disk — the interesting inputs here are files a test machine does not
        // happen to have, most of all one whose only recent rows are
        // untimestamped bridge housekeeping.
        //
        // Newest-anywhere rather than last-row: rows are appended in order in
        // practice, but a transcript ends with whatever tooling wrote last, so
        // scanning from the end for the first turn would work while scanning
        // only the final row would not. Taking the maximum costs one pass and
        // does not depend on either assumption.
        internal static DateTime? LastTurnAt(IEnumerable<string> lines)
        {
            DateTime? newest = null;

            foreach (var line in lines)
            {
                var at = TurnTime(line);
                if (at is null) continue;

                if (newest is null || at > newest) newest = at;
            }

            return newest;
        }

        // One line's turn time, if it is a turn and carries one.
        //
        // Deliberately tolerant. This reads a format nobody here controls, on
        // a machine that may be running a different version of the CLI, and
        // the cost of misreading a line is an orb that lingers — so anything
        // unrecognised is "not a turn" rather than an exception.
        internal static DateTime? TurnTime(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            try
            {
                using var row = JsonDocument.Parse(line);

                if (row.RootElement.ValueKind != JsonValueKind.Object) return null;

                if (!row.RootElement.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || !IsATurn(type.GetString()))
                    return null;

                if (!row.RootElement.TryGetProperty("timestamp", out var stamp)
                    || stamp.ValueKind != JsonValueKind.String)
                    return null;

                var text = stamp.GetString();
                if (string.IsNullOrWhiteSpace(text)) return null;

                // Round-trip: the CLI writes `2026-08-31T03:07:07.499Z`, and
                // AdjustToUniversal keeps a transcript written in another zone
                // comparable with this machine's clock.
                if (!DateTime.TryParse(
                        text, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out var when))
                    return null;

                return when;
            }
            catch (JsonException)
            {
                // A truncated line, which is the ordinary case at the start of
                // a tail read rather than a fault.
                return null;
            }
        }
    }
}
