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

    // --- case 1: a background job, in any phase ------------------------------

    [Fact]
    public void ABackgroundJobIsAttachedByIdWhateverElseItRecorded()
    {
        Assert.Equal(
            ClickFallback.AttachBackground,
            Fallback(Status(shape: LocalSessionShape.Background)));

        // Even one that inherited or was adopted into terminal coordinates: it
        // has no terminal of its own and never will, so attach is the answer
        // rather than a fallback.
        Assert.Equal(
            ClickFallback.AttachBackground,
            Fallback(Status(shape: LocalSessionShape.Background, tmuxPane: "%7")));
    }

    // A hook older than the session_pid field writes 0, and that is still a
    // session with nowhere of its own — the rule the shape test was widened
    // from, kept alongside it rather than replaced by it.
    [Fact]
    public void ASessionThatRecordedNoPidIsStillAttachedById()
    {
        Assert.Equal(ClickFallback.AttachBackground, Fallback(Status(pid: 0)));
        Assert.Equal(ClickFallback.AttachBackground, Fallback(Status(pid: -1)));
    }

    // Precision: a parked job can have been adopted into a `claude agents`
    // viewer pane, and if that viewer's own server is detached, attaching the
    // socket would land on the *roster* rather than on the session that was
    // clicked. Naming the session wins.
    [Fact]
    public void ABackgroundJobBeatsTheSocketAnswerWhenBothWouldApply()
    {
        Assert.Equal(
            ClickFallback.AttachBackground,
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
        Assert.Equal(ClickFallback.None, Fallback(Status(), sessionId: sessionId));
        Assert.Equal(
            ClickFallback.None,
            Fallback(Status(shape: LocalSessionShape.Background), sessionId: sessionId));
    }
}
