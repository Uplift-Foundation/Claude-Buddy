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
        private static TimeSpan? StaleAfter
        {
            get
            {
                var minutes = ClaudeBuddySettings.OrbLifetimeMinutes;
                return minutes == ClaudeBuddySettings.OrbLifetimeForever
                    ? null
                    : TimeSpan.FromMinutes(minutes);
            }
        }

        private readonly string _statusDir =
            Path.Combine(Path.GetTempPath(), "claude_buddy");

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

        public void Start()
        {
            Instance = this;
            Directory.CreateDirectory(_statusDir);

            _tray = new TrayController();

            StartWatching();

            _pollTimer.Tick += (_, _) => ScanAndUpdate();
            _pollTimer.Start();

            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                ScanAndUpdate();
            };

            ScanAndUpdate();
        }

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

        private void RestartDebounce()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        // One status file, already parsed. Written is the file's mtime, which is
        // what "how long since this session last said anything" means throughout.
        private sealed record ScanEntry(string SessionId, SessionStatus Status, DateTime Written);

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
        // pids, so this can't collapse them into one.
        //
        // A pid of 0 means a hook older than the session_pid field. Grouping those
        // would put every such file in one bucket and drop all but one, so they're
        // left alone and keep the old behaviour — the same reason
        // ProcessLiveness.IsRunning treats 0 as alive.
        private static HashSet<string> Superseded(List<ScanEntry> found)
        {
            var newest = new Dictionary<int, ScanEntry>();

            foreach (var entry in found)
            {
                var pid = entry.Status.SessionPid;
                if (pid <= 0) continue;

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
                var pid = entry.Status.SessionPid;
                if (pid <= 0) continue;

                if (newest.TryGetValue(pid, out var best) && best.SessionId != entry.SessionId)
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
        private static bool KnowsATerminal(SessionStatus status) =>
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
        private static void InheritTerminalInfo(List<ScanEntry> found)
        {
            foreach (var group in found.Where(e => e.Status.SessionPid > 0)
                                       .GroupBy(e => e.Status.SessionPid))
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

        private void ScanAndUpdate()
        {
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

                found.Add(new ScanEntry(Path.GetFileNameWithoutExtension(file), status, written));
            }

            InheritTerminalInfo(found);

            var superseded = Superseded(found);

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
                // An id this process has already moved on from. Dropped here
                // rather than left to the lifetime timer, which is the only other
                // thing that would ever catch it.
                if (superseded.Contains(sessionId))
                {
                    continue;   // removed in the pass below
                }

                // Gone is gone: if the claude process that wrote this file has
                // exited, no lifetime setting should keep its orb — that's the
                // Ctrl+C case, which fires no SessionEnd and so leaves the file
                // behind. This applies to `waiting` as well, which the timer
                // below deliberately never touches; an unanswered prompt whose
                // session was killed used to sit on screen indefinitely.
                if (status.SessionPid > 0 && !ProcessLiveness.IsRunning(status.SessionPid))
                {
                    continue;   // removed in the pass below
                }

                // A session waiting on you (permission prompt / question) never
                // goes stale on its own — no further hook fires until you
                // respond, so the file's mtime is frozen for as long as you're
                // away. Pruning it would hide the orb exactly when it matters
                // most. Use "Reset this session to idle" to clear a genuinely
                // abandoned one manually.
                var staleAfter = StaleAfter;
                if (staleAfter is not null
                    && status.State != "waiting"
                    && now - written > staleAfter)
                {
                    continue; // treat as gone; cleaned up in the removal pass below
                }

                // A team lead can be a background session with no terminal of
                // its own — run inside `claude daemon run`, which has none, and
                // reparented to launchd once the session that started it went
                // away. You watch it through `claude agents`, a separate
                // process in a real window that nothing in the lead's own
                // process tree points at, so the hook could never have recorded
                // it. See AgentTeamViewer, which finds it by directory.
                // A session with no pid of its own is in the same bind and
                // for the same reason: nothing in a process tree points at
                // the window you actually watch it in, so the directory is
                // the only link back to its `claude agents` viewer. Widened
                // to cover it because the machinery is identical — TryAdopt
                // no-ops off macOS, without a cwd, and when no viewer for
                // that directory is running.
                if (!KnowsATerminal(status)
                    && (leadsWithLiveAgents.Contains(sessionId) || status.SessionPid <= 0))
                {
                    AgentTeamViewer.TryAdopt(status);
                }

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
                if (string.IsNullOrEmpty(status.Tty)
                    && string.IsNullOrEmpty(status.TermProgram)
                    && string.IsNullOrEmpty(status.TmuxPane)
                    && status.TermPid == 0
                    && !leadsWithLiveAgents.Contains(sessionId)
                    && status.SessionPid > 0)
                {
                    continue;
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
                if (status.SessionPid <= 0 && !BackgroundJobs.IsLiveJob(sessionId))
                {
                    continue;
                }

                seen.Add(sessionId);

                // Whether this session is an agent-team member, and whose. Read
                // from its process rather than its status file — see AgentTeam.
                // Asked after the liveness rules above so a dead session never
                // costs a lookup.
                var membership = AgentTeam.Of(status.SessionPid);
                status.Lead = membership.Lead == sessionId ? "" : membership.Lead;
                status.Agent = string.IsNullOrEmpty(status.Lead) ? "" : membership.Name;

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
            }

            if (setChanged)
            {
                ReflowPositions();
            }

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
        private List<string> DisplayOrder()
        {
            var byLead = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var id in _order)
            {
                if (!_statuses.TryGetValue(id, out var status)) continue;

                var lead = status.Lead;
                if (string.IsNullOrEmpty(lead) || lead == id) continue;
                if (!_statuses.ContainsKey(lead)) continue;

                if (!byLead.TryGetValue(lead, out var members))
                {
                    byLead[lead] = members = new List<string>();
                }
                members.Add(id);
            }

            var gathered = new HashSet<string>(byLead.Values.SelectMany(m => m), StringComparer.Ordinal);

            var order = new List<string>(_order.Count);
            foreach (var id in _order)
            {
                if (!_statuses.ContainsKey(id)) continue;
                if (gathered.Contains(id)) continue;   // emitted under its lead below

                order.Add(id);
                if (byLead.TryGetValue(id, out var members)) order.AddRange(members);
            }

            // A team whose lead is itself a member of another team would leave
            // its members unemitted above; nesting isn't a thing today, but a
            // dropped orb would be a silent one, so sweep up anything missed.
            foreach (var id in _order)
            {
                if (_statuses.ContainsKey(id) && !order.Contains(id)) order.Add(id);
            }

            return order;
        }

        // The last known state of another session — what an orb needs to hand
        // its team lead to TerminalFocuser. Null for a session that isn't
        // tracked, including the empty id a non-member passes in.
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

        private void ReflowPositions()
        {
            if (_order.Count == 0 || !OrbsVisible) return;

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

        // --- dragged orb positions -------------------------------------------
        // A dragged orb stays where it was put. Within a run that's the pinned
        // flag above; across runs it's settings.json, keyed by the session's
        // directory — session ids are new every time, so they'd remember
        // nothing. Two live sessions in one directory therefore share a key:
        // the first orb to appear claims the saved spot, the others stack
        // normally, and whichever one you drag last is what gets remembered.

        private static string PositionKeyFor(SessionStatus status) =>
            string.IsNullOrEmpty(status.Cwd) ? "" : status.Cwd.TrimEnd('\\', '/');

        private void RestoreOrbPosition(OrbWindow window, SessionStatus status)
        {
            var key = PositionKeyFor(status);
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
            if (saved is null) return;

            var point = new PixelPoint(saved.X, saved.Y);

            // The monitor it was dragged onto may be gone, or its layout
            // changed. Anything that no longer lands on a screen falls back to
            // the default stack rather than being stranded off-canvas.
            var screen = window.Screens.ScreenFromPoint(point);
            if (screen is null) return;

            var work = screen.WorkingArea;
            int size = (int)(56 * screen.Scaling);
            point = new PixelPoint(
                Math.Clamp(point.X, work.X, Math.Max(work.X, work.Right - size)),
                Math.Clamp(point.Y, work.Y, Math.Max(work.Y, work.Bottom - size)));

            window.PinAt(point);
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
                w.HideFlyout();

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
        private List<(OrbWindow Orb, PixelPoint Target)> ComputeClusteredPositions(List<OrbWindow> allOrbs)
        {
            var teams = new Dictionary<string, List<OrbWindow>>(StringComparer.Ordinal);
            var anchors = new List<OrbWindow>();

            foreach (var orb in allOrbs)
            {
                var lead = _statuses.TryGetValue(orb.SessionId, out var s) ? s.Lead : "";
                if (!string.IsNullOrEmpty(lead) && lead != orb.SessionId
                    && allOrbs.Any(o => o.SessionId == lead))
                {
                    if (!teams.TryGetValue(lead, out var members))
                        teams[lead] = members = new List<OrbWindow>();
                    members.Add(orb);
                }
                else
                {
                    anchors.Add(orb);
                }
            }

            var shapeTargets = ShapePositions(anchors);
            var result = new List<(OrbWindow, PixelPoint)>();

            var (_, _, orbSize, _, cx, cy) = ShapeAnchor(allOrbs);

            for (int i = 0; i < anchors.Count; i++)
            {
                var anchor = anchors[i];
                var pos = shapeTargets[i];
                result.Add((anchor, pos));

                if (!teams.TryGetValue(anchor.SessionId, out var members)) continue;

                // Direction from shape centre outward through this lead
                double dx = pos.X - cx;
                double dy = pos.Y - cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1) { dx = 0; dy = -1; dist = 1; }
                double nx = dx / dist;
                double ny = dy / dist;

                double memberRadius = orbSize * 1.2;
                double fanSpread = Math.PI / 3;

                for (int m = 0; m < members.Count; m++)
                {
                    double baseAngle = Math.Atan2(ny, nx);
                    double offset = members.Count == 1
                        ? 0
                        : fanSpread * (m - (members.Count - 1) / 2.0) / Math.Max(members.Count - 1, 1);
                    double angle = baseAngle + offset;

                    var memberPos = new PixelPoint(
                        (int)Math.Round(pos.X + memberRadius * Math.Cos(angle)),
                        (int)Math.Round(pos.Y + memberRadius * Math.Sin(angle)));
                    result.Add((members[m], memberPos));
                }
            }

            return result;
        }

        private void RestoreFromPattern()
        {
            if (_arrangeAnimTargets is not null) return;

            foreach (var w in _windows.Values)
                w.HideFlyout();

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

        // ---- shape generators ---------------------------------------------------

        private static List<PixelPoint> ShapePositions(List<OrbWindow> orbs)
        {
            var shape = ClaudeBuddySettings.ArrangeShape;
            var pts = shape switch
            {
                "circle"  => CirclePositions(orbs),
                "diamond" => DiamondPositions(orbs),
                "star"    => StarPositions(orbs),
                "grid"    => GridPositions(orbs),
                _         => HeartPositions(orbs),
            };
            return EnsureMinSpacing(pts, orbs);
        }

        // After a shape generator runs, check whether any two orbs ended
        // up too close and uniformly scale the whole pattern outward from
        // its centre until every pair clears the minimum gap.
        private static List<PixelPoint> EnsureMinSpacing(List<PixelPoint> pts, List<OrbWindow> orbs)
        {
            if (pts.Count < 2) return pts;

            var screen = orbs[0].Screens.Primary ?? orbs[0].Screens.All.FirstOrDefault();
            double scale = screen?.Scaling ?? 1.0;
            int orbSize = (int)(56 * scale);
            double minGap = orbSize * (0.3 + ClaudeBuddySettings.ArrangeSpacing);

            double minDist = double.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                for (int j = i + 1; j < pts.Count; j++)
                {
                    double dx = pts[i].X - pts[j].X;
                    double dy = pts[i].Y - pts[j].Y;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < minDist) minDist = dist;
                }
            }

            if (minDist >= minGap || minDist < 1) return pts;

            double cx = pts.Average(p => (double)p.X);
            double cy = pts.Average(p => (double)p.Y);
            double factor = minGap / minDist;

            return pts.Select(p => new PixelPoint(
                (int)Math.Round(cx + (p.X - cx) * factor),
                (int)Math.Round(cy + (p.Y - cy) * factor)
            )).ToList();
        }

        private static (PixelRect Work, double Scale, int OrbSize, int Margin, double Cx, double Cy) ShapeAnchor(List<OrbWindow> orbs)
        {
            var screen = orbs[0].Screens.Primary ?? orbs[0].Screens.All.FirstOrDefault();
            var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            double scale = screen?.Scaling ?? 1.0;
            int orbSize = (int)(56 * scale);
            int margin = (int)(24 * scale);

            double cx = work.Right - margin - orbSize * 1.5;
            double cy = work.Y + margin + orbSize * 2.5;

            return (work, scale, orbSize, margin, cx, cy);
        }

        private static double SpacingScale(int orbSize)
        {
            return orbSize * ClaudeBuddySettings.ArrangeSpacing / 16.0;
        }

        // Heart: x = 16sin³t, y = 13cos(t) - 5cos(2t) - 2cos(3t) - cos(4t)
        private static List<PixelPoint> HeartPositions(List<OrbWindow> orbs)
        {
            int n = orbs.Count;
            var (_, _, orbSize, _, cx, cy) = ShapeAnchor(orbs);

            if (n == 1)
                return new List<PixelPoint> { new((int)Math.Round(cx), (int)Math.Round(cy)) };

            var pts = new (double X, double Y)[n];
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * i / n;
                double sinT = Math.Sin(t);
                pts[i] = (
                    16 * sinT * sinT * sinT,
                    -(13 * Math.Cos(t) - 5 * Math.Cos(2 * t)
                      - 2 * Math.Cos(3 * t) - Math.Cos(4 * t))
                );
            }

            double s = SpacingScale(orbSize);

            return pts.Select(p => new PixelPoint(
                (int)Math.Round(cx + p.X * s),
                (int)Math.Round(cy + p.Y * s)
            )).ToList();
        }

        private static List<PixelPoint> CirclePositions(List<OrbWindow> orbs)
        {
            int n = orbs.Count;
            var (_, _, orbSize, _, cx, cy) = ShapeAnchor(orbs);

            if (n == 1)
                return new List<PixelPoint> { new((int)Math.Round(cx), (int)Math.Round(cy)) };

            double radius = SpacingScale(orbSize) * 10;

            return Enumerable.Range(0, n).Select(i =>
            {
                double t = 2 * Math.PI * i / n - Math.PI / 2;
                return new PixelPoint(
                    (int)Math.Round(cx + radius * Math.Cos(t)),
                    (int)Math.Round(cy + radius * Math.Sin(t)));
            }).ToList();
        }

        private static List<PixelPoint> DiamondPositions(List<OrbWindow> orbs)
        {
            int n = orbs.Count;
            var (_, _, orbSize, _, cx, cy) = ShapeAnchor(orbs);

            if (n == 1)
                return new List<PixelPoint> { new((int)Math.Round(cx), (int)Math.Round(cy)) };

            double s = SpacingScale(orbSize) * 10;
            var pts = new List<PixelPoint>();
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * i / n - Math.PI / 2;
                double cos = Math.Cos(t), sin = Math.Sin(t);
                double r = s / Math.Max(Math.Abs(cos) + Math.Abs(sin), 0.01);
                pts.Add(new PixelPoint(
                    (int)Math.Round(cx + r * cos),
                    (int)Math.Round(cy + r * sin)));
            }
            return pts;
        }

        // Five-pointed star: 10 vertices (5 outer tips, 5 inner valleys)
        // with N orbs distributed evenly along the perimeter, interpolating
        // between vertices so any count traces the star shape.
        private static List<PixelPoint> StarPositions(List<OrbWindow> orbs)
        {
            int n = orbs.Count;
            var (_, _, orbSize, _, cx, cy) = ShapeAnchor(orbs);

            if (n == 1)
                return new List<PixelPoint> { new((int)Math.Round(cx), (int)Math.Round(cy)) };

            double outer = SpacingScale(orbSize) * 12;
            double inner = outer * 0.4;
            const int verts = 10;

            var starX = new double[verts];
            var starY = new double[verts];
            for (int v = 0; v < verts; v++)
            {
                double angle = 2 * Math.PI * v / verts - Math.PI / 2;
                double r = (v % 2 == 0) ? outer : inner;
                starX[v] = r * Math.Cos(angle);
                starY[v] = r * Math.Sin(angle);
            }

            return Enumerable.Range(0, n).Select(i =>
            {
                double pos = (double)i * verts / n;
                int idx = (int)pos;
                double frac = pos - idx;
                int a = idx % verts;
                int b = (idx + 1) % verts;

                double x = starX[a] * (1 - frac) + starX[b] * frac;
                double y = starY[a] * (1 - frac) + starY[b] * frac;

                return new PixelPoint(
                    (int)Math.Round(cx + x),
                    (int)Math.Round(cy + y));
            }).ToList();
        }

        private static List<PixelPoint> GridPositions(List<OrbWindow> orbs)
        {
            int n = orbs.Count;
            var (_, _, orbSize, _, cx, cy) = ShapeAnchor(orbs);

            if (n == 1)
                return new List<PixelPoint> { new((int)Math.Round(cx), (int)Math.Round(cy)) };

            int cols = (int)Math.Ceiling(Math.Sqrt(n));
            int rows = (int)Math.Ceiling((double)n / cols);
            double gap = orbSize * (0.5 + ClaudeBuddySettings.ArrangeSpacing);

            double startX = cx - (cols - 1) * gap / 2;
            double startY = cy - (rows - 1) * gap / 2;

            return Enumerable.Range(0, n).Select(i =>
            {
                int col = i % cols;
                int row = i / cols;
                return new PixelPoint(
                    (int)Math.Round(startX + col * gap),
                    (int)Math.Round(startY + row * gap));
            }).ToList();
        }
    }
}
