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

    // --- KnownAttachClient ---------------------------------------------------

    // The click's half of the same question, and the direction is the whole
    // reason it is a second function rather than a second caller. It is the one
    // authority on "is anything already attached" for a click — it replaced
    // AgentTeamViewer.AttachedAlready, which asked the identical question of the
    // identical `ps -eo args=` population and differed only in being uncached.
    [Fact]
    public void TheClickSeesTheSameMatchesTheDimmingRuleDoes()
    {
        const string full = "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c";

        Assert.True(SessionPresence.KnownAttachClient(new[] { "0e043819" }, full));
        Assert.False(SessionPresence.KnownAttachClient(new[] { "5f6960b2" }, full));
        Assert.False(SessionPresence.KnownAttachClient(Array.Empty<string>(), full));
        Assert.False(SessionPresence.KnownAttachClient(new[] { "0e043819" }, ""));
    }

    // And where the two part company. A scan that could not be done means
    // "attached" for dimming, because being wrong there dims a session somebody
    // is typing into; it means "not attached" for a click, because being wrong
    // there raises an app with no such window and creates nothing — a gesture
    // that does nothing, which is the complaint the whole click ladder exists to
    // answer. One duplicate pane is visible and closable; a dead click is
    // neither.
    [Fact]
    public void AScanThatCouldNotBeDoneStopsTheDimmingAndNotTheClick()
    {
        Assert.True(SessionPresence.HasAttachClient(null, "0e043819"));
        Assert.False(SessionPresence.KnownAttachClient(null, "0e043819"));
    }

    // --- SameJobId -----------------------------------------------------------

    // The prefix rule itself, now that it is one function rather than three
    // copies of an inline comparison. It decides whether a click opens a *second*
    // window onto a conversation somebody is already reading, which is why the
    // copies were worth collapsing: two of them disagreeing about one pair of ids
    // is a duplicate window or a dead click, and nothing in between.
    [Fact]
    public void TwoFormsOfOneJobIdAreTheSameJob()
    {
        const string full = "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c";

        Assert.True(SessionPresence.SameJobId(full, "0e043819"));
        Assert.True(SessionPresence.SameJobId("0e043819", full));
        Assert.True(SessionPresence.SameJobId(full, full));
    }

    [Fact]
    public void DifferentJobIdsAreNotTheSameJob()
    {
        Assert.False(SessionPresence.SameJobId("0e043819", "5f6960b2"));

        // Sharing a leading character is not sharing a prefix in either
        // direction — asserted because a looser rule would match half the
        // machine's jobs to each other.
        Assert.False(SessionPresence.SameJobId("0e043819", "0f043819"));
    }

    // Empty matches nothing, in either position. Without this a bare StartsWith
    // would make an empty id match every job on the machine — and the empty case
    // is real on both sides: a `ps` line whose third word is missing, and an orb
    // whose session id never reached the click.
    [Fact]
    public void AnEmptyIdIsNeverTheSameJobAsAnything()
    {
        Assert.False(SessionPresence.SameJobId("", "0e043819"));
        Assert.False(SessionPresence.SameJobId("0e043819", ""));
        Assert.False(SessionPresence.SameJobId("", ""));
    }

    // And through the collection rule, since that is where an empty id actually
    // arrives from: AttachedJobIds parses argv, and a malformed line can put a
    // blank in the set beside good ones.
    [Fact]
    public void ABlankInTheAttachedSetDoesNotMatchEverything()
    {
        Assert.False(SessionPresence.HasAttachClient(
            new[] { "", "5f6960b2" }, "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c"));

        Assert.True(SessionPresence.HasAttachClient(
            new[] { "", "0e043819" }, "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c"));
    }

    // --- TitleSaysViewing ----------------------------------------------------

    private const string Glyph = "\u2733 ";
    private const string Work = "Claude desktop app multiple profiles bug";

    // What Claude Code's TUI actually writes, measured off a live pane: the
    // glyph, a space, then the conversation title verbatim.
    [Fact]
    public void APaneTitledByClaudeCodeMatchesItsSession()
    {
        Assert.True(SessionPresence.TitleSaysViewing(Glyph + Work, Work));
    }

    // The false positive this whole clause exists for. A pane title is writable
    // by any program — one `printf` of an OSC 2 escape — so an editor opened on a
    // file of the same name would otherwise be mistaken for the session, and the
    // click would be sent to it. Without the glyph there is no match.
    [Fact]
    public void APaneSomeOtherProgramTitledDoesNotMatch()
    {
        Assert.False(SessionPresence.TitleSaysViewing(Work, Work));
        Assert.False(SessionPresence.TitleSaysViewing("vim " + Work, Work));
        Assert.False(SessionPresence.TitleSaysViewing("~/src \u2014 " + Work, Work));
    }

    // A different glyph is not this glyph. Stated because the observed set is one
    // member and a future version could grow another, and the direction of that
    // failure is the safe one: no match sends the click down the attach ladder,
    // where the worst case is a duplicate pane the user can see and close.
    [Fact]
    public void AnotherGlyphIsNotTheOne()
    {
        Assert.False(SessionPresence.TitleSaysViewing("\u2733" + Work, Work));   // no space
        Assert.False(SessionPresence.TitleSaysViewing("\u273b " + Work, Work));  // a spinner frame
        Assert.False(SessionPresence.TitleSaysViewing("* " + Work, Work));
    }

    // An exact suffix, not a contains: a pane whose title merely mentions the
    // session — a shell sitting in a directory of that name, a log being tailed —
    // is not showing the conversation.
    [Fact]
    public void APaneThatOnlyMentionsTheTitleDoesNotMatch()
    {
        Assert.False(SessionPresence.TitleSaysViewing(Glyph + Work + " (log)", Work));
        Assert.False(SessionPresence.TitleSaysViewing(Glyph + "re: " + Work + " notes", Work));

        // A longer conversation title that ends with the shorter one is a genuine
        // suffix match and is allowed — the app cannot tell those apart, and the
        // process check plus the claimed-pane rule are what narrow it.
        Assert.True(SessionPresence.TitleSaysViewing(Glyph + "re: " + Work, Work));
    }

    // Nothing to match on either side. A session whose title the hook never
    // caught is the common case here, and it must fall through rather than match
    // every Claude pane on the machine.
    [Theory]
    [InlineData(null, "x")]
    [InlineData("", "x")]
    [InlineData("\u2733 x", null)]
    [InlineData("\u2733 x", "")]
    [InlineData(null, null)]
    public void NothingToMatchIsNotAMatch(string? paneTitle, string? sessionTitle)
    {
        Assert.False(SessionPresence.TitleSaysViewing(paneTitle, sessionTitle));
    }

    // The glyph alone, with no title after it — the degenerate case that would
    // otherwise satisfy both StartsWith and EndsWith against a title that is
    // itself the glyph.
    [Fact]
    public void TheGlyphWithNothingAfterItIsNotAMatch()
    {
        Assert.False(SessionPresence.TitleSaysViewing("\u2733 ", "\u2733 "));
        Assert.False(SessionPresence.TitleSaysViewing("\u2733", "\u2733"));
    }

    // --- LooksLikeClaudeBinary -----------------------------------------------

    // Both shapes observed live on one machine, and neither is what the obvious
    // rule catches. An interactive session runs as plain `claude`; a team member
    // runs as the versioned install path, whose file *name* is a version number —
    // which is also what tmux reports as that pane's current command.
    [Fact]
    public void BothShapesOfTheClaudeBinaryAreRecognised()
    {
        Assert.True(SessionPresence.LooksLikeClaudeBinary("claude"));
        Assert.True(SessionPresence.LooksLikeClaudeBinary("/Users/w/.local/bin/claude"));
        Assert.True(SessionPresence.LooksLikeClaudeBinary(
            "/Users/w/.local/share/claude/versions/2.1.246"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-zsh")]
    [InlineData("vim")]
    [InlineData("/usr/bin/tmux")]
    [InlineData("/Users/w/claude-notes/versions/1.0")]
    public void AnythingElseIsNotTheClaudeBinary(string? argv0)
    {
        Assert.False(SessionPresence.LooksLikeClaudeBinary(argv0));
    }

    // --- ClaimStillHolds -----------------------------------------------------

    // The claim describes something still true: the pane is showing the
    // claimant's own conversation.
    [Fact]
    public void AClaimTheCurrentTitleConfirmsStillHolds()
    {
        Assert.True(SessionPresence.ClaimStillHolds(Work, Glyph + Work));
    }

    // The client moved on — the pane is showing a different conversation now — so
    // the claim describes the past and the pane returns to candidacy. Without this
    // a stale exclusion pushes a click that could have focused a real viewer down
    // into the attach ladder to make a duplicate, which is the failure the
    // exclusion exists to prevent, arriving from the other side.
    [Fact]
    public void AClaimTheCurrentTitleContradictsIsReleased()
    {
        Assert.False(SessionPresence.ClaimStillHolds(Work, Glyph + "Something else entirely"));
    }

    // A pane that is no longer running a Claude session at all — someone quit and
    // typed something. Not the claimant's conversation, so not the claimant's
    // pane any more.
    [Fact]
    public void AClaimOnAPaneNoLongerShowingASessionIsReleased()
    {
        Assert.False(SessionPresence.ClaimStillHolds(Work, "~/src"));
        Assert.False(SessionPresence.ClaimStillHolds(Work, ""));
        Assert.False(SessionPresence.ClaimStillHolds(Work, null));
    }

    // An untitled claimant keeps its claim, and the direction is the decision
    // rather than a fallthrough. An empty title is not evidence that the client
    // moved on; it is the absence of evidence, and it is common for an honest
    // reason — the hook records a title when it fires, so a session renamed since
    // reads as untitled. Releasing on no evidence would strip the exclusion from
    // exactly the claims that cannot defend themselves. A stale claim costs a
    // duplicate pane, visible and closable; a released good claim costs a click
    // landing in someone else's conversation, silently.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnUntitledClaimantKeepsItsClaim(string? claimantTitle)
    {
        Assert.True(SessionPresence.ClaimStillHolds(claimantTitle, Glyph + Work));
        Assert.True(SessionPresence.ClaimStillHolds(claimantTitle, "anything at all"));
        Assert.True(SessionPresence.ClaimStillHolds(claimantTitle, null));
    }

    // The collision case, which must keep working: every member of an agent team
    // shares the team session's title, so a teammate's claim on its own pane is
    // confirmed by that pane's title even though the title is also the clicked
    // session's. That is what keeps the teammate protection intact — the reason
    // the exclusion was added in the first place.
    [Fact]
    public void ATeammatesClaimSurvivesTheSharedTitle()
    {
        Assert.True(SessionPresence.ClaimStillHolds(Work, Glyph + Work));
    }

    // --- ViewerAmong ---------------------------------------------------------

    private static SessionPresence.ViewerPane Pane(
        string id, string window = "warren:1", bool active = true, bool claimed = false) =>
        new(id, window, active, claimed);

    [Fact]
    public void NoCandidatesIsNoViewer()
    {
        var (verdict, pane) = SessionPresence.ViewerAmong(
            Array.Empty<SessionPresence.ViewerPane>(), "warren:1");

        Assert.Equal(SessionPresence.ViewerVerdict.NoneFound, verdict);
        Assert.Null(pane);
    }

    // The case round seven exists for: the pane in front of them is showing it,
    // so the click does nothing.
    [Fact]
    public void TheActivePaneOfTheUsersOwnWindowMeansTheyAreLookingAtIt()
    {
        var (verdict, _) = SessionPresence.ViewerAmong(
            new[] { Pane("%6", "warren:1", active: true) }, "warren:1");

        Assert.Equal(SessionPresence.ViewerVerdict.TheUserIsLookingAtIt, verdict);
    }

    // Same window, but not the pane they are looking at — a split they have
    // scrolled away from. That is "elsewhere": selecting it is a real move and a
    // wanted one.
    [Fact]
    public void AnInactivePaneInTheirWindowIsStillElsewhere()
    {
        var (verdict, pane) = SessionPresence.ViewerAmong(
            new[] { Pane("%7", "warren:1", active: false) }, "warren:1");

        Assert.Equal(SessionPresence.ViewerVerdict.ElsewhereInTmux, verdict);
        Assert.Equal("%7", pane);
    }

    // Their window could not be resolved, so nothing can be called the pane they
    // are looking at and every match is treated as elsewhere. Fails toward doing
    // something visible rather than toward silently doing nothing, which is the
    // right way round: a click that opens a pane is recoverable, a click that
    // decides you were already looking at something is not distinguishable from
    // a broken one.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithNoKnownWindowEveryMatchIsElsewhere(string? usersWindow)
    {
        var (verdict, pane) = SessionPresence.ViewerAmong(
            new[] { Pane("%6", "warren:1", active: true) }, usersWindow);

        Assert.Equal(SessionPresence.ViewerVerdict.ElsewhereInTmux, verdict);
        Assert.Equal("%6", pane);
    }

    // The collision guard, and the asymmetry that makes a shared title safe. Four
    // panes carried one identical title for three sessions on the machine this was
    // built for, because every member of an agent team inherits the team session's
    // title. A pane another session's status file claims is the pane most likely to
    // be one of those, and it is reachable by its own recorded coordinates anyway.
    [Fact]
    public void APaneAnotherSessionClaimsIsNotFocused()
    {
        var (verdict, pane) = SessionPresence.ViewerAmong(
            new[] { Pane("%53", "claude-swarm:1", active: true, claimed: true) }, "warren:1");

        Assert.Equal(SessionPresence.ViewerVerdict.NoneFound, verdict);
        Assert.Null(pane);
    }

    // ...but the claim does not stop the do-nothing answer, and that is
    // deliberate. Whatever a matched pane in front of the user holds, they are
    // reading it, so doing nothing cannot be wrong about which session it found.
    [Fact]
    public void AClaimedPaneTheUserIsLookingAtStillStopsTheClick()
    {
        var (verdict, _) = SessionPresence.ViewerAmong(
            new[] { Pane("%6", "warren:1", active: true, claimed: true) }, "warren:1");

        Assert.Equal(SessionPresence.ViewerVerdict.TheUserIsLookingAtIt, verdict);
    }

    // The residual ambiguity, and the one case rarity is an honest answer to: two
    // sessions with the same title each viewed in an *unclaimed* pane — two leads
    // someone renamed identically, both open. Nothing can tell those apart, so the
    // tie-break is stated: active in its own window, else first found. The team
    // collision is a different thing and is not answered by rarity — see
    // APaneAnotherSessionClaimsIsNotFocused.
    [Fact]
    public void AmongSeveralElsewhereTheActiveOneWins()
    {
        var (verdict, pane) = SessionPresence.ViewerAmong(
            new[]
            {
                Pane("%21", "claude-swarm:1", active: false),
                Pane("%53", "claude-swarm:1", active: true),
            },
            "warren:1");

        Assert.Equal(SessionPresence.ViewerVerdict.ElsewhereInTmux, verdict);
        Assert.Equal("%53", pane);
    }

    [Fact]
    public void WithNoneActiveTheFirstFoundWins()
    {
        var (_, pane) = SessionPresence.ViewerAmong(
            new[]
            {
                Pane("%21", "claude-swarm:1", active: false),
                Pane("%53", "claude-swarm:2", active: false),
            },
            "warren:1");

        Assert.Equal("%21", pane);
    }

    // A claimed pane is skipped rather than taken as the answer, so an unclaimed
    // one behind it still wins.
    [Fact]
    public void AClaimedPaneDoesNotShadowAnUnclaimedOne()
    {
        var (verdict, pane) = SessionPresence.ViewerAmong(
            new[]
            {
                Pane("%53", "claude-swarm:1", active: true, claimed: true),
                Pane("%99", "other:1", active: false),
            },
            "warren:1");

        Assert.Equal(SessionPresence.ViewerVerdict.ElsewhereInTmux, verdict);
        Assert.Equal("%99", pane);
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
