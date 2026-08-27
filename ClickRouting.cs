namespace ClaudeBuddy
{
    // Where a click goes when the terminal the hook recorded could not be
    // brought forward — the last thing tried before a click does nothing at all.
    //
    // "Nothing at all" was the actual complaint: "most of these dead orbs when I
    // try to go to terminal they do nothing." Every failure on that path is
    // silent, and there are five of them in a row. For a team member in a
    // detached swarm socket: FocusTmux selects the pane and then returns false
    // because no client is attached anywhere; the app switch has no case for
    // term_program "tmux"; FocusByTty walks up from a tmux-server pty and finds
    // no app bundle; the team-lead fallback lands on a headless background
    // session and fails the same way; the attach fallback was gated on having no
    // pid, which a team member has; and ReportFocusFailure writes to stderr,
    // which a bundled .app has nowhere to show. Six things went right and the
    // user saw nothing move.
    internal enum ClickFallback
    {
        // Nothing further to try, which is not quite the same as "coordinates
        // were recorded". The rule below reaches this for a session that
        // recorded coordinates *and* has a pid, and for one this app will not
        // attach for at all (Codex, or an orb whose id was not passed in) —
        // while a session with coordinates and no pid is answered by
        // AttachBackground, because a hook too old to record a pid is too old to
        // be trusted about the rest.
        //
        // Deliberately nothing in the case that matters, though: a session that
        // named a terminal has one, and opening a *second* window onto it would
        // hide a real failure behind a new window every time. That is the old
        // gate's diagnosis-over-surprise-window reasoning, and it still holds
        // exactly where it was written for.
        None,

        // A background job, in any phase: `claude attach <id>`. It has no
        // terminal of its own and never will, so this is the answer rather
        // than a fallback.
        AttachBackground,

        // The pane is alive in a tmux server nothing is attached to. Open a
        // terminal on that server: the select-window/select-pane that already
        // ran means a plain attach lands on the right pane.
        AttachSocket,

        // No terminal coordinates at all, and not a job the daemon lists —
        // an agent-mode direct child (`claude --session-id <id> --agent
        // <name>`). `claude attach <id>` regardless of pid.
        AttachById
    }

    // Which of those applies. Pure, for the reason everything else on this
    // branch is: TerminalFocuser is six hundred lines of tmux, ps and osascript
    // subprocesses that a headless runner must not execute, and this decision
    // was inline in the middle of it — reachable only by clicking an orb on a
    // real machine and watching what happened, which is precisely how it came
    // to be wrong for a whole class of session without anyone noticing.
    internal static class ClickRouting
    {
        // Whether this session recorded nothing a click could be aimed at.
        //
        // The same four fields JudgeReachability's no-terminal rule reads, plus
        // the tty — and the tty is the difference between this and
        // SessionManager.KnowsATerminal, which deliberately ignores it. The two
        // questions are not the same. KnowsATerminal asks "is there a *window*
        // behind this", and a tty alone can name a tmux server's pty, which
        // belongs to no window. This asks "is there anything to *try*", and a
        // tty alone is enough: FocusByTty walks the process tree above it and
        // can find the app that owns it.
        //
        // That distinction is what keeps AttachById off every ordinary session.
        // Read the other way round it would fire for any session whose only
        // coordinate is a tty, which is most background-ish sessions that do in
        // fact have a window.
        internal static bool NoCoordinatesAtAll(SessionStatus status) =>
            string.IsNullOrEmpty(status.Tty)
            && string.IsNullOrEmpty(status.TermProgram)
            && string.IsNullOrEmpty(status.TermId)
            && string.IsNullOrEmpty(status.TmuxPane)
            && status.TermPid == 0;

        // Whether an attach would reach this session at all — the question the
        // chat panel asks, where the click asks "what should this gesture do".
        //
        // Answered by running the same rule with paneAliveButDetached false,
        // rather than by a second rule that agrees with it today: the panel must
        // never offer an attach for a session a click would not attach, and the
        // two would drift the first time either changed. False is the honest
        // input here — the panel has not tried to focus anything, so it has not
        // learned that anything is detached, and the socket answer is not one it
        // can offer anyway (that one is about a pane the *click* already
        // selected).
        internal static bool AttachWouldReach(SessionStatus status, string? sessionId) =>
            FallbackFor(status, sessionId, paneAliveButDetached: false)
                is ClickFallback.AttachBackground or ClickFallback.AttachById;

        // paneAliveButDetached is what FocusTmux learned on the way past: the
        // pane exists, its server answered, the pane was selected — and no
        // client is attached to it anywhere. It is passed in rather than
        // re-derived because answering it costs three tmux subprocesses, and
        // because it is a fact about a moment that has already gone.
        //
        // Asked only after the recorded terminal *and* the team lead have both
        // failed to come forward. That ordering is the important half of this
        // whole change and belongs to the caller, but it is worth stating here
        // too: a team member whose lead is focusable today lands on the lead's
        // window, which works well and is what the user expects. Opening a
        // terminal on the swarm socket instead would be a surprise window for a
        // click that already did the right thing.
        internal static ClickFallback FallbackFor(
            SessionStatus status, string? sessionId, bool paneAliveButDetached)
        {
            var named = !string.IsNullOrEmpty(sessionId);

            // `claude attach` is Claude Code's own verb and there is no Codex
            // equivalent, so both attach-by-id answers are Claude Code's alone.
            // The scan already drops a pid-less Codex session before it can have
            // an orb, so nothing should reach here — this is the belt to that
            // braces, because the failure it prevents is a window opening onto
            // someone else's session.
            var claudeCode = status.Source == SessionSource.ClaudeCode;

            // First, because it is the most precise answer available: a
            // background job's own session, named directly. A parked job can
            // have been adopted into a `claude agents` viewer pane, and if that
            // viewer's server happens to be detached this would otherwise
            // attach to the *roster* rather than to the session that was
            // clicked.
            //
            // The pid test is kept alongside the shape test rather than
            // replaced by it: a hook older than the session_pid field still
            // writes 0, and that is still a session with nowhere of its own.
            if (claudeCode && named
                && (status.SessionPid <= 0 || status.Shape == LocalSessionShape.Background))
            {
                return ClickFallback.AttachBackground;
            }

            // The only one of the three that is not `claude attach`, and so the
            // only one that is not Claude Code's alone: attaching to a tmux
            // server is a tmux operation, and a Codex session in a detached
            // pane is in exactly the same bind for exactly the same reason.
            if (paneAliveButDetached) return ClickFallback.AttachSocket;

            // Nothing recorded at all. This is the agent-mode direct child —
            // `claude --session-id <id> --agent <name>`, which has a real pid
            // and no terminal anywhere — and the reason the diagnosis rule
            // above does not cover it: there is no recorded terminal that
            // failed to resolve, so there is nothing to diagnose and no window
            // for a second one to be confused with.
            //
            // Reached only when the session is not a job the daemon lists,
            // because a job would have been answered by AttachBackground
            // above. An attach that turns out to name nothing prints "No job
            // matching" in a window the user opened on purpose, which is a
            // visible answer — and a visible wrong answer beats an invisible
            // line on a stderr nobody is reading.
            if (claudeCode && named && NoCoordinatesAtAll(status))
            {
                return ClickFallback.AttachById;
            }

            return ClickFallback.None;
        }
    }
}
