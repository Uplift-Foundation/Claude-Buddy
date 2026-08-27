using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

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
    // Excluded from coverage, as a class. Every member of it either runs tmux as
    // a real subprocess to find or open a viewer pane, or shells out to `open -a`
    // to bring a terminal application forward — fourteen members already carried
    // the attribute individually, and what was left uncovered was the static
    // fields they share, which only exist because those members do.
    //
    // Marking the class rather than the members also fixes something the
    // per-member version could not: a field initializer belongs to the type
    // initializer, not to any method, so it is reported unhit whenever nothing
    // touches the class at all — which is exactly the situation a fully excluded
    // class is in.
    [ExcludeFromCodeCoverage]
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
        // Excluded from coverage: reaches the ps/lsof/tmux scan below.
        [ExcludeFromCodeCoverage]
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

        // Excluded from coverage: a wall-clock cache around the process scan
        // below.
        [ExcludeFromCodeCoverage]
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

        // Excluded from coverage: walks live pids and reads each one's cwd and
        // tty.
        [ExcludeFromCodeCoverage]
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

        // `<absolute claude> <verb> [arg]`, quoted for `sh -c`. Null when the
        // claude binary cannot be found at all, which is the one failure worth
        // returning rather than papering over: every caller's next move is to
        // hand this to a shell.
        //
        // Absolute, never a bare `claude` resolved by a login shell. That was the
        // original approach and it silently didn't work: `zsh -lc` skips .zshrc
        // (non-interactive), which is where a PATH addition for ~/.local/bin
        // normally lives, so the script died with "command not found" whenever
        // the app was launched from Finder rather than a terminal. See
        // ClaudeBinary.
        // Excluded from coverage: probes the filesystem for the claude binary.
        [ExcludeFromCodeCoverage]
        private static string? ClaudeCommand(string verb, string? argument = null)
        {
            var claude = ClaudeBinary.Path;
            if (claude is null) return null;

            var command = TerminalScripts.ShellQuote(claude) + " " + verb;
            return argument is null ? command : command + " " + TerminalScripts.ShellQuote(argument);
        }

        // Opens a `claude agents` roster, in tmux for someone who lives there and
        // in a terminal window otherwise.
        //
        // In tmux when there is a tmux to go into, for the reason AttachSession
        // gives at the same fork: a bare window for someone whose windows are all
        // inside tmux puts the thing *outside* what they use to move between
        // windows, which is worse than an extra pane inside it.
        // Excluded from coverage: writes a script and opens a terminal on it.
        [ExcludeFromCodeCoverage]
        private static string? OpenAgentsView(string cwd)
        {
            if (ClaudeCommand("agents") is not { } command) return null;

            if (PlaceInTmux(command, cwd) is { Length: > 0 } pane) return pane;

            try
            {
                System.IO.Directory.CreateDirectory(ClaudeBuddySettings.Directory);
                var script = Path.Combine(ClaudeBuddySettings.Directory, "open-agents-roster.sh");

                // The cd is for after the roster is quit rather than for the
                // roster itself, the same as every other script this file writes:
                // the useful place to land is the directory whose orb was clicked.
                // Skipped with no cwd, because `cd ''` fails and `|| exit 1` would
                // take the roster down with it.
                var body = "#!/bin/sh\n";
                if (!string.IsNullOrEmpty(cwd))
                {
                    body += "cd " + TerminalScripts.ShellQuote(cwd) + " || exit 1\n";
                }

                File.WriteAllText(script, body + "exec " + command + "\n");
                File.SetUnixFileMode(script,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                psi.ArgumentList.Add("-a");
                psi.ArgumentList.Add(TerminalApp());
                psi.ArgumentList.Add(script);
                Process.Start(psi);

                // The window takes a moment to appear, and until it does the
                // process scan above says nothing is open — which is what would
                // otherwise let a second click open a second roster.
                Forget(cwd);
                return null;
            }
            catch
            {
                // Same contract as everything else on this path: failing to open
                // a window is a click that did nothing, never a crash.
                return null;
            }
        }

        // The `claude agents` view: brought forward if one is running, opened if
        // not.
        //
        // The click destination for a background orb, and it is a decision about
        // vocabulary rather than a mechanism. The user's words, looking at the
        // roster: "don't understand why the orbs can't just match this and attach
        // to this", and then "I don't understand why you can't go straight to
        // it!" The view already groups what needs answering at the top, already
        // knows how to attach to a row, and is where these sessions are managed
        // from — so an orb that lands there lands somewhere the user recognises,
        // rather than in a terminal running one session with no way back to the
        // others.
        //
        // Unfiltered, deliberately: `claude agents --help` offers only `--cwd
        // <path>` and no per-session preselect, and jobs on the machine this was
        // written for span several projects — a filtered view per project would
        // multiply windows for no gain, since the roster is short and sorted with
        // the ones wanting attention first.
        //
        // Returns the pane it went into when it went into tmux, for the caller to
        // select and raise through the ordinary path — the same contract
        // AttachSession has, and for the same reason: this file does not grow its
        // own copy of client resolution and window raising.
        // Excluded from coverage: walks the process table, then either activates
        // a real application or opens a terminal on a script.
        [ExcludeFromCodeCoverage]
        public static string? OpenOrFocusAgentsView(string cwd)
        {
            if (!OperatingSystem.IsMacOS()) return null;

            // Already open somewhere. Focused rather than duplicated: a second
            // roster of the same sessions is not a second thing to look at.
            //
            // Asked of the process table rather than of Launched, so a view the
            // user opened by hand counts — which is the common case, since this
            // is where they manage these sessions from.
            foreach (var pid in ViewerPids())
            {
                var env = MacOSProcessScan.EnvironmentValues(pid, "TMUX", "TMUX_PANE");
                var pane = env.GetValueOrDefault("TMUX_PANE", "");

                // In tmux: hand the pane back, exactly as AttachSession does, so
                // the caller selects it and raises its client's window.
                if (!string.IsNullOrEmpty(pane)) return pane;

                // Outside tmux: its tty is enough for the app to find the window
                // that owns it, which is what ActivateApp's caller does with it.
                var tty = TtyOf(pid);
                if (string.IsNullOrEmpty(tty)) continue;

                ActivateApp(TerminalApp());
                return null;
            }

            return OpenAgentsView(cwd);
        }

        // Every `claude attach <id>` client running on this machine, keyed by the
        // id it was given — or null when the process table could not be read at
        // all, which is a different answer and the caller treats it as one.
        //
        // The point of it is a contradiction the user hit immediately: they
        // attached to all three parked sessions and the orbs stayed grey.
        // Attaching changes nothing this app was looking at — the status file
        // records the *worker's* ancestry and never the viewer's, so the tty
        // stays empty, and the daemon still says "blocked", because from its side
        // nothing has changed. The person's presence exists in exactly one place,
        // which is the process table.
        //
        // Matched on the arguments rather than the executable path, for the
        // reason ViewerPids gives: the path is a version-stamped location that
        // moves under you. Cached on the same clock as the viewer lookup — one
        // `ps` per five seconds rather than one per session per scan.
        // Excluded from coverage: walks the live process table. What is decided
        // with the answer is SessionPresence.HasAttachClient, which is pure and
        // covered per case, including the null.
        [ExcludeFromCodeCoverage]
        // fresh skips the cache read, for a caller answering a gesture the user
        // just made rather than a scan that runs every two seconds. Five seconds
        // of staleness is nothing to the scan and everything to someone who closed
        // their attach window and immediately clicked the orb: they would get an
        // app raised that has no such window, and the click would appear to do
        // nothing. It still *writes* the cache, so a click warms the next scan
        // rather than costing an extra `ps`.
        //
        // A parameter rather than a second method, because a second method is how
        // this file came to hold two scans of the same process table — see
        // SessionPresence.KnownAttachClient for what that cost.
        public static HashSet<string>? AttachedJobIds(bool fresh = false)
        {
            var now = Environment.TickCount64;

            lock (Gate)
            {
                if (!fresh && _attachedStamp != 0 && now - _attachedStamp < CacheMs)
                {
                    return _attached;
                }
            }

            HashSet<string>? found = null;

            if (TryRun("/bin/ps", out var listing, "-eo", "args="))
            {
                found = new HashSet<string>(StringComparer.Ordinal);

                foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length < 3) continue;
                    if (Path.GetFileName(words[0]) is not ("claude" or "claude.exe")) continue;
                    if (words[1] != "attach") continue;

                    found.Add(words[2]);
                }
            }

            lock (Gate)
            {
                // A failed read does not clear a good answer for the rest of the
                // cache window, the same way BackgroundJobs' listing does not:
                // the process table being briefly unreadable is not evidence that
                // anybody detached.
                if (found is not null)
                {
                    _attached = found;
                    _attachedStamp = now;
                }

                return _attached;
            }
        }

        private static HashSet<string>? _attached;
        private static long _attachedStamp;

        // The tmux pane already *showing* this session's conversation, if there is
        // one — step zero of the click ladder, ahead of everything that creates a
        // window.
        //
        // It exists because of the one thing every earlier round assumed was
        // undiscoverable. The socket, file, argv and environment proofs all
        // stand: a session's rv socket has exactly one external peer, `claude
        // daemon run`, so the topology is a star and the viewer mapping is
        // hub-internal, and the interactive client holds no transcript file open.
        // What none of that covered is that the TUI *publishes* the conversation
        // title to the terminal, and tmux keeps it in `#{pane_title}`. Found in
        // the reporter's own screenshot, which is worth recording: the answer had
        // been on screen the whole time.
        //
        // It is a weaker signal than it first looks, and the code is shaped around
        // that. The title is per *conversation*, not per session, and every member
        // of an agent team inherits the team session's title — measured here, four
        // panes with one identical title for three different sessions. So the
        // title only nominates candidates; SessionPresence.ViewerAmong decides,
        // and it is deliberately readier to answer "do nothing" than "go here",
        // because the first cannot be wrong about which session it found.
        //
        // The default socket only. A pane in a detached `claude-swarm-<pid>`
        // server is not being looked at by definition, which is the whole reason
        // the socket answer exists further down the ladder.
        // Excluded from coverage: lists live tmux panes and reads their processes.
        // Every rule it applies is pure and covered per clause — see
        // SessionPresence.TitleSaysViewing, LooksLikeClaudeBinary and ViewerAmong.
        [ExcludeFromCodeCoverage]
        public static (SessionPresence.ViewerVerdict Verdict, string? Pane) ViewingPane(
            string? sessionTitle, IReadOnlySet<string>? panesClaimedByOthers)
        {
            var none = (SessionPresence.ViewerVerdict.NoneFound, (string?)null);

            if (!OperatingSystem.IsMacOS()) return none;
            if (string.IsNullOrEmpty(sessionTitle)) return none;

            var tmux = ResolveTmux();
            if (tmux is null) return none;

            // pane_title last, and split with a limit: a conversation title
            // contains spaces, and every field before it does not.
            if (!TryRun(tmux, out var listing, "list-panes", "-a", "-F",
                    "#{pane_id} #{pane_pid} #{pane_active} #{session_name}:#{window_index} #{pane_title}"))
            {
                return none;
            }

            var candidates = new List<SessionPresence.ViewerPane>();

            foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(' ', 5, StringSplitOptions.None);
                if (parts.Length < 5) continue;

                if (!SessionPresence.TitleSaysViewing(parts[4], sessionTitle)) continue;

                // The belt, paid for only by a pane whose title already matched —
                // usually none, and never more than a team's worth. Asked of the
                // pane's own process and its children, because an interactive
                // session runs as a child of the pane's shell while a team member
                // is the pane's process itself.
                if (!int.TryParse(parts[1], out var panePid)) continue;
                if (!RunsClaude(panePid)) continue;

                candidates.Add(new SessionPresence.ViewerPane(
                    Pane: parts[0],
                    Window: parts[3],
                    ActiveInItsWindow: parts[2] == "1",
                    ClaimedByAnother: panesClaimedByOthers?.Contains(parts[0]) ?? false));
            }

            return SessionPresence.ViewerAmong(candidates, ActiveWindowOfAttachedClient(tmux));
        }

        // Whether this pane is running Claude Code — as its own process, or as a
        // child of the shell it started.
        // Excluded from coverage: reads the live process table.
        [ExcludeFromCodeCoverage]
        private static bool RunsClaude(int panePid)
        {
            if (TryRun("/bin/ps", out var own, "-p", panePid.ToString(), "-o", "args=")
                && SessionPresence.LooksLikeClaudeBinary(FirstWord(own)))
            {
                return true;
            }

            // One level down is enough: a pane runs a shell, and the shell runs
            // the session. Walking further would start counting a session's own
            // subprocesses, which are not what is being displayed.
            if (!TryRun("/bin/ps", out var children, "-o", "args=", "--ppid", panePid.ToString()))
            {
                // BSD ps has no --ppid; ask for the whole table and filter. Kept
                // as a fallback rather than the only path so a platform that does
                // support it pays for one process instead of all of them.
                if (!TryRun("/bin/ps", out var all, "-eo", "ppid=,args=")) return false;

                foreach (var line in all.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    var split = trimmed.IndexOf(' ');
                    if (split <= 0) continue;
                    if (!int.TryParse(trimmed[..split], out var ppid) || ppid != panePid) continue;

                    if (SessionPresence.LooksLikeClaudeBinary(FirstWord(trimmed[(split + 1)..])))
                    {
                        return true;
                    }
                }

                return false;
            }

            foreach (var line in children.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (SessionPresence.LooksLikeClaudeBinary(FirstWord(line))) return true;
            }

            return false;
        }

        private static string? FirstWord(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) return null;

            var space = trimmed.IndexOf(' ');
            return space < 0 ? trimmed : trimmed[..space];
        }

        // The window the user's attached client is showing, as
        // "<session>:<index>", or null when nothing is attached or the lookup
        // fails. Shared by the viewer scan above and PlaceInTmux below, which ask
        // it for opposite reasons — one to recognise the pane the user is in, the
        // other to split into it.
        // Excluded from coverage: runs tmux.
        [ExcludeFromCodeCoverage]
        private static string? ActiveWindowOfAttachedClient(string tmux)
        {
            if (!TryRun(tmux, out var clients, "list-clients", "-F", "#{client_session}")) return null;

            var session = FirstLine(clients);
            if (session is null) return null;

            return TryRun(tmux, out var window,
                    "display-message", "-p", "-t", session, "#{session_name}:#{window_index}")
                ? FirstLine(window)
                : null;
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
        // Excluded from coverage: opens a terminal and runs `claude attach` in it.
        [ExcludeFromCodeCoverage]
        public static string? AttachSession(string sessionId, string cwd)
        {
            if (!OperatingSystem.IsMacOS()) return null;
            if (string.IsNullOrEmpty(sessionId)) return null;

            var jobId = JobIdOf(sessionId);

            // Step one of the ladder: does a client for this session already
            // exist? One question, asked once, of one population.
            //
            // It used to be asked twice, of two populations that mostly agreed.
            // AttachedAlready ran its own `ps -eo args=` and saw every `claude
            // attach` process on the machine but knew nothing about where any of
            // them was; ExistingAttachPane ran `tmux list-panes -a` plus a `ps`
            // per pane and saw only those running as the direct process of a pane
            // on the default socket; and Launched saw only what this app itself
            // opened, this run. All three were consulted as whether-checks, and
            // the case where the first two disagree — an attach in a window of its
            // own — is exactly the one that decided whether a click opened a
            // duplicate. So *whether* is settled here, once, by
            // SessionPresence.KnownAttachClient over the single scan;
            // ExistingAttachPane is demoted to answering *where*, which no set of
            // argv strings can; and Launched answers only "into which app", which
            // is neither of those questions. It is also no longer paid for on every
            // click: the common case is a set lookup rather than a pane walk.
            //
            // A terminal may be attached to this session and still not be
            // adoptable, which is why existence rather than addressability is the
            // right question: Locate needs a tty to hand back, and a window opened
            // by `open` has none, so it declines one that is plainly there.
            if (SessionPresence.KnownAttachClient(AttachedJobIds(fresh: true), jobId))
            {
                // In a pane: hand it back so the caller selects it, rather than
                // only bringing the app forward. The two look the same when you
                // happen to be looking at that window already and nothing like
                // each other when you aren't, which is why a second click once
                // appeared to do nothing.
                //
                // Focused rather than only selected even when it sits in another of
                // the user's windows, and that is a judgment rather than an
                // oversight. Round 6a's rule is "never move the user", and
                // switching windows is a move — but it is a move to a window they
                // created and have now asked for by clicking, and the case 6a is
                // actually about costs nothing either way: select-window onto the
                // window that is already current changes nothing, so a pane beside
                // them is reached without anything being disturbed.
                if (ExistingAttachPane(jobId) is { Length: > 0 } existing) return existing;

                // Attached, but in a terminal window of its own. Nothing to
                // select, so raising its app is the whole of what can be done —
                // into the app this app opened it into if it remembers, which is
                // the only thing Launched is still consulted for.
                string? remembered;
                lock (Gate) remembered = Launched.GetValueOrDefault(jobId);

                ActivateApp(remembered ?? TerminalApp());
                return null;
            }

            // Nothing is attached, so one has to be made. Into tmux when there is
            // a tmux to go into — *beside* the user if they are in one, which is
            // PlaceInTmux's whole job now. Opening a bare terminal window for
            // someone who lives in tmux puts the session somewhere their usual
            // navigation cannot reach it.
            if (ClaudeCommand("attach", jobId) is { } attachCommand
                && PlaceInTmux(attachCommand, cwd) is { Length: > 0 } pane)
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

                // An absolute path, not a bare name resolved by a login shell.
                // That was the original approach and it silently didn't work:
                // `zsh -lc` skips .zshrc (non-interactive), which is where a
                // PATH addition for ~/.local/bin normally lives, so the script
                // died with "command not found" whenever the app was launched
                // from Finder rather than a terminal. See ClaudeBinary.
                var claude = ClaudeBinary.Path;
                if (claude is null) return null;

                var quotedClaude = "'" + claude.Replace("'", "'\\''") + "'";

                // attach wants the *job* id, which is the first segment of the
                // session uuid and not the uuid itself — `claude logs` with a
                // full id answers "No job matching", with the short one it
                // prints the session's output. `claude agents --json` shows
                // both side by side ("id": "162e0b4b", "sessionId":
                // "162e0b4b-3c45-..."), which is where the relationship is
                // confirmed rather than assumed.
                var quotedId = "'" + jobId.Replace("'", "'\\''") + "'";

                // The cd matters even though attach names the session outright:
                // Ctrl+Z drops you back to a shell in this window, and the
                // useful place to land is the directory whose orb you clicked.
                File.WriteAllText(script,
                    "#!/bin/sh\n"
                    + "cd " + quoted + " || exit 1\n"
                    + "exec " + quotedClaude + " attach " + quotedId + "\n");

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

        // Opens a terminal attached to an existing tmux server, for a session
        // whose pane is alive in one nothing is attached to.
        //
        // The other half of "a click on an orb must produce a visible result".
        // AttachSession above answers the session that has no terminal anywhere;
        // this answers the one that has a *pane* and no screen — an agent-team
        // member in a detached `claude-swarm-<pid>` socket, which is what the
        // user was clicking when they reported orbs that "do nothing". Six
        // things went right on that path and the last one had nowhere to go.
        //
        // Here rather than in TerminalFocuser because opening a terminal on a
        // script file is this file's mechanism, and a second copy of it there
        // would be a second thing to keep right about which terminal app the
        // user has and how `open -a` behaves. The command itself is built by
        // TerminalScripts.TmuxAttachScript, which is pure and tested; what is
        // left here is the file and the launch.
        //
        // Deliberately no "already open?" guard, unlike AttachSession, and the
        // asymmetry is the point: the moment a client attaches to that server,
        // the ordinary focus path finds it and raises its window, so the second
        // click never reaches this. AttachSession needs a guard because a window
        // running `claude attach` is not discoverable that way.
        //
        // Excluded from coverage: writes a script and opens a terminal on it.
        [ExcludeFromCodeCoverage]
        public static bool AttachTmuxSocket(string tmuxBinary, string? socket, string cwd)
        {
            if (!OperatingSystem.IsMacOS()) return false;
            if (string.IsNullOrEmpty(tmuxBinary)) return false;

            try
            {
                System.IO.Directory.CreateDirectory(ClaudeBuddySettings.Directory);

                // Its own name, not open-agents-view.sh: the two can be launched
                // moments apart, and a shared file would mean the second write
                // deciding what the first window runs.
                var script = Path.Combine(ClaudeBuddySettings.Directory, "attach-tmux-socket.sh");

                File.WriteAllText(script, TerminalScripts.TmuxAttachScript(tmuxBinary, socket, cwd));
                File.SetUnixFileMode(script,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                psi.ArgumentList.Add("-a");
                psi.ArgumentList.Add(TerminalApp());
                psi.ArgumentList.Add(script);
                Process.Start(psi);
                return true;
            }
            catch
            {
                // Same contract as everything else on this path: failing to open
                // a window is a click that did nothing, never a crash.
                return false;
            }
        }

        // Runs the attach in a new window of whichever tmux session already has
        // a client, and hands back its pane. Nothing is selected or raised here
        // — returning the pane lets the caller reuse the focus path every other
        // pane goes through, which already knows how to find the client, pick
        // the window and bring its app forward.
        // Excluded from coverage: creates a real tmux window and sends keys to it.
        [ExcludeFromCodeCoverage]
        private static string? PlaceInTmux(string command, string cwd)
        {
            var tmux = ResolveTmux();
            if (tmux is null) return null;

            // A server with no client attached is a detached session: making a
            // window in it — let alone splitting one — would put the attach
            // somewhere with no screen, which is the same nowhere the orb already
            // pointed at. TerminalScripts.PlacementFor turns that into the
            // ATerminalWindow answer, and this returning null is how the caller
            // takes it.
            if (!TryRun(tmux, out var clients, "list-clients", "-F", "#{client_session}"))
            {
                return null;
            }

            var session = FirstLine(clients);

            // Which window that client is *showing*. The second question, and it
            // can fail on its own — hence the middle placement rather than one
            // bool. Shared with the viewer scan above rather than asked twice:
            // the two want the same fact for opposite reasons, one to recognise
            // the pane the user is in and one to split into it.
            var activeWindow = session is null ? null : ActiveWindowOfAttachedClient(tmux);

            // `command` arrives already quoted and already naming an absolute
            // claude, for the reason its builders record: tmux runs it with
            // `sh -c`, and the server's environment is whatever it happened to be
            // started with — which needn't include wherever `claude` lives, and
            // can't be assumed to match this app's. See ClaudeBinary for why
            // asking a login shell to resolve it isn't the fix it looks like.
            var args = TerminalScripts.PlacementFor(session, activeWindow) switch
            {
                // The whole of round 6a. "It's taking me to a different tmux
                // window - not this one" — so the conversation comes to them as a
                // pane in the window they are already in, and nothing moves.
                TerminalScripts.AttachPlacement.BesideTheUser =>
                    TerminalScripts.TmuxSplitArgs(null, activeWindow!, cwd, command),

                TerminalScripts.AttachPlacement.ItsOwnTmuxWindow =>
                    TerminalScripts.TmuxNewWindowArgs(null, session!, cwd, command),

                _ => null
            };

            if (args is null) return null;

            if (!TryRun(tmux, out var pane, args)) return null;

            var id = pane.Trim();
            return id.StartsWith('%') ? id : null;
        }

        // The first non-blank line of a tmux answer, trimmed. Both lookups above
        // ask for one field and can be handed several lines — `list-clients`
        // genuinely lists, and `display-message -p` appends a newline — and both
        // want the same thing from that.
        private static string? FirstLine(string output)
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }

            return null;
        }

        // Where tmux actually is. The app can't count on PATH — launched from
        // Finder it gets the bare system one — and unlike a session's status
        // file there's no recorded location to start from here, so this is the
        // same candidate list TerminalFocuser falls back to.
        // Excluded from coverage: probes the filesystem for a tmux binary.
        [ExcludeFromCodeCoverage]
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
        // Excluded from coverage: reads the frontmost terminal application from
        // the OS.
        [ExcludeFromCodeCoverage]
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
        // internal, not private: the short form the daemon uses, and the only
        // thing in this file that decides anything without asking the OS.
        internal static string JobIdOf(string sessionId)
        {
            var dash = sessionId.IndexOf('-');
            return dash > 0 ? sessionId[..dash] : sessionId;
        }

        // The tmux pane already running `claude attach <id>`, if there is one.
        //
        // Found through each pane's own process rather than anything this app
        // remembered, so it still works after a restart, and finds a pane the
        // user opened by hand just as well as one opened from an orb.
        // Excluded from coverage: lists live tmux panes.
        [ExcludeFromCodeCoverage]
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

                if (SessionPresence.SameJobId(jobId, words[2])) return parts[0];
            }

            return null;
        }

        private static void Forget(string cwd)
        {
            lock (Gate) Cache.Remove(cwd.TrimEnd('/'));
        }

        // `open -a` on a running app just brings it forward — the same trick
        // TerminalFocuser.ActivateApp uses, kept here rather than shared
        // because that one is private to a class this file must not depend on.
        // Excluded from coverage: activates a real application through osascript.
        [ExcludeFromCodeCoverage]
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
        // Excluded from coverage: walks the live process table.
        [ExcludeFromCodeCoverage]
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
        // Excluded from coverage: runs lsof against a live pid.
        [ExcludeFromCodeCoverage]
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

        // Excluded from coverage: runs ps against a live pid.
        [ExcludeFromCodeCoverage]
        private static string TtyOf(int pid)
        {
            if (!TryRun("/bin/ps", out var tty, "-o", "tty=", "-p", pid.ToString())) return "";

            tty = tty.Trim();
            return tty is "" or "??" ? "" : tty;
        }

        // Excluded from coverage: starts a subprocess and reads its output.
        [ExcludeFromCodeCoverage]
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
