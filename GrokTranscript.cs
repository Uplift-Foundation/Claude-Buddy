using System.Text;
using System.Text.Json;

namespace ClaudeBuddy
{
    // Turning Grok Build's ACP update stream into what the chat panel shows.
    //
    // The sibling of ChatTranscript and CodexTranscript, and pure for the same
    // reason: no files, no windows, no settings. Rows come out as
    // ChatTranscript.Row so LocalCliChatSession does not care which CLI wrote
    // the file it is tailing.
    //
    // Grok writes ~/.grok/sessions/<urlencode(cwd)>/<session-id>/updates.jsonl.
    // Each line is an ACP session/update (or _x.ai/session/update) envelope:
    //
    //     {"timestamp":…,"method":"session/update","params":{"update":{…}}}
    //
    // The conversation as a person saw it is in sessionUpdate values
    // user_message_chunk, agent_message_chunk, agent_thought_chunk and
    // tool_call. Those first three are streamed in pieces — measured on a
    // live session, one user prompt was a single chunk and one assistant
    // reply was many — so this stitches consecutive chunks of the same kind
    // into one turn. tool_call_update, hook_execution, turn_completed and
    // current_mode_update carry no new text the panel should show.
    //
    // Confirmed against a real updates.jsonl from grok 1.0.13, 31 Aug 2026.
    // See docs/grok-findings.md.
    public static class GrokTranscript
    {
        private const int MaxParseBytes = 2 * 1024 * 1024;

        public static List<ChatTranscript.Row> Map(IEnumerable<string> lines)
        {
            var mapped = new List<ChatTranscript.Row>();
            var user = new StringBuilder();
            var assistant = new StringBuilder();
            var thought = new StringBuilder();
            string? userKey = null;
            string? assistantKey = null;
            string? thoughtKey = null;
            DateTimeOffset userAt = default;
            DateTimeOffset assistantAt = default;
            DateTimeOffset thoughtAt = default;
            var index = 0;

            void FlushUser()
            {
                if (user.Length == 0) return;
                mapped.Add(new ChatTranscript.Row(userKey, new ChatTurn
                {
                    Role = ChatRole.User,
                    Text = user.ToString(),
                    IsComplete = true,
                    At = userAt == default ? DateTimeOffset.Now : userAt
                }));
                user.Clear();
                userKey = null;
            }

            void FlushAssistant()
            {
                if (assistant.Length == 0) return;
                mapped.Add(new ChatTranscript.Row(assistantKey, new ChatTurn
                {
                    Role = ChatRole.Assistant,
                    Text = assistant.ToString(),
                    IsComplete = true,
                    At = assistantAt == default ? DateTimeOffset.Now : assistantAt
                }));
                assistant.Clear();
                assistantKey = null;
            }

            void FlushThought()
            {
                if (thought.Length == 0) return;
                mapped.Add(new ChatTranscript.Row(thoughtKey, new ChatTurn
                {
                    Role = ChatRole.System,
                    Text = thought.ToString(),
                    IsComplete = true,
                    At = thoughtAt == default ? DateTimeOffset.Now : thoughtAt
                }));
                thought.Clear();
                thoughtKey = null;
            }

            foreach (var line in lines)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                if (!IsInteresting(line)) continue;

                if (line.Length > MaxParseBytes) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!TryUpdate(root, out var update)) continue;

                    var kind = Str(update, "sessionUpdate");
                    var at = Time(root);
                    var key = Str(update, "toolCallId")
                              ?? Str(update, "prompt_id")
                              ?? (++index).ToString();

                    switch (kind)
                    {
                        case "user_message_chunk":
                            FlushAssistant();
                            FlushThought();
                            var userText = ContentText(update);
                            if (userText.Length == 0) break;
                            if (user.Length == 0)
                            {
                                userKey = "user:" + key;
                                userAt = at;
                            }
                            user.Append(userText);
                            break;

                        case "agent_message_chunk":
                            FlushUser();
                            FlushThought();
                            var assistantText = ContentText(update);
                            if (assistantText.Length == 0) break;
                            if (assistant.Length == 0)
                            {
                                assistantKey = "assistant:" + key;
                                assistantAt = at;
                            }
                            assistant.Append(assistantText);
                            break;

                        case "agent_thought_chunk":
                            FlushUser();
                            FlushAssistant();
                            var thoughtText = ContentText(update);
                            if (thoughtText.Length == 0) break;
                            if (thought.Length == 0)
                            {
                                thoughtKey = "thought:" + key;
                                thoughtAt = at;
                            }
                            thought.Append(thoughtText);
                            break;

                        case "tool_call":
                            FlushUser();
                            FlushAssistant();
                            FlushThought();
                            var title = Str(update, "title");
                            var toolKind = Str(update, "kind");
                            var label = string.IsNullOrEmpty(title)
                                ? (toolKind ?? "tool")
                                : title;
                            mapped.Add(new ChatTranscript.Row("tool:" + key, new ChatTurn
                            {
                                Role = ChatRole.System,
                                Text = "· " + label,
                                IsComplete = true,
                                At = at == default ? DateTimeOffset.Now : at
                            }));
                            break;
                    }
                }
                catch
                {
                    // A row we can't parse is a row we don't show.
                }
            }

            FlushUser();
            FlushAssistant();
            FlushThought();
            return mapped;
        }

        // A false positive costs one parse of a row that Map then ignores.
        // A false negative silently drops a message. The four strings are
        // exactly the sessionUpdate values that produce a turn.
        public static bool IsInteresting(string line) =>
            line.Contains("user_message_chunk", StringComparison.Ordinal)
            || line.Contains("agent_message_chunk", StringComparison.Ordinal)
            || line.Contains("agent_thought_chunk", StringComparison.Ordinal)
            || line.Contains("\"sessionUpdate\":\"tool_call\"", StringComparison.Ordinal)
            || line.Contains("\"sessionUpdate\": \"tool_call\"", StringComparison.Ordinal);

        private static bool TryUpdate(JsonElement root, out JsonElement update)
        {
            if (root.TryGetProperty("params", out var parameters)
                && parameters.ValueKind == JsonValueKind.Object
                && parameters.TryGetProperty("update", out update)
                && update.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            if (root.TryGetProperty("update", out update)
                && update.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            update = default;
            return false;
        }

        private static string ContentText(JsonElement update)
        {
            if (!update.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Object)
            {
                return "";
            }

            return Str(content, "text") ?? "";
        }

        private static string? Str(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return value.GetString();
        }

        private static DateTimeOffset Time(JsonElement root)
        {
            if (root.TryGetProperty("timestamp", out var value))
            {
                if (value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt64(out var unix))
                {
                    // Live rows used a unix timestamp. Values around 1.7e9 are
                    // seconds; larger ones are milliseconds.
                    if (unix > 10_000_000_000)
                        return DateTimeOffset.FromUnixTimeMilliseconds(unix);
                    if (unix > 0)
                        return DateTimeOffset.FromUnixTimeSeconds(unix);
                }

                if (value.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(value.GetString(), out var parsed))
                {
                    return parsed;
                }
            }

            return default;
        }
    }
}
