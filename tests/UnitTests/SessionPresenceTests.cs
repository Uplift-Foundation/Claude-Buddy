using Xunit;

namespace ClaudeBuddy.Tests;

// SessionPresence's classifier: which shape of local session a status file
// describes, whether anything is on the other end of it, and which of the two
// lifecycle actions its orb may offer.
//
// Every case below is one of the orbs on the screenshot this ticket was filed
// from — fifteen orbs on a machine the user considered idle, all breathing, all
// identical. The rules are what tell them apart, and the mistakes they can make
// are directional: parking an orb that is genuinely working says a session at
// work is doing nothing, which is worse than the bug being fixed. So the matrix
// below is deliberately as interested in what must *not* be parked as in what
// must.
public class SessionPresenceTests
{
    private static SessionStatus Status(
        string state = "idle", string lead = "", int pid = 4321,
        SessionSource source = SessionSource.ClaudeCode) =>
        new() { State = state, Lead = lead, SessionPid = pid, Source = source };

    // --- ShapeOf -------------------------------------------------------------

    // The job phase decides first and unconditionally: it is the only signal
    // from outside this app, and if the daemon lists a session as a job then it
    // is one whatever the file looks like.
    // Looped rather than a [Theory] here and below: JobPhase is internal, and a
    // public xUnit test method may not take an internal parameter type. The enum
    // belongs beside BackgroundJobs rather than being widened to suit the
    // harness.
    [Fact]
    public void AnySessionTheDaemonListsAsAJobIsABackgroundSession()
    {
        foreach (var phase in new[] { JobPhase.Working, JobPhase.Parked, JobPhase.Done })
        {
            Assert.Equal(LocalSessionShape.Background, SessionPresence.ShapeOf(Status(), phase));
        }
    }

    // A finished job is still a background session. The rules that drop its orb
    // live in JudgeReachability and the sweep, and neither of them needs this to
    // lie about what it was.
    [Fact]
    public void AFinishedJobIsStillABackgroundSessionRatherThanATerminalOne()
    {
        Assert.Equal(
            LocalSessionShape.Background,
            SessionPresence.ShapeOf(Status(), JobPhase.Done));
    }

    // A background worker is started by the daemon and carries no
    // --parent-session-id, so nothing ever writes a Lead onto one — but the
    // order is asserted rather than left to that being permanently true.
    [Fact]
    public void AJobThatSomehowCarriesALeadIsStillABackgroundSession()
    {
        Assert.Equal(
            LocalSessionShape.Background,
            SessionPresence.ShapeOf(Status(lead: "lead-1"), JobPhase.Parked));
    }

    [Fact]
    public void ASessionWithALeadAndNoJobRowIsATeammate()
    {
        Assert.Equal(
            LocalSessionShape.Teammate,
            SessionPresence.ShapeOf(Status(lead: "lead-1"), JobPhase.NotAJob));
    }

    [Fact]
    public void ASessionWithNoLeadAndNoJobRowIsATerminalSession()
    {
        Assert.Equal(
            LocalSessionShape.Terminal,
            SessionPresence.ShapeOf(Status(), JobPhase.NotAJob));
    }

    // An unreadable listing must not reclassify anything. Unknown falls through
    // to the team test exactly as NotAJob does, which is what keeps a momentary
    // CLI failure from turning every teammate on screen into a terminal session
    // — and, one rule down, from un-parking every orphan in the same tick.
    [Fact]
    public void AnUnreadableListingLeavesTheTeamAndTerminalReadingUntouched()
    {
        Assert.Equal(
            LocalSessionShape.Teammate,
            SessionPresence.ShapeOf(Status(lead: "lead-1"), JobPhase.Unknown));

        Assert.Equal(
            LocalSessionShape.Terminal,
            SessionPresence.ShapeOf(Status(), JobPhase.Unknown));
    }

    // --- WorthAskingTheDaemon ------------------------------------------------

    // The gate that keeps the subprocess off a quiet machine. `claude agents
    // --json` is cached for ten seconds and the scan runs every two, so a rule
    // that asked about every session would spawn a `claude` process every ten
    // seconds forever — on a machine with nothing but terminal sessions, in
    // service of a question whose answer cannot change anything.
    [Fact]
    public void OnlyASessionTheDaemonCouldKnowAboutIsWorthAsking()
    {
        // A background worker's file: a pid of its own, and no terminal, because
        // the daemon that runs it has none to inherit.
        Assert.True(SessionPresence.WorthAskingTheDaemon(
            Status(), knowsATerminal: false, sharesItsPid: false));

        // A hook older than the session_pid field. Asked whatever else is true,
        // which is the rule that predates this one.
        Assert.True(SessionPresence.WorthAskingTheDaemon(
            Status(pid: 0), knowsATerminal: true, sharesItsPid: false));

        // An ordinary session: a window on this machine, its own pid, and nothing
        // else sharing it. Almost every session almost all of the time, and the
        // one case that must not spend a subprocess.
        Assert.False(SessionPresence.WorthAskingTheDaemon(
            Status(), knowsATerminal: true, sharesItsPid: false));
    }

