using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // Retires the legacy hook-script folders (~/.claude/claude-buddy and its
    // Codex, Grok and Windows %LOCALAPPDATA% twins) once the installer has
    // marked them superseded long enough ago that no running session can
    // still be calling the old script (CB-255 §1).
    //
    // Why not delete them the moment the installer re-wires: Claude Code and
    // Codex read hooks at session start, so a session alive across the
    // upgrade keeps invoking the old path until it restarts. Pull the script
    // out from under it and every event that session raises is a hook error.
    // So the installer leaves the folder and touches `.superseded` in it
    // instead (once — it keeps the first mtime), and this deletes the folder
    // on a later launch once that marker is Grace old. Fourteen days is the
    // longest a session plausibly survives without a restart; past that, a
    // missing script is a logged hook error rather than lost data, and the
    // Codex PermissionRequest hook is `async: true` precisely so it cannot
    // deny anything (install-codex-hooks.sh).
    //
    // Three refusals, each deliberate:
    //
    //   - **No marker, no deletion.** The installer has not run since the
    //     upgrade, so the CLI's settings still point here and the script is
    //     the live hook. A DMG user who never re-runs "Install Hooks.command"
    //     lives in this state indefinitely, and that is correct.
    //   - **Anything we did not put there, no deletion.** The folder may hold
    //     only our two script names and the marker. The Windows legacy folder
    //     `%LOCALAPPDATA%\ClaudeBuddy` is also the old Logs root, which the
    //     data-dir migration moves out first; if it has not, or a user kept
    //     something of their own in one of these folders, the folder stays.
    //   - **Never recursive.** The recognised files are deleted by name and
    //     then the folder non-recursively, so a file that appears between the
    //     look and the delete makes the delete fail rather than vanish with it.
    //
    // Pure rule (Decide), pure folder list (LegacyFolders), thin IO (Retire).
    // The contract Startup.Run depends on is unit A's: Run is static, takes
    // nothing, never throws, and is a no-op under the test suites'
    // environment overrides.
    internal static class LegacyHookCleanup
    {
        internal const string MarkerName = ".superseded";

        internal static readonly TimeSpan Grace = TimeSpan.FromDays(14);

        // What one legacy folder came to. Decide only ever answers NoMarker,
        // TooSoon, Unrecognised or Retire; Absent and Failed are Retire's,
        // for a folder that is not there and for IO that threw.
        internal enum Verdict
        {
            Absent,
            NoMarker,
            TooSoon,
            Unrecognised,
            Retire,
            Failed
        }

        // Every name this cleanup will delete, the marker last — so a delete
        // that fails half-way leaves the marker behind, and the next launch
        // finds a folder it still recognises and tries again.
        private static readonly string[] Recognised =
        {
            Brand.Legacy.HookScriptShell,
            Brand.Legacy.HookScriptPowerShell,
            MarkerName
        };

        // Case-insensitive, because both platforms' default filesystems are,
        // and a script copied under a differently-cased name is still ours.
        internal static bool IsRecognised(string name) =>
            Recognised.Contains(name, StringComparer.OrdinalIgnoreCase);

        // The whole rule. markerAge is null when there is no marker; a
        // negative age (a marker stamped in the future by a clock that has
        // since gone back) is simply not old enough yet.
        internal static Verdict Decide(TimeSpan? markerAge, IReadOnlyCollection<string> folderContents)
        {
            if (markerAge is null) return Verdict.NoMarker;
            if (markerAge < Grace) return Verdict.TooSoon;

            return folderContents.All(IsRecognised) ? Verdict.Retire : Verdict.Unrecognised;
        }

        // Every folder a pre-Orbweaver installer may have copied the script
        // into, deduplicated. CODEX_HOME and GROK_HOME are each installer's
        // own override (install-codex-hooks.{sh,ps1}, install-grok-hooks.{sh,ps1}),
        // so a folder under either is as much ours as the default one; both
        // the override and the default are listed, because a user may have
        // run the installer with and without it. %LOCALAPPDATA%\ClaudeBuddy is
        // where install-windows-hooks.ps1 put the Claude Code copy — Windows
        // only, since nothing on macOS ever wrote there.
        //
        // Deliberately not listed: extra Codex/Grok accounts from settings
        // (CodexHomes, GrokHomes). Their legacy folders stay until a later
        // cleanup; staying is the safe direction, and reading settings here
        // would tie this step to a file the migration step may be moving.
        internal static IReadOnlyList<string> LegacyFolders(
            bool onWindows, string userProfile, string localAppData, Func<string, string?> env)
        {
            var folders = new List<string>
            {
                Path.Combine(userProfile, ".claude", Brand.Legacy.Slug),
                Path.Combine(userProfile, ".codex", Brand.Legacy.Slug)
            };

            if (env("CODEX_HOME") is { Length: > 0 } codexHome)
                folders.Add(Path.Combine(codexHome, Brand.Legacy.Slug));

            folders.Add(Path.Combine(userProfile, ".grok", Brand.Legacy.Slug));

            if (env("GROK_HOME") is { Length: > 0 } grokHome)
                folders.Add(Path.Combine(grokHome, Brand.Legacy.Slug));

            if (onWindows)
                folders.Add(Path.Combine(localAppData, Brand.Legacy.DataDirName));

            return folders
                .Distinct(onWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToList();
        }

        // Whether this process is under a test suite's sandbox, where the real
        // home directory is not this process's to clean. Every suite's
        // TestBootstrap sets CLAUDE_BUDDY_SETTINGS_DIR, and the scan suites
        // CLAUDE_BUDDY_STATUS_ROOT; either one means "not a real launch".
        internal static bool Skipped(Func<string, string?> env) =>
            env("CLAUDE_BUDDY_SETTINGS_DIR") is { Length: > 0 }
            || env("CLAUDE_BUDDY_STATUS_ROOT") is { Length: > 0 };

        // One folder: look, decide, and — only on Retire — remove. Never
        // throws; anything the filesystem throws is Failed, and the folder is
        // left for the next launch to try again. `remove` is a seam so the
        // Failed arm after a Retire verdict is a test rather than a permission
        // trick that only works on one platform.
        internal static Verdict Retire(string folder, DateTime nowUtc, Action<string>? remove = null)
        {
            try
            {
                if (!Directory.Exists(folder)) return Verdict.Absent;

                var marker = Path.Combine(folder, MarkerName);
                TimeSpan? age = File.Exists(marker) ? nowUtc - File.GetLastWriteTimeUtc(marker) : null;

                var contents = Directory.EnumerateFileSystemEntries(folder)
                    .Select(entry => Path.GetFileName(entry))
                    .ToList();

                var verdict = Decide(age, contents);
                if (verdict != Verdict.Retire) return verdict;

                (remove ?? RemoveRecognised)(folder);
                return Verdict.Retire;
            }
            catch
            {
                return Verdict.Failed;
            }
        }

        // The delete itself: our files by name, marker last, then the folder
        // non-recursively. Throws if anything else is in there by now, which
        // Retire turns into Failed.
        internal static void RemoveRecognised(string folder)
        {
            var ours = Directory.EnumerateFiles(folder)
                .Where(file => IsRecognised(Path.GetFileName(file)))
                .OrderBy(file => string.Equals(Path.GetFileName(file), MarkerName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var file in ours) File.Delete(file);

            Directory.Delete(folder, recursive: false);
        }

        // Every legacy folder, retired or left, with what each came to. A
        // no-op returning nothing under a test sandbox. Logged through
        // MirrorLog (off unless asked for) only for the outcomes somebody
        // might come looking for: a folder removed, one refused, one that
        // failed. "No marker" and "too soon" are the normal state for a
        // fortnight and say nothing.
        internal static IReadOnlyList<(string Folder, Verdict Verdict)> Run(
            Func<string, string?> env, bool onWindows, string userProfile, string localAppData,
            DateTime nowUtc, Action<string>? remove = null)
        {
            if (Skipped(env)) return Array.Empty<(string, Verdict)>();

            var results = new List<(string, Verdict)>();
            foreach (var folder in LegacyFolders(onWindows, userProfile, localAppData, env))
            {
                var verdict = Retire(folder, nowUtc, remove);
                results.Add((folder, verdict));

                if (verdict is Verdict.Retire or Verdict.Unrecognised or Verdict.Failed)
                    MirrorLog.SayOnce("legacy-hook-cleanup", $"{verdict} {folder}");
            }

            return results;
        }

        // The startup step. Excluded from coverage: it only hands the real
        // environment, platform, home directories and clock to the Run above,
        // which is tested against temp folders with back-dated markers. Under
        // the suites' own environment it returns at Skipped, which
        // StartupOrderTests already calls it to prove.
        [ExcludeFromCodeCoverage]
        internal static void Run()
        {
            try
            {
                Run(
                    Environment.GetEnvironmentVariable,
                    OperatingSystem.IsWindows(),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    DateTime.UtcNow);
            }
            catch
            {
                // Retire already swallows everything per folder; this is the
                // contract's belt for the rest — a startup step that threw
                // would stop the app launching over a tidy-up.
            }
        }
    }
}
