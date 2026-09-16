using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeBuddy
{
    // The answer to the one question a pane claim cannot answer: is the Claude
    // process displayed in this pane still running this *session*? A pane id is
    // a location and tmux deliberately reuses it; only the child's exact
    // --session-id is authority for a conversation.
    internal enum TmuxPaneOwnership
    {
        Match,
        Mismatch,
        Unknown
    }

    internal sealed record ProcessCommand(int Pid, int ParentPid, string Arguments);

    internal static class TmuxPaneOwnershipRules
    {
        internal static string? SessionIdIn(IEnumerable<ProcessCommand>? processes, int panePid)
        {
            if (panePid <= 0 || processes is null) return null;

            var all = processes.ToList();
            var descendants = new HashSet<int> { panePid };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var process in all)
                {
                    if (descendants.Contains(process.ParentPid) && descendants.Add(process.Pid))
                        changed = true;
                }
            }

            var ids = all.Where(process => descendants.Contains(process.Pid))
                .Select(process => SessionIdFrom(process.Arguments))
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return ids.Count == 1 ? ids[0] : null;
        }

        internal static TmuxPaneOwnership For(
            string? expectedSessionId, int panePid, IEnumerable<ProcessCommand>? processes)
        {
            if (string.IsNullOrEmpty(expectedSessionId) || panePid <= 0 || processes is null)
                return TmuxPaneOwnership.Unknown;

            var owner = SessionIdIn(processes, panePid);
            if (owner is null) return TmuxPaneOwnership.Unknown;
            return string.Equals(owner, expectedSessionId, StringComparison.OrdinalIgnoreCase)
                ? TmuxPaneOwnership.Match
                : TmuxPaneOwnership.Mismatch;
        }

        // Claude's argv uses two words, and deliberately not a substring
        // search: a prompt, path, or child tool merely mentioning a UUID must
        // never become authority for a pane.
        internal static string? SessionIdFrom(string? arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return null;
            var words = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || !SessionPresence.LooksLikeClaudeBinary(words[0])) return null;

            for (var i = 0; i + 1 < words.Length; i++)
            {
                if (words[i] == "--session-id") return words[i + 1];
            }

            return null;
        }
    }
}
