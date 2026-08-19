using System.Text.Json;

namespace ClaudeBuddy
{
    // Turning what Codex writes down into what the chat panel shows.
    //
    // The sibling of ChatTranscript, and pure for the same reason: no files, no
    // windows, no settings, no dispatcher, so `dotnet run --project
    // tests/TranscriptTests` can hold it to a fixture instead of a person
    // holding it to a screenshot. Rows come out as ChatTranscript.Row because
    // the chat session that consumes them does not care which CLI produced the
    // file it is tailing — see LocalCliChatSession.
    //
    // Codex's transcript is a rollout at
    // ~/.codex/sessions/<yyyy>/<mm>/<dd>/rollout-<iso>-<session-id>.jsonl, and
    // it is a different animal from Claude Code's in one way that decides the
    // whole design: **the same conversation is written twice.** Every row is
    //
    //     {"timestamp":…,"ordinal":N,"type":T,"payload":{…}}
    //
    // and two values of T carry content. `response_item` is the model-facing
    // wire log — the raw request items, including the developer messages, the
    // environment context and the full stdout of every command. `event_msg`
    // with payload.type `item_completed` is the *TUI's* own record of what it
    // drew. They overlap almost entirely, and only the second is a description
    // of the conversation as a person experienced it.
    //
    // So this reads item_completed and ignores response_item, which is also
    // where the bytes are: measured across two real rollouts, 253 of 504 rows
    // were response_item and not one of them contained the string
    // "item_completed", so the cheap pre-filter separates them exactly.
    public static class CodexTranscript
    {
        // Above this, a row is summarised from its head rather than parsed.
        //
        // Not a hypothetical. Across two real rollouts the median item_completed
        // row is 675 bytes and the largest is **1,046,104** — a CommandExecution
        // whose `aggregated_output` began at offset 503,008. Everything this
        // shows for such a row sits in the first kilobyte (`command` at offset
        // 311, `parsed_cmd` at 445), so parsing the megabyte would be a
        // megabyte of allocation to read forty characters, and a `cat` of
        // something large has no upper bound at all.
        //
        // Set above the largest row actually measured so that every ordinary
        // row still takes the exact path, and the scan below is reached only by
        // a row that is already pathological.
        private const int MaxParseBytes = 2 * 1024 * 1024;

        // How far into such a row to look. Generous against the 445-byte offset
        // measured, and bounded so the cost does not depend on the row.
        private const int HeadBytes = 4096;

        public static List<ChatTranscript.Row> Map(IEnumerable<string> lines)
        {
            var mapped = new List<ChatTranscript.Row>();

            foreach (var line in lines)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                if (!IsInteresting(line)) continue;

                if (line.Length > MaxParseBytes)
                {
                    MapOversized(line, mapped);
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    MapRow(doc.RootElement, mapped);
                }
                catch
                {
                    // A row we can't parse is a row we don't show.
                }
            }

            return mapped;
        }

        // Deliberately not anchored to `"type":"item_completed"`.
        //
        // A false positive costs one parse of a row that turns out to have no
        // item — a command whose output happens to quote this string, which is
        // what reading a rollout aloud in a terminal would do. A false negative
        // drops a message and reports nothing, so the test that matters is that
        // this cannot be defeated by whitespace the writer might start emitting
        // between the key and the value.
        public static bool IsInteresting(string line) =>
            line.Contains("item_completed", StringComparison.Ordinal);

