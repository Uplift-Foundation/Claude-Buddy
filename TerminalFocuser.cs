using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeBuddy
{
    // Best-effort "take me to that session's terminal" for a left-click on
    // an orb. Silently does nothing when the status file predates the hook
    // scripts that record terminal info.
    //
    // Precision degrades gracefully. macOS: the exact tmux pane (see
    // FocusTmux), the exact iTerm2 pane (via its session UUID), the exact
    // Terminal.app tab (via tty), otherwise just activate the terminal app;
    // the first click triggers a macOS Automation permission prompt for
    // controlling the terminal — that's expected; approve it once.
    // Windows: the terminal window whose PID the hook recorded, otherwise
    // any window of the app named by term_program (the WSL case, where the
    // Windows-side parent chain dead-ends in an interop bridge). Selecting
    // the exact Windows Terminal *tab* isn't possible — WT doesn't expose
    // its tabs to other processes.
    internal static class TerminalFocuser
    {
        public static void Focus(SessionStatus? status)
        {
            if (status is null) return;

            // Resolving a target runs several short-lived processes (tmux
            // queries, ps walks, osascript) and waits on their output; doing
            // that on the UI thread would stall every orb's animation for the
            // duration of the click.
            Task.Run(() => FocusCore(status));
        }

        private static void FocusCore(SessionStatus status)
        {
            if (OperatingSystem.IsWindows())
            {
                FocusWindows(status);
                return;
            }

            if (!OperatingSystem.IsMacOS()) return;

            // tmux first: when a session is inside tmux, nothing else the hook
            // recorded points at a window you can actually see.
            if (!string.IsNullOrEmpty(status.TmuxPane) && FocusTmux(status)) return;

            string? script;
            if (!string.IsNullOrEmpty(status.TermId))
            {
                script = ITermSelectScript("id", status.TermId);
            }
            else
            {
                script = status.TermProgram switch
                {
                    "Apple_Terminal" when !string.IsNullOrEmpty(status.Tty) => TerminalSelectScript(status.Tty),
                    "Apple_Terminal" => "tell application \"Terminal\" to activate",
                    "iTerm.app" => "tell application \"iTerm\" to activate",
                    "vscode" => "tell application \"Visual Studio Code\" to activate",
                    "ghostty" => "tell application \"Ghostty\" to activate",
                    "WezTerm" => "tell application \"WezTerm\" to activate",
                    _ => null
                };
            }

            RunOsaScript(script);
        }

        // --- tmux ---
        //
        // Two separate jobs, and skipping either one leaves you looking at the
        // wrong thing:
        //   1. Make the session's pane current *inside* tmux — the attached
        //      client is very likely showing some other window/pane, so
        //      activating its terminal alone would land you somewhere else.
        //   2. Activate the terminal app that hosts a client attached to that
        //      session. Which terminal that is can't be recorded at hook time:
        //      you can detach and reattach a tmux session from a different app
        //      (or from none at all), so it's resolved from the live client's
        //      tty on every click.
        private static bool FocusTmux(SessionStatus status)
        {
            var tmux = ResolveTmuxBinary(status.TmuxBin);
            if (tmux is null) return false;

            var pane = status.TmuxPane;

            // Also serves as the liveness check: a pane id from a server that
            // has since exited (or a pane that's been killed) fails here, and
            // we fall back to the non-tmux heuristics.
            if (!TryRun(tmux, out var sessionName, TmuxArgs(status, "display-message", "-p", "-t", pane, "#{session_name}")))
            {
                return false;
            }
            sessionName = sessionName.Trim();
            if (sessionName.Length == 0) return false;

            TryRun(tmux, out _, TmuxArgs(status, "select-window", "-t", pane));
            TryRun(tmux, out _, TmuxArgs(status, "select-pane", "-t", pane));

            var client = ResolveClient(tmux, status, sessionName);

            // No client attached anywhere: the pane is now selected, so the
            // session is waiting correctly for whenever it's next attached,
            // but there's no window to bring forward. Report that we didn't
            // activate anything so the caller can still try its own heuristics
            // rather than treating the click as handled.
            if (client is null) return false;

            var (clientTty, controlMode) = client.Value;
            var app = ResolveAppBundleForTty(clientTty);

            // iTerm2 and Terminal.app can both select the exact tab the client
            // runs in, which matters when several tmux clients share one app.
            //
            // Except in control mode (iTerm2's native tmux integration,
            // `tmux -CC`), where that tty belongs to the hidden control tab
            // rather than to any window you'd want to look at — iTerm2 mirrors
            // tmux windows as native tabs and follows the select-pane above on
            // its own, so activating the app is both sufficient and correct.
            var script = controlMode ? null : Path.GetFileName(app) switch
            {
                "iTerm.app" => ITermSelectScript("tty", clientTty),
                "Terminal.app" => TerminalSelectScript(clientTty),
                _ => null
            };

            if (script is not null)
            {
                RunOsaScript(script);
                return true;
            }

            if (app is not null)
            {
                ActivateApp(app);
                return true;
            }

            // Couldn't work out which app owns the client's tty. The pane is
            // selected, but nothing was brought forward — say so, so the
            // caller falls through instead of swallowing the click.
            return false;
        }

        // Works for any terminal without a case per app: `open -a` on a running
        // app just brings it forward.
        private static void ActivateApp(string appBundlePath)
        {
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                psi.ArgumentList.Add("-a");
                psi.ArgumentList.Add(appBundlePath);
                Process.Start(psi);
            }
            catch { }
        }

        private static string[] TmuxArgs(SessionStatus status, params string[] args)
        {
            // -S pins the server: several can coexist (plain tmux, tmuxinator,
            // a -L named socket), and the pane id is only unique within one.
            if (string.IsNullOrEmpty(status.TmuxSocket)) return args;

            var full = new string[args.Length + 2];
            full[0] = "-S";
            full[1] = status.TmuxSocket;
            args.CopyTo(full, 2);
            return full;
        }

        // The app can't count on PATH: launched from Finder or Login Items it
        // gets the bare system PATH, with no Homebrew or MacPorts in it. The
        // hook records where tmux actually was, and these are the fallbacks
        // for status files written before it did.
        private static readonly string[] TmuxCandidates =
        {
            "/opt/homebrew/bin/tmux",
            "/usr/local/bin/tmux",
            "/opt/local/bin/tmux",
            "/usr/bin/tmux"
        };

        private static string? ResolveTmuxBinary(string recorded)
        {
            if (!string.IsNullOrEmpty(recorded) && File.Exists(recorded)) return recorded;
            return TmuxCandidates.FirstOrDefault(File.Exists);
        }

        // Prefer a client already looking at the session; otherwise commandeer
        // one — switching some client to it is the only way to get the session
        // on screen at all. Either way, ties break toward the most recently
        // active client: a session can be attached from several terminals at
        // once, and the one you touched last is the one you're sitting at.
        private static (string Tty, bool ControlMode)? ResolveClient(string tmux, SessionStatus status, string sessionName)
        {
            if (!TryRun(tmux, out var listing, TmuxArgs(status, "list-clients", "-F",
                    "#{client_tty}\t#{client_session}\t#{client_activity}\t#{client_control_mode}")))
            {
                return null;
            }

            (string Tty, bool ControlMode)? onSession = null, anyClient = null;
            string? onSessionBest = null, anyClientBest = null;

            foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;

                var tty = parts[0].Trim();
                if (tty.Length == 0) continue;

                // client_activity is a unix timestamp; string-compare is fine
                // for equal-width integers and avoids caring about the format.
                var activity = parts.Length > 2 ? parts[2].Trim() : "";
                var candidate = (tty, parts.Length > 3 && parts[3].Trim() == "1");

                if (parts[1].Trim() == sessionName)
                {
                    if (onSession is null || activity.CompareTo(onSessionBest) > 0)
                    {
                        onSession = candidate;
                        onSessionBest = activity;
                    }
                }
                else if (anyClient is null || activity.CompareTo(anyClientBest) > 0)
                {
                    anyClient = candidate;
                    anyClientBest = activity;
                }
            }

            if (onSession is not null) return onSession;
            if (anyClient is null) return null;

            TryRun(tmux, out _, TmuxArgs(status, "switch-client", "-c", anyClient.Value.Tty, "-t", sessionName));
            return anyClient;
        }

        // Walks up from whatever is running on a tty until it hits a process
        // living inside an .app bundle — that's the terminal emulator hosting
        // it. Covers Ghostty, WezTerm, kitty, Alacritty, VS Code and friends
        // without needing a case per app.
        private static string? ResolveAppBundleForTty(string tty)
        {
            var name = tty.StartsWith("/dev/") ? tty[5..] : tty;

            if (!TryRun("/bin/ps", out var listing, "-t", name, "-o", "pid=")) return null;

            var pid = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => int.TryParse(l.Trim(), out var p) ? p : 0)
                .FirstOrDefault(p => p > 0);
            if (pid == 0) return null;

            for (var hop = 0; hop < 12 && pid > 1; hop++)
            {
                if (!TryRun("/bin/ps", out var row, "-o", "ppid=,comm=", "-p", pid.ToString())) return null;

                row = row.Trim();
                var split = row.IndexOf(' ');
                if (split <= 0) return null;

                var command = row[(split + 1)..].Trim();
                var marker = command.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
                if (marker >= 0) return command[..(marker + 4)];

                if (!int.TryParse(row[..split].Trim(), out pid)) return null;
            }

            return null;
        }

        // --- process helpers ---

        private static bool TryRun(string exe, out string stdout, params string[] args)
        {
            stdout = "";
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var arg in args) psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process is null) return false;

                // Read both pipes concurrently and only then wait. Doing a
                // blocking ReadToEnd() first would make the timeout below
                // unreachable — it returns when the pipe closes, which a wedged
                // child never does — and leaving stderr undrained can deadlock
                // a chatty one once its pipe buffer fills.
                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();

                // A wedged tmux server would otherwise hang this click forever.
                if (!process.WaitForExit(3000))
                {
                    try { process.Kill(true); } catch { }
                    return false;
                }

                stdout = outTask.GetAwaiter().GetResult();
                errTask.GetAwaiter().GetResult();

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static void RunOsaScript(string? script)
        {
            if (script is null) return;

            try
            {
                var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);
                Process.Start(psi);
            }
            catch
            {
                // Focusing is a convenience; never let it take the app down.
            }
        }

        // --- Windows ---

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private static void FocusWindows(SessionStatus status)
        {
            try
            {
                var hwnd = IntPtr.Zero;

                if (status.TermPid > 0)
                {
                    try
                    {
                        hwnd = Process.GetProcessById(status.TermPid).MainWindowHandle;
                    }
                    catch { } // terminal exited; fall through
                }

                if (hwnd == IntPtr.Zero)
                {
                    var processName = status.TermProgram switch
                    {
                        "WindowsTerminal" => "WindowsTerminal",
                        "vscode" => "Code",
                        _ => null
                    };
                    if (processName is null) return;

                    hwnd = Process.GetProcessesByName(processName)
                        .Select(p => p.MainWindowHandle)
                        .FirstOrDefault(h => h != IntPtr.Zero);
                }

                if (hwnd == IntPtr.Zero) return;

                if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            catch
            {
                // Same convenience-only rule as macOS.
            }
        }

        // property is "id" (a session UUID recorded by the hook) or "tty" (the
        // live tty of an attached tmux client). Both are iTerm2 session
        // properties; a no-match still activates, which is better than nothing.
        //
        // `activate` MUST come last, after the selects — this is load-bearing,
        // not style. Activating first and then selecting focuses the window in
        // place and macOS never follows to the Space that window lives on, so a
        // click from another desktop appears to do nothing. Selecting first and
        // activating last makes macOS switch Spaces. (Verified from a second
        // desktop: activate-only switches, activate-then-select doesn't,
        // select-then-activate does.)
        private static string ITermSelectScript(string property, string value) => $$"""
            tell application "iTerm"
                repeat with w in windows
                    repeat with t in tabs of w
                        repeat with s in sessions of t
                            if {{property}} of s is "{{value}}" then
                                select w
                                select t
                                select s
                                activate
                                return
                            end if
                        end repeat
                    end repeat
                end repeat
                activate
            end tell
            """;

        // Accepts either form the two paths produce: a bare "ttys004" from the
        // hook, or a "/dev/ttys004" client tty from tmux.
        //
        // `activate` last, for the same Spaces reason as ITermSelectScript.
        private static string TerminalSelectScript(string tty) => $$"""
            tell application "Terminal"
                repeat with w in windows
                    repeat with t in tabs of w
                        if tty of t is "{{(tty.StartsWith("/dev/") ? tty : "/dev/" + tty)}}" then
                            set selected of t to true
                            set index of w to 1
                            activate
                            return
                        end if
                    end repeat
                end repeat
                activate
            end tell
            """;
    }
}
