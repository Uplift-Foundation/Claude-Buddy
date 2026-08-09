using System.Diagnostics;

namespace ClaudeBuddy
{
    // Where to find a team lead that has no terminal of its own.
    //
    // A lead can be a background session: Claude Code runs it inside
    // `claude daemon run`, which has no controlling terminal, and if the
    // interactive session that started it has since gone away that process ends
    // up reparented to launchd with no tty anywhere above it. The hook walks up
    // looking for one, finds nothing, and writes a status file naming no
    // terminal at all — so the app dropped the orb, on the reasonable-sounding
    // grounds that clicking it could go nowhere.
    //
    // That premise is wrong. You watch such a team through `claude agents`,
    // which is a *separate* process sitting in a terminal — a real window, just
    // not one anything in the session's own process tree points at. Nothing
    // else on disk points at it either: ~/.claude/session-env/<id>/ is empty,
    // and the team config records the lead as "in-process" with no pane. The
    // process table is the only place the connection exists.
    //
    // So the connection is made here, by the one thing the two share: the
    // directory. `claude agents` is the view of the team in the directory it
    // was run from, which is the same directory the lead session reports. Two
    // teams led from one directory would be ambiguous — the cost of that is
    // landing on the other team's viewer, which is a window you wanted to see
    // anyway, so it degrades to something harmless rather than wrong.
    internal static class AgentTeamViewer
    {
        // The lookup runs `ps` and `lsof`, so it is cached rather than repeated
        // every two-second scan. Short, because the viewer is a thing you open
        // and close by hand and an orb that stays unclickable for a minute
        // after you open one would read as broken.
        private const long CacheMs = 5_000;

        private static readonly object Gate = new();
        private static readonly Dictionary<string, (Viewer? Found, long Stamp)> Cache =
            new(StringComparer.Ordinal);

        // Which terminal app a viewer for a directory was opened into, so a
        // later click can bring that app forward instead of opening a second
        // window onto the same team.
        private static readonly Dictionary<string, string> Launched =
            new(StringComparer.Ordinal);

        private readonly record struct Viewer(string Socket, string Pane, string Tty);

        // Fills in a status that names no terminal, from the viewer for its
        // directory. Returns whether anything was learned; the caller shows the
        // orb either way, since a team that is running is worth seeing even
        // when you can't yet click your way to it.
        public static bool TryAdopt(SessionStatus status)
        {
            if (!OperatingSystem.IsMacOS()) return false;
            if (string.IsNullOrEmpty(status.Cwd)) return false;

            var viewer = For(status.Cwd);
            if (viewer is null) return false;

            status.TmuxSocket = viewer.Value.Socket;
            status.TmuxPane = viewer.Value.Pane;
            status.Tty = viewer.Value.Tty;

            // TmuxBin is deliberately left empty: it records where the *hook*
            // found tmux, and this didn't come from a hook. TerminalFocuser
            // falls back to the usual install locations.
            return true;
        }

