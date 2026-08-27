using Xunit;

namespace ClaudeBuddy.Tests;

// Where a click goes when the terminal the hook recorded could not be brought
// forward.
//
// This exists because of a user report — "most of these dead orbs when I try to
// go to terminal they do nothing" — and because of *why* that happened: every
// failure on the click path is silent, and there are six of them in a row for a
// team member in a detached swarm socket. FocusTmux selects the pane and returns
// false for want of an attached client; the app switch has no case for
// term_program "tmux"; FocusByTty walks up from a tmux-server pty and finds no
// app bundle; the team-lead fallback lands on a headless background session and
// fails the same way; the attach fallback was gated on having no pid, which a
// team member has; and the failure is reported to a stderr a bundled .app has
// nowhere to show.
//
// The decision used to be inline in TerminalFocuser, which is 600 lines of
// tmux/ps/osascript subprocesses that a headless runner must not execute — so it
// was reachable only by clicking an orb on a real machine and watching what
// happened. That is exactly how it came to be wrong for a whole class of session
// without anyone noticing, which is the argument for every other pure seam on
// this branch.
public class ClickRoutingTests
{
    private static SessionStatus Status(
        SessionSource source = SessionSource.ClaudeCode,
        int pid = 4321,
        LocalSessionShape shape = LocalSessionShape.Terminal,
        string tty = "",
        string termProgram = "",
        string termId = "",
        string tmuxPane = "",
        int termPid = 0) =>
        new()
        {
            Source = source,
            SessionPid = pid,
            Shape = shape,
            Tty = tty,
            TermProgram = termProgram,
            TermId = termId,
            TmuxPane = tmuxPane,
            TermPid = termPid,
        };

    private static ClickFallback Fallback(
        SessionStatus status, string? sessionId = "session-1", bool detached = false) =>
        ClickRouting.FallbackFor(status, sessionId, detached);

    // --- the case that must not change ---------------------------------------

    // A session that recorded a terminal has one. Focus failing to bring it
    // forward is a failure to diagnose, not an invitation to open a second
    // window onto it — which would hide the real failure behind a new window
    // every single time.
    [Fact]
    public void ASessionWhoseRecordedTerminalFailedToFocusOpensNothing()
    {
        var cases = new[]
        {
            Status(tty: "/dev/ttys004"),
            Status(termProgram: "iTerm.app"),
            Status(termId: "ABC-123"),
            Status(tmuxPane: "%7"),
            Status(termPid: 9182),
            Status(tty: "/dev/ttys004", termProgram: "Apple_Terminal"),
        };

        foreach (var status in cases)
        {
            Assert.Equal(ClickFallback.None, Fallback(status));
        }
    }

    // A tty alone is not "no coordinates". That distinction is the whole of
    // NoCoordinatesAtAll and is the opposite of SessionManager.KnowsATerminal's,
    // deliberately: KnowsATerminal ignores the tty because a tty can name a tmux
    // server's pty and prove no window; this counts it, because FocusByTty walks
    // the process tree above one and can find the app that owns it. Read the
    // other way round, attach would fire for most sessions that do have a window.
    [Fact]
    public void ATtyIsSomethingToTryAndSoIsNotNoCoordinatesAtAll()
    {
        Assert.False(ClickRouting.NoCoordinatesAtAll(Status(tty: "/dev/ttys004")));
        Assert.True(ClickRouting.NoCoordinatesAtAll(Status()));
    }

    // --- case 1: a background session, in any phase --------------------------

    // The `claude agents` roster, which is where these sessions are managed
    // from. The user's words, looking at it: "I don't understand why you can't go
    // straight to it!" — and the *shape* decides, not the phase, because the
    // roster shows a working job, one holding a question and one that has
    // finished alike. The orb's rendering is what distinguishes those; where the
    // click goes does not need to.
    [Fact]
    public void ABackgroundSessionGoesToTheAgentsViewWhateverElseItRecorded()
    {
        Assert.Equal(
            ClickFallback.AgentsView,
            Fallback(Status(shape: LocalSessionShape.Background)));

        // Even one that inherited or was adopted into terminal coordinates: it
        // has no terminal of its own and never will, so this is the answer rather
        // than a fallback.
        Assert.Equal(
            ClickFallback.AgentsView,
            Fallback(Status(shape: LocalSessionShape.Background, tmuxPane: "%7")));
    }

    // The roster is not named per session — `claude agents --help` offers only
    // `--cwd`, and no preselect — so unlike both attach answers this one needs no
    // session id at all. Asserted rather than left to the reader, because an orb
    // whose id somehow did not reach the click would otherwise be the one orb
    // with nowhere to go.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TheAgentsViewNeedsNoSessionId(string? sessionId)
    {
        Assert.Equal(
            ClickFallback.AgentsView,
            Fallback(Status(shape: LocalSessionShape.Background), sessionId: sessionId));
    }

