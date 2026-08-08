using System;
using System.IO;
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

        public static string? LatestAssistantText(string transcriptPath)
        {
            if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath))
                return null;

            try
            {
                var lines = TailLines(transcriptPath);
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    var line = lines[i];
                    if (!line.StartsWith("{\"type\":\"assistant\""))
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

        // Transcript assistant records look like:
        //   {"type":"assistant","message":{"role":"assistant","content":[
        //     {"type":"text","text":"The answer is..."},
        //     {"type":"tool_use",...}
        //   ],...}}
        // We extract and concatenate the text blocks.
        private static string? ExtractText(string jsonLine)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonLine);
                var root = doc.RootElement;

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
