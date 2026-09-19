using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;

namespace ClaudeBuddy
{
    // How the cloud arm gets a credential, and the rules it handles one under.
    //
    // **This is the first credential this application has ever held.** Every
    // other account-scoped path in the app — UsagePoller above all — deliberately
    // never touches one: it spawns `claude -p` and writes a control request, so
    // the CLI owns the token and a compromise of Claude Buddy is not a token
    // disclosure. That posture is not being relaxed. CB-164 established there is
    // no control-protocol subtype for cloud sessions, so the cloud roster cannot
    // be reached that way, and this file is the scoped exception rather than a
    // new general capability.
    //
    // The credential read here is the *Claude Code CLI's own*, which is option A
    // of the three CB-164 weighed. On macOS it is a login-Keychain
    // generic-password item under service "Claude Code-credentials"; elsewhere it
    // is `~/.claude/.credentials.json`. Reading the Keychain item from a
    // different application raises a macOS consent prompt, and that prompt is the
    // user grant — the same supported-OS-mechanism shape as the Automation and
    // Local Network grants this app already depends on, and subject to the same
    // code-signature sensitivity CLAUDE.md documents.
    //
    // Minting our own token under Claude Code's OAuth client_id (option B) was
    // rejected: the consent screen would name Claude Code rather than Claude
    // Buddy, so the one screen whose entire job is to say who is asking would say
    // the wrong thing, and Anthropic can restrict or rotate that client id at any
    // time, after which no version of our code works again.
    //
    // ## Token custody — the rules this file is written to honour
    //
    // * The access token is a **local** everywhere it appears. It is never a
    //   static, never a field, never cached, and never placed in anything that
    //   outlives the request it authorises. CredentialRead is the one record that
    //   carries it, it is constructed at the call site and dropped at the end of
    //   the call, and nothing stores one.
    // * ParseCredentials reads **only** `claudeAiOauth.accessToken` and
    //   `expiresAt`. The stored blob also carries a refresh token. The record
    //   below deliberately has **no field a refresh token could land in**, so a
    //   later well-meaning "also parse the rest" is a compile error rather than a
    //   quiet widening. We do not refresh — the CLI owns refresh, and doing it
    //   ourselves needs the refresh token plus a client id, which is option B by
    //   another road. A stale copy is re-read, not renewed.
    // * Nothing token-derived reaches Detail, Describe, a status string, a log, a
    //   settings file or the disk. Stamp() returns a change-detector — a file
    //   mtime or a Keychain modification date — and never secret bytes.
    // * **The token is not zeroed and cannot be.** A .NET string is immutable and
    //   its bytes live wherever the GC last copied them until the heap is reused.
    //   Claiming otherwise would be the sort of assertion-with-good-grammar
    //   CLAUDE.md warns about, so it is written down here as a known limit
    //   instead. Reducing it would mean SecureString or a byte[] carried through
    //   HttpClient, and neither survives the framework's own string copies.
    internal enum CredentialOutcome
    {
        // A usable, unexpired credential was read.
        Found,

        // Nothing is stored, or what is stored has expired. An ordinary state,
        // not an error: a user who has never run `claude` is here, and so is one
        // whose CLI has not refreshed lately.
        NotLoggedIn,

        // The OS refused us — on macOS, the user answered "Don't Allow" to the
        // Keychain prompt, or the prompt could not be shown. Not retryable
        // without the user changing their mind, so the caller stops rather than
        // asking again and producing a prompt storm.
        Denied,

        // The store answered with something we could not turn into text at all:
        // an I/O error, a permission error on the file, an unmapped OSStatus.
        Unreadable,

        // We read it and it is not the shape we expect. Distinguished from
        // Unreadable on purpose — this one says the *format* moved, which is the
        // failure an undocumented dependency produces, and it is the one worth
        // surfacing differently from a transient read error.
        Malformed,
    }

    // One reading of the credential store.
    //
    // AccessToken is populated only for Found, and only for as long as the caller
    // holds this record. See the custody rules above: there is no refresh-token
    // field here and there must not be one.
    internal sealed record CredentialRead(
        CredentialOutcome Outcome,
        string? AccessToken,
        DateTimeOffset? ExpiresAt,
        string? Detail);