    // The third shape, and the one that was missing. An Agent-View-dispatched
    // background session does not fork a process — it starts a second
    // conversation inside the `claude` process already running — so its file
    // names a live interactive session's pid. BackgroundJobs' own comment names
    // it as one of exactly two shapes only the daemon can settle, and the first
    // version of this gate refused to ask about it.
    //
    // Refused twice over, in fact: InheritTerminalInfo donates terminal fields
    // between files sharing a pid, so such a file *acquires* a terminal before
    // this is asked and then reads as an ordinary session. Which is also why the
    // clause needs no pre-inheritance snapshot beside it — the donation only
    // happens inside a (pid, source) group, so anything it could have touched
    // shares its pid by definition.
    [Fact]
    public void AFileSharingItsPidWithAnotherIsAlwaysWorthAsking()
    {
        Assert.True(SessionPresence.WorthAskingTheDaemon(
            Status(), knowsATerminal: true, sharesItsPid: true));

        Assert.True(SessionPresence.WorthAskingTheDaemon(
            Status(pid: 0), knowsATerminal: true, sharesItsPid: true));
    }

    // Codex has no background jobs to be one of, and a gateway or bridged
    // session is not on this machine at all — asking the local daemon about one
    // is asking about a session it has never heard of, once per scan, forever.
    [Fact]
    public void TheDaemonIsNeverAskedAboutAnythingButClaudeCode()
    {
        var others = new[]
        {
            SessionSource.Codex, SessionSource.OpenClaw, SessionSource.RemoteControl,
        };

        foreach (var source in others)
        {
            Assert.False(SessionPresence.WorthAskingTheDaemon(
                Status(source: source), knowsATerminal: false, sharesItsPid: false));

            Assert.False(SessionPresence.WorthAskingTheDaemon(
                Status(source: source, pid: 0), knowsATerminal: false, sharesItsPid: false));

            Assert.False(SessionPresence.WorthAskingTheDaemon(
                Status(source: source), knowsATerminal: true, sharesItsPid: true));
        }
    }

    // --- IsParked: background sessions ---------------------------------------

    [Fact]
    public void ABackgroundJobBetweenTurnsIsParked()
    {
        Assert.True(SessionPresence.IsParked(
            LocalSessionShape.Background, "idle", JobPhase.Parked,
            leadSeen: false, leadIsLiveJob: false));
    }

    [Fact]
    public void ABackgroundJobMidTurnIsNotParked()
    {
        Assert.False(SessionPresence.IsParked(
            LocalSessionShape.Background, "generating", JobPhase.Working,
            leadSeen: false, leadIsLiveJob: false));
    }

    // The cache-lag fix, and the reason the file's own state is consulted at all.
    //
    // The daemon's listing is cached for ten seconds; the hook rewrites the
    // status file the instant a job resumes. So when the two disagree, the file
    // is the fresher of them — and requiring it to still say "idle" is what
    // makes un-dimming prompt (the next 2s scan) even though dimming lags. That
    // asymmetry is deliberate: an orb still dim while work visibly resumes is a
    // lie about the thing the user is watching happen.
    [Theory]
    [InlineData("generating")]
    [InlineData("waiting")]
    public void ABlockedRowWhoseFileSaysWorkResumedIsNotParked(string state)
    {
        Assert.False(SessionPresence.IsParked(
            LocalSessionShape.Background, state, JobPhase.Parked,
            leadSeen: false, leadIsLiveJob: false));
    }

    // Fail open. Unknown is "there was no listing", and no orb may change what
    // it looks like on the strength of a question that could not be asked.
    [Fact]
    public void OnlyBlockedParksABackgroundOrb()
    {
        var phases = new[] { JobPhase.Unknown, JobPhase.NotAJob, JobPhase.Working, JobPhase.Done };

        foreach (var phase in phases)
        {
            Assert.False(SessionPresence.IsParked(
                LocalSessionShape.Background, "idle", phase,
                leadSeen: false, leadIsLiveJob: false));
        }
    }

    // --- IsParked: teammates and terminals -----------------------------------

