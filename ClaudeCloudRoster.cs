using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ClaudeBuddy
{
    // Reading the cloud roster: what the payload says, which rows are ours, and
    // what to say about the ones that are not.
    //
    // Pure on purpose, and kept out of ClaudeCloudSessions for the same reason
    // OrbArrangement is kept out of SessionManager and ChatTranscript out of the
    // chat session: no sockets, no settings, no dispatcher — text in, rows out —
    // so every rule below is testable a case at a time.
    //
    // ## The filter is the feature
    //
    // **Measured across 578 real rows: 573 were `environment_kind: bridge`.**
    // Those are the user's own *local* sessions, registered for remote control,
    // which Claude Buddy already draws orbs for from its hooks. Five were
    // `anthropic_cloud`, one of them not archived. So `Keep` is not a nicety —
    // getting it wrong doubles every local orb, and the doubling would look like
    // a layout bug rather than a filter bug.
    //
    // **And the filter is client-side because it has to be.** `environment_kind=`,
    // `session_status=`, `status_bucket=`, `exclude_archived=` and `order=` were
    // all sent as query parameters and all **silently ignored** — no error, just a
    // full unfiltered page back. A silently-ignored parameter is the worst kind:
    // it reads as working. Only `limit` and `after_id` do anything.
    //
    // ## The rule that exists because a filter can fail invisibly
    //
    // A filter that matches nothing and a world containing nothing look identical
    // from the outside, and only one of them is a bug. So a reduction that
    // inspected rows and recognised *no* `environment_kind` on any of them is
    // ShapeChanged rather than "no cloud sessions", and the phrase "no cloud
    // sessions" is never said without the count of what was inspected beside it.
    // That count is what lets somebody reading a screenshot tell "we looked at 578
    // sessions and none was a cloud one" from "we looked at nothing".
    internal static class ClaudeCloudRoster
    {
        // The two kinds seen on a real account. Anything else is unknown, which
        // is a state with its own handling rather than a synonym for "not ours".
        internal const string CloudKind = "anthropic_cloud";
        internal const string BridgeKind = "bridge";

        // The one status that means "do not draw this".
        internal const string ArchivedStatus = "archived";

        // Ids look like `session_01ABC…`. The prefix is checked before the id can
        // reach a URL — see UrlFor.
        internal const string IdPrefix = "session_";

        // One row of the roster, with only the fields anything downstream reads.
        //
        // Kind and Status are non-null because a row missing either is not a Row
        // at all — ParsePage refuses it and says the shape moved, rather than
        // defaulting the field and quietly deciding on a value the payload never
        // carried.
        internal sealed record Row(
            string Id,
            string Kind,
            string Status,
            string Bucket,
            string Title,
            DateTime UpdatedAt,
            bool NeedsAction,
            string? Model,
            int? ContextPercent,
            string? StatusDetail,
            string? RecentAction);

        // What was wrong with a payload, where something was.
        //
        // An enum rather than a bool because the three say different things to
        // whoever is reading the status line: a body that is not JSON is a
        // different morning from a body that is JSON with no `data` array, which
        // is different again from rows that parse but have lost a field.
        internal enum PageShape
        {
            Ok,
            NotJson,
            NoData,
            RowMissingFields,
            NoKnownKind,
        }

        // One page of the listing.
        //
        // Rows are whatever parsed, *even when Shape is not Ok*. Degrading rather
        // than breaking is this arm's stated posture: one row of 578 that has lost
        // a field should cost that row and a sentence on the status line, not the
        // whole feature. What it must never do is cost the row silently, which is
        // what Shape is for.
        internal sealed record Page(
            IReadOnlyList<Row> Rows,
            bool HasMore,
            string? LastId,
            PageShape Shape,
            int Inspected);

        internal static string DescribeShape(PageShape shape) => shape switch
        {
            PageShape.NotJson => "the roster did not come back as JSON",
            PageShape.NoData => "the roster came back without a `data` array",
            PageShape.RowMissingFields =>
                "some rows arrived without an environment kind or a session status",
            PageShape.NoKnownKind =>
                "no row carried an environment kind this version knows — the filter may no longer match anything",
            _ => "",
        };

        // --- parsing ---------------------------------------------------------

        // The envelope is `{ data, first_id, has_more, last_id }`. `first_id` is
        // read by nothing: the walk goes forwards from `last_id` and there is no
        // backwards case.
        internal static Page ParsePage(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Page(Array.Empty<Row>(), false, null, PageShape.NotJson, 0);
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return new Page(Array.Empty<Row>(), false, null, PageShape.NotJson, 0);
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                {
                    return new Page(Array.Empty<Row>(), false, null, PageShape.NoData, 0);
                }

                var rows = new List<Row>();
                var inspected = 0;
                var missing = false;

                foreach (var element in data.EnumerateArray())
                {
                    inspected++;
                    var row = RowFrom(element);
                    if (row is null) missing = true;
                    else rows.Add(row);
                }

                var hasMore = root.TryGetProperty("has_more", out var more)
                              && more.ValueKind == JsonValueKind.True;

                var lastId = root.TryGetProperty("last_id", out var last)
                             && last.ValueKind == JsonValueKind.String
                    ? last.GetString()
                    : null;

                return new Page(rows, hasMore, string.IsNullOrWhiteSpace(lastId) ? null : lastId,
                    missing ? PageShape.RowMissingFields : PageShape.Ok, inspected);
            }
        }

        // The single-session read, which returns a bare session object rather than
        // an envelope. Same row shape, so the short cycle and the deep walk feed
        // the same reduction.
        internal static Row? ParseSession(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                return RowFrom(doc.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Null means "this is not a row" — no id, or missing one of the two fields
        // every decision below is made on. Returning a Row with a defaulted kind
        // would be the quiet version of this, and the quiet version is what the
        // ShapeChanged rule exists to prevent.
        private static Row? RowFrom(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;

            var id = Str(element, "id");
            if (string.IsNullOrWhiteSpace(id)) return null;

            var kind = Str(element, "environment_kind");
            var status = Str(element, "session_status");
            if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(status)) return null;

            var bucket = Str(element, "status_bucket") ?? "";
            var title = Str(element, "title") ?? "";
            var updated = TimeFrom(Str(element, "updated_at"));

            string? model = null;
            int? contextPercent = null;
            var needsAction = false;
            string? statusDetail = null;
            string? recentAction = null;

            if (element.TryGetProperty("external_metadata", out var meta)
                && meta.ValueKind == JsonValueKind.Object)
            {
                model = Str(meta, "model");
                contextPercent = ContextPercentFrom(meta);

                // **Null on every bridge row measured, a dict on cloud rows.** So
                // the absence is ordinary rather than a shape change, and reading
                // it has to survive a null without comment.
                if (meta.TryGetProperty("post_turn_summary", out var summary)
                    && summary.ValueKind == JsonValueKind.Object)
                {
                    needsAction = summary.TryGetProperty("needs_action", out var needs)
                                  && needs.ValueKind == JsonValueKind.True;
                    statusDetail = Str(summary, "status_detail");
                    recentAction = Str(summary, "recent_action");
                }
            }

            return new Row(id!, kind!, status!, bucket, title, updated, needsAction,
                model, contextPercent, statusDetail, recentAction);
        }

        // `context_usage` is `{ max_tokens, used_tokens }`. A zero or absent
        // maximum yields null rather than a division: "0%" and "we do not know"
        // are different answers and the ring drawn for them is different too.
        private static int? ContextPercentFrom(JsonElement meta)
        {
            if (!meta.TryGetProperty("context_usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!usage.TryGetProperty("max_tokens", out var max)
                || max.ValueKind != JsonValueKind.Number
                || !max.TryGetInt64(out var maxTokens)
                || maxTokens <= 0)
            {
                return null;
            }

            if (!usage.TryGetProperty("used_tokens", out var used)
                || used.ValueKind != JsonValueKind.Number
                || !used.TryGetInt64(out var usedTokens))
            {
                return null;
            }

            if (usedTokens < 0) usedTokens = 0;

            var percent = (int)Math.Round(usedTokens * 100.0 / maxTokens,
                MidpointRounding.AwayFromZero);

            return percent > 100 ? 100 : percent;
        }

        private static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        // An unparseable or absent `updated_at` becomes MinValue rather than now.
        //
        // This is the trap ClaudeCloudSessions.Session's own comment names: stamp
        // "now" and every session the account has ever had gets a permanent orb,
        // because the scan's recency window measures a time we invented rather
        // than a time anything happened.
        private static DateTime TimeFrom(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return DateTime.MinValue;

            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.UtcDateTime
                : DateTime.MinValue;
        }

        // --- the decisions ---------------------------------------------------

        // Cloud, and not archived. Both halves measured; see the header.
        internal static bool Keep(Row row) =>
            string.Equals(row.Kind, CloudKind, StringComparison.Ordinal)
            && !string.Equals(row.Status, ArchivedStatus, StringComparison.OrdinalIgnoreCase);

        internal static bool IsKnownKind(string? kind) =>
            string.Equals(kind, CloudKind, StringComparison.Ordinal)
            || string.Equals(kind, BridgeKind, StringComparison.Ordinal);

        // What the orb pulses as.
        //
        // **An unrecognised value is never "generating", and that is the whole
        // rule.** "generating" is the state that animates — it is what a person
        // reads as *something is happening right now* — so a status this version
        // has never heard of defaulting to it would mean a shape change on the
        // server showing up as every cloud session appearing permanently busy.
        // Falling to "idle" instead makes the same shape change show up as orbs
        // that sit still, which is wrong in the direction nobody acts on.
        //
        // Both fields are consulted because both carry the answer and neither is
        // documented as authoritative. Needing attention outranks being busy: a
        // session that is both blocked and running is one somebody has to go and
        // deal with, and saying "generating" about it would hide exactly the state
        // worth surfacing.
        internal static string StateFor(string? sessionStatus, string? statusBucket)
        {
            if (Says(sessionStatus, "blocked") || Says(statusBucket, "blocked")
                || Says(sessionStatus, "review_ready") || Says(statusBucket, "review_ready"))
            {
                return "waiting";
            }

            if (Says(sessionStatus, "running") || Says(statusBucket, "running"))
            {
                return "generating";
            }

            return "idle";

            static bool Says(string? value, string expected) =>
                string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
        }

        // Where a click goes.
        //
        // `session_url` is empty on every one of the 578 rows measured, so the
        // address is built from the id — which makes the id something that reaches
        // a URL, and on macOS a URL reaches `open`. So it is checked first, and
        // checked for what it must *be* rather than for what it must not contain:
        // the prefix plus an allow-list of the characters an id is made of. A
        // deny-list is a promise about every character somebody might one day put
        // in a payload, and this one does not need to make that promise.
        //
        // Null means "no link", which the caller shows as a session with no click
        // rather than as an error.
        internal static string? UrlFor(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (!id.StartsWith(IdPrefix, StringComparison.Ordinal)) return null;
            if (id.Length <= IdPrefix.Length) return null;

            foreach (var c in id)
            {
                var ok = c is >= 'a' and <= 'z' || c is >= 'A' and <= 'Z'
                         || c is >= '0' and <= '9' || c == '_' || c == '-';
                if (!ok) return null;
            }

            return "https://claude.ai/code/" + id;
        }

        // A kept row as the orb layer's own record. Sessions with no usable id are
        // already gone by here — UrlFor's refusal drops the row rather than
        // producing an orb whose click goes nowhere.
        internal static ClaudeCloudSessions.Session? ToSession(Row row)
        {
            var url = UrlFor(row.Id);
            if (url is null) return null;

            return new ClaudeCloudSessions.Session(
                row.Id,
                row.Title,
                StateFor(row.Status, row.Bucket),
                row.UpdatedAt,
                url,
                row.Bucket,
                row.NeedsAction,
                row.Model,
                row.ContextPercent,
                row.StatusDetail,
                row.RecentAction);
        }

        // --- the walk's result ------------------------------------------------

        // What a walk saw, reduced to what anything downstream needs.
        //
        // The histogram is not decoration. It is what makes "no cloud sessions"
        // checkable: a reduction reporting 578 inspected and 578 bridge is a
        // working filter finding nothing, and one reporting 578 inspected and 578
        // of some kind nobody recognises is a filter that has stopped matching.
        // Those two are indistinguishable without it.
        internal sealed record Reduction(
            IReadOnlyList<ClaudeCloudSessions.Session> Sessions,
            IReadOnlyDictionary<string, int> Kinds,
            int Inspected,
            bool Truncated,
            PageShape Shape)
        {
            internal bool ShapeChanged => Shape != PageShape.Ok;

            internal IReadOnlyList<string> UnknownKinds =>
                Kinds.Keys.Where(k => !IsKnownKind(k)).OrderBy(k => k, StringComparer.Ordinal)
                    .ToList();
        }

        internal static Reduction Reduce(IEnumerable<Page> pages, bool truncated)
        {
            var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
            var kept = new List<ClaudeCloudSessions.Session>();
            var inspected = 0;
            var shape = PageShape.Ok;
            var knownKinds = 0;

            foreach (var page in pages)
            {
                inspected += page.Inspected;
                if (shape == PageShape.Ok && page.Shape != PageShape.Ok) shape = page.Shape;

                foreach (var row in page.Rows)
                {
                    kinds[row.Kind] = kinds.TryGetValue(row.Kind, out var count) ? count + 1 : 1;
                    if (IsKnownKind(row.Kind)) knownKinds++;
                    if (!Keep(row)) continue;

                    var session = ToSession(row);
                    if (session is not null) kept.Add(session);
                }
            }

            // Rows arrived and not one of them carried a kind this version knows.
            // That is the silently-empty filter, and it is reported as a shape
            // change rather than as an empty roster — see the header.
            if (inspected > 0 && knownKinds == 0 && shape == PageShape.Ok)
            {
                shape = PageShape.NoKnownKind;
            }

            return new Reduction(Order(kept), kinds, inspected, truncated, shape);
        }

        // Newest first, ties broken by id.
        //
        // Ordered here rather than left to whatever order the pages arrived in,
        // because the payload's own order is `created_at` DESC and **`updated_at`
        // is unsorted** — so the roster's order is not the order anything happened
        // in, and two reductions over the same sessions would otherwise differ for
        // no reason a reader could see.
        private static List<ClaudeCloudSessions.Session> Order(
            IEnumerable<ClaudeCloudSessions.Session> sessions) =>
            sessions
                .OrderByDescending(s => s.LastActivity)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .ToList();

        // --- what to fetch next ------------------------------------------------

        // One cycle's shape.
        //
        // A deep walk pages the whole roster; the short cycle reads page one and
        // then asks about each session already known, by id. The second half is
        // the part worth explaining: the roster is `created_at` DESC, so a cloud
        // session created last month that resumes today does **not** move to the
        // front of page one. Without the per-session reads it would stay invisible
        // — or worse, stay frozen at whatever state the last deep walk saw — until
        // the next walk came round.
        internal sealed record Plan(bool DeepWalk, IReadOnlyList<string> RefreshIds);

        internal static Plan PlanFor(DateTime lastWalkUtc, DateTime nowUtc,
            IReadOnlyList<ClaudeCloudSessions.Session> known)
        {
            var due = lastWalkUtc == default
                      || nowUtc - lastWalkUtc >= CloudRequest.UnmeasuredWalkInterval;

            if (due) return new Plan(true, Array.Empty<string>());

            return new Plan(false, known.Select(s => s.Id).ToList());
        }

        // Fold a short cycle's answers into what is already on screen.
        //
        // `resolved` is every id this cycle got a *definitive* answer about —
        // whether that answer was "here it is" or "it is archived now". A known
        // session in `resolved` but not in `fresh` has genuinely ended and its orb
        // goes. A known session not in `resolved` at all is one this cycle simply
        // did not ask about, and it survives untouched: a short cycle is a partial
        // view by construction, and treating a partial view as authoritative is
        // how an orb disappears for no reason.
        internal static IReadOnlyList<ClaudeCloudSessions.Session> Merge(
            IReadOnlyList<ClaudeCloudSessions.Session> known,
            IReadOnlyList<ClaudeCloudSessions.Session> fresh,
            IReadOnlyCollection<string> resolved)
        {
            var seen = new HashSet<string>(fresh.Select(s => s.Id), StringComparer.Ordinal);
            var answered = new HashSet<string>(resolved, StringComparer.Ordinal);

            var merged = new List<ClaudeCloudSessions.Session>(fresh);

            foreach (var session in known)
            {
                if (seen.Contains(session.Id)) continue;
                if (answered.Contains(session.Id)) continue;
                merged.Add(session);
            }

            return Order(merged);
        }

        // --- what to say ------------------------------------------------------

        // The status line, and the one place the "no cloud sessions" wording is
        // allowed to appear — always with the count of what was inspected beside
        // it, because the two failure modes it could be hiding (a filter matching
        // nothing, a fetch that read nothing) are otherwise the same sentence.
        internal static string Describe(Reduction reduction)
        {
            var parts = new List<string>();

            if (reduction.ShapeChanged)
            {
                parts.Add(DescribeShape(reduction.Shape));
            }

            // **The one case that must not also say "no cloud sessions".** When
            // not a single row carried a kind this version knows, the empty result
            // is a symptom of the shape change rather than a second fact about the
            // account, and printing both invites the reader to take the reassuring
            // half — "no cloud sessions, fine" — and skip the alarming one. The
            // inspected count still goes out, because it is what makes the claim
            // checkable.
            if (reduction.Shape == PageShape.NoKnownKind)
            {
                parts.Add($"{reduction.Inspected} sessions inspected");
            }
            else if (reduction.Sessions.Count == 0)
            {
                parts.Add(reduction.Inspected == 0
                    ? "no sessions came back"
                    : $"no cloud sessions ({reduction.Inspected} sessions inspected)");
            }
            else
            {
                parts.Add(reduction.Sessions.Count == 1
                    ? $"1 cloud session ({reduction.Inspected} sessions inspected)"
                    : $"{reduction.Sessions.Count} cloud sessions ({reduction.Inspected} sessions inspected)");
            }

            if (reduction.Truncated)
            {
                parts.Add($"stopped after {CloudRequest.MaxPagesPerWalk} pages, so there may be more");
            }

            var unknown = reduction.UnknownKinds;
            if (unknown.Count > 0)
            {
                parts.Add("also saw " + string.Join(", ", unknown)
                                       + " — an environment kind this version does not know");
            }

            return string.Join("; ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }
}
