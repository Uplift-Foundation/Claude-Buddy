using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;

namespace ClaudeBuddy
{
    // Where the usage readings come from, as an interface, so the windows above
    // can be driven in a test without launching anything.
    //
    // The same reason RemoteChat.cs gives for IRemoteChatSession existing: a
    // surface whose data arrives from another process is untestable until the
    // arrival is a seam. UiTests hands the orbs a fake implementing this and
    // asserts on what a user would see.
    internal interface IUsageSource
    {
        IReadOnlyList<AccountUsage> Read();
    }

    // Which accounts to ask, and what to call them.
    //
    // Pure and separated from the launching for the usual reason, but also
    // because the naming rule has a trap in it worth a test of its own — see
    // AccountFilePath.
    internal static class UsageAccounts
    {
        // The file holding an account's identity.
        //
        // **The default account's is a sibling of its config directory, not
        // inside it.** Claude Code reads `(CLAUDE_CONFIG_DIR ?? homedir) +
        // "/.claude.json"`, so the account this app runs under is described by
        // `~/.claude.json` while `~/.claude-work` is described by
        // `~/.claude-work/.claude.json`. The trap is that `~/.claude/.claude.json`
        // also exists, is a different and older file, and has no oauthAccount in
        // it at all — so a reasonable-looking Path.Combine(configDir,
        // ".claude.json") for the default account finds a real file, parses it
        // successfully, and silently comes back with no name.
        internal static string AccountFilePath(string home, string? configDir) =>
            Path.Combine(configDir ?? home, ".claude.json");

        // What to call an account when its identity file cannot be read, or has
        // no account in it — an account that has never been logged in, which is
        // an ordinary state and not an error.
        //
        // The directory name with its leading dot and its "claude-" prefix
        // removed, because every one of these directories starts with both and a
        // row of orbs reading "claude-work", "claude-board" says nothing that the
        // orbs' own presence does not.
        internal static string FallbackLabel(string? configDir)
        {
            if (string.IsNullOrWhiteSpace(configDir)) return "default";

            var name = Path.GetFileName(configDir.TrimEnd(Path.DirectorySeparatorChar,
                                                          Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) return "default";

            name = name.TrimStart('.');
            if (name.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
                && name.Length > "claude-".Length)
            {
                name = name["claude-".Length..];
            }

            return name.Length == 0 ? "default" : name;
        }

        // The account's own name for itself, out of its identity file.
        //
        // The email's local part rather than the display name: two accounts at
        // the same organisation share a display name often enough
        // ("Warren Thompson" twice tells you nothing), while the local part is
        // what actually distinguishes them and is what the person typed to log
        // in. Falls back through displayName to the directory name.
        internal static string LabelFrom(string? json, string? configDir)
        {
            var fallback = FallbackLabel(configDir);
            if (string.IsNullOrWhiteSpace(json)) return fallback;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("oauthAccount", out var account)
                    || account.ValueKind != JsonValueKind.Object)
                {
                    return fallback;
                }

                if (account.TryGetProperty("emailAddress", out var email)
                    && email.ValueKind == JsonValueKind.String)
                {
                    var address = email.GetString();
                    if (!string.IsNullOrWhiteSpace(address))
                    {
                        var at = address.IndexOf('@');
                        var local = at > 0 ? address[..at] : address;
                        if (local.Length > 0) return local;
                    }
                }

                if (account.TryGetProperty("displayName", out var display)
                    && display.ValueKind == JsonValueKind.String)
                {
                    var name = display.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }

                return fallback;
            }
            catch (JsonException)
            {
                return fallback;
            }
        }

        // The full email, for the card, which has room to be precise where the
        // orb does not. Null when the account has never been logged in.
        internal static string? EmailFrom(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("oauthAccount", out var account)
                    || account.ValueKind != JsonValueKind.Object
                    || !account.TryGetProperty("emailAddress", out var email)
                    || email.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                var address = email.GetString();
                return string.IsNullOrWhiteSpace(address) ? null : address;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Every account to ask, this app's own first.
        //
        // null leads, meaning "leave the environment alone", which is how the
        // account this app runs under is read and the only one nearly every
        // machine has. The rest come from BackgroundJobs.ExtraAccountDirs rather
        // than being re-derived here: it already holds ~/.claude out by path so a
        // settings list naming ".claude" explicitly does not ask the same account
        // twice, and it is already covered.
        internal static List<string?> ConfigDirs(string home, IReadOnlyList<string> extras)
        {
            var dirs = new List<string?> { null };
            foreach (var dir in BackgroundJobs.ExtraAccountDirs(home, extras)) dirs.Add(dir);
            return dirs;
        }
    }

