using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

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
    internal static class ClaudeCloudSessions
    {
        private static readonly object Gate = new();

        // Published whole and replaced whole, like OpenClawSessions' own: the
        // scan reads it on the UI thread and never locks anything else.
        private static volatile IReadOnlyList<Session> _snapshot = Array.Empty<Session>();

        private static string _state = "off";

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

        // Excluded from coverage: starts the poll loop that talks to the real
        // endpoint. Everything it decides lives in ClaudeCloudRoster and in the
        // arm's step function, both of which are pure and covered.
        [ExcludeFromCodeCoverage]
        public static void Restart()
        {
            lock (Gate)
            {
                _snapshot = Array.Empty<Session>();

                if (!ClaudeBuddySettings.ClaudeCloudEnabled)
                {
                    _state = "off";
                    return;
                }

                _state = "checking…";
            }
        }
    }
}
