using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeBuddy
{
    // The "New chat…" dialog's folder combo: live local-CLI cwds first (so a
    // folder you're already working in shows up without having launched a
    // chat there before), then whatever was saved from previous launches,
    // de-duplicated and capped.
    //
    // Pure and window-free on purpose — same reasoning OrbArrangement's own
    // comment gives: the rule that decides ordering and the cap belongs in a
    // function with no settings and no scan behind it, so it can be given a
    // case per outcome rather than only ever exercised through a live window.
    internal static class RecentFolders
    {
        public const int DefaultMax = 10;

        // `live` is scanned for local-CLI sessions only — an OpenClaw or
        // remote-control session has no local folder to offer here, and
        // SessionStatus.IsLocalCli is exactly the test the rest of the app
        // already uses to draw that line. `saved` is whatever
        // ClaudeBuddySettings.NewChatRecentFolders last held, oldest choice
        // first is not assumed: callers pass it in whatever order they kept
        // it, and this method preserves that order for the part it doesn't
        // already know is live.
        public static IReadOnlyList<string> Merge(
            IEnumerable<SessionStatus> live,
            IReadOnlyList<string> saved,
            int max = DefaultMax)
        {
            ArgumentNullException.ThrowIfNull(live);
            ArgumentNullException.ThrowIfNull(saved);
            if (max <= 0) return Array.Empty<string>();

            // Case-insensitive dedup only on Windows, matching the rest of
            // this app's path handling (ClaudeBuddySettings' own
            // RemoteControlProfileDirs comment and ClaudeCodeProfileDirs
            // callers do the same) — macOS and Linux paths are case-sensitive
            // by default, so folding case there could merge two genuinely
            // different folders.
            var comparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

            var seen = new HashSet<string>(comparer);
            var result = new List<string>();

            void Add(string? folder)
            {
                if (result.Count >= max) return;
                if (string.IsNullOrWhiteSpace(folder)) return;
                if (!seen.Add(folder)) return;
                result.Add(folder);
            }

            foreach (var session in live)
            {
                if (session is null) continue;
                if (!session.IsLocalCli) continue;
                Add(session.Cwd);
            }

            foreach (var folder in saved)
            {
                Add(folder);
            }

            return result;
        }
    }
}
