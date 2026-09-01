using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;

namespace ClaudeBuddy
{
    // Turning Grok's credits-config JSON into AccountUsage, and nothing else.
    //
    // Pure so a fixture from a real `billing: fetched credits config` log line
    // covers every field without launching grok or reading a live auth.json.
    // See docs/grok-findings.md.
    internal static class GrokUsageParse
    {
        internal static AccountUsage? FromCreditsConfig(
            string? json, string? grokHome, string label, DateTimeOffset readAt)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                // The log wraps the config in ctx.config; a bare config object
                // is accepted too so a test does not have to fake the envelope.
                var config = root;
                if (root.TryGetProperty("ctx", out var ctx)
                    && ctx.ValueKind == JsonValueKind.Object
                    && ctx.TryGetProperty("config", out var nested)
                    && nested.ValueKind == JsonValueKind.Object)
                {
                    config = nested;
                }
                else if (root.TryGetProperty("config", out var inner)
                         && inner.ValueKind == JsonValueKind.Object)
                {
                    config = inner;
                }

                var weekly = Window(config);
                var extra = Extra(config);
                var tier = Str(config, "subscriptionTier")
                           ?? Str(root, "subscriptionTier");

                return new AccountUsage(
                    grokHome,
                    label,
                    weekly is not null,
                    tier,
                    null,
                    weekly,
                    extra,
                    readAt);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static UsageWindow? Window(JsonElement config)
        {
            if (!config.TryGetProperty("creditUsagePercent", out var percentEl)
                || !percentEl.TryGetDouble(out var percent))
            {
                return null;
            }

            DateTimeOffset? resets = null;
            if (config.TryGetProperty("currentPeriod", out var period)
                && period.ValueKind == JsonValueKind.Object
                && period.TryGetProperty("end", out var end)
                && end.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(end.GetString(), out var parsed))
            {
                resets = parsed;
            }

            return new UsageWindow(percent, resets);
        }

        private static ExtraUsage Extra(JsonElement config)
        {
            var cap = Val(config, "onDemandCap");
            var used = Val(config, "onDemandUsed");
            var enabled = cap is > 0;

            return new ExtraUsage(
                enabled,
                used,
                cap,
                "",
                0,
                enabled ? null : "no_on_demand");
        }

        private static long? Val(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var el)) return null;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n)) return n;
            if (el.ValueKind == JsonValueKind.Object
                && el.TryGetProperty("val", out var val)
                && val.ValueKind == JsonValueKind.Number
                && val.TryGetInt64(out var wrapped))
            {
                return wrapped;
            }

            return null;
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

    internal static class GrokUsageAccounts
    {
        internal static string AuthFilePath(string grokHome) =>
            Path.Combine(grokHome, "auth.json");

        internal static string FallbackLabel(string? grokHome)
        {
            if (string.IsNullOrWhiteSpace(grokHome)) return "grok";
            var name = Path.GetFileName(grokHome.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) return "grok";
            name = name.TrimStart('.');
            if (name.StartsWith("grok-", StringComparison.OrdinalIgnoreCase)
                && name.Length > "grok-".Length)
            {
                name = name["grok-".Length..];
            }

            return name.Length == 0 ? "grok" : name;
        }

        // Email local-part from auth.json, never the refresh token.
        internal static string LabelFrom(string? json, string? grokHome)
        {
            var fallback = FallbackLabel(grokHome);
            if (string.IsNullOrWhiteSpace(json)) return fallback;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return fallback;

                foreach (var property in root.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object) continue;
                    if (property.Value.TryGetProperty("email", out var email)
                        && email.ValueKind == JsonValueKind.String)
                    {
                        var address = email.GetString();
                        if (string.IsNullOrWhiteSpace(address)) continue;
                        var at = address.IndexOf('@');
                        var local = at > 0 ? address[..at] : address;
                        if (local.Length > 0) return local;
                    }
                }
            }
            catch (JsonException)
            {
                return fallback;
            }

            return fallback;
        }

        internal static List<string> Homes(string home, IReadOnlyList<string> extras)
        {
            var grok = Path.Combine(home, ".grok");
            var dirs = new List<string> { grok };
            foreach (var extra in extras)
            {
                if (string.IsNullOrWhiteSpace(extra)) continue;
                var path = extra.StartsWith(Path.DirectorySeparatorChar)
                           || extra.StartsWith(Path.AltDirectorySeparatorChar)
                    ? extra
                    : Path.Combine(home, extra);
                if (!string.Equals(path, grok, StringComparison.Ordinal))
                    dirs.Add(path);
            }

            return dirs;
        }
    }

    // Last-resort read: the latest `billing: fetched credits config` line in
    // $GROK_HOME/logs/unified.jsonl. No token, no process. Freshness is "as of
    // the last Grok session", which the card already shows via ReadAt.
    internal sealed class GrokUsagePoller : IUsageSource
    {
        public IReadOnlyList<AccountUsage> Read()
        {
            if (!ClaudeBuddySettings.GrokAccountUsageEnabled)
                return Array.Empty<AccountUsage>();

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var readings = new List<AccountUsage>();
            foreach (var grokHome in GrokUsageAccounts.Homes(home, ClaudeBuddySettings.GrokHomes))
            {
                var label = GrokUsageAccounts.LabelFrom(ReadAuth(grokHome), grokHome);
                var line = LatestCreditsLine(grokHome);
                var usage = GrokUsageParse.FromCreditsConfig(
                    line, grokHome, label, DateTimeOffset.UtcNow);
                if (usage is not null) readings.Add(usage);
            }

            return readings;
        }

        [ExcludeFromCodeCoverage]
        private static string? ReadAuth(string grokHome)
        {
            try
            {
                var path = GrokUsageAccounts.AuthFilePath(grokHome);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        [ExcludeFromCodeCoverage]
        private static string? LatestCreditsLine(string grokHome)
        {
            try
            {
                var log = Path.Combine(grokHome, "logs", "unified.jsonl");
                if (!File.Exists(log)) return null;

                string? last = null;
                foreach (var line in File.ReadLines(log))
                {
                    if (line.Contains("\"creditUsagePercent\"", StringComparison.Ordinal)
                        || line.Contains("fetched credits config", StringComparison.Ordinal))
                    {
                        last = line;
                    }
                }

                return last;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    internal sealed class CompositeUsageSource : IUsageSource
    {
        private readonly IUsageSource[] _sources;

        internal CompositeUsageSource(params IUsageSource[] sources) => _sources = sources;

        public IReadOnlyList<AccountUsage> Read()
        {
            var readings = new List<AccountUsage>();
            foreach (var source in _sources)
            {
                try { readings.AddRange(source.Read()); }
                catch { /* one source failing must not blank the others */ }
            }

            return readings;
        }
    }
}
