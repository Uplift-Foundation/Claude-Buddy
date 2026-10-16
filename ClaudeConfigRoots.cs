using System;
using System.Collections.Generic;
using System.IO;

namespace Orbweaver
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

            foreach (var extra in OrbweaverSettings.ClaudeCodeProfileDirs)
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

        // The config root a transcript lives under: the parent of the "projects"
        // segment, so ~/.claude/projects/<slug>/<id>.jsonl gives ~/.claude. The
        // layout is fixed — projects, a slug, the file — so the segment is read
        // at that position rather than searched for, which keeps a home directory
        // or slug that is itself called "projects" from shifting the answer. Null
        // when the path is not that shape, which callers read as "unknown, use the
        // default account". Both separators are accepted so a Windows path reads
        // the same in a test on a Mac.
        internal static string? RootOfTranscript(string? transcriptPath)
        {
            if (string.IsNullOrWhiteSpace(transcriptPath)) return null;

            var parts = transcriptPath.Split('/', '\\');
            var at = parts.Length - 3;
            if (at < 1 || parts[at] != "projects") return null;

            var sep = transcriptPath.Contains('/') ? "/" : "\\";
            var root = string.Join(sep, parts[..at]);
            return root.Length == 0 ? sep : root;
        }
    }
}
