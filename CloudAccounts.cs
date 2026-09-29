using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace ClaudeBuddy
{
    // The Claude Code accounts on this machine, as the cloud arm sees them.
    //
    // An account is a config root. Which roots exist is ClaudeConfigRoots' answer
    // (the default ~/.claude plus each extra the user listed in settings); this
    // class only turns that list into credential sources and remembers the result,
    // so the poll loop and the chat panels share **the same instances**. That
    // matters on macOS, where a second source per panel would be a second consent
    // surface for the same Keychain item.
    internal static class CloudAccounts
    {
        private static readonly object Gate = new();
        private static IReadOnlyList<CloudAccount>? _current;

        internal static IReadOnlyList<CloudAccount> Current
        {
            get
            {
                lock (Gate) return _current ??= Build();
            }
        }

        // Rebuilt on every Restart so a root added in settings is picked up
        // without relaunching.
        internal static IReadOnlyList<CloudAccount> Rebuild()
        {
            lock (Gate) return _current = Build();
        }

        internal static void SetForTests(IReadOnlyList<CloudAccount>? accounts)
        {
            lock (Gate) _current = accounts;
        }

        // Excluded from coverage: constructs the platform's real stores. What it
        // decides is in ClaudeCliCredentials.SourcesFor, which is covered.
        [ExcludeFromCodeCoverage]
        private static IReadOnlyList<CloudAccount> Build() =>
            ClaudeCliCredentials.SourcesFor(
                OperatingSystem.IsMacOS(),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));

        // The credential a session's owner logged in with.
        //
        // **A chat send must use its owner's login, never "whichever is first".**
        // An unknown or missing root falls back to the first account (the default
        // root) rather than to nothing, because a session recorded before this
        // existed has no owner and refusing it would be a regression.
        internal static ICloudCredentialSource SourceFor(string? ownerRoot)
        {
            var accounts = Current;
            if (ownerRoot is not null)
            {
                var root = ClaudeCliCredentials.TrimRoot(ownerRoot);
                foreach (var account in accounts)
                {
                    if (string.Equals(account.Root, root, StringComparison.Ordinal)) return account.Source;
                }
            }

            return accounts[0].Source;
        }
    }

    // What every account's poll loop has said, folded into the one thing the rest
    // of the app reads: a merged, deduplicated session list and a status text.
    //
    // A class rather than statics for the reason ArmState is a record: every
    // decision here is testable by constructing one. The loops that feed it are
    // excluded from coverage and are as thin as they can be made.
    internal sealed class CloudAccountBoard
    {
        private sealed record Slot(IReadOnlyList<ClaudeCloudSessions.Session> Sessions, string Status);

        private readonly object _gate = new();
        private readonly IReadOnlyList<CloudAccount> _accounts;
        private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (Func<string?> Current, string? At)> _declined =
            new(StringComparer.Ordinal);
        private IReadOnlyList<ClaudeCloudSessions.Session> _merged =
            Array.Empty<ClaudeCloudSessions.Session>();
        private DateTime _holdUntilUtc;

        internal const string NotAsked = "not asked — a Keychain prompt was declined";

        internal CloudAccountBoard(IReadOnlyList<CloudAccount> accounts)
        {
            _accounts = accounts;
            foreach (var account in accounts)
            {
                _slots[account.Root] = new Slot(Array.Empty<ClaudeCloudSessions.Session>(), "checking…");
            }
        }

        internal IReadOnlyList<CloudAccount> Accounts => _accounts;

        internal IReadOnlyList<ClaudeCloudSessions.Session> Merged
        {
            get { lock (_gate) return _merged; }
        }

        internal string StatusText
        {
            get
            {
                lock (_gate)
                {
                    return ClaudeCloudRoster.DescribeAccounts(
                        _accounts.Select(a => (a.Label, _slots[a.Root].Status)).ToList(),
                        _merged.Count);
                }
            }
        }

        // Whether this root's Keychain children should be skipped this tick.
        //
        // A declined or unanswered prompt on one account means the user is not
        // going to say yes to the next one; asking anyway would stack dialogs on
        // top of the one they just refused. A latch clears when the account that
        // declined has a different stamp — the user signed in again, or
        // re-approved us — and clears for that account alone.
        internal bool KeychainSkippedFor(string root)
        {
            lock (_gate)
            {
                foreach (var key in _declined.Keys.ToList())
                {
                    var (current, at) = _declined[key];
                    if (!string.Equals(current(), at, StringComparison.Ordinal))
                    {
                        _declined.Remove(key);
                    }
                }

                return _declined.Keys.Any(k => !string.Equals(k, root, StringComparison.Ordinal));
            }
        }

        // How long every account should wait before its next request, because one
        // of them was rate limited. Nobody has measured whether the limit is per
        // token or per address; if it is per address, N accounts polling on
        // their own schedules would keep re-tripping it, so a 429 on one backs off
        // all of them. A placeholder for a measurement, and named as one.
        internal TimeSpan HoldRemaining(DateTime nowUtc)
        {
            lock (_gate) return _holdUntilUtc > nowUtc ? _holdUntilUtc - nowUtc : TimeSpan.Zero;
        }

        // Fold one account's tick in. Returns the merged snapshot to publish.
        //
        // A null Snapshot keeps that account's slot: a failed page-four fetch on
        // one account must not empty its orbs. A Halted account arrives with an
        // empty snapshot and clears only its own slot.
        internal IReadOnlyList<ClaudeCloudSessions.Session> Apply(
            string root, ClaudeCloudSessions.StepResult step, bool keychainSkipped,
            Func<string?> stampNow, DateTime nowUtc)
        {
            lock (_gate)
            {
                var status = keychainSkipped && step.Next.Halted ? NotAsked : step.Status;
                var previous = _slots[root];

                var sessions = previous.Sessions;
                if (step.Snapshot is { } snapshot)
                {
                    sessions = snapshot.Select(s => s with { OwnerRoot = root }).ToList();
                }

                _slots[root] = new Slot(sessions, status);

                if (step.PromptDeclined) _declined[root] = (stampNow, stampNow());
                if (step.RateLimited) _holdUntilUtc = nowUtc + step.Wait;

                _merged = ClaudeCloudRoster.MergeAccounts(
                    _accounts.Select(a => _slots[a.Root].Sessions).ToList(), _merged);
                return _merged;
            }
        }

        // An unexpected exception in one account's loop: say so on that account's
        // line and touch nothing else.
        internal void ApplyError(string root, string message)
        {
            lock (_gate) _slots[root] = _slots[root] with { Status = message };
        }
    }
}
