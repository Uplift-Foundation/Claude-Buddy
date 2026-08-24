using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClaudeBuddy
{
    // Reads Claude Code's JSONL transcript to extract the latest assistant
    // message text. Reads from the tail (transcripts reach tens of MB) and
    // stops at the first assistant record found.
    public static class TranscriptReader
    {
        private const int TailBytes = 262144;
        private const int MaxSpokenChars = 1500;

        // The last thing a Codex session said, for the orb's speak button.
        //
        // Separate entry point rather than a branch inside LatestAssistantText,
        // and the separation is the point: that method falls back to searching
        // ~/.claude/projects when it finds nothing, which for a Codex session
        // would find a *Claude Code* transcript for the same directory and speak
        // an unrelated session's last turn out of a Codex orb. There is no
        // equivalent fallback here and there should not be one — a rollout that
        // cannot be read is silence, which is the honest answer.
        public static string? LatestCodexAgentText(string? transcriptPath)
        {
            if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath))
                return null;

            try
            {
                var lines = TailLines(transcriptPath);
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    // The same pre-filter CodexTranscript uses, for the same
                    // reason: the rows that carry the bytes are the ones this
                    // does not want.
                    if (!CodexTranscript.IsInteresting(lines[i])) continue;

                    var turns = CodexTranscript.Map(new[] { lines[i] });
                    var text = turns
                        .Where(t => t.Turn.Role == ChatRole.Assistant)
                        .Select(t => t.Turn.Text)
                        .LastOrDefault();

                    if (string.IsNullOrWhiteSpace(text)) continue;

                    return text.Length > MaxSpokenChars ? text[..MaxSpokenChars] + "…" : text;
                }
            }
            catch
            {
            }

            return null;
        }

        public static string? LatestAssistantText(string? transcriptPath, string? sessionId = null)
        {
            if (string.IsNullOrEmpty(transcriptPath) && !string.IsNullOrEmpty(sessionId))
                transcriptPath = FindTranscript(sessionId);

            if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath))
                return null;

            try
            {
                var lines = TailLines(transcriptPath);
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    var line = lines[i];
                    if (!line.Contains("\"type\":\"assistant\""))
                        continue;

                    var text = ExtractText(line);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text.Length > MaxSpokenChars
                            ? text[..MaxSpokenChars] + "…"
                            : text;
                }
            }
            catch
            {
            }

            return null;
        }

        // Find the most recently written transcript in any project
        // directory whose path encodes the given CWD.  Used when a
        // controller session has no transcript of its own — its
        // background jobs live in sibling project dirs with the same
        // CWD prefix.
        // home is a parameter with the real one as its default, so the walk can be
        // pointed at a temp directory instead of the machine's own transcripts.
        public static string? LatestTranscriptForCwd(string cwd, string? home = null)
        {
            if (string.IsNullOrEmpty(cwd)) return null;

            home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dirs = new List<string> { Path.Combine(home, ".claude") };
            foreach (var extra in ClaudeBuddySettings.ClaudeCodeProfileDirs)
                dirs.Add(Path.Combine(home, extra));

            var encoded = EncodeCwd(cwd);

            string? best = null;
            DateTime bestTime = DateTime.MinValue;

            foreach (var configDir in dirs)
            {
                var projects = Path.Combine(configDir, "projects");
                if (!Directory.Exists(projects)) continue;

                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(projects))
                    {
                        if (!ProjectDirMatches(Path.GetFileName(dir), encoded)) continue;

                        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
                        {
                            try
                            {
                                var mod = File.GetLastWriteTimeUtc(file);
                                if (mod > bestTime)
                                {
                                    bestTime = mod;
                                    best = file;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            return best;
        }

        // When the hook hasn't written transcript_path yet (old status
        // file), find <session-id>.jsonl under the known Claude Code
        // config directories.
        //
        // Public as well as used internally: the chat panel needs the same
        // answer for the same reason, and a session whose status file predates
        // transcript_path is exactly the one whose orb you'd click wondering
        // what it had been doing.
        public static string? FindTranscriptFor(string sessionId, string? home = null) =>
            FindTranscript(sessionId, home);

        private static string? FindTranscript(string sessionId, string? home = null)
        {
            home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dirs = new List<string> { Path.Combine(home, ".claude") };

            foreach (var extra in ClaudeBuddySettings.ClaudeCodeProfileDirs)
                dirs.Add(Path.Combine(home, extra));

            var filename = sessionId + ".jsonl";
            foreach (var configDir in dirs)
            {
                var projects = Path.Combine(configDir, "projects");
                if (!Directory.Exists(projects)) continue;

                try
                {
                    var match = Directory.EnumerateFiles(projects, filename, SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (match is not null) return match;
                }
                catch { }
            }

            return null;
        }

        // Claude Code encodes /Users/foo/Source/Bar as -Users-foo-Source-Bar
        // inside its projects directory. Pure, and worth its own name because the
        // encoding is lossy — a directory whose name contains a dash is
        // indistinguishable from a path separator once encoded — which is exactly
        // why the match below cannot be a plain prefix test.
        internal static string EncodeCwd(string cwd)
        {
            var encoded = cwd.Replace(Path.DirectorySeparatorChar, '-');

            return encoded.Length > 0 && encoded[0] != '-' ? "-" + encoded : encoded;
        }

        // Whether one projects-directory name is this cwd or something under it.
        //
        // The second condition is the one that matters: a plain StartsWith would
        // match -Users-foo-Source-Barn for -Users-foo-Source-Bar and hand back a
        // transcript from an entirely different project. Requiring the next
        // character to be the separator is what keeps sibling directories with a
        // shared prefix apart.
        internal static bool ProjectDirMatches(string dirName, string encoded)
        {
            if (!dirName.StartsWith(encoded, StringComparison.Ordinal)) return false;

            return dirName.Length <= encoded.Length || dirName[encoded.Length] == '-';
        }

        // Excluded from coverage: the try/catch only. The window logic it wraps is
        // in ReadTail below and stays covered — what cannot be arranged is the
        // catch, which is for the file disappearing between the caller's
        // File.Exists and this open. That is a real race, since the file belongs
        // to a session that may be ending, but it is a race and not a state a test
        // can hold still.
        [ExcludeFromCodeCoverage]
        private static string[] TailLines(string path)
        {
            try { return ReadTail(path); }
            catch { return Array.Empty<string>(); }
        }

        private static string[] ReadTail(string path)
        {
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long start = Math.Max(0, fs.Length - TailBytes);
                fs.Seek(start, SeekOrigin.Begin);

                using var reader = new StreamReader(fs);
                var chunk = reader.ReadToEnd();

                // If we seeked past the beginning, the first partial line is
                // garbage — drop it.
                if (start > 0)
                {
                    int nl = chunk.IndexOf('\n');
                    if (nl >= 0)
                        chunk = chunk[(nl + 1)..];
                }

                return chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
        }

        // Transcript assistant records have a top-level "type":"assistant"
        // (not necessarily the first key) and a "message" object whose
        // "content" array holds text and tool_use blocks. We extract and
        // concatenate the text blocks.
        private static string? ExtractText(string jsonLine)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonLine);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp)
                    || typeProp.GetString() != "assistant")
                    return null;

                if (!root.TryGetProperty("message", out var message))
                    return null;
                if (!message.TryGetProperty("content", out var content))
                    return null;
                if (content.ValueKind != JsonValueKind.Array)
                    return null;

                var sb = new System.Text.StringBuilder();
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var blockType)
                        && blockType.GetString() == "text"
                        && block.TryGetProperty("text", out var textProp))
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(textProp.GetString());
                    }
                }

                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
