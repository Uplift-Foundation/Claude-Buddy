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

        // Whether an *action* -- focusing a pane, typing a keystroke into it --
        // may proceed against a pane whose live owner resolved to resolvedOwner.
        // This is the caller's half of the answer above: For and SessionIdIn
        // report the truth about what the probe could establish, including when
        // that truth is "nothing" -- it is deliberately not their job to collapse
        // Unknown into a refusal just because a refusal looks like the safe
        // default.
        //
        // resolvedOwner is null or empty when tmux or ps failed, or -- the
        // ordinary case -- when the pane's claude process carries no
        // --session-id in its argv at all, which is true of a plain interactive
        // session and of an agent-team member alike (its argv names
        // --agent-id/--team-name, never --session-id). Refusing on that would
        // mean every click and every dictation into an ordinary session's pane
        // does nothing, silently, forever: "I cannot prove this pane is yours"
        // and "I have proven this pane belongs to someone else" would read as
        // the same failure, and they are not the same risk. An indeterminate
        // probe has found no evidence the pane changed hands, so there is
        // nothing here to refuse on. A resolvedOwner that positively names a
        // *different* session -- tmux having reused the pane id for a new
        // conversation after the old one exited -- is a real answer, and that
        // one still fails closed; see commit 7e9fd51a for why this half must
        // never soften. An empty expectedSessionId permits for the same reason:
        // with nothing to compare against, there is no expectation left to
        // violate.
        internal static bool PermitsAction(string? expectedSessionId, string? resolvedOwner)
        {
            if (string.IsNullOrEmpty(expectedSessionId) || string.IsNullOrEmpty(resolvedOwner)) return true;
            return string.Equals(resolvedOwner, expectedSessionId, StringComparison.OrdinalIgnoreCase);
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
