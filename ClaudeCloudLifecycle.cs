using System.Net.Http;
using System.Text.Json;

namespace ClaudeBuddy
{
    // CB-225: archiving and deleting a Claude Code cloud session from its orb.
    //
    // What these two requests are, and what their answers mean, was measured
    // before any of this was written — against throwaway sessions made for the
    // purpose with `claude --cloud`, never a real one — and is recorded in
    // docs/claude-cloud-findings.md under "Archive and delete (CB-225)". Four
    // of those facts shape this file:
    //
    //  * **Archive** is `POST /v1/code/sessions/{id}/archive` with `{}`: 200,
    //    with the session back as `status: "archived"`, and 200 again if it is
    //    asked twice. It is the route the Claude Code CLI's own fleet view
    //    calls.
    //  * **Delete** is `DELETE /v1/code/sessions/{id}`: 200 and `{}`, on an
    //    archived session or an active one. The CLI never sends it — it is
    //    the one route here most likely to move.
    //  * **A 404 from either comes in two kinds that mean opposite things.** A
    //    JSON `not_found_error` is a handler saying there is no such session; a
    //    plain-text "404 page not found" is the router saying there is no such
    //    route. OutcomeFor's Kind reads both as SessionGone; its
    //    SessionNotFound flag tells them apart. **Nothing here treats a 404 on
    //    the action as success** — if the route ever moved, "Deleted" on the
    //    row would be a lie the user had no way to catch.
    //  * Both refuse a bogus token, a missing token and a missing
    //    `anthropic-version` exactly as a send does, so the existing outcome
    //    arms already have the right words for every refusal.
    //
    // Pure except RunAsync, which does the exchange through the ICloudApi seam
    // and an ICloudCredentialSource — both of which a test fakes — and keeps
    // no token past the call, under the custody rules in ClaudeCliCredentials.
    public enum CloudLifecycleAction
    {
        Archive,
        Delete,
    }

    internal enum CloudLifecycleVerdict
    {
        // The endpoint did it, and for a delete a follow-up read agreed.
        Done,

        // The endpoint answered 2xx to a delete, and the read that should have
        // confirmed it could not be made or did not answer. The 2xx came from a
        // handler — a router does not 200 — so the session is treated as gone;
        // the row says it could not be checked rather than claiming it was.
        DoneUnconfirmed,

        // Anything else. Detail says what, in the endpoint's terms.
        Refused,
    }

    internal sealed record CloudLifecycleResult(CloudLifecycleVerdict Verdict, string? Detail = null)
    {
        // Done in either form: what decides whether the orb goes.
        public bool Succeeded => Verdict != CloudLifecycleVerdict.Refused;
    }

    // One request, as data, so the exact verb, path and body are testable
    // without a socket.
    internal readonly record struct CloudLifecycleRequest(HttpMethod Method, string Path, string? Body);

    internal static class ClaudeCloudLifecycle
    {
        // Exactly what the CLI posts. Measured: the endpoint takes it.
        internal const string ArchiveBody = "{}";

        internal const string SessionNotFoundDetail = "not found — the session may already be gone";
        internal const string RouteNotFoundDetail = "not found — the endpoint did not recognise the request";
        internal const string StillThereDetail = "the endpoint said it was deleted, but the session is still there";
        internal const string NoCredentialDetail = "no usable Claude Code login for this session's account";

        // Null for an id that is not well formed, which is a refusal rather
        // than an escape — the rule CloudRequest.CodeEventsPath gives for
        // writes, and both of these are writes.
        internal static CloudLifecycleRequest? RequestFor(CloudLifecycleAction action, string? id)
        {
            switch (action)
            {
                case CloudLifecycleAction.Archive:
                    return CloudRequest.ArchivePath(id) is { } archive
                        ? new CloudLifecycleRequest(HttpMethod.Post, archive, ArchiveBody)
                        : null;

                default:
                    return CloudRequest.CodeSessionPath(id) is { } session
                        ? new CloudLifecycleRequest(HttpMethod.Delete, session, null)
                        : null;
            }
        }

        // The verdict on the action's own answer.
        //
        // **2xx only.** A 404 is reported as not found, worded for which kind
        // it was, and is never success. A 409 to an archive — which the CLI
        // counts as "already archived" — is reported as a refusal too: it was
        // never seen (a second archive answered 200), and if it ever is, the
        // roster's own filter removes an archived session's orb on the next
        // read anyway, so saying what the endpoint said costs nothing.
        internal static CloudLifecycleResult VerdictFor(CloudOutcome outcome)
        {
            if (outcome.Kind == CloudOutcomeKind.Ok) return new CloudLifecycleResult(CloudLifecycleVerdict.Done);

            if (outcome.Status == 404)
            {
                return new CloudLifecycleResult(CloudLifecycleVerdict.Refused,
                    outcome.SessionNotFound ? SessionNotFoundDetail : RouteNotFoundDetail);
            }

            return new CloudLifecycleResult(CloudLifecycleVerdict.Refused, outcome.Detail);
        }

