using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeBuddy
{
    // Every Claude Code config directory on this machine: the default ~/.claude
    // plus each extra account the user runs out of a CLAUDE_CONFIG_DIR alias.
    //
    // Pulled out into its own file because two separate rules need the same
    // list and were each getting it wrong in a different way. TranscriptReader
    // has built it inline twice since before this existed, which is where the
    // shape comes from — a ".claude" first, then ClaudeCodeProfileDirs, all
    // relative to $HOME — and it is deliberately not refactored to call this:
    // it works, and rewriting a working transcript hunt is not this branch's
    // business. Anything new asks here.
    //
    // home is a parameter with the real one as its default, the same seam
    // TranscriptReader.FindTranscriptFor uses, so a test can point the walk at
    // a temp directory rather than at the machine's own accounts.
    internal static class ClaudeConfigRoots
    {
        internal static IReadOnlyList<string> All(string? home = null)
        {
            home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var roots = new List<string> { Path.Combine(home, ".claude") };

            foreach (var extra in ClaudeBuddySettings.ClaudeCodeProfileDirs)
            {
                // A blank entry is what a half-finished settings edit leaves
                // behind, and Path.Combine would quietly answer $HOME for it —
                // which is not a config root, but *is* a directory that exists,
                // so it would be asked and would fail rather than being skipped.
                if (string.IsNullOrWhiteSpace(extra)) continue;

                var full = Path.Combine(home, extra.Trim());

                // Someone listing ".claude" alongside the default is not an
                // error worth refusing, but it must not double the work: every
                // caller here either launches a subprocess per root or stats a
                // file per root, and the second answer can only ever repeat the
                // first.
                if (!roots.Contains(full, StringComparer.Ordinal)) roots.Add(full);
            }

            return roots;
        }
    }
}
