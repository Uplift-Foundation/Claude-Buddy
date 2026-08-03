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
