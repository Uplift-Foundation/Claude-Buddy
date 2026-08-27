using System;

namespace ClaudeBuddy
{
    // What shape of local session a status file describes.
    //
    // Not a second copy of SessionSource, which says which CLI wrote the file:
    // every value here is a Claude Code session, and what separates them is
    // whose lifecycle they follow. Claude Code grew these three while the app
    // went on drawing all of them as the first one, which is what put fifteen
    // breathing orbs on screen for a machine with nothing running on it.
    public enum LocalSessionShape
    {
        // Somebody at a keyboard. Its process lives as long as the terminal
        // does, and it is never parked — a terminal session between turns is
        // still a terminal session, sitting there waiting for you.
        Terminal,

        // A background job, running inside a pooled `claude bg-spare` worker
        // that is kept alive after the turn ends so the job can be resumed.
        // "Alive" therefore says almost nothing about it, which is why the
        // daemon's own phase has to be asked.
        Background,

        // A member of an agent team, spawned by a lead that named itself on the
        // member's command line. Outlives its lead: the process is real, the
        // status file is real, and nothing about either says the thing it was
        // answering to has gone.
        Teammate
    }

    // Whether a session is *present* — someone or something is on the other end
    // of it right now — and which lifecycle affordances it can be offered.
    //
    // Pure, with no window and no settings behind it, for the reason OrbGlyph
    // and OrbArrangement are: these rules were arrived at by looking at a
    // screenshot and counting orbs, and the next person to change one of them
    // needs to be able to ask what it answers without a machine in a particular
    // state. It also matters that the mistakes here are directional and quiet.
    // Parking an orb that is genuinely working says a working session is idle;
    // failing to park one says nothing new but leaves the bug in place. So
    // every rule below is written to fail towards "present", the same way
    // BackgroundJobs.IsLive fails towards "keep the orb".
    internal static class SessionPresence
    {
        // Which of the three shapes this is.
        //
        // The job phase decides first and unconditionally, because it is the
        // only one of the three signals that comes from outside this app: if
        // the daemon lists this session as a job, it is a job whatever else the
        // file looks like. Working, Parked and Done are all Background — a
        // finished job is still a background session, and the rules that drop
        // its orb are elsewhere and say so.
        //
        // Unknown and NotAJob both fall through to the team test, which is what
        // keeps an unreadable listing from reclassifying every teammate on
        // screen as a terminal session.
        //
        // Lead can't collide with a job: a background worker is started by the
        // daemon and carries no --parent-session-id, so nothing ever writes a
        // Lead onto one. The order is stated anyway rather than left to that
        // being permanently true.
        internal static LocalSessionShape ShapeOf(SessionStatus status, JobPhase phase)
        {
            if (phase is JobPhase.Working or JobPhase.Parked or JobPhase.Done)
            {
                return LocalSessionShape.Background;
            }

            return string.IsNullOrEmpty(status.Lead)
                ? LocalSessionShape.Terminal
                : LocalSessionShape.Teammate;
        }

        // Whether the daemon is worth asking about this session at all.
        //
        // The lookup is a subprocess (`claude agents --json`, cached for ten
        // seconds), and JudgeReachability already records the rule this keeps:
        // an ordinary session never pays for it. Before this gate existed the
        // scan fetched the listing on every pass whether or not anything needed
        // it, which on a machine with nothing but terminal sessions is a `claude`
        // process spawned every ten seconds forever, in service of a question
        // whose answer cannot change anything.
        //
        // The two shapes that are worth asking about are the two the daemon can
        // actually know: a session that recorded no pid (a hook older than that
        // field, or a shape that has none), and a session whose own status file
        // names no terminal — which is what a background worker's file looks
        // like, because the daemon that runs it has no terminal to inherit one
        // from. A file that *does* name a terminal is a session in a window on
        // this machine, or one sharing a pid with such a session, and neither is
        // a pooled worker.
        //
        // knowsATerminal is passed in rather than derived, because the caller
        // has to answer it at a particular moment: before the agent-viewer
        // adoption, which can hand a terminal to a session that had none.
        internal static bool WorthAskingTheDaemon(SessionStatus status, bool knowsATerminal) =>
            status.Source == SessionSource.ClaudeCode
            && (status.SessionPid <= 0 || !knowsATerminal);