        private static void MapRow(JsonElement root, List<ChatTranscript.Row> into)
        {
            if (Str(root, "type") != "event_msg") return;
            if (!root.TryGetProperty("payload", out var payload)) return;
            if (Str(payload, "type") != "item_completed") return;
            if (!payload.TryGetProperty("item", out var item)) return;

            // The row's ordinal, not the item's id. Ordinals are dense and
            // unique within a rollout (0..503 in the file measured), whereas an
            // id is absent on some items and shared between a call and its
            // output on others — and this value's only job is to let the reader
            // recognise a row it has already shown.
            var key = Ordinal(root);
            var at = Time(root);

            switch (Str(item, "type"))
            {
                case "UserMessage":
                    // Lowercase "text" here and capital "Text" in AgentMessage
                    // below. That is not a transcription error: the two item
                    // types genuinely disagree, confirmed against real rollouts.
                    // Getting it wrong drops every reply and says nothing, which
                    // is why each has its own test.
                    AddContent(item, "text", ChatRole.User, key, at, into);
                    return;

                case "AgentMessage":
                    AddContent(item, "Text", ChatRole.Assistant, key, at, into);
                    return;

                // Shown as its own turn, the same call ChatTranscript makes for
                // a thinking block and for the same reason. Empty on all 109
                // reasoning items measured — Codex only writes a summary when
                // the model produces one — so in practice this adds nothing
                // today and is here so that it does when it starts to.
                case "Reasoning":
                    AddText(SummaryText(item), ChatRole.System, key, at, into);
                    return;

                case "CommandExecution":
                    AddText(Tool("exec", CommandOf(item)), ChatRole.System, key, at, into);
                    return;

                case "FileChange":
                    AddText(Tool("edit", ChangeOf(item)), ChatRole.System, key, at, into);
                    return;

                case "Extension":
                    AddText(Tool(Str(item, "kind") ?? "extension", Str(item, "query")),
                        ChatRole.System, key, at, into);
                    return;

                // Everything else — an item type this build has never seen — is
                // skipped rather than shown as a bare name. Same bargain
                // ChatTranscript strikes: a Codex upgrade that adds an item type
                // degrades rather than breaks. The types above are the ones
                // observed in real rollouts; McpToolCall in particular is known
                // to exist and is not among them, so an MCP call currently shows
                // as nothing. See docs/codex-findings.md.
            }
        }

        // A row too large to parse, read as far as its head.
        //
        // Only CommandExecution ever gets here in practice, which is convenient:
        // the command is the one thing worth showing and it is written before
        // the output that made the row enormous. Anything this cannot recognise
        // still produces a turn, because a row silently missing from the panel
        // is worse than a row that says only "· exec".
        private static void MapOversized(string line, List<ChatTranscript.Row> into)
        {
            var head = line[..Math.Min(HeadBytes, line.Length)];

            if (!head.Contains("\"CommandExecution\"", StringComparison.Ordinal)) return;

            into.Add(new ChatTranscript.Row(OrdinalOf(head), new ChatTurn
            {
                Role = ChatRole.System,
                Text = Tool("exec", Quoted(head, "cmd")),
                IsComplete = true,
                At = TimeOf(head)
            }));
        }

        // --- what each item contributes ---

        private static void AddContent(
            JsonElement item, string blockType, ChatRole role,
            string? key, DateTimeOffset at, List<ChatTranscript.Row> into)
        {
            if (!item.TryGetProperty("content", out var content)) return;
            if (content.ValueKind != JsonValueKind.Array) return;

            foreach (var block in content.EnumerateArray())
            {
                if (Str(block, "type") != blockType) continue;
                AddText(Str(block, "text"), role, key, at, into);
            }
        }

        private static void AddText(
            string? text, ChatRole role, string? key,
            DateTimeOffset at, List<ChatTranscript.Row> into)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (role == ChatRole.User && IsNoise(text)) return;

