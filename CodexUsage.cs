using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClaudeBuddy
{
    // Turning Codex's rate_limits snapshot into AccountUsage, and nothing else.
    //
    // Pure so a fixture copied from a real token_count event covers every field
    // without launching Codex or walking a live sessions tree. Measured on
    // codex-cli 0.151.0, 31 Aug 2026 — see docs/codex-findings.md.
    internal static class CodexUsageParse
    {
        // A five-hour window is 300 minutes; a week is 10080. Anything at or
        // under twelve hours is the inner "this session" ring, everything
        // longer is the week. Twelve rather than five so a slightly different
        // primary window still lands on the ring a person would look at.
        private const int SessionWindowMinutes = 12 * 60;

        internal static AccountUsage? FromRateLimits(
            string? json, string? codexHome, string label, DateTimeOffset readAt)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using var document = JsonDocument.Parse(json);
                var limits = Unwrap(document.RootElement);
                if (limits is null) return null;

                UsageWindow? session = null;
                UsageWindow? weekly = null;
                Assign(limits.Value, "primary", ref session, ref weekly);
                Assign(limits.Value, "secondary", ref session, ref weekly);

                var plan = Str(limits.Value, "plan_type");
                var extra = Extra(limits.Value);

                return new AccountUsage(
                    codexHome,
                    label,
                    session is not null || weekly is not null,
                    plan,
                    session,
                    weekly,
                    extra,
                    readAt,
                    AccountUsageSource.Codex);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // The snapshot is nested three different ways in the wild: a bare
        // rate_limits object (what the tests hand this), payload.rate_limits on
        // a token_count event, and a token_count event wrapped in the JSONL
        // envelope. Unwrap rather than requiring the caller to know which.
        private static JsonElement? Unwrap(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("payload", out var payload)
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("rate_limits", out var nested)
                && nested.ValueKind == JsonValueKind.Object)
            {
                return nested;
            }

            if (root.TryGetProperty("rate_limits", out var mid)
                && mid.ValueKind == JsonValueKind.Object)
            {
                return mid;
            }

            if (root.TryGetProperty("primary", out _)) return root;

            return null;
        }

        private static void Assign(
            JsonElement limits, string name,
            ref UsageWindow? session, ref UsageWindow? weekly)
        {
            if (!limits.TryGetProperty(name, out var window)
                || window.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!window.TryGetProperty("used_percent", out var percentEl)
                || !percentEl.TryGetDouble(out var percent))
            {
                return;
            }

            DateTimeOffset? resets = null;
            if (window.TryGetProperty("resets_at", out var at)
                && at.ValueKind == JsonValueKind.Number
                && at.TryGetInt64(out var unix))
            {
                resets = DateTimeOffset.FromUnixTimeSeconds(unix);
            }

            var parsed = new UsageWindow(percent, resets);

            var minutes = window.TryGetProperty("window_minutes", out var minEl)
                          && minEl.TryGetInt32(out var m)
                ? m
                : (int?)null;

            if (minutes is { } n && n <= SessionWindowMinutes) session = parsed;
            else weekly = parsed;
        }

        private static ExtraUsage Extra(JsonElement limits)
        {
            if (!limits.TryGetProperty("credits", out var credits)
                || credits.ValueKind != JsonValueKind.Object)
            {
                return new ExtraUsage(false, null, null, "", 0, "no_credits");
            }

            var unlimited = credits.TryGetProperty("unlimited", out var unl)
                            && unl.ValueKind == JsonValueKind.True;
            if (unlimited)
                return new ExtraUsage(false, null, null, "", 0, "unlimited");

            var has = credits.TryGetProperty("has_credits", out var hasEl)
                      && hasEl.ValueKind == JsonValueKind.True;
            if (!has)
                return new ExtraUsage(false, null, null, "", 0, "no_credits");

            // A remaining balance without a cap is not a percentage. Drawing it
            // as a ring would invent a share of a budget Codex did not send.
            return new ExtraUsage(false, null, null, "", 0, "credits_no_cap");
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
    }

    internal static class CodexUsageAccounts
    {
        internal static string AuthFilePath(string codexHome) =>
            Path.Combine(codexHome, "auth.json");

        internal static string FallbackLabel(string? codexHome)
        {
            if (string.IsNullOrWhiteSpace(codexHome)) return "codex";
            var name = Path.GetFileName(codexHome.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) return "codex";
            name = name.TrimStart('.');
            if (name.StartsWith("codex-", StringComparison.OrdinalIgnoreCase)
                && name.Length > "codex-".Length)
            {
                name = name["codex-".Length..];
            }

            return name.Length == 0 ? "codex" : name;
        }

        // Email local-part if auth.json has one. Never the tokens object, never
        // OPENAI_API_KEY — those rotate, and a second reader that loses a
        // compare-and-swap logs the user out. Same argument GrokUsageAccounts
        // makes for not reading Grok's refresh token.
        internal static string LabelFrom(string? json, string? codexHome)
        {
            var fallback = FallbackLabel(codexHome);
            if (string.IsNullOrWhiteSpace(json)) return fallback;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return fallback;

                foreach (var property in root.EnumerateObject())
                {
                    if (property.NameEquals("tokens")
                        || property.NameEquals("OPENAI_API_KEY"))
                    {
                        continue;
                    }

                    if (property.Value.ValueKind == JsonValueKind.String
                        && property.NameEquals("email"))
                    {
                        var local = LocalPart(property.Value.GetString());
                        if (local is not null) return local;
                    }

                    if (property.Value.ValueKind != JsonValueKind.Object) continue;
                    if (property.Value.TryGetProperty("email", out var email)
                        && email.ValueKind == JsonValueKind.String)
                    {
                        var local = LocalPart(email.GetString());
                        if (local is not null) return local;
                    }
                }
            }
            catch (JsonException)
            {
                return fallback;
            }

            return fallback;
        }

        private static string? LocalPart(string? address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            var at = address.IndexOf('@');
            var local = at > 0 ? address[..at] : address;
            return local.Length > 0 ? local : null;
        }

        internal static List<string> Homes(string home, IReadOnlyList<string> extras)
        {
            var codex = Path.Combine(home, ".codex");
            var dirs = new List<string> { codex };
            foreach (var extra in extras)
            {
                if (string.IsNullOrWhiteSpace(extra)) continue;
                var path = extra.StartsWith(Path.DirectorySeparatorChar)
                           || extra.StartsWith(Path.AltDirectorySeparatorChar)
                    ? extra
                    : Path.Combine(home, extra);
                if (!string.Equals(path, codex, StringComparison.Ordinal))
                    dirs.Add(path);
            }

            return dirs;
        }
    }

    // Last-resort read: the newest token_count rate_limits snapshot in
    // $CODEX_HOME/sessions/**/rollout-*.jsonl. No token, no process. Freshness
    // is "as of the last Codex session", which the card already shows via ReadAt.
    internal sealed class CodexUsagePoller : IUsageSource
    {
        internal const int MaxLineBytes = 32_768;

        public IReadOnlyList<AccountUsage> Read()
        {
            if (!ClaudeBuddySettings.CodexAccountUsageEnabled)
                return Array.Empty<AccountUsage>();

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var readings = new List<AccountUsage>();
            foreach (var codexHome in CodexUsageAccounts.Homes(home, ClaudeBuddySettings.CodexHomes))
            {
                var label = CodexUsageAccounts.LabelFrom(ReadAuth(codexHome), codexHome);
                var json = LatestRateLimitsJson(codexHome);
                var usage = CodexUsageParse.FromRateLimits(
                    json, codexHome, label, DateTimeOffset.UtcNow);
                if (usage is not null) readings.Add(usage);
            }

            return readings;
        }

        // Newest file first; first snapshot found wins. A session that has not
        // yet received a token_count is skipped rather than treated as zero.
        internal static string? LatestRateLimitsJsonFrom(string sessionsDir)
        {
            if (!Directory.Exists(sessionsDir)) return null;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(sessionsDir, "rollout-*.jsonl",
                    SearchOption.AllDirectories);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            foreach (var file in files.OrderByDescending(File.GetLastWriteTimeUtc))
            {
                var line = LatestTokenCountLine(file);
                if (line is not null) return line;
            }

            return null;
        }

        internal static string? LatestTokenCountLine(string path)
        {
            try
            {
                string? last = null;
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Length > MaxLineBytes) continue;
                    if (line.Contains("\"token_count\"", StringComparison.Ordinal)
                        && line.Contains("\"rate_limits\"", StringComparison.Ordinal))
                    {
                        last = line;
                    }
                }

                return last;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        [ExcludeFromCodeCoverage]
        private static string? LatestRateLimitsJson(string codexHome) =>
            LatestRateLimitsJsonFrom(Path.Combine(codexHome, "sessions"));

        [ExcludeFromCodeCoverage]
        private static string? ReadAuth(string codexHome)
        {
            try
            {
                var path = CodexUsageAccounts.AuthFilePath(codexHome);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
