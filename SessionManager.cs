using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    public class SessionStatus
    {
        [JsonPropertyName("state")]
        public string State { get; set; } = "idle";

        [JsonPropertyName("cwd")]
        public string Cwd { get; set; } = "";

        // What Claude Code calls this chat (its own generated title), empty
        // until a session has been going long enough to be named. Everything
        // user-facing prefers it and falls back to the cwd's folder name.
        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        // The session's /color, as a name ("green"). Empty when it hasn't been
        // given one. Drives the orb's border and letter — never its fill,
        // which stays reserved for state. See OrbWindow.ApplyAccent.
        [JsonPropertyName("color")]
        public string Color { get; set; } = "";

        // Set only on an agent-team member: the session id of the team lead
        // this session answers to. Empty for a lead and for any session that
        // isn't in a team, so a non-empty value is also the app's only "this
        // is a team member" signal — see OrbWindow.SetTeamRole and TeamLinks.
        //
        // Filled in by the app during the scan, from the member process's own
        // command line (AgentTeam.LeadOf) — the hooks know nothing about teams
        // and write no such field. It lives here anyway because everything that
        // consumes a session's state consumes it through this object.
        //
        // Nothing guarantees the lead is on screen: it can have ended, or been
        // filtered out, and the member outlives it either way. So this is a
        // hint, not a reference — everything that follows it checks.
        [JsonIgnore]
        public string Lead { get; set; } = "";

        // What this agent is called inside its team — MenuUX, HitReactSpec.
        // Empty for everything that isn't a team member, and filled in beside
        // Lead from the same read of the process. Everything user-facing
        // prefers it over the title, because a team's members all inherit the
        // team session's title and would otherwise be indistinguishable.
        [JsonIgnore]
        public string Agent { get; set; } = "";

        // Which CLI wrote this file, in its own words: "codex", or absent for
        // Claude Code.
        //
        // Serialized, unlike Lead and Agent and Kind, because unlike them the
        // hook is what knows the answer. That distinction is the whole of it:
        // ResetSessionToIdle reads a status file, changes the state and writes
        // the object back over it, so a field the *app* derives would appear in
        // a hook-owned file and vanish again on the next write — while a field
        // the *hook* owns has to survive exactly that round trip or a reset
        // turns a Codex session into a Claude Code one.
        //
        // Absent means Claude Code rather than unknown, so every status file
        // already on disk and every hook older than this reads correctly with
        // no migration.
        //
        // Not to be confused with Agent three fields up, which is what an
        // agent-team member is called. Both words are right and neither is
        // available for the other.
        [JsonPropertyName("cli")]
        public string Cli { get; set; } = "";

        // Which kind of thing this session is. Derived from Cli rather than
        // stored, by SourceOf, and never serialized — an enum here because it
        // is entirely internal and a typo should not compile, a string on the
        // wire because that is what a shell script can write.
        //
        // Defaulting to ClaudeCode means an object nobody has run SourceOf over
        // behaves as everything did before this existed.
        [JsonIgnore]
        public SessionSource Source { get; set; } = SessionSource.ClaudeCode;

        // Whether this session is a CLI running in a terminal on this machine.
        //
        // Most of the rules in this file that name ClaudeCode mean this and not
        // that: it has a process, it has a terminal you can be sent to, and it
        // has a transcript file on disk. Codex is all three. The ones that
        // genuinely mean Claude Code — agent teams, background jobs, the
        // projects directory — still say so, and each says why.
        [JsonIgnore]
        public bool IsLocalCli => Source is SessionSource.ClaudeCode or SessionSource.Codex;

        // What kind of gateway conversation this is. [JsonIgnore] for the same
        // reason Source is: it is derived from the gateway's answer during the
        // scan, and ResetSessionToIdle rewrites a status file from this object.
        [JsonIgnore]
        public SessionKind Kind { get; set; } = SessionKind.Unknown;

        // Whether the gateway's heartbeat is what wakes this session. [JsonIgnore]
        // for the same reason Kind is — derived during the scan, and
        // ResetSessionToIdle rewrites a status file from this object.
        //
        // Separate from Kind because it is a separate question: a heartbeat is
        // how a session gets woken, not what kind of conversation it is. See
        // OpenClawHeartbeat.
        [JsonIgnore]
        public bool Heartbeat { get; set; }

        // A stand-in orb for a channel, invented by this app rather than
        // reported by anything. It has no conversation of its own — it is the
        // thing the agents in that channel point at, so a room reads as one
        // place instead of as eight unrelated orbs that happen to share a badge.
        [JsonIgnore]
        public bool IsRoom { get; set; }

        // Where the session's terminal lives (macOS hook only; empty on
        // Windows or with an older hook script). See TerminalFocuser.
        [JsonPropertyName("term_program")]
        public string TermProgram { get; set; } = "";

        [JsonPropertyName("term_id")]
        public string TermId { get; set; } = "";

        [JsonPropertyName("tty")]
        public string Tty { get; set; } = "";

        // Set only when the session runs inside tmux (macOS hook). The pane id
        // ("%3") is server-unique and outlives window/session renames; the
        // socket pins which tmux server, and tmux_bin is where the hook found
        // the tmux binary (the app can't rely on PATH — launched from Finder
        // it doesn't have Homebrew's). See TerminalFocuser.
        [JsonPropertyName("tmux_socket")]
        public string TmuxSocket { get; set; } = "";

        [JsonPropertyName("tmux_pane")]
        public string TmuxPane { get; set; } = "";

        [JsonPropertyName("tmux_bin")]
        public string TmuxBin { get; set; } = "";

        // Windows hook only: PID of the terminal process that owns a window.
        [JsonPropertyName("term_pid")]
        public int TermPid { get; set; }

        // The claude process running this session. Recorded by both hooks; 0
        // from a hook older than this field, in which case liveness can't be
        // checked and only the lifetime timer applies. See SessionGone.
        [JsonPropertyName("session_pid")]
        public int SessionPid { get; set; }

        // Absolute path to the session's JSONL transcript file. The hooks
        // receive it from Claude Code's hook payload and pass it through so
        // the app can read conversation content (e.g. to speak the latest
        // turn aloud). Empty from hooks older than this field.
        [JsonPropertyName("transcript_path")]
        public string TranscriptPath { get; set; } = "";
    }

    // What produced a session. ClaudeCode and Codex are local processes that
    // fire the hook; OpenClaw is a conversation on a remote gateway with no
    // process, no terminal and no transcript file here. Almost every rule in
    // this file was written for the first and is wrong for the third, which is
    // why this exists rather than being inferred from which fields happen to be
    // empty.
    //
    // The split that matters most is not one-of-three but two-against-one — see
    // SessionStatus.IsLocalCli. Codex differs from Claude Code in what its
    // transcript looks like and in what it can be asked to do, not in whether
    // there is a terminal behind it.
    public enum SessionSource
    {
        ClaudeCode,
        Codex,
        OpenClaw,

        // A Claude Code session on another machine, seen through the bridge (see
        // RemoteControlBridge). Its own CLI is Claude Code, but it is not local
        // and there is no terminal here to focus, which is the distinction
        // IsLocalCli draws and the only one the rest of the app cares about.
        RemoteControl
    }

    // Watches %TEMP%\claude_buddy\<session_id>.txt (one per running Claude
    // Code session, written by ClaudeBuddyHook.ps1) and keeps one OrbWindow
    // per session in sync. A session is considered gone once its file is
    // deleted (SessionEnd hook, on graceful exit) or hasn't been touched in
    // StaleAfter (fallback for Ctrl+C and other ungraceful termination,
    // which SessionEnd isn't reliably delivered for).
    public class SessionManager
    {
        public static SessionManager? Instance { get; private set; }

        // How long an orb outlives its session's last hook write. Settable in the
        // settings window, and null means never — an orb then lasts until its
        // status file is deleted (SessionEnd) or you reset it by hand. Read per
        // scan rather than cached, so a change applies on the next tick.
        internal static TimeSpan? StaleAfter
        {
            get
            {
                var minutes = ClaudeBuddySettings.OrbLifetimeMinutes;
                return minutes == ClaudeBuddySettings.OrbLifetimeForever
                    ? null
                    : TimeSpan.FromMinutes(minutes);
            }
        }

        private readonly string _statusDir;

        public SessionManager()
            : this(Path.Combine(Path.GetTempPath(), "claude_buddy"))
        {
        }

        // The status directory, said out loud.
        //
        // It has always come from the temp path, and CLAUDE.md documents
        // TMPDIR=<dir> as the way to give a second instance its own fake
        // sessions — which still works, because that is what Path.GetTempPath
        // reads. This overload only makes the same seam nameable, so a test can
        // hand over a scratch directory without reaching for an environment
        // variable that the rest of the process is also reading.
        internal SessionManager(string statusDir)
        {
            _statusDir = statusDir;
        }

        private readonly Dictionary<string, OrbWindow> _windows = new();
        private readonly Dictionary<string, SessionStatus> _statuses = new();
        private readonly List<string> _order = new(); // stable stacking order

        private TrayController? _tray;

        // Orbs can be hidden from the tray menu; sessions keep being tracked
        // either way, so the tray icon and its menu stay accurate.
        public bool OrbsVisible { get; private set; } = ClaudeBuddySettings.ShowOrbs;

        private FileSystemWatcher? _watcher;
        private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };

        // The file the hooks look for to decide whether to colour a session.
        //
        // A marker rather than an argument in the hook command, because the
        // command is part of Codex's hooks.json and Codex hashes that file —
        // changing it marks the entries `modified` and stops them running until
        // the user re-approves the review. A setting that silently switched
        // every Codex orb off until someone noticed is not a setting.
        //
        // Written from the app rather than by the installer so it also needs no
        // re-wiring: the hooks stay exactly as they were and simply see a
        // different answer on their next call.
        private string AutoColorMarker => Path.Combine(_statusDir, ".auto-color");

        // Reconciled on every scan rather than only when the toggle is flipped.
        // The status directory lives in the temp path, which the OS is entitled
        // to clear out; a marker that vanished would turn the feature off with
        // nothing said. Two cheap file operations against a directory already
        // being enumerated.
        // internal: the marker file is how the hook learns about the colour
        // setting — the hook runs on every tool call and reading a setting there
        // would be an osascript each time — so what this writes is a contract
        // with a script, not an implementation detail.
        internal void SyncAutoColorMarker()
        {
            try
            {
                var wanted = ClaudeBuddySettings.AutoColorSessions;
                var present = File.Exists(AutoColorMarker);

                if (wanted && !present) File.WriteAllText(AutoColorMarker, "");
                else if (!wanted && present) File.Delete(AutoColorMarker);
            }
            catch
            {
                // Worst case the colour setting does not take effect, which is
                // not worth interrupting a scan for.
            }
        }

        // Excluded from coverage: creates the tray icon, subscribes the speech
        // engine, starts a FileSystemWatcher and a two-second Avalonia timer, and
        // opens the gateway connection. What it schedules is ScanAndUpdate, which
        // is tested directly against a scratch status directory.
        [ExcludeFromCodeCoverage]
        public void Start()
        {
            Instance = this;
            Directory.CreateDirectory(_statusDir);
            SyncAutoColorMarker();

            // Subscribed once for the app's lifetime, so no unsubscribe: this
            // object outlives every orb, which is the point — an orb closing must
            // not stop the other flyouts hearing about speech.
            TextToSpeech.StateChanged += OnSpeakStateChanged;

            _tray = new TrayController();

            StartWatching();

            _pollTimer.Tick += (_, _) => ScanAndUpdate();
            _pollTimer.Start();

            // Connects only if the user has turned it on and given it an
            // address; otherwise this returns having done nothing at all.
            OpenClawSessions.Restart();

            // Subscribed unconditionally, unlike OpenClawSessions.Restart above:
            // this only wires up an event, and starting the bridge is a separate,
            // deliberate act because it costs the user's quota. Nothing fires
            // here until something asks for it.
            //
            // Routed centrally rather than each chat session subscribing for
            // itself, because there is one bridge feeding all of them and a
            // message names only who it came from — so the fan-out belongs
            // wherever the sessions are already indexed by name, which is here.
            RemoteControlSessions.MessageReceived += OnRemoteMessage;
            RemoteControlSessions.WorkingChanged += OnRemoteWorkingChanged;

            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                ScanAndUpdate();
            };

            ScanAndUpdate();
        }

        // Excluded from coverage: creates a FileSystemWatcher on the status
        // directory. The poll timer covers the same job — the comment in the catch
        // below says so — and what either of them schedules is ScanAndUpdate,
        // which is tested directly against a scratch directory.
        [ExcludeFromCodeCoverage]
        private void StartWatching()
        {
            try
            {
                _watcher = new FileSystemWatcher(_statusDir, "*.txt")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += (_, _) => Dispatcher.UIThread.Post(RestartDebounce);
                _watcher.Created += (_, _) => Dispatcher.UIThread.Post(RestartDebounce);
                _watcher.Deleted += (_, _) => Dispatcher.UIThread.Post(RestartDebounce);
            }
            catch
            {
                // If the watcher can't be set up for some reason, the poll timer still covers us.
            }
        }

        // Excluded from coverage: restarts an Avalonia timer, and is only ever
        // called from the watcher's own events.
        [ExcludeFromCodeCoverage]
        private void RestartDebounce()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        // One status file, already parsed. Written is the file's mtime, which is
        // what "how long since this session last said anything" means throughout.
        internal sealed record ScanEntry(string SessionId, SessionStatus Status, DateTime Written);

        // Whether the user wants this kind of session tracked at all.
        //
        // OpenClaw's switch predates this and means something stronger — while
        // it is off the app opens no socket and generates no key — so it is
        // consulted where the gateway is asked, not here. These two are display
        // switches over files that are being written regardless.
        internal static bool EnabledFor(SessionSource source) => source switch
        {
            SessionSource.Codex => ClaudeBuddySettings.CodexEnabled,
            SessionSource.ClaudeCode => ClaudeBuddySettings.ClaudeCodeEnabled,
            _ => true
        };

        // Which CLI a status file came from, as the enum the rest of the app
        // branches on.
        //
        // One function, called at every point a SessionStatus is deserialized —
        // there are two, the scan below and ResetSessionToIdle — because Source
        // is [JsonIgnore] and therefore arrives as its default no matter what
        // the file said. Missing one of the two does not fail loudly; it
        // produces a Codex session that claims to be a Claude Code one until
        // the next scan corrects it, which is long enough to send a click to
        // the wrong place.
        //
        // Anything unrecognised is Claude Code, deliberately. A status file
        // written before this key existed has no "cli" at all and was Claude
        // Code, and a hook from some future version naming something this build
        // has never heard of is still, at worst, a local session in a terminal.
        internal static SessionSource SourceOf(SessionStatus status) =>
            string.Equals(status.Cli, "codex", StringComparison.OrdinalIgnoreCase)
                ? SessionSource.Codex
                : SessionSource.ClaudeCode;

        // Session ids one process has already moved on from.
        //
        // A Claude Code process mints a new session id every time you /clear,
        // resume, or start a new conversation, and the hook writes a *new*
        // <session-id>.txt for each. Nothing deletes the old ones: SessionEnd only
        // fires when the process exits, and it hasn't. So one terminal accumulates
        // several files that all name a live pid — the process-gone rule can't
        // touch them, and the lifetime timer was the only thing that ever would.
        //
        // That put duplicate orbs on screen for a whole lifetime. Observed with
        // four files on one pid: three orbs for one terminal, two of them showing
        // `generating`, because a superseded id keeps whatever state it was last
        // written with and no further hook ever corrects it. The stale one is
        // indistinguishable from real work.
        //
        // Within one process only the newest file is the live session, so the rest
        // go now. Two genuinely concurrent sessions are two processes with two
        // pids, so this can't collapse them into one — or so it was until Agent
        // View. Dispatching a background session from `←` doesn't fork a new OS
        // process; it starts a second conversation inside the one `claude` process
        // already running, so that process's pid now names several *simultaneously
        // live* status files, not a trail of ones the terminal has moved on from.
        // Observed directly: one pid, four files, three different titles, one of
        // them in a different cwd entirely (a worktree) — plainly not the same
        // conversation re-minting its session id.
        //
        // The two cases can't be told apart by mtime alone — a live background
        // session and last week's abandoned /clear both just sit there once
        // nothing is happening to them. `isLiveJob` (the caller passes
        // BackgroundJobs.IsLiveJob) is what actually knows: it asks the daemon's
        // own job list, which a leftover /clear id was never on and a running
        // background session still is. A non-newest entry only goes stale if that
        // check also says it's not live — so the original single-terminal case is
        // untouched, and a pid hosting several genuine Agent View sessions keeps
        // an orb for each.
        //
        // Passed in rather than called directly so this stays what the rest of
        // the file's rules are — pure and fast to test — instead of shelling out
        // to `claude agents --json` from inside a unit test.
        //
        // A pid of 0 means a hook older than the session_pid field. Grouping those
        // would put every such file in one bucket and drop all but one, so they're
        // left alone and keep the old behaviour — the same reason
        // ProcessLiveness.IsRunning treats 0 as alive.
        internal static HashSet<string> Superseded(List<ScanEntry> found, Func<string, bool> isLiveJob)
        {
            // Keyed by pid *and* which CLI, not by pid alone.
            //
            // A pid is only unique among the files one CLI wrote. Claude Code
            // running `codex exec` as a Bash tool is the case that breaks the
            // assumption: the nested codex process sits in a pipe with no tty
            // of its own, so the hook's walk can record the pid of the Claude
            // Code session that started it, and a Codex status file lands in
            // the same bucket as a live Claude Code one. Being newer it would
            // win, and this rule would delete the Claude orb — an orb the user
            // is watching, for the session that is doing the work.
            //
            // Two CLIs never share a real process, so pairing the source with
            // the pid costs nothing and cannot collapse two genuine sessions.
            var newest = new Dictionary<(int Pid, SessionSource Source), ScanEntry>();

            foreach (var entry in found)
            {
                var pid = (entry.Status.SessionPid, entry.Status.Source);
                if (pid.SessionPid <= 0) continue;

                // The ordinal tie-break only matters if two files somehow share an
                // mtime, and exists so the choice doesn't depend on the order the
                // directory happened to enumerate in.
                if (!newest.TryGetValue(pid, out var best)
                    || entry.Written > best.Written
                    || (entry.Written == best.Written
                        && string.CompareOrdinal(entry.SessionId, best.SessionId) > 0))
                {
                    newest[pid] = entry;
                }
            }

            var stale = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in found)
            {
                var pid = (entry.Status.SessionPid, entry.Status.Source);
                if (pid.SessionPid <= 0) continue;

                if (newest.TryGetValue(pid, out var best) && best.SessionId != entry.SessionId
                    && !isLiveJob(entry.SessionId))
                {
                    stale.Add(entry.SessionId);
                }
            }

            return stale;
        }

        // Whether a status file says anything about where its session can be
        // seen. A tty alone doesn't count here: it's the one field the walk
        // always fills in, and on its own it can name a tmux pane's pty, which
        // belongs to a detached server rather than to any window.
        internal static bool KnowsATerminal(SessionStatus status) =>
            !string.IsNullOrEmpty(status.TmuxPane)
            || !string.IsNullOrEmpty(status.TermProgram)
            || !string.IsNullOrEmpty(status.TermId)
            || status.TermPid != 0;

        // Terminal coordinates belong to the *process*, not to the session id,
        // so a status file that has none can take them from a sibling with the
        // same pid.
        //
        // Background sessions are why this is needed. Claude Code spawns one
        // through `claude daemon run`, which has no controlling terminal, so
        // the hook's walk up the process tree goes straight past it to the
        // interactive session that started it — recording *that* pid and tty
        // while carrying none of its environment, because the daemon has no
        // TMUX or TERM_PROGRAM to inherit. The result is a second, thinner file
        // for a pid that already had a good one, and being newer it wins the
        // superseded rule below and replaces a clickable orb with a dead one.
        //
        // Observed exactly that way: one pid with three files, the oldest
        // naming tmux pane %7 and the two newest naming nothing, and an orb
        // that had quietly stopped going anywhere. The inheritance is sound
        // because the pid is the same process: same window, same tmux pane.
        //
        // Only the empty fields are filled, so a file that knows its own
        // terminal is never overwritten by an older one's idea of it.
        internal static void InheritTerminalInfo(List<ScanEntry> found)
        {
            // Grouped by pid and source together, for the reason Superseded
            // is: a nested `codex exec` can record the pid of the Claude Code
            // session that spawned it, and this would then hand one CLI's
            // session the tmux pane of the other's. Clicking that orb would go
            // somewhere plausible and wrong, which is worse than a dead click.
            foreach (var group in found.Where(e => e.Status.SessionPid > 0)
                                       .GroupBy(e => (e.Status.SessionPid, e.Status.Source)))
            {
                var donor = group.Where(e => KnowsATerminal(e.Status))
                                 .OrderByDescending(e => e.Written)
                                 .FirstOrDefault();
                if (donor is null) continue;

                foreach (var entry in group)
                {
                    var status = entry.Status;
                    if (KnowsATerminal(status)) continue;

                    status.TermProgram = donor.Status.TermProgram;
                    status.TermId = donor.Status.TermId;
                    status.TermPid = donor.Status.TermPid;
                    status.TmuxSocket = donor.Status.TmuxSocket;
                    status.TmuxPane = donor.Status.TmuxPane;
                    status.TmuxBin = donor.Status.TmuxBin;

                    if (string.IsNullOrEmpty(status.Tty)) status.Tty = donor.Status.Tty;
                }
            }
        }

        // Why a status file gets no orb this scan, or Keep when it does.
        //
        // Named answers rather than a bool, because every one of these five
        // reasons has been wrong in a build somebody shipped — a Ctrl+C'd
        // session whose orb never went away, an orb per subagent, a Codex file
        // nothing on any path could remove. "Should this orb exist" is only
        // half a rule; which rule dropped it is the half that can be checked,
        // and the half a regression shows up in.
        internal enum ScanVerdict
        {
            Keep,

            // An id this process has already moved on from — see Superseded.
            Superseded,

            // The CLI process that wrote the file has exited.
            ProcessGone,

            // Quiet for longer than the user's "Keep orbs for" allows.
            Expired,

            // Nothing recorded that a click could be sent to.
            NoTerminal,

            // No process named, and the daemon does not know it as a job.
            NotALiveJob,
        }

        // The rules that need nothing but the file, its mtime, and whether its
        // process is alive.
        //
        // isRunning is passed in for the reason Superseded's isLiveJob is: the
        // real one is a kill(2) against a pid this machine may or may not have,
        // which is not a thing a unit test should be deciding.
        internal static ScanVerdict JudgeLiveness(
            string sessionId, SessionStatus status, DateTime written, DateTime now,
            TimeSpan? staleAfter, ISet<string> superseded, Func<int, bool> isRunning)
        {
            // Dropped here rather than left to the lifetime timer, which is the
            // only other thing that would ever catch it.
            if (superseded.Contains(sessionId)) return ScanVerdict.Superseded;

            // Gone is gone: if the claude process that wrote this file has
            // exited, no lifetime setting should keep its orb — that's the
            // Ctrl+C case, which fires no SessionEnd and so leaves the file
            // behind. This applies to `waiting` as well, which the timer
            // below deliberately never touches; an unanswered prompt whose
            // session was killed used to sit on screen indefinitely.
            if (status.SessionPid > 0 && !isRunning(status.SessionPid)) return ScanVerdict.ProcessGone;

            // A session waiting on you (permission prompt / question) never
            // goes stale on its own — no further hook fires until you
            // respond, so the file's mtime is frozen for as long as you're
            // away. Pruning it would hide the orb exactly when it matters
            // most. Use "Reset this session to idle" to clear a genuinely
            // abandoned one manually.
            // "generating" is exempt for gateway sessions for the same
            // reason "waiting" is exempt for local ones: it is the state
            // where hiding the orb is worst. A local session can't be caught
            // by this because its file is being rewritten as it works, which
            // a gateway session has no equivalent of.
            if (staleAfter is not null
                && status.State != "waiting"
                && !(status.Source == SessionSource.OpenClaw && status.State == "generating")
                && now - written > staleAfter)
            {
                return ScanVerdict.Expired;
            }

            return ScanVerdict.Keep;
        }

        // Whether this session is one of the shapes that is watched through a
        // `claude agents` window rather than through a terminal of its own, and
        // so worth hunting for one. See AgentTeamViewer, which finds it by
        // directory.
        //
        // A team lead can be a background session with no terminal of its own —
        // run inside `claude daemon run`, which has none, and reparented to
        // launchd once the session that started it went away. You watch it
        // through `claude agents`, a separate process in a real window that
        // nothing in the lead's own process tree points at, so the hook could
        // never have recorded it. A session with no pid of its own is in the
        // same bind and for the same reason, and the machinery is identical —
        // TryAdopt no-ops off macOS, without a cwd, and when no viewer for that
        // directory is running.
        //
        // Not for a gateway session: TryAdopt matches on cwd string equality
        // alone, and the machine running the gateway usually has the same
        // repositories checked out at the same paths. It would hand a remote
        // session the tmux pane of unrelated local work, and clicking that orb
        // would jump to it — worse than a dead click, because it looks like it
        // worked.
        //
        // Not for Codex either, for a plainer reason: the viewer this adopts is
        // a `claude agents` window, and there is no such thing to find for a
        // Codex session. The cwd-collision hazard above applies just as much,
        // and here both repositories would be on this machine.
        //
        // "pid <= 0" here was standing in for "this is a background agent", and
        // it stopped being true the moment the hook learned to record a
        // background agent's own pid instead of whatever ancestor owned a
        // terminal. The proxy is replaced by the thing it was approximating
        // rather than kept alongside it: a session with no pid is either a
        // background job — in which case isLiveJob says so — or a leftover that
        // should not be adopting a viewer window in the first place.
        //
        // Getting this wrong is not subtle. With the pid recorded and this
        // condition unchanged, adoption stopped running for every background
        // agent, so none of them found the `claude agents` window they are
        // watched through, and JudgeReachability below then dropped them for
        // having no terminal. One orb vanished on this machine before the cause
        // was obvious.
        internal static bool WantsAgentViewer(
            string sessionId, SessionStatus status,
            ISet<string> leadsWithLiveAgents, Func<string, bool> isLiveJob) =>
            status.Source == SessionSource.ClaudeCode
            && !KnowsATerminal(status)
            && (leadsWithLiveAgents.Contains(sessionId)
                || status.SessionPid <= 0
                || isLiveJob(sessionId));

        // Whether an orb for this session would go anywhere when it was
        // clicked. Asked after adoption above, which is what can give a
        // background lead a terminal it had none of a moment earlier.
        internal static ScanVerdict JudgeReachability(
            string sessionId, SessionStatus status,
            ISet<string> leadsWithLiveAgents, Func<string, bool> isLiveJob)
        {
            // A session with no terminal recorded at all can't be jumped
            // to, so an orb for it is a dead click. This is what headless and
            // bridged invocations look like: no tty, no terminal program, no
            // tmux pane, no Windows terminal pid. An interactive session
            // always has at least one of those.
            //
            // Except a lead with live agents, which is exempt whether or not
            // the viewer above was found: agents on screen pointing at
            // nothing is a worse lie than an orb you might not be able to
            // click. It's also a session you can see is running, which is
            // what an orb is for.
            // A gateway session has none of these and never will — it has
            // no terminal anywhere, which is the point of it. Left ungated
            // this rule alone drops every OpenClaw orb, every scan.
            // A live background job is exempt for the same reason a lead
            // with live agents is: it has no terminal of its own by nature,
            // not because it is a leftover. That used to be covered for free
            // by the pid test below — a background agent recorded no pid — and
            // is now stated, because the hook records its real pid.
            if (status.IsLocalCli
                && string.IsNullOrEmpty(status.Tty)
                && string.IsNullOrEmpty(status.TermProgram)
                && string.IsNullOrEmpty(status.TmuxPane)
                && status.TermPid == 0
                && !leadsWithLiveAgents.Contains(sessionId)
                && !(status.Source == SessionSource.ClaudeCode && isLiveJob(sessionId))
                && status.SessionPid > 0)
            {
                return ScanVerdict.NoTerminal;
            }

            // A session recording no pid is only worth an orb if it's a
            // background job that is still running.
            //
            // "No pid" is a wider net than it first appears. A background
            // agent has none — which is what the exemption above is for —
            // but neither does a subagent, and neither does a status file
            // whose session ended without clearing it. All three write the
            // same "idle" state, so on disk they're identical; only the
            // daemon knows which is which. Taking pid-less to mean
            // "background agent" put a permanent orb on screen for every
            // subagent anyone spawned, and with the lifetime set to forever
            // they only accumulated.
            //
            // Asked of nothing else, so an ordinary session never pays for
            // the lookup, and a listing that can't be read keeps every orb
            // — see BackgroundJobs.
            // Likewise: a gateway session records no pid, so this would ask
            // the local daemon about a session it has never heard of — once
            // per scan, per session — and drop the orb when the answer came
            // back "not a job", which it always would.
            if (status.Source == SessionSource.ClaudeCode
                && status.SessionPid <= 0
                && !isLiveJob(sessionId))
            {
                return ScanVerdict.NotALiveJob;
            }

            // The same rule for Codex, with the exemption removed rather
            // than reused. `claude agents` is what makes a pid-less Claude
            // Code session worth an orb, and Codex has no equivalent — no
            // background job to attach to, nothing to ask. So a Codex file
            // naming no process is a session that ended without clearing
            // up, and an orb for it is a permanent dead click.
            //
            // Written out rather than folded into the rule above because
            // leaving Codex to fall through it was a real hole: the
            // no-terminal rule two blocks up requires a pid to fire, and
            // the liveness check treats pid 0 as alive, so nothing else
            // would ever have removed it. With the orb lifetime set to
            // "forever", nothing would have removed it at all.
            if (status.Source == SessionSource.Codex && status.SessionPid <= 0)
            {
                return ScanVerdict.NotALiveJob;
            }

            return ScanVerdict.Keep;
        }

        // One pass over the status directory: read everything, decide what
        // deserves an orb, and reconcile the windows, the arrows and the tray
        // with the answer.
        //
        // Internal rather than private so a test can drive a pass directly
        // instead of waiting on the two-second timer that normally calls it.
        // Nothing outside this class should: it is idempotent, but it is also
        // the whole of the scan, and calling it from anywhere but the timer, the
        // watcher's debounce or Start would mean two passes racing over the same
        // dictionaries on the same thread's re-entrancy.
        internal void ScanAndUpdate()
        {
            SyncAutoColorMarker();

            var seen = new HashSet<string>();
            var now = DateTime.UtcNow;
            bool setChanged = false;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(_statusDir, "*.txt");
            }
            catch
            {
                files = Enumerable.Empty<string>();
            }

            // Read everything before judging any of it: whether a file is live can
            // depend on the *other* files — see Superseded.
            var found = new List<ScanEntry>();
            foreach (var file in files)
            {
                SessionStatus? status;
                DateTime written;
                try
                {
                    written = File.GetLastWriteTimeUtc(file);
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    status = System.Text.Json.JsonSerializer.Deserialize<SessionStatus>(stream);
                }
                catch
                {
                    continue; // mid-write or vanished; retry next tick
                }

                if (status is null) continue;

                status.Source = SourceOf(status);

                // A CLI switched off is ignored, not unwired. Its hooks keep
                // writing status files — they are the user's own config, and a
                // display switch that rewrote it would be a surprise, and for
                // Codex would cost them their hook trust on top. Skipping here
                // rather than later means everything downstream, including the
                // pid grouping and the tray, behaves as though those sessions
                // were not running.
                if (!EnabledFor(status.Source)) continue;

                found.Add(new ScanEntry(Path.GetFileNameWithoutExtension(file), status, written));
            }

            // The gateway's sessions join the same list the status files
            // produced, so everything downstream — ordering, stacking, pinning,
            // the tray, the removal pass — stays a single code path. Empty and
            // free when the feature is off.
            //
            // Written is the session's own last activity, deliberately, not the
            // time of this scan. A gateway lists every conversation it has ever
            // had (59 on the machine this was built against, of which two had
            // been touched in the last five minutes), so stamping "now" would
            // put an orb on screen for all of them forever. Using real activity
            // lets the user's own "Keep orbs for" setting do the filtering, with
            // no new concept and no second timeout to reason about.
            var gatewaySessions = OpenClawSessions.Snapshot();

            // Channel -> the room orb to stand for it. Keyed by
            // OpenClawSessionKind.RoomOf, so every agent in a channel agrees.
            var rooms = new Dictionary<string, (string Title, DateTime Activity, bool Working)>(
                StringComparer.Ordinal);

            foreach (var session in gatewaySessions)
            {
                var status = new SessionStatus
                {
                    Source = SessionSource.OpenClaw,
                    State = session.State,
                    Title = session.Title,

                    // Per *agent*, not per session, so an agent's DM and its two
                    // channels read as one thing in three places rather than as
                    // three unrelated orbs. Derived from the id, so it is the
                    // same colour next launch without anything being stored.
                    //
                    // This field used to be handed session.Channel, which is a
                    // channel name and matches no colour Claude Code knows — so
                    // every gateway orb fell through to the plain ring, and six
                    // of them were indistinguishable.
                    // Asked for rather than computed here: an agent's ring and
                    // a chat bubble from that agent have to agree, so exactly
                    // one place decides. See OpenClawSessions.ColourForAgent.
                    Color = OpenClawSessions.ColourForAgent(
                        OpenClawSessions.AgentIdOf(session.Key) ?? session.Key),
                    Kind = session.Kind,
                    Heartbeat = session.Heartbeat,
                };

                // Namespaced because these ids share a dictionary with Claude
                // Code's UUIDs, and because a gateway key contains colons and
                // slashes that ResetSessionToIdle would otherwise splice into a
                // file path.
                // Which room this is standing in, if any. The room orb itself is
                // added below, once, however many agents point at it.
                var room = OpenClawSessionKind.RoomOf(session.Key);
                if (room is not null)
                {
                    status.Lead = RoomId(room);

                    if (!rooms.TryGetValue(room, out var seenRoom)
                        || session.LastActivity > seenRoom.Activity)
                    {
                        rooms[room] = (RoomTitle(session.Title), session.LastActivity,
                            (seenRoom.Working || session.State == "generating"));
                    }
                    else if (session.State == "generating")
                    {
                        rooms[room] = (seenRoom.Title, seenRoom.Activity, true);
                    }
                }

                found.Add(new ScanEntry("openclaw:" + session.Key, status, session.LastActivity));
            }

            // One orb per channel, for the agents in it to point at. Invented
            // here rather than reported by the gateway, which has no notion of a
            // room as a thing — it has a session per agent per channel, and
            // eight of those on screen is eight orbs with nothing saying they
            // are the same conversation.
            foreach (var (key, room) in rooms)
            {
                found.Add(new ScanEntry(
                    RoomId(key),
                    new SessionStatus
                    {
                        Source = SessionSource.OpenClaw,
                        IsRoom = true,
                        Title = room.Title,

                        // For the panel's header chip. The orb itself skips the
                        // badge — it *is* the channel — but the panel is a
                        // window onto a conversation and saying which kind is
                        // the same help it is anywhere else.
                        Kind = SessionKind.Channel,

                        // Busy while anyone in it is, which is what a room
                        // being "active" means.
                        State = room.Working ? "generating" : "idle",

                        // And its own colour. This used to stay empty on the
                        // reasoning that a ring identifies an agent and a room
                        // is not one — true while one room was on screen, and
                        // wrong with several, where every room is a dark circle
                        // with a # on it and only the badge distinguishes them,
                        // which says what they are rather than which. See
                        // OpenClawSessions.ColourForRoom for why it is keyed on
                        // the room rather than dealt from the agents' pool.
                        Color = OpenClawSessions.ColourForRoom(key),
                    },
                    room.Activity));
            }

            // Claude Code sessions on the user's other machines, seen through
            // the bridge. Empty unless the feature is on *and* something has
            // asked for the bridge — see RemoteControlSessions.EnsureStarted for
            // why merely enabling it isn't enough.
            //
            // Much simpler than the gateway branch above, and for a reason worth
            // stating: there are no rooms, no leads and no colour pool here. A
            // remote session is one Claude Code session on one machine, so the
            // only thing being invented is the namespaced id.
            foreach (var remote in RemoteControlSessions.Snapshot())
            {
                found.Add(new ScanEntry(
                    remote.Key,
                    new SessionStatus
                    {
                        Source = SessionSource.RemoteControl,

                        // The peer list's own word, translated into the two
                        // states an orb draws. Anything that isn't recognisably
                        // work counts as idle: an orb that spins forever because
                        // a label changed upstream is worse than one that never
                        // spins.
                        State = remote.Working ? "generating" : "idle",

                        // Its name on the other machine, which is all the peer
                        // list gives us — no hostname, no path. That is thin for
                        // a title, and deliberately not padded out with a guess:
                        // see docs/remote-control-findings.md.
                        Title = remote.Name,

                        // What the session itself said, when it has been asked
                        // and answered — a remote session's colour cannot be
                        // derived here, because a peer row carries neither the
                        // transcript /color writes into nor the cwd auto-colour
                        // hashes. See RemoteControlSessions.AskForMissingColorsAsync.
                        //
                        // Falls back to a colour hashed from the name, which is
                        // stable per session and unrelated to whatever it wears
                        // at home — better than every remote orb being identical
                        // while the answer is still in flight, or if it never
                        // comes.
                        Color = remote.Color ?? OpenClawSessions.ColourForAgent(remote.Name),

                        Kind = SessionKind.Remote,
                    },
                    remote.Seen));
            }

            InheritTerminalInfo(found);

            var superseded = Superseded(found, BackgroundJobs.IsLiveJob);

            // Sessions that live agents name as their lead. Used for two things
            // below, and for nothing else — in particular *not* to excuse a
            // lead from the lifetime timer, which is the user's setting to make.
            var leadsWithLiveAgents = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in found)
            {
                if (superseded.Contains(entry.SessionId)) continue;
                if (!ProcessLiveness.IsRunning(entry.Status.SessionPid)) continue;

                var agentLead = AgentTeam.LeadOf(entry.Status.SessionPid);
                if (!string.IsNullOrEmpty(agentLead) && agentLead != entry.SessionId)
                {
                    leadsWithLiveAgents.Add(agentLead);
                }
            }

            // Note on what is deliberately *not* here: agent teams get no
            // exemption from the lifetime timer, in either direction. A quiet
            // lead is pruned like any other quiet session, and so is a member
            // whose agent has finished and gone silent — even though its
            // process is still alive and its file's mtime is frozen because
            // nothing fires a hook for a session that isn't doing anything.
            //
            // Both were tried and taken back out. "Keep orbs for" is the user's
            // statement about how long a quiet session stays on screen, and a
            // team is not a special enough case to quietly overrule it; a team
            // that should stay visible is a reason to set a longer lifetime,
            // not a reason for the app to keep its own exceptions. The visible
            // consequence is that a team shows the agents that are working,
            // and an arrow disappears with the lead it pointed at.

            foreach (var (sessionId, status, written) in found)
            {
                // Two verdicts rather than one, with the viewer hunt sitting
                // between them, because the order is load-bearing in both
                // directions: adoption can hand a background lead the very
                // terminal JudgeReachability is about to ask for, and it walks
                // the process table to do it, which is far too expensive to
                // spend on a session JudgeLiveness has already dropped.
                if (JudgeLiveness(sessionId, status, written, now, StaleAfter,
                                  superseded, ProcessLiveness.IsRunning) != ScanVerdict.Keep)
                {
                    continue;   // removed in the pass below
                }

                if (WantsAgentViewer(sessionId, status, leadsWithLiveAgents, BackgroundJobs.IsLiveJob))
                {
                    AgentTeamViewer.TryAdopt(status);
                }

                if (JudgeReachability(sessionId, status, leadsWithLiveAgents,
                                      BackgroundJobs.IsLiveJob) != ScanVerdict.Keep)
                {
                    continue;   // removed in the pass below
                }

                seen.Add(sessionId);

                // Whether this session is an agent-team member, and whose. Read
                // from its process rather than its status file — see AgentTeam.
                // Asked after the liveness rules above so a dead session never
                // costs a lookup.
                var membership = status.Source == SessionSource.ClaudeCode
                    ? AgentTeam.Of(status.SessionPid)
                    : AgentTeam.None;

                // Guarded, where it used to run for everything. A gateway
                // session has no process to ask, so `default` came back and this
                // assigned Lead = "" — which silently erased the room a channel
                // session had already been put in, a few lines earlier and in
                // another file. Teams and rooms are different things that happen
                // to use the same field.
                if (status.Source == SessionSource.ClaudeCode)
                {
                    status.Lead = membership.Lead == sessionId ? "" : membership.Lead;
                    status.Agent = string.IsNullOrEmpty(status.Lead) ? "" : membership.Name;
                }

                // The colour Claude Code gave this agent when the team was
                // built. Only used when the session hasn't set one itself: a
                // `/color` run inside the agent is a deliberate choice and
                // outranks the assigned one. This is not the automatic per-
                // process accent the README declines to guess at — that one is
                // nowhere on disk, whereas this was passed to the process on
                // its command line and is what Claude Code's own team UI shows.
                if (string.IsNullOrEmpty(status.Color)) status.Color = membership.Color;

                // Joining or leaving a team changes where this orb belongs in
                // the stack, not just what it looks like, so it has to reflow.
                // It isn't a set change and would otherwise go unnoticed — a
                // team member can appear before its lead does.
                if (_statuses.TryGetValue(sessionId, out var previous)
                    && previous.Lead != status.Lead)
                {
                    setChanged = true;
                }

                _statuses[sessionId] = status;

                var isNew = !_windows.TryGetValue(sessionId, out var window);
                if (isNew)
                {
                    window = new OrbWindow(sessionId);
                    _windows[sessionId] = window;
                    _order.Add(sessionId);
                    if (OrbsVisible) window.Show();
                    setChanged = true;
                }

                window!.UpdateFrom(status);

                // The chat panel's view of "waiting for a permission prompt"
                // comes from here rather than from the transcript, because the
                // dialog never reaches the transcript. This is the only place
                // that state arrives.
                if (_chats.TryGetValue(sessionId, out var chat)) chat.UpdateStatus(status);

                // After UpdateFrom, so the window is already showing something
                // if the position turns out to be unusable. Before the reflow
                // below, which steps over whatever this pins.
                if (isNew) RestoreOrbPosition(window, status);
            }

            var gone = _windows.Keys.Where(id => !seen.Contains(id)).ToList();
            foreach (var id in gone)
            {
                _windows[id].Close();
                _windows.Remove(id);
                _statuses.Remove(id);
                _order.Remove(id);
                setChanged = true;

                // The watcher goes with the orb. A session that has ended has a
                // transcript that will never grow again, and a FileSystemWatcher
                // per dead session is a handle leak measured in days.
                if (_chats.Remove(id, out var chat)) chat.Dispose();
            }

            if (setChanged)
            {
                ReflowPositions();
            }

            RescueOffscreenOrbs();

            // Not only on setChanged: an orb that was already on screen can
            // gain or lose a lead, and the arrows have to follow either way.
            UpdateTeamLinks();

            UpdateTray();
        }

        private void UpdateTray()
        {
            // Feed the tray in stacking order so the menu reads top-to-bottom
            // like the orbs do on screen.
            _tray?.Update(DisplayOrder()
                .Select(id => new TrayController.SessionEntry(id, _statuses[id]))
                .ToList());
        }

        // --- agent teams ------------------------------------------------------

        // Stacking order with each team gathered: a lead, then its members,
        // then whatever came next. _order stays in first-seen order and is
        // still the tie-breaker between teams — this only moves members up
        // behind their lead, which keeps a team's arrows short and stops them
        // crossing the unrelated sessions that happened to appear in between.
        //
        // A member whose lead isn't on screen (ended, or filtered out) is left
        // exactly where it was: there's nothing to gather it under.
        private List<string> DisplayOrder() =>
            GatherTeams(_order, _statuses.ContainsKey,
                        id => _statuses.TryGetValue(id, out var status) ? status.Lead : null);

        // The gathering itself, with the dictionaries behind it reduced to two
        // questions: is this id still tracked, and what does it say its lead is?
        //
        // Two questions and not one, which is the whole shape of it. "Tracked"
        // and "names a lead" are separate facts and this rule turns on both: an
        // id in _order with no status behind it — which happens for one pass
        // during the removal pass — is not laid out at all, while a member
        // whose *lead* has ended stays exactly where it was, because there is
        // nothing to gather it under. Folding them into one nullable answer
        // reads tidier and drops an orb: SessionStatus.Lead is itself allowed
        // to be null for a session with no pid to ask about, and a null lead
        // would then be indistinguishable from a session that is gone.
        //
        // Separated out so the ordering can be checked without windows,
        // statuses or a scan behind it; the shape it produces is what decides
        // which orb sits where in the stack and how far a team's arrows have to
        // reach.
        internal static List<string> GatherTeams(
            IReadOnlyList<string> order, Func<string, bool> tracked, Func<string, string?> leadOf)
        {
            var byLead = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var id in order)
            {
                if (!tracked(id)) continue;

                var lead = leadOf(id);
                if (string.IsNullOrEmpty(lead) || lead == id) continue;   // leads nobody, or itself
                if (!tracked(lead)) continue;                             // its lead isn't on screen

                if (!byLead.TryGetValue(lead, out var members))
                {
                    byLead[lead] = members = new List<string>();
                }
                members.Add(id);
            }

            var gathered = new HashSet<string>(byLead.Values.SelectMany(m => m), StringComparer.Ordinal);

            var gatheredOrder = new List<string>(order.Count);
            foreach (var id in order)
            {
                if (!tracked(id)) continue;
                if (gathered.Contains(id)) continue;   // emitted under its lead below

                gatheredOrder.Add(id);
                if (byLead.TryGetValue(id, out var members)) gatheredOrder.AddRange(members);
            }

            // A team whose lead is itself a member of another team would leave
            // its members unemitted above; nesting isn't a thing today, but a
            // dropped orb would be a silent one, so sweep up anything missed.
            foreach (var id in order)
            {
                if (tracked(id) && !gatheredOrder.Contains(id)) gatheredOrder.Add(id);
            }

            return gatheredOrder;
        }

        // The last known state of another session — what an orb needs to hand
        // its team lead to TerminalFocuser. Null for a session that isn't
        // tracked, including the empty id a non-member passes in.
        // A session this orb can hold a conversation with, or null when its
        // click means something else. Null is the whole of the "not one of
        // those" signal the orb understands — it deliberately knows nothing
        // about which source a session came from, or whether the feature is on.
        public IRemoteChatSession? RemoteChatFor(string sessionId)
        {
            if (!_statuses.TryGetValue(sessionId, out var status)) return null;

            // A room's conversation is its members' transcripts merged, since
            // the gateway has no room to ask about. Assembled from the same
            // Lead field the arrows are drawn from, so what opens is exactly
            // what the orb is pointing at.
            if (status.IsRoom)
            {
                // Asked of the gateway's own list rather than assembled from
                // the orbs on screen. The orbs are filtered by "show sessions
                // active within", which is a question about what is worth
                // drawing — and an agent that spoke an hour ago is still in the
                // room. Built from the orbs, its half of the conversation went
                // missing and its messages showed up as yours.
                const string RoomPrefix = "openclaw:room:";
                var members = OpenClawSessions.MembersOfRoom(sessionId[RoomPrefix.Length..]);

                return OpenClawSessions.RoomChatFor(sessionId, status.Title, members);
            }

            if (status.Source == SessionSource.OpenClaw)
                return OpenClawSessions.ChatFor(sessionId, status.Title);

            // A session on another machine. Cached like the local ones below,
            // and for a stronger reason: this conversation exists *only* here.
            // A local panel rebuilt from scratch re-reads the transcript and
            // loses nothing but scroll position, whereas rebuilding this one
            // would throw away the entire exchange — there is no file on this
            // machine to read it back from.
            if (status.Source == SessionSource.RemoteControl)
            {
                if (_remoteChats.TryGetValue(sessionId, out var existingRemote)) return existingRemote;

                // The peer name, recovered from the id the scan minted. Taken
                // from the id rather than the title so it survives a title that
                // gets prettied up later — the name is what SendMessage
                // addresses, and it has to stay exact.
                // "rc:<account>:<name>". Split from the right, because a session
                // name can itself contain a colon and an account directory
                // cannot — so the *first* separator after the prefix is the one
                // that divides them.
                var rest = sessionId.StartsWith("rc:", StringComparison.Ordinal) ? sessionId[3..] : "";
                var split = rest.IndexOf(':');
                var account = split > 0 ? rest[..split] : ClaudeBuddySettings.DefaultRemoteControlProfileDir;
                var remoteName = split > 0 ? rest[(split + 1)..] : status.Title;

                var remote = new RemoteControlChatSession(sessionId, account, remoteName);
                _remoteChats[sessionId] = remote;

                // Opening the panel counts as asking for the bridge, so the
                // conversation is usable the moment it appears rather than only
                // after the first message is typed.
                RemoteControlSessions.EnsureStarted();

                return remote;
            }

            // Both local CLIs from here down. Which transcript format to read
            // and which pair of settings governs it is the whole of the
            // difference, and it lives in CliChatFormat.
            if (!CliChatFormat.For(status.Source).ChatEnabled()) return null;

            // Cached rather than made per click: the session owns a file watcher
            // and a byte offset into a transcript, and rebuilding it every time
            // the panel opened would re-read the tail and lose the scrollback
            // someone had already paged in.
            if (_chats.TryGetValue(sessionId, out var existing))
            {
                existing.UpdateStatus(status);
                return existing;
            }

            var chat = new LocalCliChatSession(sessionId, status);
            _chats[sessionId] = chat;
            chat.Start();
            return chat;
        }

        // Local chat sessions, by session id. Only ever populated by a click —
        // there is no reason to watch a transcript nobody is reading — and
        // emptied with the orb.
        private readonly Dictionary<string, LocalCliChatSession> _chats = new(StringComparer.Ordinal);

        // Separate from _chats above because these are not disposable and are
        // not keyed to a status file's lifetime. A remote conversation outlives
        // the orb: the bridge idling out empties the snapshot and the orb goes,
        // but what was said should still be there when it comes back.
        private readonly Dictionary<string, RemoteControlChatSession> _remoteChats =
            new(StringComparer.Ordinal);

        // Namespaced away from both Claude Code's UUIDs and the gateway's own
        // keys, because it is neither: nothing on the gateway answers to it.
        internal static string RoomId(string roomKey) => "openclaw:room:" + roomKey;

        // A member's title is "Lilibeth — general"; the room is just "general".
        internal static string RoomTitle(string sessionTitle)
        {
            var dash = sessionTitle.IndexOf(" — ", StringComparison.Ordinal);
            return dash > 0 ? sessionTitle[(dash + 3)..].Trim() : sessionTitle;
        }

        // A message from a session on another machine, handed to the one
        // conversation it belongs to.
        //
        // Delivered only to an already-open conversation, and that is the right
        // shape rather than a gap: this channel is a reply to something someone
        // typed here, so an inbound message with no panel behind it would be a
        // reply to nothing. A remote session cannot start a conversation.
        private void OnRemoteMessage(BridgeProtocol.InboundMessage message)
        {
            // Keyed the way the scan mints ids, so this is a lookup rather than
            // a walk — and it means a remote session named the same as a local
            // one cannot be delivered to the local one's panel.
            // Offered to every open remote conversation and filtered by each.
            // A direct dictionary hit would need the exact key, and the peer
            // list's casing is upstream's to change — so the sessions decide,
            // each checking both the name and the account it belongs to.
            foreach (var candidate in _remoteChats.Values) candidate.OnInbound(message);
        }

        // A remote session started or stopped working. The orb learns this from
        // the snapshot on the next scan; this is for the panel, which has no scan
        // to wait on and would otherwise show a sent message and nothing else
        // for however long the other machine takes.
        private void OnRemoteWorkingChanged(string sessionKey, bool working)
        {
            if (_remoteChats.TryGetValue(sessionKey, out var chat)) chat.SetWorking(working);
        }

        public SessionStatus? StatusFor(string? sessionId) =>
            string.IsNullOrEmpty(sessionId) ? null : _statuses.GetValueOrDefault(sessionId);

        // The orbs that follow this one when it's dragged: the members of the
        // team it leads. Empty for everything else, including a member — a
        // member is dragged on its own, which is how you pull one out of the
        // group to look at it.
        public List<OrbWindow> MembersOf(string leadSessionId)
        {
            var members = new List<OrbWindow>();

            foreach (var id in _order)
            {
                if (!_statuses.TryGetValue(id, out var status)) continue;
                if (status.Lead != leadSessionId || id == leadSessionId) continue;
                if (_windows.TryGetValue(id, out var window)) members.Add(window);
            }

            return members;
        }

        private void UpdateTeamLinks()
        {
            TeamLinks.SetVisible(OrbsVisible);

            var pairs = new List<(OrbWindow Member, OrbWindow Lead)>();
            foreach (var (id, status) in _statuses)
            {
                if (string.IsNullOrEmpty(status.Lead) || status.Lead == id) continue;
                if (!_windows.TryGetValue(id, out var member)) continue;
                if (!_windows.TryGetValue(status.Lead, out var lead)) continue;

                pairs.Add((member, lead));
            }

            TeamLinks.Update(pairs);
        }

        public void SetOrbsVisible(bool visible)
        {
            if (OrbsVisible == visible) return;
            OrbsVisible = visible;
            ClaudeBuddySettings.ShowOrbs = visible;

            foreach (var window in _windows.Values)
            {
                if (visible) window.Show();
                else window.Hide();
            }

            // Arrows go with the orbs they join — two invisible orbs joined by a
            // visible arrow is a line from nowhere to nowhere.
            TeamLinks.SetVisible(visible);

            if (visible) ReflowPositions();
            UpdateTray();
        }

        // A colour change isn't a session change, so nothing on the scan path
        // would notice one: ScanAndUpdate calls UpdateFrom, and UpdateFrom only
        // calls ApplyState when the state actually differs. Same shape as
        // SetOrbsVisible above — whoever changed the setting says so.
        //
        // Hidden orbs get walked too. They're still loaded windows, and they'll
        // be right when they come back.
        public void ReapplyStateColors()
        {
            foreach (var window in _windows.Values)
            {
                window.ReapplyStateColors();
            }

            _tray?.ReapplyStateColors();
        }

        // Same shape as ReapplyStateColors, for the "Two-letter initials"
        // toggle: a cosmetic setting change isn't a session change, so
        // nothing on the scan path would otherwise notice it until whatever
        // orb's session next fires a hook.
        public void ReapplyGlyphs()
        {
            foreach (var window in _windows.Values)
            {
                window.ReapplyGlyph();
            }
        }

        // Speech is one global thing, not one per orb: whichever orb started it,
        // every open flyout's speak button has to agree about whether something
        // is being read. Broadcasting from here rather than from the orb that
        // clicked, for the same reason ReapplyGlyphs lives here — this class is
        // already what owns "one change, every orb".
        //
        // Posted to the UI thread because the state changes on whatever thread
        // the speech engine's process exited on.
        private void OnSpeakStateChanged(TextToSpeech.SpeakState state)
        {
            // Inside the Post, not before it. TextToSpeech raises this from
            // whichever thread noticed the speech engine change state — the
            // reason the orb updates below were already marshalled — and the
            // panel's own glyph is an Avalonia control like any other, so
            // touching it from there took the app down mid-sentence.
            Dispatcher.UIThread.Post(() =>
            {
                ChatPanel.SetSpeakState(state);

                foreach (var window in _windows.Values)
                {
                    window.SetFlyoutSpeakState(state);
                }
            });
        }

        private void ReflowPositions()
        {
            if (_order.Count == 0 || !OrbsVisible) return;

            if (_isArranged)
            {
                AbsorbIntoArrangement();
                return;
            }

            var first = _windows[_order[0]];
            var screen = first.Screens.Primary ?? first.Screens.All.FirstOrDefault();
            if (screen is null) return;

            // WorkingArea and Window.Position are in physical pixels; the
            // 56/12/24 design sizes are DIPs, so scale them.
            var work = screen.WorkingArea;
            var scale = screen.Scaling;
            int size = (int)(56 * scale);
            int spacing = (int)(12 * scale);
            int margin = (int)(24 * scale);

            // Orbs the user has placed by hand keep their spot and don't take up
            // a slot, so the rest of the stack closes up behind them.
            int slot = 0;
            foreach (var id in DisplayOrder())
            {
                var window = _windows[id];
                if (window.IsPinned) continue;

                window.Position = new PixelPoint(
                    work.Right - size - margin,
                    work.Y + margin + slot * (size + spacing));
                slot++;
            }

            // Every arrow's geometry just moved.
            TeamLinks.Refresh();
        }

        // A new orb appeared or an old one vanished while the shape is active.
        // Re-fit the whole shape and glide everything into it.
        //
        // The opposite of what this did until now, and the reversal is
        // deliberate rather than a regression, so the old reasoning is worth
        // keeping. Only the newcomer used to move, because re-fitting means an
        // orb that was sitting still moves for a reason that has nothing to do
        // with it — measured against the real geometry, an orb already on
        // screen shifts 33px on average when a sixth joins a circle, 111px in a
        // heart and up to 161px in a grid. The judgement was that a display
        // which rearranges itself because something unrelated started is a
        // display you stop trusting.
        //
        // Living with it says otherwise. A shape that absorbs arrivals where
        // they happen to fit stops being the shape after a handful of them, and
        // one orb hanging off the edge of a heart is read as something wrong —
        // it draws the eye every time, where six orbs sliding a few dozen pixels
        // is over in half a second and leaves a heart. Stillness was the wrong
        // thing to optimise for; the shape is the point of the shape.
        //
        // Removals re-fit too, on the same reasoning. A gap in a ring is the
        // same wrongness as a stray orb beside it, and "the gap is the honest
        // picture of what is running" was true and not worth the look of it.
        private void AbsorbIntoArrangement()
        {
            // Orbs gone since the pattern was drawn — drop their saved state.
            foreach (var id in _preArrangeState.Keys
                         .Where(id => !_windows.ContainsKey(id)).ToList())
            {
                _preArrangeState.Remove(id);
            }

            // Orbs that arrived after the pattern was drawn — record where
            // they would have stacked so Restore can put them back there.
            foreach (var id in _windows.Keys)
            {
                if (_preArrangeState.ContainsKey(id)) continue;
                var w = _windows[id];
                _preArrangeState[id] = (w.Position, w.IsPinned);
                w.SetFlyoutArranged(true);
            }

            // Mid-glide. Asking for a second shape while the first is still
            // being flown to would fight it, and dropping the request would
            // strand whichever orb arrived during the half-second — which is
            // the whole complaint. It gets picked up when this one lands.
            if (_arrangeAnimTargets is not null)
            {
                _refitPending = true;
                return;
            }

            var allOrbs = DisplayOrder()
                .Where(id => _windows.ContainsKey(id) && _windows[id].IsVisible)
                .Select(id => _windows[id])
                .ToList();

            if (allOrbs.Count < 1) return;

            var positioned = ComputeClusteredPositions(allOrbs);

            // Nothing to do if every orb is already where the new shape wants
            // it. Worth the check: this runs on every scan that changes the set
            // at all, including changes that do not move anybody.
            if (positioned.All(p => p.Orb.Position == p.Target))
            {
                TeamLinks.Refresh();
                return;
            }

            _arrangeAnimTargets = new();
            foreach (var (orb, target) in positioned)
                _arrangeAnimTargets[orb.SessionId] = (orb.Position, target);

            AnimateArrangement(() =>
            {
                foreach (var (id, (_, to)) in _arrangeAnimTargets ?? new())
                {
                    if (_windows.TryGetValue(id, out var window))
                        window.PinAt(to);
                }
            });
        }

        // --- dragged orb positions -------------------------------------------
        // A dragged orb stays where it was put. Within a run that's the pinned
        // flag above; across runs it's settings.json, keyed by whichever part of
        // a session survives a restart. For Claude Code that is its directory,
        // because its session id is new every run and would remember nothing;
        // for a gateway session it is the id itself, which is not.
        //
        // A local key is the directory *and* the session's name, because two
        // sessions in one directory are common and sharing a slot meant neither
        // stayed put. The name can change under you — Claude Code writes an
        // automatic title that follows the conversation — so a lookup falls back
        // to the directory alone, which also covers positions saved before names
        // were part of this.

        // A gateway session has no directory to be keyed by — the findings doc
        // notes the absence of `cwd` as a *simplification*, since there is no
        // local checkout for a key to collide with. What it missed is that the
        // key was doing a second job: an empty one is never saved and never
        // restored, so every agent orb went back to the stack on every launch
        // while local ones stayed put.
        //
        // Its session id is the stable thing instead. Unlike a Claude Code
        // session id, which is new every run, a gateway key is derived from the
        // agent and the channel and is the same string next week — which is what
        // made it a good room key and makes it a good position key.
        internal static string PositionKeyFor(SessionStatus status, string sessionId)
        {
            if (!status.IsLocalCli) return sessionId;

            var cwd = DirectoryKeyFor(status);
            if (cwd.Length == 0) return "";

            // Which CLI, in the key, for Codex only.
            //
            // Not decoration: a Codex session and a Claude Code session open in
            // one directory would otherwise share a slot whenever the Claude
            // one has not been auto-titled yet, which is exactly the collision
            // ccfee1d fixed for two Claude sessions. First orb to appear claims
            // the position, the other stacks, and whichever is dragged last
            // overwrites the one entry.
            //
            // Only Codex is prefixed, so every position already saved under a
            // bare directory still matches the session it was saved for. A
            // scheme that renamed both would have been tidier and would have
            // moved every pinned orb on this machine back to the stack once.
            var prefix = status.Source == SessionSource.Codex ? "codex\n" : "";

            // The directory alone is not enough when two sessions are open in
            // one: "makayla-lawyer" and "job-lawyer" both live in Evidence, so
            // they shared a slot — first orb to appear claimed it, the other
            // stacked, and whichever was dragged last overwrote the one entry.
            // Two orbs that would not stay where they were put, while every
            // other orb did.
            //
            // The session's name is what separates them, and it is the right
            // thing rather than a convenient one: it is what *you* called that
            // session, so an orb follows the name you gave it rather than the
            // folder it happens to share.
            var title = (status.Title ?? "").Trim();
            return prefix + (title.Length == 0 ? cwd : cwd + "\n" + title);
        }

        internal static string DirectoryKeyFor(SessionStatus status) =>
            string.IsNullOrEmpty(status.Cwd) ? "" : status.Cwd.TrimEnd('\\', '/');

        private void RestoreOrbPosition(OrbWindow window, SessionStatus status)
        {
            var key = PositionKeyFor(status, window.SessionId);
            window.PositionKey = key;
            if (string.IsNullOrEmpty(key)) return;

            // A sibling session in the same directory already sits there;
            // stacking a second orb on top of it would just hide one.
            if (_windows.Values.Any(other => other != window
                                             && other.IsPinned
                                             && other.PositionKey == key))
            {
                return;
            }

            var saved = ClaudeBuddySettings.OrbPositionFor(key);

            // Nothing under the name, so try the directory on its own. That
            // covers a position saved before names were part of the key, and a
            // session whose title has changed since — Claude Code writes an
            // automatic one that moves as a conversation does, and losing your
            // placement to a retitle would be a worse bug than the one this
            // fixes.
            // Claude Code only, and it has to stay that way. This exists for
            // positions saved before names were part of the key, and there are
            // no such Codex positions — every one of them has been written by a
            // build that prefixes the CLI. Widening it would hand a Codex orb
            // the place a Claude Code orb was put.
            if (saved is null && status.Source == SessionSource.ClaudeCode)
            {
                var directory = DirectoryKeyFor(status);
                if (directory.Length > 0 && directory != key)
                {
                    saved = ClaudeBuddySettings.OrbPositionFor(directory);
                }
            }

            if (saved is null) return;

            var point = new PixelPoint(saved.X, saved.Y);

            // The monitor it was dragged onto may be gone, or its layout
            // changed. Anything that no longer lands on a screen falls back to
            // the default stack rather than being stranded off-canvas.
            var screen = window.Screens.ScreenFromPoint(point);
            if (screen is null) return;

            window.PinAt(ClampIntoWork(point, screen.WorkingArea, (int)(56 * screen.Scaling)));
        }

        // An orb's top-left corner, pulled back until the whole orb is inside
        // the working area.
        //
        // The Math.Max guards are not belt-and-braces: a work area narrower than
        // an orb makes `Right - orbSize` smaller than `X`, and Math.Clamp throws
        // rather than picking a side when its bounds are inverted. Shared by the
        // two callers — a position restored from settings and one rescued from a
        // display that has changed shape — because they clamp for the same
        // reason and drifting apart would mean one of them stranding an orb the
        // other would have saved.
        internal static PixelPoint ClampIntoWork(PixelPoint point, PixelRect work, int orbSize) =>
            new(Math.Clamp(point.X, work.X, Math.Max(work.X, work.Right - orbSize)),
                Math.Clamp(point.Y, work.Y, Math.Max(work.Y, work.Bottom - orbSize)));

        // Bring back any orb that is no longer on a screen.
        //
        // Nothing in the app could do this, and the gap was reported the way
        // gaps like this always are: "I seem to have lost all my orbs." They
        // were sitting in a neat row 223 points above the top of the display,
        // which is exactly where they had been put while the desktop was a
        // different shape.
        //
        // Nothing had moved them. A restored position is already checked
        // against the screens it lands on — see RestoreOrbPosition — but an orb
        // *already placed* was checked once and never again, and the thing that
        // changes underneath it is the desktop: a monitor unplugged, an
        // arrangement rearranged, a resolution changed. The orb keeps
        // coordinates that were true when it got them and stops being anywhere
        // a person can click.
        //
        // Judged on the orb's centre rather than its corner, so one hanging
        // half off an edge is left alone — that is a placement somebody may
        // have chosen, and dragging it back would be the app overruling them.
        // Only an orb with nothing under its middle is unreachable, and only
        // those are moved.
        //
        // The position is not written back to settings. If it came from there
        // it is still a good position for the display it was saved on, and
        // RestoreOrbPosition already declines to use it anywhere else.
        private void RescueOffscreenOrbs()
        {
            foreach (var window in _windows.Values)
            {
                if (!window.IsVisible) continue;

                var size = 56;
                var centre = new PixelPoint(
                    window.Position.X + size / 2,
                    window.Position.Y + size / 2);

                if (window.Screens.ScreenFromPoint(centre) is not null) continue;

                var screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
                if (screen is null) continue;

                window.PinAt(ClampIntoWork(
                    window.Position, screen.WorkingArea, (int)(56 * screen.Scaling)));
            }
        }

        public void RememberOrbPosition(OrbWindow window)
        {
            if (string.IsNullOrEmpty(window.PositionKey)) return;

            var position = window.Position;
            ClaudeBuddySettings.SetOrbPosition(window.PositionKey, position.X, position.Y);
        }

        public void ReturnOrbToStack(string sessionId)
        {
            if (!_windows.TryGetValue(sessionId, out var window)) return;

            window.Unpin();
            if (!string.IsNullOrEmpty(window.PositionKey))
            {
                ClaudeBuddySettings.ClearOrbPosition(window.PositionKey);
            }

            ReflowPositions();
        }

        public void ResetSessionToIdle(string sessionId)
        {
            // There is no status file to rewrite for a gateway session, and the
            // path this would build from its key ("openclaw:agent:main:…")
            // is not one this app should be writing at all. The gateway owns
            // that session's state; we only display it.
            if (_statuses.TryGetValue(sessionId, out var known) && !known.IsLocalCli)
            {
                return;
            }

            var file = Path.Combine(_statusDir, sessionId + ".txt");
            SessionStatus? existing = null;
            try
            {
                existing = System.Text.Json.JsonSerializer.Deserialize<SessionStatus>(File.ReadAllText(file));
            }
            catch { }

            // Keep everything but the state (cwd, terminal info) intact.
            var reset = existing ?? new SessionStatus();
            reset.State = "idle";

            // The second of the two places a status file is read, and the
            // reason SourceOf exists rather than the scan resolving this
            // inline. Without it the object handed to UpdateFrom below claims
            // to be Claude Code — Source is [JsonIgnore], so it arrives as its
            // default — and the orb would say so, and TerminalFocuser would
            // believe it, until the next scan put it right.
            //
            // `existing ?? new SessionStatus()` is the case that makes this
            // more than tidiness: a file that could not be read has no Cli
            // either, so the fallback has to go through the same resolution
            // rather than inheriting whatever the caller assumed.
            reset.Source = SourceOf(reset);
            try
            {
                File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(reset));
            }
            catch { }

            _statuses[sessionId] = reset;

            if (_windows.TryGetValue(sessionId, out var window))
            {
                window.UpdateFrom(reset);
            }

            UpdateTray();
        }

        public void ResetAllSessionsToIdle()
        {
            foreach (var sessionId in _order.ToList())
            {
                ResetSessionToIdle(sessionId);
            }
        }

        // --- orb arrangement patterns ----------------------------------------
        // Clicking the arrange button on any orb's flyout gathers every
        // visible orb into a heart shape, centred on the primary screen.
        // Clicking again restores them — a toggle, not a one-way trip.

        private readonly Dictionary<string, (PixelPoint Position, bool Pinned)> _preArrangeState = new();
        private bool _isArranged;

        public bool IsArranged => _isArranged;

        private DispatcherTimer? _arrangeAnimTimer;
        private Dictionary<string, (PixelPoint From, PixelPoint To)>? _arrangeAnimTargets;
        private long _arrangeAnimStart;
        private Action? _arrangeAnimComplete;

        // A membership change that arrived mid-glide, to be re-fitted once the
        // current one lands. See AbsorbIntoArrangement.
        private bool _refitPending;
        private const int ArrangeAnimMs = 600;

        public void ArrangeOrbsInPattern()
        {
            if (_arrangeAnimTargets is not null) return;

            if (_isArranged)
            {
                RestoreFromPattern();
                return;
            }

            var allOrbs = DisplayOrder()
                .Where(id => _windows.ContainsKey(id) && _windows[id].IsVisible)
                .Select(id => _windows[id])
                .ToList();

            if (allOrbs.Count < 1) return;

            foreach (var w in _windows.Values)
            {
                w.HideFlyout();
                ChatPanel.HideFor(w.SessionId);
            }

            _preArrangeState.Clear();
            foreach (var orb in allOrbs)
                _preArrangeState[orb.SessionId] = (orb.Position, orb.IsPinned);

            var positioned = ComputeClusteredPositions(allOrbs);

            _arrangeAnimTargets = new();
            foreach (var (orb, target) in positioned)
                _arrangeAnimTargets[orb.SessionId] = (orb.Position, target);

            _isArranged = true;
            AnimateArrangement(() =>
            {
                foreach (var (id, (_, to)) in _arrangeAnimTargets ?? new())
                {
                    if (_windows.TryGetValue(id, out var window))
                        window.PinAt(to);
                }
            });

            foreach (var w in _windows.Values)
                w.SetFlyoutArranged(true);
        }

        // Leads and solo orbs define the shape; members radiate outward
        // from their lead, away from the shape's centre — so the pattern
        // reads cleanly and the small member orbs fan out like spokes.
        // Maps the orbs on screen onto OrbArrangement's inputs and its answer
        // back onto them. All the geometry lives there, where it can be tested —
        // see tests/ArrangementTests, which walks every shape at every spacing
        // across the team shapes that occur and checks that nothing leaves the
        // screen or lands on top of anything else.
        private List<(OrbWindow Orb, PixelPoint Target)> ComputeClusteredPositions(List<OrbWindow> allOrbs)
        {
            if (allOrbs.Count == 0) return new List<(OrbWindow, PixelPoint)>();

            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < allOrbs.Count; i++) index[allOrbs[i].SessionId] = i;

            var leadOf = new int[allOrbs.Count];
            for (var i = 0; i < allOrbs.Count; i++)
            {
                var lead = _statuses.TryGetValue(allOrbs[i].SessionId, out var status) ? status.Lead : "";

                leadOf[i] = !string.IsNullOrEmpty(lead) && index.TryGetValue(lead, out var at) && at != i
                    ? at
                    : -1;
            }

            var screen = allOrbs[0].Screens.Primary ?? allOrbs[0].Screens.All.FirstOrDefault();
            var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);

            var layout = new OrbArrangement.Layout(
                work,
                screen?.Scaling ?? 1.0,
                ClaudeBuddySettings.ArrangeShape,
                ClaudeBuddySettings.ArrangeSpacing,
                ArrangementAnchor(work));

            var placed = OrbArrangement.Compute(allOrbs.Count, leadOf, layout);

            return allOrbs.Select((orb, i) => (orb, placed[i])).ToList();
        }

        // Where the shape gets drawn. The first time ever, that's the middle
        // of the work area — same point OrbArrangement used to compute on its
        // own — but saved from then on, so a later orb joining or leaving
        // re-fits around where the shape already is rather than the screen's
        // middle. ShiftArrangementAnchor below is the other way this moves:
        // the user dragging the whole shape somewhere else.
        internal static PixelPoint ArrangementAnchor(PixelRect work)
        {
            if (ClaudeBuddySettings.ArrangeAnchor is { } saved) return new PixelPoint(saved.X, saved.Y);

            var center = new PixelPoint(work.X + work.Width / 2, work.Y + work.Height / 2);
            ClaudeBuddySettings.ArrangeAnchor = new ClaudeBuddySettings.OrbPlacement(center.X, center.Y);
            return center;
        }

        // A whole-shape drag (see OrbWindow.OnPointerReleased) moves every
        // arranged orb by the same delta, so the shape's saved centre needs
        // the same nudge or the next membership change would snap it back to
        // wherever it was before the drag.
        public void ShiftArrangementAnchor(int dx, int dy)
        {
            if (dx == 0 && dy == 0) return;
            if (ClaudeBuddySettings.ArrangeAnchor is not { } anchor) return;

            ClaudeBuddySettings.ArrangeAnchor = new ClaudeBuddySettings.OrbPlacement(anchor.X + dx, anchor.Y + dy);
        }

        private void RestoreFromPattern()
        {
            if (_arrangeAnimTargets is not null) return;

            foreach (var w in _windows.Values)
            {
                w.HideFlyout();
                ChatPanel.HideFor(w.SessionId);
            }

            var targets = new Dictionary<string, (PixelPoint From, PixelPoint To)>();
            foreach (var (id, (origPos, _)) in _preArrangeState)
            {
                if (!_windows.TryGetValue(id, out var window)) continue;
                targets[id] = (window.Position, origPos);
            }

            _arrangeAnimTargets = targets;
            _isArranged = false;

            AnimateArrangement(() =>
            {
                foreach (var (id, (origPos, wasPinned)) in _preArrangeState)
                {
                    if (!_windows.TryGetValue(id, out var window)) continue;
                    if (wasPinned)
                        window.PinAt(origPos);
                    else
                        window.Unpin();
                }
                _preArrangeState.Clear();
                ReflowPositions();
            });

            foreach (var w in _windows.Values)
                w.SetFlyoutArranged(false);
        }

        private void AnimateArrangement(Action? onComplete = null)
        {
            _arrangeAnimComplete = onComplete;
            _arrangeAnimStart = Environment.TickCount64;

            if (_arrangeAnimTimer is null)
            {
                _arrangeAnimTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1000.0 / 60)
                };
                _arrangeAnimTimer.Tick += OnArrangeAnimTick;
            }
            _arrangeAnimTimer.Start();
        }

        private void OnArrangeAnimTick(object? sender, EventArgs e)
        {
            if (_arrangeAnimTargets is null)
            {
                _arrangeAnimTimer?.Stop();
                return;
            }

            var elapsed = Environment.TickCount64 - _arrangeAnimStart;
            var t = Math.Min(1.0, elapsed / (double)ArrangeAnimMs);
            var eased = 1 - Math.Pow(1 - t, 3);

            foreach (var (id, (from, to)) in _arrangeAnimTargets)
            {
                if (!_windows.TryGetValue(id, out var window)) continue;
                window.Position = new PixelPoint(
                    (int)Math.Round(from.X + (to.X - from.X) * eased),
                    (int)Math.Round(from.Y + (to.Y - from.Y) * eased));
            }
            TeamLinks.Refresh();

            if (t < 1.0) return;

            _arrangeAnimTimer!.Stop();
            var complete = _arrangeAnimComplete;
            _arrangeAnimComplete = null;
            var targets = _arrangeAnimTargets;
            _arrangeAnimTargets = null;
            complete?.Invoke();

            // An orb arrived or left while that was flying. Fit it in now,
            // rather than leaving it as the one orb outside the shape.
            if (!_refitPending) return;

            _refitPending = false;
            if (_isArranged) AbsorbIntoArrangement();
        }

        public List<OrbWindow> ArrangedSiblings(string excludeSessionId)
        {
            if (!_isArranged) return new();

            return _preArrangeState.Keys
                .Where(id => id != excludeSessionId
                          && _windows.ContainsKey(id)
                          && _windows[id].IsVisible)
                .Select(id => _windows[id])
                .ToList();
        }

        // Called from the settings slider so the user sees orbs reposition
        // in real time while dragging. Only acts when orbs are already arranged;
        // if they're not, the new spacing is just saved for next time.
        public void ReapplyArrangement()
        {
            if (!_isArranged) return;
            if (_arrangeAnimTargets is not null) return;

            var allOrbs = DisplayOrder()
                .Where(id => _windows.ContainsKey(id) && _windows[id].IsVisible)
                .Select(id => _windows[id])
                .ToList();

            if (allOrbs.Count < 1) return;

            var positioned = ComputeClusteredPositions(allOrbs);
            foreach (var (orb, target) in positioned)
                orb.Position = target;

            TeamLinks.Refresh();
        }
    }
}