    // A hook older than the session_pid field writes 0, and such a session may
    // not be a job at all — nobody can enumerate it, so the roster might not list
    // it. It keeps `claude attach` with its own name, which is the one answer that
    // works for something nothing else can name.
    [Fact]
    public void ASessionThatRecordedNoPidIsStillAttachedById()
    {
        Assert.Equal(ClickFallback.AttachBackground, Fallback(Status(pid: 0)));
        Assert.Equal(ClickFallback.AttachBackground, Fallback(Status(pid: -1)));

        // ...unless the scan did place it as a background session, in which case
        // the roster knows it and is the better destination.
        Assert.Equal(
            ClickFallback.AgentsView,
            Fallback(Status(pid: 0, shape: LocalSessionShape.Background)));
    }

    // A parked job can have been adopted into a `claude agents` viewer pane, and
    // if that viewer's own server is detached, the socket answer would attach a
    // terminal to the roster's server — the same destination by a worse route,
    // and one that cannot bring an already-open roster forward. The roster answer
    // wins.
    [Fact]
    public void ABackgroundSessionBeatsTheSocketAnswerWhenBothWouldApply()
    {
        Assert.Equal(
            ClickFallback.AgentsView,
            Fallback(Status(shape: LocalSessionShape.Background, tmuxPane: "%7"), detached: true));
    }

    // --- case 2: a live pane in a server nothing is attached to --------------

    [Fact]
    public void APaneWithNoAttachedClientOpensATerminalOnItsServer()
    {
        Assert.Equal(
            ClickFallback.AttachSocket,
            Fallback(Status(tmuxPane: "%7"), detached: true));
    }

    // The only one of the three that is not `claude attach`, and so the only one
    // that is not Claude Code's alone: attaching to a tmux server is a tmux
    // operation, and a Codex session in a detached pane is in the same bind for
    // the same reason.
    [Fact]
    public void TheSocketAnswerAppliesToAnyLocalCliNotJustClaudeCode()
    {
        Assert.Equal(
            ClickFallback.AttachSocket,
            Fallback(Status(source: SessionSource.Codex, tmuxPane: "%7"), detached: true));
    }

    // It does not need the orb's id, unlike both attach-by-id answers: the
    // server and the already-selected pane are the whole address.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TheSocketAnswerDoesNotNeedTheSessionId(string? sessionId)
    {
        Assert.Equal(
            ClickFallback.AttachSocket,
            Fallback(Status(tmuxPane: "%7"), sessionId: sessionId, detached: true));
    }

    // --- case 3: nothing recorded at all -------------------------------------

    // The agent-mode direct child: `claude --session-id <id> --agent <name>`,
    // with a real pid and no terminal anywhere. The diagnosis rule does not
    // cover it, because there is no recorded terminal that failed to resolve —
    // nothing to diagnose, and no window for a second one to be confused with.
    [Fact]
    public void AHeadlessSessionWithNothingRecordedIsAttachedByIdDespiteItsPid()
    {
        Assert.Equal(ClickFallback.AttachById, Fallback(Status()));
    }

    // --- what the chat panel asks ---------------------------------------------

    // The panel's question is not the click's — "is there an attach that would
    // reach this session" rather than "what should this gesture do" — but it must
    // never offer an attach for a session a click would not attach, so it is the
    // same rule run with paneAliveButDetached false rather than a second rule
    // that agrees today.
    [Fact]
    public void ThePanelOffersAnAttachForExactlyTheSessionsAClickWouldAttach()
    {
        // A background session in any phase (the roster), and a headless session
        // with nothing recorded (an attach): the answers the click has for a
        // session with nowhere of its own.
        Assert.True(ClickRouting.AttachWouldReach(
            Status(shape: LocalSessionShape.Background), "session-1"));

        // Including with no id, since the roster does not need one.
        Assert.True(ClickRouting.AttachWouldReach(
            Status(shape: LocalSessionShape.Background), null));
        Assert.True(ClickRouting.AttachWouldReach(Status(), "session-1"));
        Assert.True(ClickRouting.AttachWouldReach(Status(pid: 0), "session-1"));

        // An ordinary session in a pane or a window: the click focuses it, so
        // there is nothing to offer and no button.
        Assert.False(ClickRouting.AttachWouldReach(Status(tmuxPane: "%7"), "session-1"));
        Assert.False(ClickRouting.AttachWouldReach(Status(tty: "/dev/ttys004"), "session-1"));
        Assert.False(ClickRouting.AttachWouldReach(
            Status(termProgram: "iTerm.app"), "session-1"));
    }

    // Codex has no `claude attach` to be offered, and a session with no id
    // cannot be named to one. Both are the click's refusals, which is the point
    // of asking the same function.
    [Fact]
    public void ThePanelOffersNothingWhereTheClickWouldRefuse()
    {
        Assert.False(ClickRouting.AttachWouldReach(
            Status(source: SessionSource.Codex), "session-1"));
        Assert.False(ClickRouting.AttachWouldReach(
            Status(source: SessionSource.Codex, shape: LocalSessionShape.Background), "session-1"));
        Assert.False(ClickRouting.AttachWouldReach(Status(), null));
        Assert.False(ClickRouting.AttachWouldReach(Status(), ""));
    }