    // Where a credential comes from, as an interface, for the same reason
    // IUsageSource and IRemoteChatSession exist: the real one is an OS prompt or
    // a file in someone's home directory, and a surface that cannot be faked
    // cannot be tested. Every test in this repository talks to a fake; nothing in
    // the suites touches the real Keychain.
    internal interface ICloudCredentialSource
    {
        // A cheap value that changes when the stored credential changes, and
        // otherwise does not. It exists so a poll can notice a re-login without
        // reading the secret every tick — on macOS that matters twice over,
        // because the attributes-only Keychain query does not return data and so
        // is not the query the consent prompt guards.
        //
        // Never secret bytes. A file mtime, a Keychain modification date.
        string? Stamp();

        // Read the credential. On macOS this is the call that can prompt.
        CredentialRead Read();
    }

    // The pure half: paths, parsing and wording, with no store behind any of it.
    internal static class ClaudeCliCredentials
    {
        // The Keychain generic-password service the Claude Code CLI stores under
        // on macOS. Read off a real machine, not from documentation.
        internal const string KeychainService = "Claude Code-credentials";

        // Where the CLI keeps the same thing on Windows and Linux.
        //
        // Note this is *inside* the config root, unlike UsageAccounts'
        // `.claude.json`, which is a sibling of it. The two files are neighbours
        // with different rules and it is worth not reasoning from one to the
        // other — see the comment on UsageAccounts.AccountFilePath for what that
        // trap already cost once.
        internal static string CredentialsFilePath(string configRoot) =>
            Path.Combine(configRoot, ".credentials.json");

        // The organisation the account belongs to, out of `~/.claude.json`.
        //
        // **Not sent anywhere, and that correction is the point of this comment.**
        // It used to say `x-organization-uuid` was one of six headers the endpoint
        // refuses a request without. That was measured against claude.ai, which is
        // the wrong host — against api.anthropic.com the header is ignored, along
        // with the other three claude.ai-specific ones, and only Authorization and
        // anthropic-version are required. `CloudRequestContext` no longer has a
        // field to put this in.
        //
        // Kept because it is the only place in the app that can name the account's
        // organisation, and `tools/claude-cloud-probe` still prints whether one was
        // found — a useful thing to know when diagnosing whose credential is in the
        // store. It is diagnostic now rather than load-bearing.
        internal static string? OrganizationUuidFrom(string? claudeJson)
        {
            if (string.IsNullOrWhiteSpace(claudeJson)) return null;

            try
            {
                using var doc = JsonDocument.Parse(claudeJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                if (!doc.RootElement.TryGetProperty("oauthAccount", out var account)) return null;
                if (account.ValueKind != JsonValueKind.Object) return null;
                if (!account.TryGetProperty("organizationUuid", out var uuid)) return null;
                if (uuid.ValueKind != JsonValueKind.String) return null;

                var value = uuid.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Turn a stored blob into a reading.
        //
        // `now` is a parameter rather than DateTimeOffset.UtcNow so the expiry
        // arm is testable without a clock, the same argument the transcript
        // parsers make for taking their input rather than reading it.
        //
        // Reads two fields and no others, on purpose. See the custody rules.
        internal static CredentialRead ParseCredentials(string? json, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new CredentialRead(CredentialOutcome.NotLoggedIn, null, null,
                    "no credential stored");
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return new CredentialRead(CredentialOutcome.Malformed, null, null,
                    "the stored credential is not JSON");
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                    || oauth.ValueKind != JsonValueKind.Object)
                {
                    return new CredentialRead(CredentialOutcome.Malformed, null, null,
                        "the stored credential has no claudeAiOauth object");
                }

                if (!oauth.TryGetProperty("accessToken", out var token)
                    || token.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(token.GetString()))
                {
                    return new CredentialRead(CredentialOutcome.Malformed, null, null,
                        "the stored credential has no access token");
                }

                var expiresAt = ExpiryFrom(oauth);

                // An expired copy is NotLoggedIn rather than an error, because
                // that is what it actually means to us: the CLI refreshes on its
                // own next use and we re-read afterwards. It is not Malformed —
                // nothing is wrong with it — and treating it as a failure would
                // invite exactly the retry loop this file is meant to avoid.
                if (expiresAt is { } when && when <= now)
                {
                    return new CredentialRead(CredentialOutcome.NotLoggedIn, null, when,
                        "the stored credential expired; the CLI refreshes it on its next use");
                }

                return new CredentialRead(CredentialOutcome.Found, token.GetString(), expiresAt,
                    "a credential is present");
            }
        }

        // `expiresAt` is written by the CLI as Unix milliseconds. An ISO string is
        // accepted too rather than assumed away: this is an undocumented format we
        // do not own, and a shape change there should cost us an expiry check, not
        // a parse failure.
        private static DateTimeOffset? ExpiryFrom(JsonElement oauth)
        {
            if (!oauth.TryGetProperty("expiresAt", out var expires)) return null;

            if (expires.ValueKind == JsonValueKind.Number
                && expires.TryGetInt64(out var millis))
            {
                try
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(millis);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }

            if (expires.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(expires.GetString(), out var parsed))
            {
                return parsed;
            }

            return null;
        }

        // What to say on screen about an outcome.
        //
        // Every string here is safe to render: none of them is derived from the
        // credential, and the negative-control test asserts that for a fake token
        // chosen to be findable if it ever leaked into one.
        internal static string Describe(CredentialOutcome outcome) => outcome switch
        {
            CredentialOutcome.Found => "signed in to Claude Code",
            CredentialOutcome.NotLoggedIn => "no Claude Code login found — run `claude` and sign in",
            CredentialOutcome.Denied => "access to the Claude Code login was denied",
            CredentialOutcome.Unreadable => "the Claude Code login could not be read",
            CredentialOutcome.Malformed => "the Claude Code login is not in a shape this version understands",
            _ => "the Claude Code login is in an unknown state",
        };

        // Which store this platform keeps it in.
        //
        // Takes the platform as an argument rather than asking the runtime, so the
        // choice itself is testable on either machine — the same reason OrbGlyph
        // takes the two-letter setting instead of reading it.
        internal static ICloudCredentialSource SourceFor(bool isMacOS, string home) =>
            isMacOS
                ? new KeychainCredentialSource()
                : new FileCredentialSource(CredentialsFilePath(Path.Combine(home, ".claude")));
    }

