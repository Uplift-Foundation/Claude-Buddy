using System;
using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.Tests;

// The three rules that decide whether a status file becomes an orb:
// SessionManager.JudgeLiveness, WantsAgentViewer and JudgeReachability.
//
// These are the most-corrected lines in the app and every case below is a bug
// somebody shipped, not a shape invented to walk a branch. The comments in
// SessionManager name them: a Ctrl+C'd session whose orb never went away, an
// orb per subagent that accumulated forever on the "keep orbs forever"
// setting, a Codex file that no rule on any path could remove, a background
// agent that vanished the moment the hook learned to record its own pid. A
// wrong answer here is either an orb that is a dead click or a live session
// with nothing on screen, and neither announces itself.
//
// isRunning and isLiveJob are the two seams: the real ones are a kill(2)
// against a pid this machine may not have and a shell-out to
// `claude agents --json`. Passing them in is what makes the rules answerable
// without either — the same reasoning Superseded's own comment records.
public class ScanVerdictTests
{
    private static readonly Func<int, bool> AllAlive = _ => true;
    private static readonly Func<int, bool> AllDead = _ => false;
    private static readonly Func<string, bool> NoLiveJobs = _ => false;
    private static readonly Func<string, bool> EveryIdIsALiveJob = _ => true;

    private static readonly HashSet<string> Nothing = new(StringComparer.Ordinal);

    private static readonly DateTime Now = new(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

    // A session that is doing everything right: a live process, a terminal to
    // jump to, and a file written a moment ago. Every test below is this with
    // one thing changed, which is what keeps each case about one rule.
    private static SessionStatus Healthy(
        SessionSource source = SessionSource.ClaudeCode,
        string state = "idle",
        int pid = 4321,
        string termProgram = "iTerm.app") =>
        new()
        {
            Source = source,
            State = state,
            SessionPid = pid,
            TermProgram = termProgram,
            Tty = "/dev/ttys004",
        };

    private static SessionManager.ScanVerdict Liveness(
        SessionStatus status,
        TimeSpan? staleAfter = null,
        DateTime? written = null,
        ISet<string>? superseded = null,
        Func<int, bool>? isRunning = null) =>
        SessionManager.JudgeLiveness(
            "session-1", status, written ?? Now, Now, staleAfter,
            superseded ?? Nothing, isRunning ?? AllAlive);

    // Defaults to "the transcript is there", so every case written before the
    // NothingToShow rule existed still asks what it was asking. A status with no
    // TranscriptPath at all cannot reach that rule anyway — which is most of
    // them below, and is the conservative half of the rule rather than an
    // accident of the fixtures.
    private static SessionManager.ScanVerdict Reachability(
        SessionStatus status,
        ISet<string>? leads = null,
        Func<string, bool>? isLiveJob = null,
        string sessionId = "session-1",
        Func<string, bool>? transcriptExists = null) =>
        SessionManager.JudgeReachability(
            sessionId, status, leads ?? Nothing, isLiveJob ?? NoLiveJobs,
            transcriptExists ?? TranscriptIsThere);

    private static readonly Func<string, bool> TranscriptIsThere = _ => true;
    private static readonly Func<string, bool> TranscriptIsGone = _ => false;

    // The shape CB-9 was filed for: a background job with no terminal of its
    // own, live in the daemon's listing, naming a transcript that was never
    // written. Everything about it is real — this is job "evidence" off a real
    // machine, `state: blocked`, `needs: send a prompt to start`.
    private static SessionStatus UnpromptedJob(
        SessionSource source = SessionSource.ClaudeCode,
        string transcriptPath =
            "/Users/x/.claude/projects/-Users-x-Evidence/0e043819.jsonl") =>
        new()
        {
            Source = source,
            State = "idle",
            SessionPid = 57319,
            TranscriptPath = transcriptPath,
        };

    // --- JudgeLiveness -------------------------------------------------------

    [Fact]
    public void AnOrdinarySessionSurvivesBothVerdicts()
    {
        Assert.Equal(SessionManager.ScanVerdict.Keep, Liveness(Healthy()));
        Assert.Equal(SessionManager.ScanVerdict.Keep, Reachability(Healthy()));
    }

    [Fact]
    public void ASupersededIdIsDroppedBeforeAnythingElseIsAsked()
    {
        // Superseded wins over every other reason, including reasons that would
        // also have dropped it — the caller only needs to know it is going, but
        // asking the daemon or the kernel about an id this process has already
        // moved on from is work nobody should be doing.
        var superseded = new HashSet<string>(StringComparer.Ordinal) { "session-1" };

        Assert.Equal(
            SessionManager.ScanVerdict.Superseded,
            Liveness(Healthy(), superseded: superseded, isRunning: AllDead));
    }

    [Fact]
    public void ADeadProcessDropsTheOrbEvenWhenTheSessionIsWaitingOnYou()
    {
        // The Ctrl+C case. `waiting` is exempt from the lifetime timer below
        // because no further hook fires until the user answers — so before this
        // rule existed, an unanswered prompt whose session had been killed sat
        // on screen indefinitely with nothing able to remove it.
        Assert.Equal(
            SessionManager.ScanVerdict.ProcessGone,
            Liveness(Healthy(state: "waiting"), isRunning: AllDead));
    }

    [Fact]
    public void ADeadProcessIsNotEvenAskedAboutWhenNoPidWasRecorded()
    {
        // A hook older than the session_pid field records 0, and liveness
        // cannot be checked at all — the lifetime timer is the only thing left.
        // isRunning is the failing one here to prove it is never consulted.
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Liveness(Healthy(pid: 0), isRunning: AllDead));
    }

