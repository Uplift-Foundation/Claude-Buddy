using System.Diagnostics;

namespace ClaudeBuddy
{
    // Claude Buddy as the handler for Claude Desktop's URL schemes, forwarding
    // each link to the profile it belongs to.
    //
    // ClaudeDesktopUrlRouting has the why and the decision; this file is the
    // machinery around it: claiming the schemes from LaunchServices, keeping
    // track of which instance the user was last in, and doing the delivery.
    //
    // Delivery is `/usr/bin/open -a <bundle path> [--env ...] <url>`, the same
    // command the launcher already uses, and deliberately *without* -n. -n is
    // right when launching a profile, because the caller has just proved
    // nothing is running on that directory; here the opposite is wanted — an
    // already-running instance should receive the link, and starting a second
    // Chromium on a live userData directory is the leveldb corruption the whole
    // profile feature exists to avoid. Verified on a real machine: `open -a`
    // against a clone path delivered to the running instance and started
    // nothing new.
    internal static class ClaudeDesktopUrlRouter
    {
        private const string ClaudeDesktopBundleId = "com.anthropic.claudefordesktop";

        private static readonly object Gate = new();
        private static int _lastActivePid;
        private static bool _started;

        // What the last claim attempt did, for the settings window to show. A
        // silent failure here means links keep going to the wrong profile with
        // nothing to explain why, which is the state this bug was found in.
        public static string? Status { get; private set; }

        public static void Start()
        {
            if (!OperatingSystem.IsMacOS()) return;

            lock (Gate)
            {
                if (_started) return;
                _started = true;
            }

            // Off the UI thread: this scans processes and shells out to plutil.
            Task.Run(() =>
            {
                try { ClaimIfWorthwhile(); }
                catch { /* never worth taking the app down for */ }
            });
        }

        // Claiming a system-wide URL scheme is not something to do speculatively.
        // With one profile there is nothing to route — a link can only be meant
        // for that profile — so the schemes are left exactly as they were, and
        // an install that never creates a second profile never notices this
        // feature exists.
        private static void ClaimIfWorthwhile()
        {
            if (!ClaudeBuddySettings.RouteClaudeUrls)
            {
                Restore();
                return;
            }

            if (ClaudeDesktopManager.RouteCandidates().Count < 2)
            {
                Status = "not needed (one profile)";
                return;
            }

            Claim();
        }

        internal static bool Claim()
        {
            if (!OperatingSystem.IsMacOS()) return false;

            var ownId = MacOSUrlScheme.OwnBundleId();
            if (ownId is null)
            {
                // A loose `dotnet run` binary has no bundle, so there would be
                // nothing for macOS to launch when a link arrived later —
                // claiming from here would break links until the next bundled
                // run put it right.
                Status = "not claimed (running unbundled)";
                return false;
            }

            var claimed = 0;

            foreach (var scheme in ClaudeDesktopUrlRouting.Schemes)
            {
                var current = MacOSUrlScheme.CurrentHandler(scheme);
                if (string.Equals(current, ownId, StringComparison.OrdinalIgnoreCase))
                {
                    claimed++;
                    continue;
                }

                // Remember what to put back, once, and only something that
                // isn't us — re-claiming must not overwrite the real previous
                // handler with our own id.
                if (scheme == "claude"
                    && current is { Length: > 0 }
                    && ClaudeBuddySettings.PreviousClaudeUrlHandler.Length == 0)
                {
                    ClaudeBuddySettings.PreviousClaudeUrlHandler = current;
                }

                if (MacOSUrlScheme.SetHandler(scheme, ownId)) claimed++;
            }

            var total = ClaudeDesktopUrlRouting.Schemes.Length;
            Status = claimed == total ? "routing links" : $"claimed {claimed} of {total} schemes";
            return claimed == total;
        }