        // Whether nothing is on the other end of this session right now.
        //
        // Parked is not the same claim as gone, which is why it dims an orb
        // rather than removing one. The session is resumable, it is still worth
        // clicking, and the user asked for it to stay on screen — what is wrong
        // today is only that it looks like work in progress.
        //
        // `state` is the status file's own word, and for a background session it
        // is load-bearing rather than decorative. The daemon's listing is cached
        // for ten seconds while the hook rewrites the file the instant a job
        // resumes, so the file is the fresher of the two sources: requiring it
        // to still say "idle" is what makes un-dimming prompt (next 2s scan)
        // even though dimming lags (up to the cache plus a scan). The lag is one
        // way round on purpose. An orb that stays dim for ten seconds after work
        // resumes is a lie about the thing the user is watching happen; an orb
        // that takes ten seconds to go dim is a lie about nothing happening,
        // which nobody is watching for.
        //
        // leadSeen / leadIsLiveJob are the orphan rule, and both have to be
        // false. A teammate whose lead is on this scan is part of a live team.
        // A teammate whose lead is a background job the daemon still lists is
        // also part of a live team — the lead simply has no status file of its
        // own to be seen through, which is the ordinary case for a team led from
        // a job. Only when neither holds is the member answering to something
        // that is not there any more, and an orphaned teammate is the one shape
        // here whose arrows have already silently vanished, so the orb is the
        // last thing left saying anything about it.
        internal static bool IsParked(
            LocalSessionShape shape, string state, JobPhase phase,
            bool leadSeen, bool leadIsLiveJob) => shape switch
        {
            LocalSessionShape.Background =>
                phase == JobPhase.Parked && string.Equals(state, "idle", StringComparison.Ordinal),

            LocalSessionShape.Teammate => !leadSeen && !leadIsLiveJob,

            // Including Terminal, said as the default rather than as a case so
            // a fourth shape added later is present until someone decides
            // otherwise, rather than dim because a switch was not revisited.
            _ => false
        };

        // Whether "Dismiss this orb" should be offered: it deletes a status
        // file, and only a local CLI session has one. A gateway or bridged
        // session's orb comes from a socket, so there is nothing on disk to
        // delete and the path this would build from a namespaced key
        // ("openclaw:agent:main:…") is not one this app should write to at all —
        // the same reason ResetSessionToIdle refuses those.
        //
        // Hidden rather than disabled for the sessions it cannot serve. A
        // greyed-out row invites the question "why not", and the answer — this
        // conversation lives somewhere else — is not one a menu can give.
        internal static bool CanDismiss(SessionStatus status) => status.IsLocalCli;

        // Whether "End this session" should be offered. Needs a pid to signal on
        // top of everything Dismiss needs: with no pid recorded there is nothing
        // to terminate, and this is the one action in the app that cannot be
        // undone, so it is offered only where it will do exactly what it says.
        //
        // A pid of 0 is a hook older than the session_pid field, not a session
        // without a process — which is the reason this is a separate rule rather
        // than Dismiss with an extra clause. Such a session can still be
        // dismissed; it just cannot be ended.
        internal static bool CanEndSession(SessionStatus status) =>
            status.IsLocalCli && status.SessionPid > 0;

        // --- the hygiene sweep ------------------------------------------------
        // Nothing but the SessionEnd hook's `rm -f` has ever deleted a status
        // file, and SessionEnd only fires on a graceful exit. So a Ctrl+C'd
        // session leaves its file behind for good, and a finished background job
        // leaves one that no liveness rule can ever touch — its worker is still
        // alive by design, so the pid keeps answering. Six dead-pid files and
        // every `done` job's file were sitting in one real status directory when
        // this was written, and with "Keep orbs for" set to Forever nothing in
        // the app would ever have removed any of them.

        // Whether this scan has *evidence* that the session behind a file is
        // over, as opposed to a reason not to draw its orb today.
        //
        // The distinction is the whole safety of the sweep, because the thing on
        // the other side of it is File.Delete. Only two facts qualify: the
        // process that wrote the file has exited, and the daemon says the job it
        // ran has finished. Both are statements about the session.
        //
        // Every other verdict is a statement about *us* — Expired is the user's
        // display setting, NoTerminal and NotALiveJob are about whether a click
        // could go anywhere, Superseded is about which of several files for one
        // live process is the current one, and Unknown means the CLI could not
        // be asked. Sweeping on any of those would delete a live session's file
        // because an orb was quiet for an hour, and the hook would then write
        // nothing more until the session's next event: the orb would go, and the
        // session would still be there.
        internal static bool EvidenceOfDeath(SessionManager.ScanVerdict verdict, JobPhase phase) =>
            verdict == SessionManager.ScanVerdict.ProcessGone || phase == JobPhase.Done;

        // Whether the grace period has run out on a file that has looked dead
        // since `deadSince`.
        //
        // Measured from when this process first saw the evidence, deliberately
        // not from the file's mtime. ResetSessionToIdle rewrites a status file in
        // place, which refreshes its mtime without the session coming back —
        // reading age off disk would mean "Reset this session to idle" quietly
        // granted a dead file another ten minutes, every time it was clicked.
        //
        // The grace exists because the evidence can be momentarily wrong in one
        // direction that matters: a recycled or briefly unreadable pid, or a job
        // the daemon reports done a moment before its resumption is recorded.
        // Ten minutes of a file nobody is looking at costs nothing; deleting a
        // file the app was wrong about costs the user their session's identity.
        internal static bool SweepDue(DateTime deadSince, DateTime now, TimeSpan grace) =>
            now - deadSince >= grace;
    }
}