            into.Add(new ChatTranscript.Row(key, new ChatTurn
            {
                Role = role,
                Text = text.Trim(),
                IsComplete = true,
                At = at
            }));
        }

        // Addressed to the model rather than to anyone reading the conversation
        // back. Codex keeps most of this out of item_completed already — the
        // environment context and the skills preamble arrive as response_item
        // rows, which never reach here — so this is a guard rather than the
        // mechanism, kept because the cost of being wrong is a wall of XML in
        // the panel and the cost of the check is a StartsWith.
        private static readonly string[] NoisePrefixes =
        {
            "<environment_context>",
            "<skills_instructions>",
            "<user_instructions>"
        };

        public static bool IsNoise(string text)
        {
            var t = text.TrimStart();
            foreach (var prefix in NoisePrefixes)
                if (t.StartsWith(prefix, StringComparison.Ordinal)) return true;

            return false;
        }

        // "· exec  rg --files" — the same shape ChatTranscript.ToolSummary
        // produces, so a panel showing one CLI's tool calls and the other's
        // reads as one column rather than two conventions.
        private const int MaxArg = 48;

        private static string Tool(string name, string? arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return "· " + name;

            arg = string.Join(" ", arg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (arg.Length > MaxArg) arg = arg[..MaxArg] + "…";

            return "· " + name + "  " + arg;
        }

        // What the session actually ran. `parsed_cmd` is Codex's own reading of
        // it and is what its TUI shows, so it is preferred; `command` is the
        // argv it handed the shell, which begins "/bin/zsh -lc" on every entry
        // and would make every row look the same.
        private static string? CommandOf(JsonElement item)
        {
            if (item.TryGetProperty("parsed_cmd", out var parsed)
                && parsed.ValueKind == JsonValueKind.Array)
            {
                foreach (var step in parsed.EnumerateArray())
                {
                    var cmd = Str(step, "cmd");
                    if (!string.IsNullOrWhiteSpace(cmd)) return cmd;
                }
            }

            if (!item.TryGetProperty("command", out var command)
                || command.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            // Drop the shell wrapper and keep what was asked of it.
            var parts = new List<string>();
            foreach (var arg in command.EnumerateArray())
                if (arg.ValueKind == JsonValueKind.String) parts.Add(arg.GetString() ?? "");

            if (parts.Count >= 3 && parts[1] is "-lc" or "-c") return parts[2];

            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        // Which file was edited, by name, plus a count when a single item
        // touched several — one row per file would bury the reply between them.
        private static string? ChangeOf(JsonElement item)
        {
            if (!item.TryGetProperty("changes", out var changes)
                || changes.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? first = null;
            var count = 0;

            foreach (var change in changes.EnumerateObject())
            {
                count++;
                first ??= Path.GetFileName(change.Name);
            }

            if (first is null) return null;

            return count > 1 ? first + " +" + (count - 1) : first;
        }

        // Reasoning's summary is an array, and never a populated one in
        // anything measured, so both shapes it could plausibly take are
        // accepted rather than guessing at the one it will turn out to be.
        private static string? SummaryText(JsonElement item)
        {
            if (!item.TryGetProperty("summary_text", out var summary)
                || summary.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var parts = new List<string>();
            foreach (var entry in summary.EnumerateArray())
            {
                var text = entry.ValueKind == JsonValueKind.String
                    ? entry.GetString()
                    : Str(entry, "text");

                if (!string.IsNullOrWhiteSpace(text)) parts.Add(text.Trim());
            }

            return parts.Count == 0 ? null : string.Join("\n\n", parts);
        }

        // --- json helpers ---

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        private static string? Ordinal(JsonElement e) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty("ordinal", out var v)
            && v.ValueKind == JsonValueKind.Number
                ? v.GetRawText()
                : null;

        private static DateTimeOffset Time(JsonElement e) =>
            Str(e, "timestamp") is { } s && DateTimeOffset.TryParse(s, out var at)
                ? at.ToLocalTime()
                : DateTimeOffset.Now;

        // --- the same three, read out of a string that was never parsed ---

        private static string? OrdinalOf(string head)
        {
            var value = Raw(head, "ordinal");
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static DateTimeOffset TimeOf(string head) =>
            Quoted(head, "timestamp") is { } s && DateTimeOffset.TryParse(s, out var at)
                ? at.ToLocalTime()
                : DateTimeOffset.Now;

        // The first `"name":"…"` in the text, unescaped only as far as a
        // backslash pair, which is all a command line can contain here.
        private static string? Quoted(string head, string name)
        {
            var at = head.IndexOf("\"" + name + "\"", StringComparison.Ordinal);
            if (at < 0) return null;

            var open = head.IndexOf('"', at + name.Length + 2);
            if (open < 0) return null;

            var text = new System.Text.StringBuilder();
            for (var i = open + 1; i < head.Length; i++)
            {
                if (head[i] == '\\' && i + 1 < head.Length)
                {
                    text.Append(head[i + 1] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        var c => c
                    });
                    i++;
                    continue;
                }

                if (head[i] == '"') return text.ToString();

                text.Append(head[i]);
            }

            return null;
        }

        // A bare number after `"name":`.
        private static string? Raw(string head, string name)
        {
            var at = head.IndexOf("\"" + name + "\"", StringComparison.Ordinal);
            if (at < 0) return null;

            var i = at + name.Length + 2;
            while (i < head.Length && (head[i] == ':' || head[i] == ' ')) i++;

            var start = i;
            while (i < head.Length && char.IsDigit(head[i])) i++;

            return i == start ? null : head[start..i];
        }
    }
}
