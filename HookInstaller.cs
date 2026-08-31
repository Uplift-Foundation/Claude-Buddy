using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace ClaudeBuddy
{
    // Re-running the hook installers from inside the app.
    //
    // Adding a profile in the Settings window has to *do* something. The list
    // it edits is only consulted when an installer runs, so without this an
    // added account sits there looking configured and produces no orbs until
    // the next time someone happens to re-run setup by hand — the same
    // "correctly configured, silently doing nothing" failure the installers
    // themselves go out of their way to avoid.
    //
    // Windows already had half of this in WslIntegration, which is where it
    // belonged when the feature was Windows-only. This is the other half, and
    // the dispatch, so the Settings window can stop being platform-shaped.
    internal static class HookInstaller
    {
        // How long to wait. The macOS installers do a handful of file reads and
        // one osascript per settings file, so they finish in well under a
        // second; this is a backstop against a hung osascript rather than a
        // real budget. Windows' own timeouts live in WslIntegration, which has
        // to account for a WSL VM cold-booting.
        private const int TimeoutMs = 20_000;

        // Re-wire every CLI. Used by settings that mean the same thing to both,
        // where re-running only one leaves the other wired to an older hook and
        // an older set of flags — which is exactly how the colour setting
        // shipped broken for Codex: the toggle re-ran Claude Code's installer
        // alone, so Codex kept a hook copy without the flag and without the
        // code the flag turns on.
        // Excluded from coverage: runs both installer scripts as subprocesses.
        [ExcludeFromCodeCoverage]
        public static void ReapplyAll()
        {
            ReapplyClaudeCode();
            ReapplyCodex();
            ReapplyGrok();
        }

        // Re-wire every Claude Code account the app knows about.
        // Excluded from coverage: runs the shipped bash installer, or
        // WslIntegration's Windows equivalent.
        [ExcludeFromCodeCoverage]
        public static void ReapplyClaudeCode()
        {
            if (OperatingSystem.IsWindows())
            {
                // Native wiring plus every already-wired distro, which is a
                // Windows-only concern and already has a home.
                WslIntegration.ReapplyProfiles();
                return;
            }

            RunScript("install-macos-hooks.sh", ClaudeBuddySettings.AutoColorSessions);
        }

        // Re-wire every Codex home the app knows about.
        // Excluded from coverage: runs the shipped Codex installer as a
        // subprocess.
        [ExcludeFromCodeCoverage]
        public static void ReapplyCodex()
        {
            if (OperatingSystem.IsWindows())
            {
                RunPowerShell("install-codex-hooks.ps1", ClaudeBuddySettings.AutoColorSessions);
                return;
            }

            RunScript("install-codex-hooks.sh", ClaudeBuddySettings.AutoColorSessions);
        }

        [ExcludeFromCodeCoverage]
        public static void ReapplyGrok()
        {
            if (OperatingSystem.IsWindows())
            {
                RunPowerShell("install-grok-hooks.ps1", ClaudeBuddySettings.AutoColorSessions);
                return;
            }

            RunScript("install-grok-hooks.sh", ClaudeBuddySettings.AutoColorSessions);
        }

        // Excluded from coverage: invokes /bin/bash on a real script; which script
        // it finds is Resolve, which is tested.
        [ExcludeFromCodeCoverage]
        private static void RunScript(string name, bool autoColor = false)
        {
            var script = Resolve(name);
            if (script is null) return;

            // The flag rather than a setting the hook reads for itself: the
            // hook runs on every tool call, and a settings read there would be
            // an osascript each time. Re-running the installer is how a change
            // to it takes effect, which is the same way the extra-profile list
            // already works.
            Run("/bin/bash", autoColor ? new[] { script, "--auto-color" } : new[] { script });
        }

        // Excluded from coverage: invokes Windows PowerShell on a real script.
        [ExcludeFromCodeCoverage]
        private static void RunPowerShell(string name, bool autoColor = false)
        {
            var script = Resolve(name);
            if (script is null) return;

            var args = new List<string>
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script
            };
            if (autoColor) args.Add("-AutoColor");

            Run(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", args.ToArray());
        }

        // Where the installers live, in both layouts this app runs from.
        //
        // Inside the .app they sit in Contents/Resources, beside the hook
        // script; AppContext.BaseDirectory is Contents/MacOS, so Resources is
        // its sibling. From a source build there is no bundle, so this walks up
        // looking for the repo's tools/ — the same two-layout resolution
        // WslIntegration does for its own script, and the same order: installed
        // wins, because a stale clone next to an installed app should not be
        // what runs.
        //
        // baseDirectory is a parameter with the real one as its default, so the
        // order below can be asserted against a temp directory. The order is the
        // part worth asserting rather than the file reads: "installed wins" is a
        // decision, and the failure it prevents — a stale clone next to an
        // installed app being what actually runs — is silent, because both
        // scripts exist and both appear to work.
        internal static string? Resolve(string name, string? baseDirectory = null)
        {
            baseDirectory ??= AppContext.BaseDirectory;

            var resources = Path.Combine(baseDirectory, "..", "Resources", name);
            if (File.Exists(resources)) return Path.GetFullPath(resources);

            var alongside = Path.Combine(baseDirectory, "tools", name);
            if (File.Exists(alongside)) return alongside;

            var dir = new DirectoryInfo(baseDirectory);
            for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tools", name);
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        // Output is swallowed on purpose. The installers are chatty by design —
        // they are written to be read by someone who ran them in a terminal —
        // and there is nowhere in the Settings window to put a page of it. What
        // matters here is that the wiring happened; if it didn't, the next
        // scan simply produces no orb for that account, which is the same
        // outcome as not having added it.
        // Excluded from coverage: starts a subprocess, drains its pipes and kills
        // its tree on timeout.
        [ExcludeFromCodeCoverage]
        private static void Run(string file, string[] arguments)
        {
            try
            {
                var psi = new ProcessStartInfo(file)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var argument in arguments) psi.ArgumentList.Add(argument);

                using var process = Process.Start(psi);
                if (process is null) return;

                // Drained before waiting: a child that fills its stdout pipe
                // blocks forever on the write, and WaitForExit would then time
                // out on a run that was otherwise fine.
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();

                if (!process.WaitForExit(TimeoutMs))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
            }
            catch
            {
                // Nothing here is worth interrupting the Settings window for.
            }
        }
    }
}
