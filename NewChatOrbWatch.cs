using System;
using System.Collections.Generic;

namespace ClaudeBuddy
{
    // The ~20s "did an orb actually appear" check CB-168's plan asks for: a
    // launch that spawned a terminal has no guarantee the CLI's hook is
    // installed and trusted, so the dialog watches the session scan for a
    // few seconds and says plainly if nothing showed up.
    //
    // Pure and window-free, the same split every other decision rule in this
    // app uses (OrbArrangement, RecentFolders, NewChatAvailability): the
    // question "is this status the one I'm waiting for" doesn't need a
    // window or a timer behind it to be answered, so it gets a case per
    // outcome instead of only ever being exercised through a live dialog and
    // a real wall-clock wait.
    internal static class NewChatOrbWatch
    {
        // The CLI a local session's Source maps to, or null for anything
        // that isn't one of the three this dialog can launch (OpenClaw,
        // remote-control, Claude Cloud). Shared with the orb context menu's
        // own prefill, so the two never name a session's CLI differently.
        internal static NewChatCli? CliOf(SessionSource source) => source switch
        {
            SessionSource.ClaudeCode => NewChatCli.ClaudeCode,
            SessionSource.Codex => NewChatCli.Codex,
            SessionSource.Grok => NewChatCli.Grok,
            _ => null
        };

        // Whether `status` is plausibly the orb a launch of `cli` into `cwd`
        // just produced: it wasn't one of the sessions already running
        // before the launch, it's a local session of the right CLI, and its
        // cwd matches the folder that was launched into.
        internal static bool IsNewMatch(
            string sessionId, SessionStatus status, IReadOnlySet<string> priorSessionIds,
            NewChatCli cli, string cwd)
        {
            ArgumentNullException.ThrowIfNull(sessionId);
            ArgumentNullException.ThrowIfNull(status);
            ArgumentNullException.ThrowIfNull(priorSessionIds);

            if (priorSessionIds.Contains(sessionId)) return false;
            if (!status.IsLocalCli) return false;
            if (CliOf(status.Source) != cli) return false;
            return PathsMatch(status.Cwd, cwd);
        }

        // Scans every currently-known session for the first one IsNewMatch
        // accepts, or null if none does yet. The real ~20s poll just calls
        // this once a second; a test calls it directly against a hand-built
        // snapshot, with no timer and no wall-clock wait involved.
        internal static string? FindMatch(
            IReadOnlyDictionary<string, SessionStatus> current, IReadOnlySet<string> priorSessionIds,
            NewChatCli cli, string cwd)
        {
            ArgumentNullException.ThrowIfNull(current);

            foreach (var (id, status) in current)
            {
                if (IsNewMatch(id, status, priorSessionIds, cli, cwd)) return id;
            }

            return null;
        }

        // Trailing separators aside, a launched terminal's cwd and the path
        // this dialog asked for should read identically — but a Windows path
        // is case-insensitive on disk while a macOS/Linux one usually isn't,
        // the same distinction RecentFolders.Merge draws for the same
        // reason.
        private static bool PathsMatch(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;

            var trimmedA = a.TrimEnd('/', '\\');
            var trimmedB = b.TrimEnd('/', '\\');

            return OperatingSystem.IsWindows()
                ? string.Equals(trimmedA, trimmedB, StringComparison.OrdinalIgnoreCase)
                : string.Equals(trimmedA, trimmedB, StringComparison.Ordinal);
        }
    }
}
