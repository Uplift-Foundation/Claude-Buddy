using System;
using System.Collections.Generic;
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
        public static string? LatestTranscriptForCwd(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return null;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dirs = new List<string> { Path.Combine(home, ".claude") };
            foreach (var extra in ClaudeBuddySettings.ClaudeCodeProfileDirs)
                dirs.Add(Path.Combine(home, extra));

            // Claude Code encodes /Users/foo/Source/Bar as
            // -Users-foo-Source-Bar inside the projects directory.
            var encoded = cwd.Replace(Path.DirectorySeparatorChar, '-');
            if (encoded.Length > 0 && encoded[0] != '-')
                encoded = "-" + encoded;

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
                        var dirName = Path.GetFileName(dir);
                        if (!dirName.StartsWith(encoded, StringComparison.Ordinal))
                            continue;
                        // Must be exact match or a sub-path separator
                        if (dirName.Length > encoded.Length && dirName[encoded.Length] != '-')
                            continue;

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
        private static string? FindTranscript(string sessionId)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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

        private static string[] TailLines(string path)
        {
            try
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
            catch
            {
                return Array.Empty<string>();
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
