using System;
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

    // --- PresenceOf: background sessions -------------------------------------

    private static OrbPresence Presence(
        LocalSessionShape shape = LocalSessionShape.Background,
        string state = "idle",
        JobPhase phase = JobPhase.Parked,
        bool leadSeen = false,
        bool leadIsLiveJob = false,
        bool attached = false) =>
        SessionPresence.PresenceOf(shape, state, phase, leadSeen, leadIsLiveJob, attached);

    // The daemon's own word for a blocked job is "needs input", and several of
    // the ones this was written for are literally holding a question — so a
    // parked job is not merely quiet, and the mark is the difference between
    // "nothing here" and "something here for you".
    [Fact]
    public void ABackgroundJobBetweenTurnsNeedsInput()
    {
        Assert.Equal(OrbPresence.NeedsInput, Presence());
    }

    [Fact]
    public void ABackgroundJobMidTurnIsPresent()
    {
        Assert.Equal(
            OrbPresence.Present,
            Presence(state: "generating", phase: JobPhase.Working));
    }

    // A job that is over. Marked differently from one that needs input because
    // the two are opposite instructions — one wants you, one wants nothing ever
    // again — and it is on its way off the screen anyway: the sweep has the same
    // ten minutes' evidence by the time this is drawn.
    [Fact]
    public void AFinishedJobIsFinishedWhateverElseIsTrue()
    {
        Assert.Equal(OrbPresence.Finished, Presence(phase: JobPhase.Done));

        // Including with somebody attached to it, which is reading rather than
        // working, and including a file that says it is mid-turn — "done" is the
        // daemon's last word on a job and there is nothing after it.
        Assert.Equal(OrbPresence.Finished, Presence(phase: JobPhase.Done, attached: true));
        Assert.Equal(
            OrbPresence.Finished,
            Presence(state: "generating", phase: JobPhase.Done));
    }

    // The contradiction the user hit within minutes: they attached to all three
    // parked sessions and the orbs stayed grey. Attaching changes nothing this
    // app was watching — the status file records the worker's ancestry and never
    // the viewer's, and the daemon still says "blocked" — so the only place the
    // person's presence exists is the process table.
    [Fact]
    public void AParkedJobSomebodyIsAttachedToIsPresent()
    {
        Assert.Equal(OrbPresence.Present, Presence(attached: true));
    }

    // The cache-lag fix, and the reason the file's own state is consulted at all.
    //
    // The daemon's listing is cached for ten seconds; the hook rewrites the
    // status file the instant a job resumes. So when the two disagree, the file
    // is the fresher of them — and requiring it to still say "idle" is what makes
    // coming back to life prompt (the next 2s scan) even though going quiet lags.
    // That asymmetry is deliberate: an orb still dim while work visibly resumes
    // is a lie about the thing the user is watching happen.
    [Theory]
    [InlineData("generating")]
    [InlineData("waiting")]
    public void ABlockedRowWhoseFileSaysWorkResumedIsPresent(string state)
    {
        Assert.Equal(OrbPresence.Present, Presence(state: state));
    }

    // Fail open. Unknown is "there was no listing", and no orb may change what it
    // looks like on the strength of a question that could not be asked.
    [Fact]
    public void OnlyBlockedEverMarksABackgroundOrbAsNeedingInput()
    {
        var phases = new[] { JobPhase.Unknown, JobPhase.NotAJob, JobPhase.Working };

        foreach (var phase in phases)
        {
            Assert.Equal(OrbPresence.Present, Presence(phase: phase));
        }
    }

    // --- PresenceOf: teammates and terminals ---------------------------------

    // The orphan case: a real claude process in a detached tmux socket, whose
    // lead has gone. Dimmed with no mark, deliberately — nothing is waiting on
    // the user and nothing has finished, so there is nothing to say beyond the
    // dimming itself. Its arrows have already silently vanished, which is why the
    // orb is the last thing left saying anything about it.
    [Fact]
    public void ATeammateWhoseLeadHasGoneIsParkedWithNoMark()
    {
        Assert.Equal(
            OrbPresence.Parked,
            Presence(shape: LocalSessionShape.Teammate, phase: JobPhase.NotAJob));
    }

    [Fact]
    public void ATeammateWhoseLeadIsOnThisScanIsPresent()
    {
        Assert.Equal(
            OrbPresence.Present,
            Presence(shape: LocalSessionShape.Teammate, phase: JobPhase.NotAJob, leadSeen: true));
    }

    // A team led from a background job is the ordinary case, not an exception:
    // the lead has no status file of its own to be seen through, so the daemon's
    // listing is the only place it exists. Dimming its members would dim a live
    // team.
    [Fact]
    public void ATeammateWhoseLeadIsALiveJobIsPresent()
    {
        Assert.Equal(
            OrbPresence.Present,
            Presence(shape: LocalSessionShape.Teammate, phase: JobPhase.NotAJob,
                leadIsLiveJob: true));
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
        Assert.Equal(
            OrbPresence.Parked,
            Presence(shape: LocalSessionShape.Teammate, state: state, phase: JobPhase.NotAJob));
    }

    // Somebody at a keyboard. A terminal session between turns is still a
    // terminal session — sitting there waiting for you is the whole of what it
    // does — so nothing about this shape is ever dimmed, whatever else is true.
    [Fact]
    public void ATerminalSessionIsAlwaysPresent()
    {
        var cases = new (JobPhase Phase, string State)[]
        {
            (JobPhase.NotAJob, "idle"),
            (JobPhase.Unknown, "idle"),
            (JobPhase.NotAJob, "generating"),

            // Including the two combinations that could not happen — a terminal
            // session the daemon calls blocked or done — because ShapeOf would
            // have called those Background, and this rule must not be the thing
            // relied on to notice.
            (JobPhase.Parked, "idle"),
            (JobPhase.Done, "idle"),
        };

        foreach (var (phase, state) in cases)
        {
            Assert.Equal(
                OrbPresence.Present,
                Presence(shape: LocalSessionShape.Terminal, state: state, phase: phase));
        }
    }

    // --- HasAttachClient -----------------------------------------------------

    // `claude attach` accepts the short job id and echoes it back that way, so a
    // window opened by hand with `claude attach bd7919f8` has to count as session
    // bd7919f8-…. Compared by prefix in both directions for that reason, the same
    // way AgentTeamViewer already compares them.
    [Fact]
    public void AnAttachClientIsMatchedByEitherFormOfTheId()
    {
        var shortForm = new[] { "0e043819" };

        Assert.True(SessionPresence.HasAttachClient(
            shortForm, "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c"));

        var fullForm = new[] { "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c" };
        Assert.True(SessionPresence.HasAttachClient(fullForm, "0e043819"));
    }

    [Fact]
    public void AnUnrelatedAttachClientDoesNotCount()
    {
        Assert.False(SessionPresence.HasAttachClient(
            new[] { "5f6960b2" }, "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c"));
    }

    [Fact]
    public void WithNothingAttachedNothingMatches()
    {
        Assert.False(SessionPresence.HasAttachClient(
            Array.Empty<string>(), "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c"));
    }

    // A scan that could not be done answers "attached", and the direction is the
    // whole point. Wrong-true leaves a genuinely parked orb bright, which is this
    // branch's original bug in its mildest form. Wrong-false dims a session the
    // user is sitting in and typing at — the contradiction that prompted the rule.
    // Only one of the two ways to be wrong argues with the person at the screen.
    [Fact]
    public void AScanThatCouldNotBeDoneCountsAsAttached()
    {
        Assert.True(SessionPresence.HasAttachClient(null, "0e043819"));

        // And it reaches all the way through: nothing is dimmed on the strength
        // of a question that could not be asked.
        Assert.Equal(OrbPresence.Present, Presence(attached: true));
    }

    // Nothing was asked, so nothing matches — rather than every attach client on
    // the machine matching an empty id by prefix, which is what a bare
    // StartsWith would do.
    [Fact]
    public void AnEmptySessionIdMatchesNothing()
    {
        Assert.False(SessionPresence.HasAttachClient(new[] { "0e043819" }, ""));
    }

    // --- RuledOutAsAJob ------------------------------------------------------

    // The gate on an orb's existence, and the reversal this round made: only a
    // listing that was read and did not name the session rules it out. A finished
    // job is no longer ruled out, which is what lets it stay on screen — dimmed
    // and marked as finished — for as long as its status file does.
    [Fact]
    public void OnlyAReadListingThatDoesNotNameTheSessionRulesItOut()
    {
        Assert.True(SessionPresence.RuledOutAsAJob(JobPhase.NotAJob));

        foreach (var phase in new[] { JobPhase.Working, JobPhase.Parked, JobPhase.Done, JobPhase.Unknown })
        {
            Assert.False(SessionPresence.RuledOutAsAJob(phase));
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