        // Hands the schemes back to whoever had them — Claude Desktop, in every
        // case we have seen. Called when the user turns routing off, so the
        // setting is genuinely reversible rather than a one-way door.
        internal static void Restore()
        {
            if (!OperatingSystem.IsMacOS()) return;

            var previous = ClaudeBuddySettings.PreviousClaudeUrlHandler;
            if (previous.Length == 0) previous = ClaudeDesktopBundleId;

            var ownId = MacOSUrlScheme.OwnBundleId();

            foreach (var scheme in ClaudeDesktopUrlRouting.Schemes)
            {
                // Only undo our own claim. If something else owns the scheme
                // now, the user or another app changed it deliberately and it
                // is not ours to overwrite.
                var current = MacOSUrlScheme.CurrentHandler(scheme);
                if (ownId is null || !string.Equals(current, ownId, StringComparison.OrdinalIgnoreCase)) continue;

                MacOSUrlScheme.SetHandler(scheme, previous);
            }

            ClaudeBuddySettings.PreviousClaudeUrlHandler = "";
            Status = "not routing links";
        }

        public static void SetEnabled(bool enabled)
        {
            if (!OperatingSystem.IsMacOS()) return;
            if (ClaudeBuddySettings.RouteClaudeUrls == enabled) return;

            ClaudeBuddySettings.RouteClaudeUrls = enabled;

            Task.Run(() =>
            {
                try
                {
                    if (enabled) ClaimIfWorthwhile();
                    else Restore();
                }
                catch { }

                TrayController.Instance?.Refresh();
            });
        }

        // Which Claude Desktop instance the user was last in. Called from the
        // tray's existing 2s tick on the UI thread — NSWorkspace wants the main
        // thread's autorelease pool, and a sign-in's browser round trip is far
        // longer than one tick, so nothing finer is needed.
        //
        // Only ever *raised* by a Claude Desktop instance: once the browser
        // takes focus it must not overwrite the answer, because the answer is
        // precisely "which Claude window sent the user to the browser".
        public static void NoteFrontmost()
        {
            if (!OperatingSystem.IsMacOS()) return;

            try
            {
                var frontmost = MacOSWindowList.FrontmostPid();
                if (frontmost <= 0) return;

                // Snapshot rather than a fresh scan: this runs every two
                // seconds, and the snapshot is refreshed on the same tick.
                var match = ClaudeDesktopManager.Snapshot.Profiles
                    .Any(p => p.IsRunning && p.Pid == frontmost);

                if (match) Volatile.Write(ref _lastActivePid, frontmost);
            }
            catch
            {
                // Losing the hint costs routing accuracy, never correctness —
                // Choose has an answer for a pid of 0.
            }
        }

        internal static int LastActivePid => Volatile.Read(ref _lastActivePid);

        // The entry point Avalonia's protocol activation calls into.
        public static void Handle(string url)
        {
            if (!OperatingSystem.IsMacOS()) return;
            if (!ClaudeDesktopUrlRouting.Handles(url)) return;

            Task.Run(() =>
            {
                try { Deliver(url); }
                catch { /* a link that fails to route must not take the app down */ }
            });
        }

        private static void Deliver(string url)
        {
            var candidates = ClaudeDesktopManager.RouteCandidates();
            var route = ClaudeDesktopUrlRouting.Choose(candidates, LastActivePid);

            var arguments = route is null
                // No profile to route to at all. Still address a bundle by
                // path rather than calling plain `open <url>`, which would
                // resolve the scheme straight back to us and loop.
                ? new[] { "-a", "/Applications/Claude.app", url }
                : Arguments(route, url);

            Run("/usr/bin/open", arguments);
        }

        // Pure, so the shape of the delivery command is testable without
        // opening anything. No -n, for the reason in this file's header.
        internal static string[] Arguments(UrlRoute route, string url)
        {
            var arguments = new List<string> { "-a", route.BundlePath };

            // Only meaningful when the instance is not already running — `open`
            // applies --env at launch — but harmless when it is, and passing it
            // unconditionally means a link that has to start the profile starts
            // it on the right userData directory.
            if (route.UserDataDir is { Length: > 0 } directory)
            {
                arguments.Add("--env");
                arguments.Add("CLAUDE_USER_DATA_DIR=" + directory);
            }

            arguments.Add(url);
            return arguments.ToArray();
        }

        private static bool Run(string executable, string[] arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

                using var process = Process.Start(startInfo);
                if (process is null) return false;

                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(10_000))
                {
                    try { process.Kill(true); } catch { }
                    return false;
                }

                stdout.GetAwaiter().GetResult();
                stderr.GetAwaiter().GetResult();
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
