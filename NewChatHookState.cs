namespace ClaudeBuddy
{
    // Whether a CLI's hook is installed, for CB-168's new-chat dialog: a CLI
    // launched with no hook wired up never gets an orb, and the dialog warns
    // about that rather than silently producing a terminal window that never
    // lights up.
    //
    // Every installer copies the hook script somewhere of its own before
    // wiring it into the CLI's settings file, and that copy existing is a
    // reliable, install-agnostic signal: it survives whichever settings
    // format a given CLI or OS uses, without this file having to parse
    // ~/.claude/settings.json, a Codex hooks.json or a Grok hooks/*.json to
    // find the same fact three different ways. Where each one puts it — see
    // each script's own $INSTALLED/$installed variable:
    //
    //   - Codex and Grok, both platforms: `<cli home>/<slug>/<script>`, the
    //     cli home being CODEX_HOME / GROK_HOME when set
    //     (install-codex-hooks.{sh,ps1}, install-grok-hooks.{sh,ps1}).
    //   - Claude Code on macOS: `~/.claude/<slug>/<script>`
    //     (install-macos-hooks.sh).
    //   - Claude Code on Windows: **`%LOCALAPPDATA%\<data dir>\<script>`**, not
    //     under ~/.claude at all (install-windows-hooks.ps1's $InstallDir). This
    //     comment used to say otherwise, the code believed it, and every wired
    //     Windows machine was told its Claude Code hook was missing (CB-255
    //     found it; filed against CB-168).
    //
    // Two copies count since CB-255 (§1): the Orbweaver one and the legacy
    // Claude Buddy one. A DMG user who never runs "Install Hooks.command"
    // again, or a Windows user whose installer has not re-wired yet, is still
    // wired to the legacy script — the hooks fire, the orbs draw — and must
    // not be told otherwise.
    internal static class NewChatHookState
    {
        internal static readonly string HookScriptName =
            OperatingSystem.IsWindows() ? Brand.HookScriptPowerShell : Brand.HookScriptShell;

        // Every path a working hook copy for this CLI can be at, new first,
        // legacy second. Pure: the platform, both home directories and the
        // environment are all parameters, so each CLI on each platform is a
        // test on either CI leg rather than whichever one the suite happens to
        // run on — the shape ClaudeBinary and CodexBinary already use their
        // Locate() overloads for, and for the same reason.
        //
        // An empty override is no override, which is the rule the installers
        // apply too (`${CODEX_HOME:-…}`, `if ($env:CODEX_HOME)`). Claude Code
        // has no override for hook installation; CLAUDE_CONFIG_DIR moves its
        // settings file, not the script copy.
        //
        // An undefined CLI has no candidates, so it reads as not installed.
        internal static IReadOnlyList<string> HookCopyCandidates(
            NewChatCli cli, bool onWindows, string userProfile, string localAppData,
            Func<string, string?> env)
        {
            var script = onWindows ? Brand.HookScriptPowerShell : Brand.HookScriptShell;
            var legacyScript = onWindows ? Brand.Legacy.HookScriptPowerShell : Brand.Legacy.HookScriptShell;

            if (cli == NewChatCli.ClaudeCode && onWindows)
            {
                return new[]
                {
                    Path.Combine(localAppData, Brand.DataDirName, script),
                    Path.Combine(localAppData, Brand.Legacy.DataDirName, legacyScript)
                };
            }

            string? home = cli switch
            {
                NewChatCli.ClaudeCode => Path.Combine(userProfile, ".claude"),
                NewChatCli.Codex => HomeOrDefault(env("CODEX_HOME"), userProfile, ".codex"),
                NewChatCli.Grok => HomeOrDefault(env("GROK_HOME"), userProfile, ".grok"),
                _ => null
            };

            return home is null
                ? Array.Empty<string>()
                : new[]
                {
                    Path.Combine(home, Brand.Slug, script),
                    Path.Combine(home, Brand.Legacy.Slug, legacyScript)
                };
        }

        private static string HomeOrDefault(string? overrideDir, string userProfile, string defaultName) =>
            overrideDir is { Length: > 0 } ? overrideDir : Path.Combine(userProfile, defaultName);

        // Installed means any candidate exists. `exists` is a parameter so the
        // UI suite can drive the dialog through this exact rule against a temp
        // tree without it being the real filesystem's File.Exists by accident.
        internal static bool AnyExists(IEnumerable<string> candidates, Func<string, bool>? exists = null) =>
            candidates.Any(exists ?? File.Exists);

        // The real answer for the running process: real environment, real
        // filesystem, real platform.
        internal static bool CurrentlyInstalled(NewChatCli cli) =>
            AnyExists(HookCopyCandidates(
                cli,
                OperatingSystem.IsWindows(),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetEnvironmentVariable));
    }
}
