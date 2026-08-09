using System.Diagnostics;
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
        // Asked of sessions that record no pid, which is a wider group than it
        // sounds: a background agent has none, but neither does a subagent, and
        // neither does a session whose status file outlived it. Only the first
        // is something to show an orb for, and the difference isn't on disk —
        // every one of them writes the same "idle" — so the daemon's own list
        // is the only place to settle it.
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
        public static bool IsLiveJob(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return true;

            var states = States();
            if (states is null) return true;

            if (!states.TryGetValue(JobIdOf(sessionId), out var state)) return false;

            return !string.Equals(state, "done", StringComparison.OrdinalIgnoreCase);
        }

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

        private static Dictionary<string, string>? Read()
        {
            try
            {
                // --json is documented as not needing a tty, which is what
                // makes it usable from a GUI app at all.
                var psi = new ProcessStartInfo("/bin/sh")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                // Through a login shell for the same reason AgentTeamViewer
                // needs one: launched from Finder this app has only the stock
                // system PATH, and `claude` isn't on it.
                var shell = Environment.GetEnvironmentVariable("SHELL");
                if (string.IsNullOrEmpty(shell)) shell = "/bin/zsh";

                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add($"exec {shell} -lc 'claude agents --json'");

                using var process = Process.Start(psi);
                if (process is null) return null;

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                if (process.ExitCode != 0) return null;

                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                using var document = JsonDocument.Parse(output);
                if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

                foreach (var entry in document.RootElement.EnumerateArray())
                {
                    // Interactive sessions carry no id at all — only background
                    // ones are jobs — so they simply don't appear here, and
                    // nothing downstream ever asks about them.
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
            catch
            {
                return null;
            }
        }

        // The short form the daemon uses, which is the first segment of the
        // session uuid. Split rather than a fixed width so an id that isn't a
        // uuid degrades to itself.
        private static string JobIdOf(string sessionId)
        {
            var dash = sessionId.IndexOf('-');
            return dash > 0 ? sessionId[..dash] : sessionId;
        }
    }
}