        private static Viewer? For(string cwd)
        {
            var key = cwd.TrimEnd('/');
            var now = Environment.TickCount64;

            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached) && now - cached.Stamp < CacheMs)
                {
                    return cached.Found;
                }
            }

            var viewer = Locate(key);

            lock (Gate)
            {
                Cache[key] = (viewer, now);
            }

            return viewer;
        }

        private static Viewer? Locate(string cwd)
        {
            foreach (var pid in ViewerPids())
            {
                if (!string.Equals(CwdOf(pid), cwd, StringComparison.Ordinal)) continue;

                // TMUX is "<socket>,<server pid>,<session index>" — the same
                // shape the hook records, and only the socket is wanted.
                var env = MacOSProcessScan.EnvironmentValues(pid, "TMUX", "TMUX_PANE");

                var tmux = env.GetValueOrDefault("TMUX", "");
                var pane = env.GetValueOrDefault("TMUX_PANE", "");
                var tty = TtyOf(pid);

                if (string.IsNullOrEmpty(tmux) || string.IsNullOrEmpty(pane))
                {
                    // Running outside tmux: the tty alone is enough for the
                    // app to find the window that owns it.
                    if (string.IsNullOrEmpty(tty)) continue;
                    return new Viewer("", "", tty);
                }

                var socket = tmux.Split(',')[0];
                return new Viewer(socket, pane, tty);
            }

            return null;
        }

        // Opens the one session an orb stands for, in a terminal.
        //
        // TryAdopt can only adopt a window that already exists, which leaves
        // the case this whole file is about only half answered: a background
        // session has no window *anywhere*, so its orb stays inert no matter
        // how good the lookup is.
        //
        // `claude attach <id>` is the answer Claude Code already provides, and
        // it is the reason this doesn't open the `claude agents` list instead.
        // The list was tried first and is the wrong destination twice over: it
        // lands on a roster rather than the session you clicked, and filtering
        // it with --cwd — which reads like "this session's directory" — means
        // "started *under* this path", so for an agent dispatched into a
        // worktree it matches nothing and the view opens on its empty state,
        // which is a new-task prompt. `attach` names the session directly, and
        // its own help is explicit that the session keeps running whether you
        // stay attached or drop out, so a click can never disturb the work.
        //
        // Driven through a script file rather than AppleScript's `do script`
        // because that verb is Terminal.app's own vocabulary; `open -a <app>
        // <executable file>` is understood by every terminal this file's
        // neighbours already name, so one path covers all of them instead of
        // one per app.
        // Returns the tmux pane the session was attached into, when it went
        // into tmux at all — the caller focuses it through the same path it
        // uses for any other pane, rather than this file growing its own copy
        // of client resolution and window raising. Null means it either went
        // into a terminal window of its own or didn't happen.
        public static string? AttachSession(string sessionId, string cwd)
        {
            if (!OperatingSystem.IsMacOS()) return null;
            if (string.IsNullOrEmpty(sessionId)) return null;

            // A terminal may already be attached to this session and still not
            // be adoptable: Locate needs a tty to hand back, and a window
            // opened by `open` has none, so it declines one that is plainly
            // there. Left at that, every click would open another terminal on
            // a session that already has one. Existence is the right question
            // here, not addressability — so ask it directly, and settle for
            // bringing its app forward rather than attaching twice.
            string? remembered;
            lock (Gate) remembered = Launched.GetValueOrDefault(JobIdOf(sessionId));

            // Already attached inside tmux. Hand the pane back so the caller
            // selects it, rather than only bringing the app forward: the two
            // look the same when you happen to be looking at that window
            // already, and nothing like each other when you aren't. This is
            // why a second click appeared to do nothing — the window existed
            // and was reachable by hand, but the click stopped short of
            // switching to it.
            if (ExistingAttachPane(JobIdOf(sessionId)) is { Length: > 0 } existing)
            {
                return existing;
            }

            // Attached, but in a terminal window of its own rather than a pane.
            // There's nothing to select in that case, so raising its app is the
            // whole of what can be done.
            if (AttachedAlready(JobIdOf(sessionId)))
            {
                ActivateApp(remembered ?? TerminalApp());
                return null;
            }

            // Into tmux when there's a tmux to go into. Opening a bare terminal
            // window for someone who lives in tmux puts the session somewhere
            // their usual navigation can't reach it — the window is *outside*
            // the thing they use to move between windows, which is worse than
            // an extra pane inside it.
            if (PlaceInTmux(JobIdOf(sessionId), cwd) is { Length: > 0 } pane)
            {
                return pane;
            }

            try
            {
                System.IO.Directory.CreateDirectory(ClaudeBuddySettings.Directory);
                var script = Path.Combine(ClaudeBuddySettings.Directory, "open-agents-view.sh");

                // Single-quoted, with any embedded quote closed and reopened
                // the shell way, so a directory with a space or an apostrophe
                // still arrives as one word.
                var quoted = "'" + cwd.Replace("'", "'\\''") + "'";

                // A login shell, not the bare `sh` that `open` would give us:
                // launched from Finder this app has only the stock system PATH,
                // and `claude` lives in ~/.local/bin. Asking the user's own
                // shell to resolve it is the same answer ResolveTmuxBinary
                // reaches for tmux, without hard-coding a second install list.
                var shell = Environment.GetEnvironmentVariable("SHELL");
                if (string.IsNullOrEmpty(shell)) shell = "/bin/zsh";

                // The directory travels in the environment rather than inline
                // in the -lc string. Written inline it would have to be quoted
                // inside a string that is itself single-quoted, and shell
                // single quotes don't nest — the two runs would flatten into
                // one unquoted word, which happens to work until the first
                // path with a space in it and then silently cd's somewhere
                // else. A variable reference needs no quoting of its own.
                // attach wants the *job* id, which is the first segment of the
                // session uuid and not the uuid itself — `claude logs` with a
                // full id answers "No job matching", with the short one it
                // prints the session's output. `claude agents --json` shows
                // both side by side ("id": "162e0b4b", "sessionId":
                // "162e0b4b-3c45-..."), which is where the relationship is
                // confirmed rather than assumed.
                var jobId = JobIdOf(sessionId);
                var quotedId = "'" + jobId.Replace("'", "'\\''") + "'";

                // The cd matters even though attach names the session outright:
                // Ctrl+Z drops you back to a shell in this window, and the
                // useful place to land is the directory whose orb you clicked.
                File.WriteAllText(script,
                    "#!/bin/sh\n"
                    + "CLAUDE_BUDDY_AGENT_DIR=" + quoted + "\n"
                    + "CLAUDE_BUDDY_SESSION=" + quotedId + "\n"
                    + "export CLAUDE_BUDDY_AGENT_DIR CLAUDE_BUDDY_SESSION\n"
                    + "exec " + shell + " -lc "
                    + "'cd \"$CLAUDE_BUDDY_AGENT_DIR\" "
                    + "&& exec claude attach \"$CLAUDE_BUDDY_SESSION\"'\n");

                File.SetUnixFileMode(script,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                var app = TerminalApp();
                lock (Gate) Launched[jobId] = app;

                var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                psi.ArgumentList.Add("-a");
                psi.ArgumentList.Add(app);
                psi.ArgumentList.Add(script);
                Process.Start(psi);

                // The window takes a moment to appear and the cache would
                // otherwise keep saying "nothing here" for its full window,
                // leaving the next click to open a second one.
                Forget(cwd);
                return null;
            }
            catch
            {
                // Same contract as everything else on this path: failing to
                // open a window is a click that did nothing, never a crash.
                return null;
            }
        }

        // Runs the attach in a new window of whichever tmux session already has
        // a client, and hands back its pane. Nothing is selected or raised here
        // — returning the pane lets the caller reuse the focus path every other
        // pane goes through, which already knows how to find the client, pick
        // the window and bring its app forward.
        private static string? PlaceInTmux(string jobId, string cwd)
        {
            var tmux = ResolveTmux();
            if (tmux is null) return null;

            // A server with no client attached is a detached session: making a
            // window in it would put the attach somewhere with no screen, which
            // is the same nowhere the orb already pointed at.
            if (!TryRun(tmux, out var clients, "list-clients", "-F", "#{client_session}"))
            {
                return null;
            }

            var session = clients
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0);

            if (session is null) return null;

            // Through a login shell for the same PATH reason as everywhere else
            // here: tmux runs a command with `sh -c`, and the server's own
            // environment is whatever it was started with, which needn't
            // include wherever `claude` lives.
            var shell = Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrEmpty(shell)) shell = "/bin/zsh";

            var command = shell + " -lc 'claude attach " + jobId.Replace("'", "'\\''") + "'";

            // "<session>:" with the colon, not the bare name. Bare, tmux reads
            // the target as a *window* and refuses with "index N in use" the
            // moment that index is taken; the trailing colon names the session
            // and lets it pick the next free index.
            if (!TryRun(tmux, out var pane,
                    "new-window", "-t", session + ":", "-c", cwd,
                    "-P", "-F", "#{pane_id}", command))
            {
                return null;
            }

            var id = pane.Trim();
            return id.StartsWith('%') ? id : null;
        }

        // Where tmux actually is. The app can't count on PATH — launched from
        // Finder it gets the bare system one — and unlike a session's status
        // file there's no recorded location to start from here, so this is the
        // same candidate list TerminalFocuser falls back to.
        private static string? ResolveTmux()
        {
            string[] candidates =
            {
                "/opt/homebrew/bin/tmux",
                "/usr/local/bin/tmux",
                "/usr/bin/tmux",
                "/opt/local/bin/tmux"
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        // Whichever terminal is already running, so the viewer opens where the
        // user's other terminals are rather than waking a second app. Ordered
        // by specificity: Terminal.app is last because it's the fallback that
        // always exists, not a preference.
        private static string TerminalApp()
        {
            string[] candidates =
            {
                "/Applications/iTerm.app",
                "/Applications/Ghostty.app",
                "/Applications/WezTerm.app"
            };

            if (TryRun("/bin/ps", out var listing, "-eo", "args="))
            {
                foreach (var candidate in candidates)
                {
                    if (listing.Contains(candidate, StringComparison.Ordinal)
                        && System.IO.Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return "/System/Applications/Utilities/Terminal.app";
        }

        // The short form `claude attach` and `claude logs` expect. Split rather
        // than a fixed eight characters so an id that isn't a uuid degrades to
        // itself instead of being sliced into nonsense.
        private static string JobIdOf(string sessionId)
        {
            var dash = sessionId.IndexOf('-');
            return dash > 0 ? sessionId[..dash] : sessionId;
        }

        // The tmux pane already running `claude attach <id>`, if there is one.
        //
        // Found through each pane's own process rather than anything this app
        // remembered, so it still works after a restart, and finds a pane the
        // user opened by hand just as well as one opened from an orb.
        private static string? ExistingAttachPane(string jobId)
        {
            var tmux = ResolveTmux();
            if (tmux is null) return null;

            if (!TryRun(tmux, out var panes, "list-panes", "-a", "-F", "#{pane_id} #{pane_pid}"))
            {
                return null;
            }

            foreach (var line in panes.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (!int.TryParse(parts[1], out var pid)) continue;

                if (!TryRun("/bin/ps", out var args, "-p", pid.ToString(), "-o", "args=")) continue;

                var words = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 3) continue;
                if (Path.GetFileName(words[0]) is not ("claude" or "claude.exe")) continue;
                if (words[1] != "attach") continue;

                var id = words[2];
                if (jobId.StartsWith(id, StringComparison.Ordinal)
                    || id.StartsWith(jobId, StringComparison.Ordinal))
                {
                    return parts[0];
                }
            }

            return null;
        }

        // Whether some terminal is already sitting on `claude attach <id>`.
        // Matched the same way ViewerPids matches the agent view, and for the
        // same reason: the executable path is version-stamped and moves, the
        // arguments don't. The id is compared by prefix because attach accepts
        // the short form and echoes it back that way, so a window opened by
        // hand with `claude attach bd7919f8` must still count as this session's.
        private static bool AttachedAlready(string sessionId)
        {
            if (!TryRun("/bin/ps", out var listing, "-eo", "args=")) return false;

            foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 3) continue;
                if (Path.GetFileName(words[0]) is not ("claude" or "claude.exe")) continue;
                if (words[1] != "attach") continue;

                var id = words[2];
                if (sessionId.StartsWith(id, StringComparison.Ordinal)
                    || id.StartsWith(sessionId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Forget(string cwd)
        {
            lock (Gate) Cache.Remove(cwd.TrimEnd('/'));
        }

        // `open -a` on a running app just brings it forward — the same trick
        // TerminalFocuser.ActivateApp uses, kept here rather than shared
        // because that one is private to a class this file must not depend on.
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

        // Processes running `claude agents`. Matched on the argument rather
        // than the executable path, which is a version-stamped location that
        // changes under you (~/.local/share/claude/versions/<n>).
        private static IEnumerable<int> ViewerPids()
        {
            if (!TryRun("/bin/ps", out var listing, "-eo", "pid=,args=")) yield break;

            foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                var split = trimmed.IndexOf(' ');
                if (split <= 0) continue;

                if (!int.TryParse(trimmed[..split], out var pid)) continue;

                var command = trimmed[(split + 1)..].Trim();
                var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // "claude agents", and nothing that merely mentions it — a
                // shell running a script with that name in its command line
                // shouldn't count.
                if (words.Length < 2 || words[1] != "agents") continue;
                if (Path.GetFileName(words[0]) is not ("claude" or "claude.exe")) continue;

                yield return pid;
            }
        }

        // No libproc equivalent worth the struct marshalling here: lsof is
        // asked for one descriptor of one process, and only for the handful of
        // viewers found above.
        private static string CwdOf(int pid)
        {
            if (!TryRun("/usr/sbin/lsof", out var listing,
                    "-a", "-p", pid.ToString(), "-d", "cwd", "-Fn"))
            {
                return "";
            }

            foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith('n')) return line[1..].TrimEnd('/');
            }

            return "";
        }

        private static string TtyOf(int pid)
        {
            if (!TryRun("/bin/ps", out var tty, "-o", "tty=", "-p", pid.ToString())) return "";

            tty = tty.Trim();
            return tty is "" or "??" ? "" : tty;
        }

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

                stdout = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(3000)) return false;

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
