namespace ClaudeBuddy
{
    // Whether a CLI's Buddy hook is installed, for CB-168's new-chat dialog:
    // a CLI launched with no hook wired up never gets an orb, and the dialog
    // warns about that rather than silently producing a terminal window that
    // never lights up.
    //
    // Every installer (install-macos-hooks.sh, install-codex-hooks.sh,
    // install-grok-hooks.sh and their Windows equivalents) copies the hook
    // script to `<cli home>/claude-buddy/ClaudeBuddyHook.{sh,ps1}` before
    // wiring it into the CLI's own settings file — see each script's own
    // $INSTALLED/$installed variable. That copy existing is therefore a
    // reliable, install-agnostic signal: it survives whichever settings
    // format a given CLI or OS uses, without this file having to parse
    // ~/.claude/settings.json, a Codex hooks.json or a Grok hooks/*.json to
    // find the same fact three different ways.
    internal static class NewChatHookState
    {
        internal static readonly string HookScriptName =
            OperatingSystem.IsWindows() ? "ClaudeBuddyHook.ps1" : "ClaudeBuddyHook.sh";

        // Where each CLI keeps its own state, honouring the same environment
        // override each installer does: CODEX_HOME and GROK_HOME can point a
        // whole account elsewhere, and the installer wires whichever
        // directory that variable names. Claude Code has no equivalent
        // override for hook installation, so it is always `<home>/.claude`.
        //
        // Parameters default to the real environment so a test can substitute
        // a temp tree and a fake env lookup — the same shape ClaudeBinary and
        // CodexBinary already use their Locate() overloads for, and for the
        // same reason: a test that only controlled one input would still see
        // whatever is really installed on the machine it runs on.
        internal static string? BaseDirectoryFor(
            NewChatCli cli, string? userProfile = null, Func<string, string?>? env = null)
        {
            userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            env ??= Environment.GetEnvironmentVariable;

            return cli switch
            {
                NewChatCli.ClaudeCode => Path.Combine(userProfile, ".claude"),
                NewChatCli.Codex => env("CODEX_HOME") is { Length: > 0 } codexHome
                    ? codexHome
                    : Path.Combine(userProfile, ".codex"),
                NewChatCli.Grok => env("GROK_HOME") is { Length: > 0 } grokHome
                    ? grokHome
                    : Path.Combine(userProfile, ".grok"),
                _ => null
            };
        }

        // Whether the hook copy exists under a given base directory. Takes
        // the script name as a parameter, not just the base directory, so a
        // test can check the Windows and Unix filenames without depending on
        // which platform it happens to run on.
        internal static bool IsInstalled(string baseDirectory, string hookScriptName) =>
            File.Exists(Path.Combine(baseDirectory, "claude-buddy", hookScriptName));

        // The real answer for the running process: real environment, real
        // filesystem, real platform's hook filename.
        internal static bool CurrentlyInstalled(NewChatCli cli) =>
            BaseDirectoryFor(cli) is { } baseDirectory && IsInstalled(baseDirectory, HookScriptName);
    }
}