    [Fact]
    public void AQuietSessionExpiresOnceItIsOlderThanTheLifetime()
    {
        var staleAfter = TimeSpan.FromMinutes(5);

        Assert.Equal(
            SessionManager.ScanVerdict.Expired,
            Liveness(Healthy(), staleAfter, written: Now - TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1))));

        // Exactly at the boundary is not past it.
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Liveness(Healthy(), staleAfter, written: Now - staleAfter));
    }

    [Fact]
    public void ForeverMeansNoOrbEverExpires()
    {
        // "null means never — an orb then lasts until its status file is
        // deleted (SessionEnd) or you reset it by hand."
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Liveness(Healthy(), staleAfter: null, written: new DateTime(2020, 1, 1)));
    }

    [Fact]
    public void AWaitingSessionNeverExpiresHoweverLongItHasBeenQuiet()
    {
        // Its mtime is frozen for as long as you are away from the prompt, so
        // the timer would hide the orb exactly when it matters most.
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Liveness(Healthy(state: "waiting"), TimeSpan.FromMinutes(5),
                     written: new DateTime(2020, 1, 1)));
    }

    [Fact]
    public void AGeneratingGatewaySessionNeverExpiresButAGeneratingLocalOneDoes()
    {
        // The asymmetry is the point, and it is not arbitrary: a local session
        // that is generating is having its file rewritten as it works, so it
        // cannot be caught by the timer in the first place. A gateway session
        // has no equivalent, so `generating` has to be exempted explicitly or
        // an agent mid-answer loses its orb.
        var old = new DateTime(2020, 1, 1);
        var staleAfter = TimeSpan.FromMinutes(5);

        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Liveness(Healthy(SessionSource.OpenClaw, state: "generating", pid: 0), staleAfter, old));

        Assert.Equal(
            SessionManager.ScanVerdict.Expired,
            Liveness(Healthy(state: "generating"), staleAfter, old));

        // And an idle gateway session is not exempt — only `generating` is.
        Assert.Equal(
            SessionManager.ScanVerdict.Expired,
            Liveness(Healthy(SessionSource.OpenClaw, pid: 0), staleAfter, old));
    }

    // --- WantsAgentViewer ----------------------------------------------------

    [Fact]
    public void AnOrdinarySessionWithATerminalIsNeverSentHuntingForAViewer()
    {
        Assert.False(SessionManager.WantsAgentViewer(
            "session-1", Healthy(), Nothing, EveryIdIsALiveJob));
    }

    [Fact]
    public void ALeadWithLiveAgentsAndNoTerminalHuntsForItsViewer()
    {
        // A team lead run inside `claude daemon run` has no terminal of its
        // own, and the window you actually watch it in — a `claude agents`
        // process — is nowhere in its process tree, so the hook could never
        // have recorded it.
        var leads = new HashSet<string>(StringComparer.Ordinal) { "session-1" };

        Assert.True(SessionManager.WantsAgentViewer(
            "session-1", Healthy(termProgram: ""), leads, NoLiveJobs));
    }

    [Fact]
    public void ASessionWithNoPidHuntsWithoutTheDaemonBeingAsked()
    {
        // The pid <= 0 arm exists so a session that records no process at all
        // still gets its viewer looked for. isLiveJob is the failing one to
        // show the arm stands on its own.
        Assert.True(SessionManager.WantsAgentViewer(
            "session-1", Healthy(pid: 0, termProgram: ""), Nothing, NoLiveJobs));
    }

    [Fact]
    public void ALiveBackgroundJobWithARecordedPidStillHunts()
    {
        // This is the regression the source comment calls "not subtle": once
        // the hook started recording a background agent's own pid, the old
        // `pid <= 0` proxy stopped matching, adoption stopped running for every
        // background agent, and JudgeReachability then dropped them all for
        // having no terminal.
        Assert.True(SessionManager.WantsAgentViewer(
            "session-1", Healthy(termProgram: ""), Nothing, EveryIdIsALiveJob));

        // And the same session, once the daemon says it is not a job, does not.
        Assert.False(SessionManager.WantsAgentViewer(
            "session-1", Healthy(termProgram: ""), Nothing, NoLiveJobs));
    }

    [Theory]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void OnlyClaudeCodeEverHuntsForAViewer(SessionSource source)
    {
        // There is no `claude agents` window to find for a Codex session, and
        // for a gateway or remote one TryAdopt's cwd-equality match would hand
        // it the tmux pane of unrelated local work — a click that looks like it
        // worked and goes somewhere else entirely.
        Assert.False(SessionManager.WantsAgentViewer(
            "session-1", Healthy(source, pid: 0, termProgram: ""), Nothing, EveryIdIsALiveJob));
    }

    [Fact]
    public void TheTtyAloneDoesNotCountAsKnowingATerminal()
    {
        // KnowsATerminal deliberately ignores Tty: it is the one field the
        // hook's walk always fills in, and on its own it can name a tmux pane's
        // pty, which belongs to a detached server rather than to any window. So
        // a tty-only session still goes looking for its viewer.
        Assert.False(SessionManager.KnowsATerminal(new SessionStatus { Tty = "/dev/ttys004" }));

        Assert.True(SessionManager.WantsAgentViewer(
            "session-1", Healthy(pid: 0, termProgram: ""), Nothing, NoLiveJobs));
    }

    [Theory]
    [InlineData("%7", "", "", 0)]
    [InlineData("", "iTerm.app", "", 0)]
    [InlineData("", "", "term-42", 0)]
    [InlineData("", "", "", 4242)]
    public void AnyOneOfTheFourRealTerminalFieldsIsEnough(
        string tmuxPane, string termProgram, string termId, int termPid)
    {
        Assert.True(SessionManager.KnowsATerminal(new SessionStatus
        {
            TmuxPane = tmuxPane, TermProgram = termProgram, TermId = termId, TermPid = termPid
        }));
    }

    // --- JudgeReachability ---------------------------------------------------

    [Fact]
    public void ALocalSessionWithNothingToClickIsDropped()
    {
        var blind = new SessionStatus { Source = SessionSource.ClaudeCode, SessionPid = 4321 };

        Assert.Equal(SessionManager.ScanVerdict.NoTerminal, Reachability(blind));
    }

    [Fact]
    public void ALeadWithLiveAgentsKeepsItsOrbWithNoTerminalAtAll()
    {
        // "Agents on screen pointing at nothing is a worse lie than an orb you
        // might not be able to click."
        var lead = new SessionStatus { Source = SessionSource.ClaudeCode, SessionPid = 4321 };
        var leads = new HashSet<string>(StringComparer.Ordinal) { "session-1" };

        Assert.Equal(SessionManager.ScanVerdict.Keep, Reachability(lead, leads));
    }

    [Fact]
    public void ALiveBackgroundJobKeepsItsOrbWithNoTerminalButOnlyForClaudeCode()
    {
        var claude = new SessionStatus { Source = SessionSource.ClaudeCode, SessionPid = 4321 };
        var codex = new SessionStatus { Source = SessionSource.Codex, SessionPid = 4321 };

        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Reachability(claude, isLiveJob: EveryIdIsALiveJob));

        // Codex has no background jobs to be one of, so the exemption must not
        // reach it however the daemon answers.
        Assert.Equal(
            SessionManager.ScanVerdict.NoTerminal,
            Reachability(codex, isLiveJob: EveryIdIsALiveJob));
    }

    [Theory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void ARemoteSessionIsNeverDroppedForHavingNoTerminal(SessionSource source)
    {
        // It has no terminal anywhere and never will — that is the point of it.
        // Left ungated this rule alone dropped every gateway orb, every scan.
        var remote = new SessionStatus { Source = source };

        Assert.Equal(SessionManager.ScanVerdict.Keep, Reachability(remote));
    }

    [Fact]
    public void AClaudeCodeFileNamingNoProcessGoesUnlessTheDaemonVouchesForIt()
    {
        // The subagent case: a subagent, a background agent and a status file
        // whose session ended all record no pid and all write "idle", so on
        // disk they are identical. Taking pid-less to mean "background agent"
        // put a permanent orb on screen for every subagent anyone spawned.
        var pidless = new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TermProgram = "iTerm.app"
        };

        Assert.Equal(SessionManager.ScanVerdict.NotALiveJob, Reachability(pidless));
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Reachability(pidless, isLiveJob: EveryIdIsALiveJob));
    }

    [Fact]
    public void ACodexFileNamingNoProcessGoesWhateverTheDaemonSays()
    {
        // The hole this closed: the no-terminal rule requires a pid to fire and
        // the liveness check treats pid 0 as alive, so with the lifetime set to
        // "forever" nothing in the app would ever have removed a pid-less Codex
        // file. `claude agents` has no equivalent to ask about one.
        var codex = new SessionStatus
        {
            Source = SessionSource.Codex, TermProgram = "iTerm.app"
        };

        Assert.Equal(SessionManager.ScanVerdict.NotALiveJob, Reachability(codex));
        Assert.Equal(
            SessionManager.ScanVerdict.NotALiveJob,
            Reachability(codex, isLiveJob: EveryIdIsALiveJob));
    }

    [Fact]
    public void APidlessGatewaySessionIsNeverAskedAboutAtAll()
    {
        // A gateway session records no pid, so this rule would ask the local
        // daemon about a session it has never heard of — once per scan, per
        // session — and drop the orb when the answer came back "not a job",
        // which it always would.
        var asked = new List<string>();
        var gateway = new SessionStatus { Source = SessionSource.OpenClaw };

        var verdict = SessionManager.JudgeReachability(
            "openclaw:agent:main", gateway, Nothing,
            id => { asked.Add(id); return false; }, TranscriptIsThere);

        Assert.Equal(SessionManager.ScanVerdict.Keep, verdict);
        Assert.Empty(asked);
    }

    [Fact]
    public void TheDaemonIsNotAskedAboutASessionThatAlreadyKnowsATerminal()
    {
        // The lookup shells out, so an ordinary session must never pay for it.
        var asked = new List<string>();

        SessionManager.JudgeReachability(
            "session-1", Healthy(), Nothing,
            id => { asked.Add(id); return true; }, TranscriptIsThere);

        Assert.Empty(asked);
    }

    // --- JudgeReachability: nothing to show (CB-9) ----------------------------

    [Fact]
    public void AnUnpromptedBackgroundJobWithNoTranscriptGetsNoOrb()
    {
        // The bug: the live-job exemption on the no-terminal rule waved this
        // through, so the orb stayed and its chat could only ever open blank.
        // The exemption is right about the terminal — a background job has none
        // by nature — and says nothing about whether there is a conversation.
        Assert.Equal(
            SessionManager.ScanVerdict.NothingToShow,
            Reachability(UnpromptedJob(), isLiveJob: EveryIdIsALiveJob,
                         transcriptExists: TranscriptIsGone));
    }

    [Fact]
    public void TheSameJobKeepsItsOrbOnceThereIsATranscriptToRead()
    {
        // The other half of the same machine's evidence: job "evidence (2)",
        // identically shaped and identically terminal-less, with 86KB of
        // transcript being written to it. Its chat works, so its orb stays.
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Reachability(UnpromptedJob(), isLiveJob: EveryIdIsALiveJob,
                         transcriptExists: TranscriptIsThere));
    }

    [Fact]
    public void ATerminalIsEnoughOnItsOwnEvenWithNoTranscript()
    {
        // Deliberately narrow: the click still lands, so the orb still earns
        // its place. A session in a real terminal has no transcript for its
        // first moment, and flickering its orb would be the worse bug.
        var inATerminal = UnpromptedJob();
        inATerminal.TermProgram = "iTerm.app";

        var withATty = UnpromptedJob();
        withATty.Tty = "/dev/ttys004";

        var inTmux = UnpromptedJob();
        inTmux.TmuxPane = "%4";

        var onWindows = UnpromptedJob();
        onWindows.TermPid = 9182;

        foreach (var reachable in new[] { inATerminal, withATty, inTmux, onWindows })
        {
            Assert.Equal(
                SessionManager.ScanVerdict.Keep,
                Reachability(reachable, isLiveJob: EveryIdIsALiveJob,
                             transcriptExists: TranscriptIsGone));
        }
    }

    [Fact]
    public void ALeadWithLiveAgentsIsExemptFromThisRuleToo()
    {
        // Same reasoning as the no-terminal rule's own exemption: agents drawn
        // on screen pointing at nothing is a worse lie than a thin chat.
        var leads = new HashSet<string>(StringComparer.Ordinal) { "session-1" };

        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Reachability(UnpromptedJob(), leads, EveryIdIsALiveJob,
                         transcriptExists: TranscriptIsGone));
    }

    [Fact]
    public void ATranscriptPathTheHookNeverRecordedAnswersKeepNotDrop()
    {
        // "Nowhere recorded" means this app does not know where the
        // conversation would be, not that there isn't one — Codex's
        // transcript_path is nullable and the chat panel falls back to hunting
        // the projects directories by session id. Same reasoning as
        // BackgroundJobs.IsLive answering true for a listing it could not read.
        //
        // The disk is not touched at all in that case, which is also what keeps
        // this O(1) per session per scan.
        var asked = new List<string>();
        var noPath = UnpromptedJob(transcriptPath: "");

        var verdict = SessionManager.JudgeReachability(
            "session-1", noPath, Nothing, EveryIdIsALiveJob,
            path => { asked.Add(path); return false; });

        Assert.Equal(SessionManager.ScanVerdict.Keep, verdict);
        Assert.Empty(asked);
    }

    [Theory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void AGatewaySessionIsNeverDroppedForHavingNoTranscriptOnThisDisk(
        SessionSource source)
    {
        // Its transcript is on the machine running the gateway, so asking this
        // disk about it can only ever answer no. Left ungated this rule alone
        // would drop every gateway orb, every scan — the same trap the
        // no-terminal rule above already carries a comment about.
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Reachability(UnpromptedJob(source), transcriptExists: TranscriptIsGone));
    }

    [Fact]
    public void NothingToShowIsAnsweredBeforeNoTerminal()
    {
        // Both rules fit a session with no terminal and no transcript, and
        // which one answers is the half a regression shows up in — the enum
        // exists so that "dropped" is never the whole story. NothingToShow is
        // the more specific reading and comes first.
        Assert.Equal(
            SessionManager.ScanVerdict.NothingToShow,
            Reachability(UnpromptedJob(), transcriptExists: TranscriptIsGone));
    }

    [Fact]
    public void ACodexSessionWithNeitherIsDroppedForTheSameReason()
    {
        // Codex is exempt from the live-job exemption, not from this rule: it
        // has no background jobs to be one of, but a Codex session with no
        // terminal and no transcript is just as inert.
        Assert.Equal(
            SessionManager.ScanVerdict.NothingToShow,
            Reachability(UnpromptedJob(SessionSource.Codex),
                         isLiveJob: EveryIdIsALiveJob,
                         transcriptExists: TranscriptIsGone));
    }
}
