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
        public static bool IsLiveJob(string sessionId) => IsLiveJobGiven(sessionId, States());

        // The decision, separated from the subprocess that answers it. Every
        // rule the comment above argues for lives here, and none of it needs a
        // `claude` binary to be reachable — which is the whole reason to split
        // it, because the rules are the part that decides whether an orb
        // vanishes and the subprocess is only where the listing comes from.
        internal static bool IsLiveJobGiven(string sessionId, Dictionary<string, string>? states)
        {
            if (string.IsNullOrEmpty(sessionId)) return true;

            if (states is null) return true;

            if (!states.TryGetValue(JobIdOf(sessionId), out var state)) return false;

            return !string.Equals(state, "done", StringComparison.OrdinalIgnoreCase);
        }

        // Excluded from coverage: shells out to the `claude` CLI, and the cache
        // it wraps is keyed on Environment.TickCount64. What it decides with the
        // answer is IsLiveJobGiven above, which is tested; what it decides about
        // the *listing* is ParseAgents below, which is also tested. This is the
        // process launch and the clock, and nothing else.
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
        // The JSON it prints is parsed by ParseAgents below, which is tested
        // against hand-written listings, so what is excluded here is the launch
        // and nothing that decides anything.
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

                return ParseAgents(output);
            }
            catch
            {
                return null;
            }
        }

        // The listing, as a function of the text the CLI printed.
        //
        // Split out for the same reason as IsLiveJobGiven: this reads a JSON
        // shape defined by another program, which is the class of thing CLAUDE.md
        // says to cover at the parsing level *as well as* at the seam, because
        // the two fail differently — the parser gets a field wrong, the seam
        // gets the whole exchange wrong. Null means "could not be read", which
        // callers deliberately treat as "hide nothing".
        internal static Dictionary<string, string>? ParseAgents(string output)
        {
            try
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                using var document = JsonDocument.Parse(output);
                if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

                foreach (var entry in document.RootElement.EnumerateArray())
                {
                    // Interactive sessions carry no id at all — only background
                    // ones are jobs — so they simply don't appear here, and
                    // nothing downstream ever asks about them.
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    if (!entry.TryGetProperty("id", out var id)) continue;
                    if (id.ValueKind != JsonValueKind.String) continue;
                    if (id.GetString() is not { Length: > 0 } jobId) continue;

                    var state = entry.TryGetProperty("state", out var s)
                                && s.ValueKind == JsonValueKind.String
                        ? s.GetString() ?? ""
                        : "";

                    map[jobId] = state;
                }

                return map;
            }
            catch (JsonException)
            {
                // Not JSON at all. Same answer as a failed read, for the same
                // reason: an unreadable listing is not evidence that anything
                // finished.
                return null;
            }
        }

        // The short form the daemon uses, which is the first segment of the
        // session uuid. Split rather than a fixed width so an id that isn't a
        // uuid degrades to itself.
        internal static string JobIdOf(string sessionId)
        {
            var dash = sessionId.IndexOf('-');
            return dash > 0 ? sessionId[..dash] : sessionId;
        }
    }
}