    // One reading per account, by asking the CLI.
    //
    // **Nothing here reads a credential.** Claude Code is asked for the answer
    // over its own control protocol and handles its own auth, refresh and
    // storage, which is what makes this work identically on the three platforms
    // this app ships to. The alternative — reading the OAuth token out of the
    // login keychain — was rejected on a specific hazard rather than on taste:
    // refreshing that token rotates the refresh token and must be written back
    // under a cross-process lock with a compare-and-swap, so a second writer
    // that loses the race can log the user out of the account it was trying to
    // report on.
    //
    // The request costs nothing. It is answered from Claude Code's own cache of
    // the usage endpoint and makes no model call — measured at
    // total_cost_usd 0 and total_api_duration_ms 0 — but it does start a process
    // and take a couple of seconds, which is why callers are expected to honour
    // MinimumInterval rather than asking whenever they would like to know.
    internal sealed class UsagePoller : IUsageSource
    {
        // The floor between polls.
        //
        // Claude Code caches the underlying fetch with a five-minute write
        // guard, so asking more often than this cannot produce a newer number —
        // it only spends a process launch to be told the same thing. The rings
        // are not a live readout and were never designed to be.
        internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

        // Generous, and deliberately not the five seconds BackgroundJobs uses
        // for `claude agents --json`. This call was measured at ~2.4s and is
        // dominated by a transcript scan the CLI performs for its own /usage
        // display, with no flag to skip it; a machine with a large history or a
        // cold cache will be slower. Five seconds here would time out on exactly
        // the machines that most need the answer.
        private const int TimeoutMs = 20000;

        private const string ControlRequest =
            "{\"type\":\"control_request\",\"request_id\":\"cb-usage\"," +
            "\"request\":{\"subtype\":\"get_usage\"}}";

        public IReadOnlyList<AccountUsage> Read()
        {
            var claude = ClaudeBinary.Path;
            if (claude is null) return Array.Empty<AccountUsage>();

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var readings = new List<AccountUsage>();

            foreach (var configDir in
                     UsageAccounts.ConfigDirs(home, ClaudeBuddySettings.ClaudeCodeProfileDirs))
            {
                var label = UsageAccounts.LabelFrom(
                    ReadAccountFile(home, configDir), configDir);

                var stdout = RunOne(claude, configDir);
                var usage = UsageParse.FromStream(
                    stdout, configDir, label, DateTimeOffset.UtcNow);

                // A failed read contributes nothing rather than a blank reading.
                // BackgroundJobs.Merge makes the argument at length: a partial
                // answer is a confident claim about an account nobody managed to
                // ask, and here it would mean drawing an empty gauge for an
                // account that might be at 99%.
                if (usage is not null) readings.Add(usage);
            }

            return readings;
        }

        [ExcludeFromCodeCoverage]
        private static string? ReadAccountFile(string home, string? configDir)
        {
            try
            {
                var path = UsageAccounts.AccountFilePath(home, configDir);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        // One account's answer, as raw stdout.
        //
        // Excluded from coverage: starts the `claude` CLI as a real subprocess.
        // What is excluded is the launch, its timeout and the kill for a CLI that
        // never answers — the JSON it prints is parsed by UsageParse, which is
        // covered against real captured payloads. The same split, for the same
        // reason, as BackgroundJobs.ReadOne.
        [ExcludeFromCodeCoverage]
        private static string? RunOne(string claude, string? configDir)
        {
            try
            {
                var psi = new ProcessStartInfo(claude)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                psi.ArgumentList.Add("-p");

                // Mandatory alongside `-p --output-format stream-json`; the CLI
                // refuses the combination without it.
                psi.ArgumentList.Add("--verbose");

                // Keeps a poll that runs every five minutes forever out of
                // ~/.claude/projects, where it would otherwise leave a transcript
                // per account per poll for a conversation that never happened.
                psi.ArgumentList.Add("--no-session-persistence");

                // Stops the user's own SessionStart hooks firing on every poll —
                // **including this app's own**, which would otherwise have the
                // poller manufacturing the orbs it is measuring.
                psi.ArgumentList.Add("--settings");
                psi.ArgumentList.Add("{\"disableAllHooks\":true}");

                psi.ArgumentList.Add("--input-format");
                psi.ArgumentList.Add("stream-json");
                psi.ArgumentList.Add("--output-format");
                psi.ArgumentList.Add("stream-json");

                if (configDir is not null) psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;

                using var process = Process.Start(psi);
                if (process is null) return null;

                // Both pipes drained before waiting, and stdin closed so the CLI
                // knows no further requests are coming and exits. A blocking
                // ReadToEnd here would make the timeout below unreachable, and an
                // undrained stderr can deadlock a chatty child — the same two
                // hazards BackgroundJobs.ReadOne documents.
                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();

                process.StandardInput.WriteLine(ControlRequest);
                process.StandardInput.Close();

                if (!process.WaitForExit(TimeoutMs))
                {
                    try { process.Kill(true); } catch { }
                    return null;
                }

                var stdout = outTask.GetAwaiter().GetResult();
                errTask.GetAwaiter().GetResult();

                return process.ExitCode == 0 ? stdout : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
