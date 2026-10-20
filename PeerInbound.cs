using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orbweaver
{
    internal enum InboundSetting { Accepting, NotAccepting, Unreadable }

    internal enum SettingsEditKind { Changed, Unchanged, Refused }

    internal readonly record struct SettingsEdit(SettingsEditKind Kind, string? Text, string? Reason);

    // CB-267: the rules for "would Claude Code hold a peer message from us", and
    // the one-key edit that stops it doing so.
    //
    // Claude Code holds a cross-session message for approval when the receiving
    // session runs without permission prompts (bypassPermissions), unless the
    // config root's settings.json says crossSessionInbound is "accept". The
    // session's mode is not in sessions/<pid>.json; the only place it appears is
    // the transcript's `permission-mode` rows, which is why LastPermissionMode
    // reads those. All of this is pure so the panel and Settings share one rule.
    //
    // The edit is deliberately conservative: strict JSON in, refuse on anything
    // we cannot parse (a refused edit is a message to the user; a guessed one is
    // a clobbered config file), and only the one key is touched.
    internal static class PeerInbound
    {
        internal const string SettingKey = "crossSessionInbound";
        internal const string AcceptValue = "accept";

        internal static readonly IReadOnlySet<string> HoldingModes =
            new HashSet<string>(StringComparer.Ordinal) { "bypassPermissions" };

        internal static string ButtonLabel => "Let orbs message sessions directly";
        internal static string ConfirmLabel => "Turn it on";
        internal static string DoneText => "Done — new messages go straight in.";
        internal static string TradeOff =>
            "Any Claude session on this computer will then be able to message sessions that run without permission prompts, and those messages go straight in without your approval.";
        internal static string OfferText(string name) =>
            $"{name} runs without permission prompts, so Claude Code holds messages from {Brand.DisplayName} until someone approves them in that session.";
        internal static string HeldNote(string name) =>
            $"Sent to {name}, but Claude Code is holding it until someone approves it in that session.";

        // The last `{"type":"permission-mode","permissionMode":"<m>"}` row, or
        // null when there is none. Rows repeat roughly every turn and the last
        // one wins. Parsed rather than substring-matched: a user message that
        // quotes such a row is a string inside a different row, whose own
        // top-level type is not permission-mode.
        internal static string? LastPermissionMode(IEnumerable<string> lines)
        {
            string? mode = null;
            foreach (var line in lines)
            {
                if (line is null || !line.Contains("permission-mode", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("type", out var type)
                        || type.ValueKind != JsonValueKind.String
                        || type.GetString() != "permission-mode") continue;
                    if (root.TryGetProperty("permissionMode", out var m)
                        && m.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(m.GetString()))
                    {
                        mode = m.GetString();
                    }
                }
                catch (JsonException)
                {
                    // A half-written last line is not a reason to forget the rest.
                }
            }

            return mode;
        }

        // permissions.defaultMode, or null when absent, not a string, or the
        // text is not a settings object we can read.
        internal static string? DefaultModeIn(string? settingsText)
        {
            if (!TryParseObject(settingsText, out var obj, out _) || obj is null) return null;
            if (obj["permissions"] is JsonObject perms
                && perms["defaultMode"] is JsonValue v
                && v.TryGetValue<string>(out var mode)
                && !string.IsNullOrEmpty(mode))
            {
                return mode;
            }

            return null;
        }

        // Absent file or key reads as NotAccepting. Ordinal "accept" only: Claude
        // Code's own check is exact, so "Accept" or `true` does not count here
        // either, and the panel must not claim a session is open when it is not.
        internal static InboundSetting ReadSetting(string? settingsText)
        {
            if (!TryParseObject(settingsText, out var obj, out _) || obj is null)
                return InboundSetting.Unreadable;
            return obj[SettingKey] is JsonValue v
                && v.TryGetValue<string>(out var s)
                && string.Equals(s, AcceptValue, StringComparison.Ordinal)
                ? InboundSetting.Accepting
                : InboundSetting.NotAccepting;
        }

        // The session's own mode (from its transcript) beats settings.json's
        // default, since a session can be started or switched into a mode the
        // file does not mention.
        internal static bool WouldHold(string? sessionMode, string? settingsText)
        {
            var mode = sessionMode ?? DefaultModeIn(settingsText);
            return mode is not null
                && HoldingModes.Contains(mode)
                && ReadSetting(settingsText) != InboundSetting.Accepting;
        }

        internal static SettingsEdit Enable(string? text) => Edit(text, accept: true);

        internal static SettingsEdit Disable(string? text) => Edit(text, accept: false);

        private static SettingsEdit Edit(string? text, bool accept)
        {
            if (!TryParseObject(text, out var obj, out var reason) || obj is null)
                return new SettingsEdit(SettingsEditKind.Refused, null, reason);

            if (accept)
            {
                if (ReadSetting(text) == InboundSetting.Accepting)
                    return new SettingsEdit(SettingsEditKind.Unchanged, null, null);
                obj[SettingKey] = AcceptValue;
            }
            else
            {
                if (!obj.Remove(SettingKey))
                    return new SettingsEdit(SettingsEditKind.Unchanged, null, null);
            }

            var original = text ?? "";
            if (original.Length > 0 && original[0] == '\uFEFF') original = original[1..];
            var crlf = original.Contains("\r\n", StringComparison.Ordinal);
            var nl = crlf ? "\r\n" : "\n";
            var json = obj.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                NewLine = nl,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            json = SurrogateEscape.Replace(json, m =>
                new string(new[]
                { (char)Convert.ToInt32(m.Groups[2].Value, 16), (char)Convert.ToInt32(m.Groups[3].Value, 16),
                })
                    .Insert(0, m.Groups[1].Value));

            // A file we are creating gets a trailing newline; an existing one
            // keeps whatever it had, so the diff is the one key and nothing else.
            var hadNewline = string.IsNullOrWhiteSpace(original) || original.EndsWith('\n');
            return new SettingsEdit(SettingsEditKind.Changed, hadNewline ? json + nl : json, null);
        }

        // The relaxed encoder leaves BMP text alone but still writes anything
        // outside it (an emoji) as a \uD83D\uDE42 pair. Both spell the same
        // string, but a person who typed the character would see their line
        // rewritten, so pairs are turned back. The run of backslashes before the
        // escape has to be an even number of backslashes: `\\uD83D` is a literal backslash
        // and the text "uD83D", which must stay as it is.
        private static readonly Regex SurrogateEscape = new(
            @"(?<!\\)((?:\\\\)*)\\u([dD][89abAB][0-9a-fA-F]{2})\\u([dD][c-fC-F][0-9a-fA-F]{2})",
            RegexOptions.CultureInvariant);

        // Strict parse. Blank text (a file we are about to create) is an empty
        // object. Comments, trailing commas, duplicate keys and a non-object top
        // level are all refused: Claude Code may read some of those differently
        // than we would, and re-serialising would silently change what they mean.
        private static bool TryParseObject(string? text, out JsonObject? obj, out string? reason)
        {
            obj = null;
            reason = null;
            var t = text ?? "";
            if (t.Length > 0 && t[0] == '\uFEFF') t = t[1..];
            if (string.IsNullOrWhiteSpace(t))
            {
                obj = new JsonObject();
                return true;
            }

            try
            {
                var node = JsonNode.Parse(t, null, new JsonDocumentOptions { AllowDuplicateProperties = false });
                if (node is JsonObject o)
                {
                    obj = o;
                    return true;
                }

                reason = "settings.json is not a JSON object.";
                return false;
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
            {
                reason = "settings.json is not strict JSON (comments, trailing commas or duplicate keys?), so it was left alone.";
                return false;
            }
        }

        // "~/.claude/settings.json" instead of the whole home path, for a line a
        // person reads. Case-insensitive on Windows, where paths are.
        internal static string DisplayPath(string path, string home)
        {
            if (string.IsNullOrEmpty(home)) return path;
            var h = home.TrimEnd('/', '\\');
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (path.Length > h.Length
                && path.StartsWith(h, cmp)
                && (path[h.Length] == '/' || path[h.Length] == '\\'))
            {
                return "~" + path[h.Length..];
            }

            return path;
        }
    }
}