    // The socket answer is deliberately not one the panel can offer: it is about
    // a pane the *click* already selected on its way past, which the panel has
    // not done and cannot claim.
    [Fact]
    public void ThePanelNeverOffersTheSocketAttach()
    {
        var inADetachedPane = Status(tmuxPane: "%7");

        Assert.Equal(ClickFallback.AttachSocket, Fallback(inADetachedPane, detached: true));
        Assert.False(ClickRouting.AttachWouldReach(inADetachedPane, "session-1"));
    }

    // --- the mic uses the same predicate --------------------------------------

    // TerminalFocuser.SendText guards on this too, and the reason is worth a
    // case of its own here rather than only a comment there: a local CLI session
    // with no coordinates passes SendText's `IsLocalCli` check, finds no pane and
    // no tty to type into, and falls through to an unconditional System Events
    // keystroke — a dictated sentence landing in a browser, an editor, or another
    // session. The mic is offered on every orb, and this branch keeps a whole
    // class of terminal-less orbs on screen that used to be dropped.
    //
    // One predicate for both gestures, deliberately. "Is there anywhere to aim
    // this" has one answer, and two copies of it would drift — with the click
    // opening a terminal for a session the mic was still typing at.
    [Fact]
    public void TheSameNoCoordinatesRuleIsWhatTheMicRefusesOn()
    {
        // What a click answers with AttachById is exactly what dictation must
        // refuse: no pane, no tty, nothing to receive the words.
        var headless = Status();

        Assert.True(ClickRouting.NoCoordinatesAtAll(headless));
        Assert.Equal(ClickFallback.AttachById, Fallback(headless));

        // And an ordinary session is untouched by either rule — a tmux pane or a
        // tty is somewhere to type, and dictation goes on working.
        Assert.False(ClickRouting.NoCoordinatesAtAll(Status(tmuxPane: "%7")));
        Assert.False(ClickRouting.NoCoordinatesAtAll(Status(tty: "/dev/ttys004")));
    }

    // --- what is refused ------------------------------------------------------

    // `claude attach` is Claude Code's own verb and Codex has no equivalent. The
    // scan already drops a pid-less Codex session before it can have an orb, so
    // nothing should reach here — this is the belt to those braces, because the
    // failure it prevents is a window opening onto someone else's session.
    [Theory]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void NeitherAttachByIdAnswerIsOfferedToAnythingButClaudeCode(SessionSource source)
    {
        Assert.Equal(ClickFallback.None, Fallback(Status(source: source)));
        Assert.Equal(ClickFallback.None, Fallback(Status(source: source, pid: 0)));
        Assert.Equal(
            ClickFallback.None,
            Fallback(Status(source: source, shape: LocalSessionShape.Background)));
    }

    // No id, nothing to name. Focus is handed the orb's id separately from the
    // status because it is the status file's *name* rather than a field inside
    // it, so it can be absent.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithNoSessionIdThereIsNothingToAttachTo(string? sessionId)
    {
        // Both attach answers name the session, so neither is available. The
        // roster is unaffected — see TheAgentsViewNeedsNoSessionId.
        Assert.Equal(ClickFallback.None, Fallback(Status(), sessionId: sessionId));
        Assert.Equal(ClickFallback.None, Fallback(Status(pid: 0), sessionId: sessionId));
    }

    // --- LeadMayAnswer -----------------------------------------------------

    // The team lead may answer only the click nothing else can, and every other
    // answer wins over it. This is the rule that had the ordering backwards, and
    // the case that matters most is AttachSocket: that is a teammate whose own
    // pane is alive in a detached swarm socket, which is precisely the orb the
    // bug was reported against.
    // One [Fact] over the four rather than a [Theory] per value: ClickFallback is
    // internal, and an InlineData of an internal type on a public test method is
    // an accessibility error rather than a style choice.
    [Fact]
    public void AnAnswerThatShowsTheClickedSessionBeatsTheTeamLead()
    {
        Assert.False(ClickRouting.LeadMayAnswer(ClickFallback.AgentsView));
        Assert.False(ClickRouting.LeadMayAnswer(ClickFallback.AttachBackground));
        Assert.False(ClickRouting.LeadMayAnswer(ClickFallback.AttachSocket));
        Assert.False(ClickRouting.LeadMayAnswer(ClickFallback.AttachById));
    }

    [Fact]
    public void TheTeamLeadAnswersTheClickNothingElseWill()
    {
        Assert.True(ClickRouting.LeadMayAnswer(ClickFallback.None));
    }

    // The two halves joined up, on the shape measured on the reporter's machine:
    // a teammate with a live pane in a server nothing is attached to. The rule
    // says AttachSocket, and AttachSocket says the lead does not get to pre-empt
    // it — which together are the whole of the fix.
    [Fact]
    public void ATeammateInADetachedPaneIsAnsweredByItsOwnSocketAndNotByItsLead()
    {
        var teammate = Status(
            shape: LocalSessionShape.Teammate,
            tty: "ttys018",
            termProgram: "tmux",
            tmuxPane: "%53");

        var fallback = Fallback(teammate, detached: true);

        Assert.Equal(ClickFallback.AttachSocket, fallback);
        Assert.False(ClickRouting.LeadMayAnswer(fallback));
    }
}