        // The verdict on the read that follows a delete's 2xx.
        //
        // The handler's JSON 404 is the confirmation. A 200 is the session
        // still being there, which is a refusal however the delete answered.
        // Anything else — the read failed, timed out, was refused — leaves the
        // 2xx standing and says it could not be checked.
        internal static CloudLifecycleResult ConfirmationFor(CloudOutcome outcome)
        {
            if (outcome.SessionNotFound)
                return new CloudLifecycleResult(CloudLifecycleVerdict.Done);

            if (outcome.Kind == CloudOutcomeKind.Ok)
                return new CloudLifecycleResult(CloudLifecycleVerdict.Refused, StillThereDetail);

            return new CloudLifecycleResult(CloudLifecycleVerdict.DoneUnconfirmed,
                outcome.Detail ?? $"the endpoint answered {outcome.Status}");
        }

        // The whole exchange for one action on one session.
        //
        // The credential is the session owner's (CB-221): the caller passes
        // CloudAccounts.SourceFor(row.OwnerRoot), never "whichever is first",
        // because a cloud session can only be archived by the account it
        // belongs to. Read within the same budget every other cloud read uses.
        internal static async Task<CloudLifecycleResult> RunAsync(
            ICloudApi api,
            ICloudCredentialSource credentials,
            CloudLifecycleAction action,
            string? id,
            CancellationToken ct)
        {
            if (RequestFor(action, id) is not { } request)
                return new CloudLifecycleResult(CloudLifecycleVerdict.Refused, "not a cloud session id");

            var read = await ClaudeCliCredentials
                .ReadWithinAsync(credentials, ClaudeCliCredentials.UnmeasuredReadBudget, ct)
                .ConfigureAwait(false);

            if (read.Outcome != CredentialOutcome.Found || read.AccessToken is not { } token)
                return new CloudLifecycleResult(CloudLifecycleVerdict.Refused, NoCredentialDetail);

            var answer = await api.SendAsync(
                new CloudRequestContext(token, request.Path, request.Method, request.Body), ct)
                .ConfigureAwait(false);

            // The outcome, never the body: HttpCloudApi hands a body back only
            // on a 2xx, and what a 404 meant is already on the outcome.
            var verdict = VerdictFor(answer.Outcome);
            if (action != CloudLifecycleAction.Delete || !verdict.Succeeded) return verdict;

            // Well formed, checked by RequestFor, so not null.
            var check = await api.SendAsync(
                new CloudRequestContext(token, CloudRequest.CodeSessionPath(id)!), ct)
                .ConfigureAwait(false);

            return ConfirmationFor(check.Outcome);
        }
    }

    // Which of the two rows a cloud orb is offered. One rule, read by the menu
    // and by nothing else, so the menu cannot offer what the action layer
    // would refuse.
    internal readonly record struct CloudOrbOffer(bool Archive, bool Delete)
    {
        public static readonly CloudOrbOffer None = new(false, false);
    }

    internal static class CloudOrbActions
    {
        // The scan's key prefix for a cloud orb. Matched, not sliced blindly:
        // anything without it is not a cloud orb, whatever its Source says.
        internal const string KeyPrefix = "cloud:";

        // The roster id inside an orb's key, or null.
        internal static string? IdFromKey(string? key) =>
            key is not null && key.StartsWith(KeyPrefix, StringComparison.Ordinal)
            && ClaudeCloudRoster.IsWellFormedId(key[KeyPrefix.Length..])
                ? key[KeyPrefix.Length..]
                : null;

        // A cloud orb only, with the cloud setting on, and a well-formed id.
        //
        // Never a `bridge` row: those are someone's live local session
        // registered for Remote Control, and ClaudeCloudRoster.Keep already
        // keeps every one of them out of SessionSource.ClaudeCloud — so the
        // source check is that guarantee, not a second copy of it. The setting
        // is read by the caller, as the cloud orbs' own gate is, so the rule
        // does not depend on the machine running it.
        internal static CloudOrbOffer Offer(SessionSource source, string? key, bool cloudEnabled) =>
            source == SessionSource.ClaudeCloud && cloudEnabled && IdFromKey(key) is not null
                ? new CloudOrbOffer(true, true)
                : CloudOrbOffer.None;
    }

    // Every word the two rows say, in one table so the menu and its tests read
    // the same sentences — the shape OpenClawActionText set.
    internal static class CloudActionText
    {
        public static string Header(CloudLifecycleAction action) => action == CloudLifecycleAction.Archive
            ? "Archive this session"
            : "Delete this session…";

        // Delete says it is permanent because it is: nothing in the API
        // restores a deleted session. Archive does not, because it isn't —
        // claude.ai lists archived sessions and their history stays readable.
        public static string Armed(CloudLifecycleAction action) => action == CloudLifecycleAction.Archive
            ? "Click again to archive"
            : "Click again to delete — this can't be undone";

        public static string Tip(CloudLifecycleAction action) => action == CloudLifecycleAction.Archive
            ? "Archives this cloud session on your Claude account. It stops running and leaves Buddy; claude.ai still lists it under archived."
            : "Deletes this cloud session from your Claude account, history and all. It cannot be restored.";

        public static string Working(CloudLifecycleAction action) => action == CloudLifecycleAction.Archive
            ? "Archiving…"
            : "Deleting…";

        public static string For(CloudLifecycleAction action, CloudLifecycleResult result)
        {
            var verb = action == CloudLifecycleAction.Archive ? "archive" : "delete";

            return result.Verdict switch
            {
                CloudLifecycleVerdict.Done => action == CloudLifecycleAction.Archive ? "Archived" : "Deleted",
                CloudLifecycleVerdict.DoneUnconfirmed => $"Deleted — couldn't confirm: {result.Detail}",
                _ => $"Couldn't {verb}: {result.Detail}",
            };
        }
    }
}
