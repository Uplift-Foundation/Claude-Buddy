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
}
