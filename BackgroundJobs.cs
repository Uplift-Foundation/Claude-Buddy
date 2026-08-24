using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace ClaudeBuddy
{
    // What the daemon thinks of a background session, which its status file
    // can't tell you.
    //
    // A background agent's hook writes the same states every other session
    // writes — "idle" when it isn't mid-turn — so a job that has finished for
    // good is indistinguishable on disk from one sitting between turns. The
    // difference only exists in `claude agents`, which carries a separate
    // `state` ("done" once the work is over) alongside the status the hook
    // reports.
    //
    // That difference matters because a finished job has nothing to show: it
    // has no terminal, and `claude attach` on it prints "Attaching…" and exits
    // immediately, so an orb for one is a click that opens a window and closes
    // it again. Better not to have the orb.
    internal static class BackgroundJobs
    {
        // The scan runs every couple of seconds and this shells out, so it is
        // cached well above that. Long enough to be cheap, short enough that an
        // agent finishing doesn't leave a stale orb around for long.
        private const long CacheMs = 10_000;

        private static readonly object Gate = new();
        private static Dictionary<string, string>? _states;
        private static long _stamp;

        // Whether this session is a background job that is still going.
        //
        // Asked of two shapes of session. The first records no pid at all — a
        // wider group than it sounds: a background agent has none, but neither
        // does a subagent, and neither does a session whose status file outlived
        // it. The second is a status file that lost SessionManager.Superseded's
        // pid tie-break — which happens for real to an Agent View background
        // session, since dispatching one shares its parent's pid rather than
        // getting one of its own. Both shapes write the same "idle" whether they
        // still have work to do or not, so the daemon's own list is the only
        // place to settle it.
        public static bool IsLiveJob(string sessionId) => IsLive(States(), sessionId);

        // The answer, separated from fetching the listing so it can be tested
        // without a daemon to ask. `states` is what Parse returned — null for a
        // listing that could not be read at all.
        //
        // Absent from a listing that was read successfully means not a job at
        // all: a subagent, or a session that ended. "done" means it was one and
        // has finished. Neither has anything left to show, and an orb for
        // either is a click that can only fail.
        //
        // A listing that couldn't be read answers true, not false. This decides
        // whether to *hide* an orb, and no orb should vanish because the CLI
        // was briefly unavailable — the failure the user can't see is the one
        // worth being careful about.
        internal static bool IsLive(Dictionary<string, string>? states, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return true;
            if (states is null) return true;

            // The session id first, because that is the question being asked and
            // a listing answers it directly. The short job id is a fallback for a
            // row that named no session — see Parse.
            if (!states.TryGetValue(sessionId, out var state)
                && !states.TryGetValue(JobIdOf(sessionId), out state))
            {
                return false;
            }

            return !string.Equals(state, "done", StringComparison.OrdinalIgnoreCase);
        }

        // Excluded from coverage: shells out to the `claude` CLI, and the cache
        // it wraps is keyed on Environment.TickCount64. What it decides with the
        // answer is IsLive above, which is tested; what it decides about the
        // *listing* is Parse below, which is also tested. This is the process
        // launch and the clock, and nothing else.
        [ExcludeFromCodeCoverage]
        private static Dictionary<string, string>? States()
        {
            lock (Gate)
            {
                if (_states is not null && Environment.TickCount64 - _stamp < CacheMs)
                {
                    return _states;
                }
            }

            var fresh = Read();

            lock (Gate)
            {
                // A failed read doesn't clear a good answer: the listing going
                // missing for one tick is not evidence that anything finished.
                if (fresh is not null)
                {
                    _states = fresh;
                    _stamp = Environment.TickCount64;
                }
                return _states;
            }
        }

        // Excluded from coverage: starts the `claude` CLI as a real subprocess.
        // The JSON it prints is parsed by Parse below, which is tested against
        // hand-written listings, so what is excluded here is the launch and
        // nothing that decides anything.
        [ExcludeFromCodeCoverage]
        private static Dictionary<string, string>? Read()
        {
            try
            {
                // Invoked directly, not through a shell. The obvious way to
                // reach a binary that isn't on this app's PATH is to ask the
                // user's shell to find it, and that quietly doesn't work:
                // `zsh -lc` reads .zshenv, .zprofile and .zlogin, but *not*
                // .zshrc, which is only for interactive shells — and .zshrc is
                // where a PATH addition for ~/.local/bin normally lives. So the
                // lookup failed with "command not found" for exactly the launch
                // this was written to survive, and because a failed read is
                // treated as "don't hide anything", every finished session and
                // every subagent got an orb again.
                var claude = ClaudeBinary.Path;
                if (claude is null) return null;

                // --json is documented as not needing a tty, which is what
                // makes it usable from a GUI app at all.
                var psi = new ProcessStartInfo(claude)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                psi.ArgumentList.Add("agents");
                psi.ArgumentList.Add("--json");

                using var process = Process.Start(psi);
                if (process is null) return null;

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                if (process.ExitCode != 0) return null;

                return Parse(output);
            }
            catch
            {
                return null;
            }
        }

        // `claude agents --json` turned into the states IsLive asks about, keyed
        // by what the caller will name the session as.
        //
        // A background row carries both an `id` — the short job id, which is
        // also the name of the job's directory under ~/.claude/jobs — and a
        // `sessionId`, the full uuid of the session running it. The first
        // version of this keyed the map on the short id and derived the same
        // string from the session id it was asked about, on the assumption that
        // one is the first segment of the other.
        //
        // It usually is, which is what let the assumption survive: a job's id is
        // the first segment of the session id it *started* with. But a job
        // outlives that session — resume it, or let it compact, and it keeps its
        // original id while the work moves on to a session with a new uuid.
        // Observed on a real machine: job `5f6960b2` running session
        // `53bd5d2c-…`, working, with the derived lookup asking about `53bd5d2c`
        // and finding nothing. Absent reads as "not a job at all", so a
        // background session that was busy working had its orb dropped on every
        // scan, and the same miss let SessionManager.Superseded call it stale.
        //
        // So the map is keyed by the session id the row states, rather than by
        // anything derived. The short id is stored only for a row that named no
        // session — an older CLI, or a job the daemon knows about before its
        // session exists — which is what keeps the fallback in IsLive worth
        // having. Storing it as well as the session id would undo the fix by the
        // back door: the *earlier* session of a resumed job would match its own
        // job's row and keep an orb for a conversation that has moved on.
        internal static Dictionary<string, string>? Parse(string json)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;

                // Interactive sessions carry no id at all — only background
                // ones are jobs — so they simply don't appear here, and
                // nothing downstream ever asks about them.
                if (Text(entry, "id") is not { Length: > 0 } jobId) continue;

                var state = Text(entry, "state") ?? "";

                map[Text(entry, "sessionId") is { Length: > 0 } sessionId ? sessionId : jobId] = state;
            }

            return map;
        }

        private static string? Text(JsonElement entry, string name) =>
            entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        // The short form the daemon uses, which is the first segment of the
        // session id a job started with. Only reached for a row that named no
        // session of its own; split rather than a fixed width so an id that
        // isn't a uuid degrades to itself.
        // internal: AgentTeamViewer carries its own copy of this rule, and the
        // two are used together — one decides which pane to reuse and the other
        // whether the job is still alive — so a test asserts they agree. A
        // session that resolved to two different job ids would be adopted into a
        // pane and then have its orb hidden.
        internal static string JobIdOf(string sessionId)
        {
            var dash = sessionId.IndexOf('-');
            return dash > 0 ? sessionId[..dash] : sessionId;
        }
    }
}
