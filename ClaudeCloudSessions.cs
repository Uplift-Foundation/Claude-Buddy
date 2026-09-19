using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeBuddy
{
    // Claude Code sessions running in Anthropic's cloud, listed by the account
    // API rather than by a hook.
    //
    // Shaped after OpenClawSessions deliberately, because the two arms answer
    // the same question: what is running somewhere this app cannot see, and
    // what state is it in. A static class with a volatile snapshot rather than
    // an interface, for the reason that file gives — a second implementation
    // that is permanently off is ceremony, and "off" here means Snapshot()
    // returns nothing.
    //
    // ## What this reads, and the one thing it is careful about
    //
    // GET https://api.anthropic.com/v2/ccr-sessions, with the Claude Code CLI's
    // own OAuth token. Two headers: Authorization and anthropic-version. That
    // host serves ordinary HTTP clients; claude.ai does not — it answers a
    // non-browser client with a Cloudflare challenge, identically for a real
    // token and an invalid one, which is how the first attempt at this wasted
    // an afternoon. See docs/claude-cloud-findings.md.
    //
    // **The roster is mostly not cloud sessions.** Measured on a real account:
    // 578 rows, of which 573 were `environment_kind: bridge` — the user's own
    // *local* sessions, registered for remote control, which Claude Buddy
    // already draws orbs for from its hooks. Drawing those again would double
    // every local orb. Five rows were genuinely `anthropic_cloud`, one of them
    // not archived. So the filter is not a nicety; it is the difference between
    // this feature and a bug report.
    //
    // ## Where the decisions live
    //
    // Almost nowhere in this file. Reading the payload, filtering it, naming a
    // state and wording the status line are all ClaudeCloudRoster's, and
    // everything this arm decides per tick — which credential state stops it,
    // which cycle is due, what to publish when a fetch fails halfway — is in
    // StepAsync, which takes its API, its credential source and its clock as
    // arguments. What is left in RunAsync is a `while` and a `Task.Delay`.
    internal static class ClaudeCloudSessions
    {
        private static readonly object Gate = new();

        // Published whole and replaced whole, like OpenClawSessions' own: the
        // scan reads it on the UI thread and never locks anything else.
        private static volatile IReadOnlyList<Session> _snapshot = Array.Empty<Session>();

        private static string _state = "off";

        private static CancellationTokenSource? _cts;

        // One row the orb layer can draw without knowing anything about JSON.
        //
        // LastActivity is the session's own `updated_at`, never the time of the
        // read. SessionManager stamps ScanEntry.Written from it, and stamping
        // "now" instead would give every session the account has ever had a
        // permanent orb — the same trap OpenClaw's mapping documents.
        //
        // Url is carried rather than built at the click, because the payload's
        // own `session_url` is empty on every row measured and the id is what
        // the address is actually made of.
        internal sealed record Session(
            string Id,
            string Title,
            string State,
            DateTime LastActivity,
            string Url,
            string StatusBucket,
            bool NeedsAction,
            string? Model,
            int? ContextPercent,
            string? StatusDetail,
            string? RecentAction);

        // The settings gate lives here rather than in SessionManager.EnabledFor,
        // following OpenClaw: off means the app asks the OS for no credential
        // and opens no socket, which is stronger than "draws no orb" and is the
        // promise the settings copy makes.
        public static IReadOnlyList<Session> Snapshot() =>
            ClaudeBuddySettings.ClaudeCloudEnabled ? _snapshot : Array.Empty<Session>();

        // What the settings window shows on its status row.
        public static string StatusText { get { lock (Gate) return _state; } }

        // The seam every downstream test uses, matching
        // OpenClawSessions.SetSnapshotForTests: the poll loop is the only
        // production publisher and is excluded from coverage, so without this
        // every orb this feature draws would be unreachable from a test.
        internal static void SetSnapshotForTests(IReadOnlyList<Session> sessions)
        {
            _snapshot = sessions;
        }

        internal static void SetStateForTests(string state)
        {
            lock (Gate) _state = state;
        }

        // How long a halted arm waits before looking again.
        //
        // It is not a retry. A halted arm sends no request and reads no secret —
        // all it does is call Stamp(), which on macOS is an attributes-only
        // Keychain query that returns no data and therefore is **not** the query
        // the consent prompt guards, and on Windows and Linux is a file mtime.
        // So this cadence costs nothing a user can perceive, and it is what makes
        // "sign in again and the orbs come back on their own" true without ever
        // producing a second prompt.
        internal static readonly TimeSpan HaltedRecheckInterval = TimeSpan.FromSeconds(30);

        // --- the arm, as a value ----------------------------------------------

        // Everything one tick needs to remember from the last one.
        //
        // A record passed in and out rather than fields on this class, for the
        // reason ArrangementTests gives about OrbArrangement: a decision that
        // reads its inputs out of statics can only be tested by arranging the
        // statics, and a suite that arranges process-wide state is a suite whose
        // cases interfere with each other.
        //
        // `Halted` is the credential-and-refusal stop. It is not a long backoff:
        // CB-164 requires that a refused or absent login produces no cloud orbs,
        // a legible reason and **no repeated OS prompt**, and only a distinct
        // stop state can promise that. It clears when `Stamp()` moves, which is
        // the user having signed in again or re-approved us.
        internal sealed record ArmState(
            IReadOnlyList<Session> Sessions,
            string Status,
            string? CredentialStamp,
            bool Halted,
            DateTime LastWalkUtc,
            TimeSpan? Backoff)
        {
            internal static ArmState Initial { get; } = new(
                Array.Empty<Session>(), "checking…", null, false, default, null);
        }

        // What one tick decided.
        //
        // **Snapshot is nullable and null is the interesting value.** It means
        // "publish nothing; leave what is on screen alone", and it exists for one
        // case in particular: a walk that failed on page four must not replace a
        // good roster with an empty one. Orbs vanishing because a request timed
        // out would read as sessions having ended, which is a lie the app would be
        // telling on the strength of a network hiccup.
        //
        // An empty list, by contrast, is a real answer — "we have access and there
        // is nothing" or "we have no access at all" — and does clear the orbs.
        internal sealed record StepResult(
            ArmState Next,
            IReadOnlyList<Session>? Snapshot,
            string Status,
            TimeSpan Wait);

        // One tick. Every decision this arm makes is here.
        //
        // `readBudget` is how long the credential read is given before the arm
        // gives up on it — a parameter rather than a constant read inside so a
        // test can drive the give-up path in milliseconds instead of waiting
        // three quarters of a minute. Production passes nothing and gets
        // ClaudeCliCredentials.UnmeasuredReadBudget.
        internal static async Task<StepResult> StepAsync(
            ICloudApi api,
            ICloudCredentialSource credentials,
            ArmState state,
            DateTime now,
            CancellationToken ct,
            TimeSpan? readBudget = null)
        {
            var stamp = credentials.Stamp();

            // Halted, and nothing about the stored credential has changed. Do not
            // read the secret, do not open a socket, do not prompt.
            if (state.Halted && string.Equals(stamp, state.CredentialStamp, StringComparison.Ordinal))
            {
                return new StepResult(state, null, state.Status, HaltedRecheckInterval);
            }

            // **Never `credentials.Read()` directly.** On macOS that is a P/Invoke
            // into Security.framework which, in a context with no window server
            // session, was measured not to return at all — at 30 seconds and at 60.
            // Called on this thread it parks the supervisor forever, leaving the
            // status on "checking…" and the user with no orbs and no error, which
            // is indistinguishable from having no cloud sessions. The budget is the
            // only thing standing between that measurement and a silent app.
            var read = await ClaudeCliCredentials.ReadWithinAsync(
                credentials, readBudget ?? ClaudeCliCredentials.UnmeasuredReadBudget, ct)
                .ConfigureAwait(false);

            if (read.Outcome != CredentialOutcome.Found || read.AccessToken is not { } token)
            {
                var wait = Backoff.Next(read.Outcome, state.Backoff);
                return Stop(state, stamp, ClaudeCliCredentials.Describe(read.Outcome), wait);
            }

            var plan = ClaudeCloudRoster.PlanFor(state.LastWalkUtc, now, state.Sessions);

            return plan.DeepWalk
                ? await WalkAsync(api, state, stamp, token, now, ct).ConfigureAwait(false)
                : await RefreshAsync(api, state, stamp, token, plan, ct).ConfigureAwait(false);
        }

        // The deep cycle: page the whole roster, capped.
        private static async Task<StepResult> WalkAsync(ICloudApi api, ArmState state,
            string? stamp, string token, DateTime now, CancellationToken ct)
        {
            var pages = new List<ClaudeCloudRoster.Page>();
            string? after = null;

            for (var i = 0; i < CloudRequest.MaxPagesPerWalk; i++)
            {
                var result = await api.GetAsync(
                    new CloudRequestContext(token, CloudRequest.ListPath(CloudRequest.MaxPageSize, after)),
                    ct).ConfigureAwait(false);

                if (result.Outcome.Kind != CloudOutcomeKind.Ok)
                {
                    return Failed(state, stamp, result.Outcome);
                }

                var page = ClaudeCloudRoster.ParsePage(result.Body);
                pages.Add(page);

                if (!page.HasMore || page.LastId is null)
                {
                    after = null;
                    break;
                }

                after = page.LastId;
            }

            // Still somewhere to go when the cap ran out. Kept, marked, and said
            // out loud by Describe — never silently fewer.
            var reduction = ClaudeCloudRoster.Reduce(pages, after is not null);
            var status = ClaudeCloudRoster.Describe(reduction);

            return new StepResult(
                state with
                {
                    Sessions = reduction.Sessions,
                    Status = status,
                    CredentialStamp = stamp,
                    Halted = false,
                    Backoff = null,
                    LastWalkUtc = now,
                },
                reduction.Sessions,
                status,
                CloudRequest.UnmeasuredFirstPageInterval);
        }

        // The short cycle: page one, then one direct read per session already
        // known. See ClaudeCloudRoster.Plan for why the second half is not
        // redundant with the first.
        private static async Task<StepResult> RefreshAsync(ICloudApi api, ArmState state,
            string? stamp, string token, ClaudeCloudRoster.Plan plan, CancellationToken ct)
        {
            var first = await api.GetAsync(
                new CloudRequestContext(token, CloudRequest.ListPath(CloudRequest.MaxPageSize, null)),
                ct).ConfigureAwait(false);

            if (first.Outcome.Kind != CloudOutcomeKind.Ok)
            {
                return Failed(state, stamp, first.Outcome);
            }

            var page = ClaudeCloudRoster.ParsePage(first.Body);

            var resolved = new HashSet<string>(page.Rows.Select(r => r.Id), StringComparer.Ordinal);
            var fresh = new List<Session>();

            foreach (var row in page.Rows)
            {
                if (!ClaudeCloudRoster.Keep(row)) continue;
                if (ClaudeCloudRoster.ToSession(row) is { } session) fresh.Add(session);
            }

            foreach (var id in plan.RefreshIds)
            {
                if (resolved.Contains(id)) continue;

                var one = await api.GetAsync(
                    new CloudRequestContext(token, CloudRequest.SessionPath(id)), ct)
                    .ConfigureAwait(false);

                if (one.Outcome.Kind != CloudOutcomeKind.Ok)
                {
                    // A refusal that would stop the arm stops it here too — there
                    // is no point walking the rest of the list to be refused eight
                    // more times. Anything retryable is treated as **no news about
                    // this session**: its orb survives on what the last deep walk
                    // saw, and the next walk is the authority that will drop it if
                    // it has really gone. A 404 is deliberately in that group; that
                    // it means "ended" rather than "briefly unreachable" is not
                    // something anybody has measured.
                    if (Backoff.Next(one.Outcome, state.Backoff) is null)
                    {
                        return Failed(state, stamp, one.Outcome);
                    }

                    continue;
                }

                if (ClaudeCloudRoster.ParseSession(one.Body) is not { } row) continue;

                resolved.Add(row.Id);
                if (!ClaudeCloudRoster.Keep(row)) continue;
                if (ClaudeCloudRoster.ToSession(row) is { } session) fresh.Add(session);
            }

            var merged = ClaudeCloudRoster.Merge(state.Sessions, fresh, resolved);

            // The reduction describes what *page one* saw — the inspected count
            // and the kind histogram are about the rows actually looked at — while
            // the sessions are the merged set, which includes ones this cycle never
            // asked about. Two different populations in one sentence, and that is
            // right: the counts are a claim about the fetch and the list is a claim
            // about what is running.
            var reduction = ClaudeCloudRoster.Reduce(new[] { page }, false) with
            {
                Sessions = merged,
            };

            var status = ClaudeCloudRoster.Describe(reduction);

            return new StepResult(
                state with
                {
                    Sessions = merged,
                    Status = status,
                    CredentialStamp = stamp,
                    Halted = false,
                    Backoff = null,
                },
                merged,
                status,
                CloudRequest.UnmeasuredFirstPageInterval);
        }

        // A request that came back wrong.
        //
        // The split is the whole point. A verdict Backoff stops on — the token was
        // refused, we are blocked — is a definite statement that there is no access,
        // so the orbs go and the arm halts until the credential changes. Anything
        // retryable leaves the orbs exactly where they are and publishes nothing.
        private static StepResult Failed(ArmState state, string? stamp, CloudOutcome outcome)
        {
            var wait = Backoff.Next(outcome, state.Backoff);
            var status = outcome.Detail ?? $"the endpoint answered {outcome.Status}";

            if (wait is null) return Stop(state, stamp, status, null);

            return new StepResult(
                state with
                {
                    Status = status,
                    CredentialStamp = stamp,
                    Halted = false,
                    Backoff = wait,
                },
                null,
                status,
                wait.Value);
        }

        // A stop, or a retryable credential problem, in one shape.
        private static StepResult Stop(ArmState state, string? stamp, string status, TimeSpan? wait)
        {
            if (wait is { } retry)
            {
                return new StepResult(
                    state with { Status = status, CredentialStamp = stamp, Halted = false, Backoff = retry },
                    null,
                    status,
                    retry);
            }

            return new StepResult(
                state with
                {
                    Sessions = Array.Empty<Session>(),
                    Status = status,
                    CredentialStamp = stamp,
                    Halted = true,
                    Backoff = null,
                },
                Array.Empty<Session>(),
                status,
                HaltedRecheckInterval);
        }

        // --- the loop ---------------------------------------------------------

        // Excluded from coverage: a `while` around StepAsync and a `Task.Delay`.
        // Everything it would be worth testing is in StepAsync, which is why this
        // is as short as it is.
        [ExcludeFromCodeCoverage]
        private static async Task RunAsync(ICloudApi api, ICloudCredentialSource credentials,
            CancellationToken ct)
        {
            var state = ArmState.Initial;

            while (!ct.IsCancellationRequested)
            {
                StepResult step;
                try
                {
                    step = await StepAsync(api, credentials, state, DateTime.UtcNow, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Nothing below this is allowed to take the app down. The arm
                    // keeps its sessions and says what happened.
                    state = state with { Status = ex.Message, Backoff = Backoff.Cap };
                    lock (Gate) _state = ex.Message;
                    try { await Task.Delay(Backoff.Cap, ct).ConfigureAwait(false); } catch { break; }
                    continue;
                }

                state = step.Next;

                if (step.Snapshot is { } published) _snapshot = published;
                lock (Gate) _state = step.Status;

                try { await Task.Delay(step.Wait, ct).ConfigureAwait(false); } catch { break; }
            }
        }

        // Excluded from coverage: starts the poll loop that talks to the real
        // endpoint, and on macOS is the path that can raise a Keychain prompt.
        // Everything it decides lives in ClaudeCloudRoster and StepAsync, both of
        // which are pure or driven by fakes and covered.
        [ExcludeFromCodeCoverage]
        public static void Restart()
        {
            lock (Gate)
            {
                _cts?.Cancel();
                _cts = null;
                _snapshot = Array.Empty<Session>();

                if (!ClaudeBuddySettings.ClaudeCloudEnabled)
                {
                    _state = "off";
                    return;
                }

                _state = "checking…";

                var cts = new CancellationTokenSource();
                _cts = cts;

                var api = new HttpCloudApi();
                var credentials = ClaudeCliCredentials.SourceFor(
                    OperatingSystem.IsMacOS(),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

                _ = Task.Run(() => RunAsync(api, credentials, cts.Token), cts.Token);
            }
        }
    }
}