    // Windows and Linux: the credential is a file.
    internal sealed class FileCredentialSource : ICloudCredentialSource
    {
        private readonly string _path;

        internal FileCredentialSource(string path) => _path = path;

        internal string Path => _path;

        // The file's last-write time in ticks. A change detector and nothing
        // else — no length, no hash of the contents, nothing derived from the
        // secret. Null when the file is absent, which is itself a state worth
        // distinguishing from an unchanged one.
        //
        // ArgumentException is caught alongside the I/O ones because FileInfo's
        // constructor throws it for a path the platform will not accept at all,
        // and this method is on a poll path: a stamp that cannot be taken must
        // read as "no credential" rather than as an exception out of a timer.
        public string? Stamp()
        {
            try
            {
                var info = new FileInfo(_path);
                return info.Exists ? info.LastWriteTimeUtc.Ticks.ToString() : null;
            }
            catch (Exception ex)
                when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        public CredentialRead Read()
        {
            string json;
            try
            {
                if (!File.Exists(_path))
                {
                    return new CredentialRead(CredentialOutcome.NotLoggedIn, null, null,
                        "no credential file");
                }

                json = File.ReadAllText(_path);
            }
            catch (UnauthorizedAccessException)
            {
                // The file-store analogue of the Keychain's "Don't Allow": the OS
                // says no, and asking again will get the same answer.
                return new CredentialRead(CredentialOutcome.Denied, null, null,
                    "the credential file is not readable by this user");
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException)
            {
                return new CredentialRead(CredentialOutcome.Unreadable, null, null,
                    "the credential file could not be read");
            }

            return ClaudeCliCredentials.ParseCredentials(json, DateTimeOffset.UtcNow);
        }
    }

    // macOS: the credential is a Keychain item, and reading it prompts.
    //
    // Excluded from coverage for the same reason MacOSScreenLock is: every line
    // is a call into Security.framework about an item a CI runner does not have,
    // behind a consent dialog no runner can answer. The mapping it performs is in
    // MacOSKeychain and is equally untestable here; what *is* covered is
    // SourceFor choosing this class, which is the decision this repository owns.
    [ExcludeFromCodeCoverage]
    internal sealed class KeychainCredentialSource : ICloudCredentialSource
    {
        // The attributes-only query. It returns no data, so it is not the query
        // the consent prompt guards — which is the whole point of Stamp() being a
        // separate call from Read() rather than a field on it.
        public string? Stamp() => MacOSKeychain.ModificationStamp(ClaudeCliCredentials.KeychainService);

        public CredentialRead Read()
        {
            var (outcome, json, detail) = MacOSKeychain.ReadGenericPassword(
                ClaudeCliCredentials.KeychainService);
            if (outcome != CredentialOutcome.Found)
            {
                return new CredentialRead(outcome, null, null,
                    detail ?? ClaudeCliCredentials.Describe(outcome));
            }

            return ClaudeCliCredentials.ParseCredentials(json, DateTimeOffset.UtcNow);
        }
    }
}