    // The orphan case: a real claude process in a detached tmux socket, whose
    // lead has gone. Its arrows have already silently vanished — TeamLinks draws
    // nothing to a lead that is not on screen — so the orb is the last thing
    // left saying anything about it.
    [Fact]
    public void ATeammateWhoseLeadHasGoneIsParked()
    {
        Assert.True(SessionPresence.IsParked(
            LocalSessionShape.Teammate, "idle", JobPhase.NotAJob,
            leadSeen: false, leadIsLiveJob: false));
    }

    [Fact]
    public void ATeammateWhoseLeadIsOnThisScanIsNotParked()
    {
        Assert.False(SessionPresence.IsParked(
            LocalSessionShape.Teammate, "idle", JobPhase.NotAJob,
            leadSeen: true, leadIsLiveJob: false));
    }

    // A team led from a background job is the ordinary case, not an exception:
    // the lead has no status file of its own to be seen through, so the daemon's
    // listing is the only place it exists. Parking its members would dim a live
    // team.
    [Fact]
    public void ATeammateWhoseLeadIsALiveJobIsNotParked()
    {
        Assert.False(SessionPresence.IsParked(
            LocalSessionShape.Teammate, "idle", JobPhase.NotAJob,
            leadSeen: false, leadIsLiveJob: true));
    }

    // A teammate's own state is not consulted, unlike a background session's:
    // there is no cached listing in the way here — the lead either is on this
    // scan or is not — and a member that is mid-turn while its lead has gone is
    // still orphaned. Asserted so the two rules do not quietly converge.
    [Theory]
    [InlineData("idle")]
    [InlineData("generating")]
    [InlineData("waiting")]
    public void AnOrphanedTeammateIsParkedWhateverItsFileSays(string state)
    {
        Assert.True(SessionPresence.IsParked(
            LocalSessionShape.Teammate, state, JobPhase.NotAJob,
            leadSeen: false, leadIsLiveJob: false));
    }

    // Somebody at a keyboard. A terminal session between turns is still a
    // terminal session — sitting there waiting for you is the whole of what it
    // does — so nothing about this shape is ever dimmed, whatever else is true.
    [Fact]
    public void ATerminalSessionIsNeverParked()
    {
        var cases = new (JobPhase Phase, string State)[]
        {
            (JobPhase.NotAJob, "idle"),
            (JobPhase.Unknown, "idle"),
            (JobPhase.NotAJob, "generating"),

            // Including the one combination that could not happen — a terminal
            // session the daemon calls blocked — because ShapeOf would have
            // called that Background, and this rule must not be the thing
            // relied on to notice.
            (JobPhase.Parked, "idle"),
        };

        foreach (var (phase, state) in cases)
        {
            Assert.False(SessionPresence.IsParked(
                LocalSessionShape.Terminal, state, phase,
                leadSeen: false, leadIsLiveJob: false));
        }
    }

    // --- the two menu items --------------------------------------------------

    // Dismiss deletes a status file, and only a local CLI session has one.
    [Theory]
    [InlineData(SessionSource.ClaudeCode, true)]
    [InlineData(SessionSource.Codex, true)]
    [InlineData(SessionSource.OpenClaw, false)]
    [InlineData(SessionSource.RemoteControl, false)]
    public void DismissIsOfferedForTheSessionsThatHaveAFileOnDisk(
        SessionSource source, bool expected)
    {
        Assert.Equal(expected, SessionPresence.CanDismiss(Status(source: source)));
    }

    // End needs a pid to signal on top of that. Offered nowhere it would not do
    // exactly what it says, because it is the one action here that cannot be
    // undone.
    [Theory]
    [InlineData(SessionSource.ClaudeCode, 4321, true)]
    [InlineData(SessionSource.Codex, 4321, true)]
    [InlineData(SessionSource.OpenClaw, 4321, false)]
    [InlineData(SessionSource.RemoteControl, 4321, false)]
    [InlineData(SessionSource.ClaudeCode, 0, false)]
    [InlineData(SessionSource.ClaudeCode, -1, false)]
    public void EndIsOfferedOnlyWhereThereIsAProcessToEnd(
        SessionSource source, int pid, bool expected)
    {
        Assert.Equal(expected, SessionPresence.CanEndSession(Status(source: source, pid: pid)));
    }

    // A hook older than the session_pid field writes 0, which is why these are
    // two rules rather than one with an extra clause: such a session can still
    // be dismissed from the screen, it just cannot be ended.
    [Fact]
    public void ASessionWithNoRecordedPidCanStillBeDismissed()
    {
        var pidless = Status(pid: 0);

        Assert.True(SessionPresence.CanDismiss(pidless));
        Assert.False(SessionPresence.CanEndSession(pidless));
    }
}
