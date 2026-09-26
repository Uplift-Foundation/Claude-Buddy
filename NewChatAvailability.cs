namespace ClaudeBuddy
{
    // One row of CB-168's new-chat dialog: a CLI, whether it can be launched,
    // and why not / what to watch out for if it can.
    //
    // Reason and Enabled are opposite in sense on purpose — Reason is set
    // exactly when Enabled is false, Warning exactly when it's true but
    // imperfect (installed, launchable, but no orb without its hook). A
    // caller never needs both at once, and the dialog reads whichever one is
    // non-null next to the row.
    internal sealed record NewChatOption(NewChatCli Cli, bool Enabled, string? Reason, string? Warning);

    // Turns "where is each CLI, is its hook installed" into the four rows the
    // dialog shows — pure, so the decision can be tested without a real
    // filesystem or a real PATH, the same split OrbArrangement/OrbGlyph use
    // for the reason CLAUDE.md gives: a function with no window and no
    // settings behind it gets a case per outcome.
    internal static class NewChatAvailability
    {
        internal const string NotFoundReasonSuffix =
            "not found on PATH or in its usual install locations";

        internal const string HookMissingWarning =
            "launches, but no orb until hooks are installed — Settings → Hooks";

        internal static readonly NewChatCli[] AllClis =
        {
            NewChatCli.ClaudeCode, NewChatCli.Codex, NewChatCli.Grok
        };

        // locate answers null for "not installed, or not findable"; hookInstalled
        // is only asked when locate found something, since a CLI that isn't
        // there has no hook state worth reporting.
        internal static IReadOnlyList<NewChatOption> Evaluate(
            Func<NewChatCli, string?> locate, Func<NewChatCli, bool> hookInstalled)
        {
            var options = new List<NewChatOption>(AllClis.Length);

            foreach (var cli in AllClis)
            {
                if (locate(cli) is null)
                {
                    options.Add(new NewChatOption(
                        cli, Enabled: false,
                        Reason: NewChatLauncher.DisplayName(cli) + " " + NotFoundReasonSuffix,
                        Warning: null));
                    continue;
                }

                options.Add(new NewChatOption(
                    cli, Enabled: true, Reason: null,
                    Warning: hookInstalled(cli) ? null : HookMissingWarning));
            }

            return options;
        }

        // The real answer, freshly computed every call — never cached,
        // because the dialog's whole point is that a CLI installed after the
        // app started should show up without a restart (CB-168's own
        // decision record).
        internal static IReadOnlyList<NewChatOption> Current() =>
            CurrentForTests?.Invoke() ?? Evaluate(RealLocate, NewChatHookState.CurrentlyInstalled);

        // The UI tests' seam, in the FakeChatSession shape CLAUDE.md already
        // asks for: a UI test substitutes this rather than needing a real
        // CLI on PATH and a real hook installed to see a disabled row.
        internal static Func<IReadOnlyList<NewChatOption>>? CurrentForTests;

        // internal rather than private so a test can reach the `_ => null`
        // arm directly with an out-of-range cast — AllClis only ever hands
        // this three real values, so that arm is otherwise unreachable from
        // any call this file itself makes. The three real arms still only
        // get their coverage through Current()'s own real-filesystem calls,
        // which is the one part of this method that has to touch the OS at
        // all; the default arm needs none of that, which is what makes it
        // worth testing on its own rather than folding it into RealLaunch's
        // style of whole-method exclusion.
        internal static string? RealLocate(NewChatCli cli) => cli switch
        {
            NewChatCli.ClaudeCode => ClaudeBinary.Locate(),
            NewChatCli.Codex => CodexBinary.Locate(),
            NewChatCli.Grok => GrokBinary.Locate(),
            _ => null
        };
    }

    // CB-201's Account picker: turns the user's configured
    // ClaudeCodeProfileDirs into the rows a combo box shows, plus the profile
    // dir NewChatLauncher.Launch should actually receive for each — pure, the
    // same reason NewChatAvailability.Evaluate above is, so every branch (an
    // empty list, a duplicate, a blank, a second spelling of the default
    // account) is a test rather than a real settings file and a real $HOME.
    internal static class NewChatAccounts
    {
        internal const string DefaultLabel = "Default (~/.claude)";

        // One row of the combo box. ProfileDir is exactly what Launch should
        // be handed — null for Default, so a caller never needs its own
        // "is this the default row" check before passing the selection
        // through.
        internal sealed record Choice(string Label, string? ProfileDir)
        {
            public override string ToString() => Label;
        }

        // Default first, then each configured extra that isn't a second
        // spelling of the default account and isn't a repeat of one already
        // added. A result of exactly one entry (Default alone) is what tells
        // NewChatWindow to hide the picker entirely — CB-201's "empty list
        // means no picker" decision.
        //
        // Resolved through ClaudeProfile.Resolve rather than a fresh
        // Path.Combine/HashSet pair, so a dir that collapses to the default
        // account here (".claude", ".claude/", the absolute $HOME/.claude
        // spelling) is the same set of dirs ConfigDirFor would also read as
        // null — one resolver, one answer, asked from two places.
        internal static IReadOnlyList<Choice> Choices(string home, IReadOnlyList<string> extras)
        {
            var choices = new List<Choice> { new(DefaultLabel, null) };

            // Resolved once and reused for every HomeRelative call below,
            // rather than handing it the raw `home` argument as-is. QA
            // (CB-201) caught this the hard way: on windows-latest CI, a
            // caller-supplied home of "/Users/me" is not already in the form
            // ClaudeProfile.Resolve's own Path.GetFullPath call would produce
            // ("D:\Users\me", since Windows resolves a leading "/" against
            // the current drive) — so comparing a resolved profile path
            // against the *unresolved* home string failed to recognise it as
            // "under home" at all, and every real entry fell through to its
            // full absolute path instead of a "~/..." label. Resolving home
            // the same way makes the two sides of the comparison agree
            // regardless of what shape the caller's home string was already
            // in.
            var homeResolved = ClaudeProfile.Resolve(home, string.Empty);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ClaudeProfile.Resolve(home, ClaudeBuddySettings.DefaultRemoteControlProfileDir)
            };

            foreach (var extra in extras)
            {
                if (string.IsNullOrWhiteSpace(extra)) continue;

                var trimmed = extra.Trim();
                var resolved = ClaudeProfile.Resolve(home, trimmed);
                if (!seen.Add(resolved)) continue;

                // The label is built from the *resolved* path, not the raw
                // setting — QA (CB-201) caught an absolute entry outside
                // $HOME ("/Volumes/Backup/.claude-mobile") rendering as
                // "~/Volumes/Backup/.claude-mobile" when the label was built
                // by trimming leading punctuation off the raw string instead.
                // ChatHeaderMeta.HomeRelative already carries this exact rule
                // (a real separator at the boundary, not just a matching
                // prefix, is what makes something "under" home) for cwd
                // display; reusing it here is one resolver rather than a
                // second copy that could disagree with it. ProfileDir stays
                // the raw, untouched string — the label is display only.
                choices.Add(new Choice(ChatHeaderMeta.HomeRelative(resolved, homeResolved), trimmed));
            }

            return choices;
        }
    }
}
