using System;
using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.Tests;

// The scan's drop rules, from the point of view of a session on another machine.
//
// Worth its own file because nothing was added to those rules for Remote
// Control, and that is a claim rather than an obvious fact. Every rule that
// removes an orb already excludes a remote session — three of them by requiring
// a pid, which a remote session has none of, and one by asking IsLocalCli. So
// the feature works by construction.
//
// Which is exactly why it needs tests. "Nothing to change here" is invisible in
// a diff: the next person to tighten a drop rule, or to widen one to cover a
// pid-less local case, has no way to know they have just made every remote orb
// vanish on the next scan — a failure that shows up as the feature quietly not
// working rather than as anything breaking. These pin the assumptions down.
public class RemoteScanRulesTests
{
    private static SessionManager.ScanEntry Remote(string name, DateTime written, string state = "idle") =>
        new(
            "rc:.claude:" + name,
            new SessionStatus
            {
                Source = SessionSource.RemoteControl,
                State = state,
                Title = name,
                Kind = SessionKind.Remote
            },
            written);

    private static bool NeverLive(string _) => false;

    // The load-bearing one. IsLocalCli is what the rest of the app means when it
    // says "has a process, has a terminal you can be sent to, has a transcript
    // on disk" — a remote session has none of those *here*. Being false is what
    // routes an orb click to a chat panel instead of a terminal, and what keeps
    // ResetSessionToIdle from trying to rewrite a status file that was never
    // written. If this ever became true, both would break at once and neither
    // would say why.
    [Fact]
    public void ARemoteSession_IsNotALocalCli()
    {
        var status = new SessionStatus { Source = SessionSource.RemoteControl };

        Assert.False(status.IsLocalCli);
    }

    // Every remote session records no pid, so a rule keyed on (pid, source)
    // would put all of them in one bucket and keep only the newest. Superseded
    // skips pid <= 0 entirely, which is what stops that — without it, a user
    // with three sessions on a home machine would see one orb.
    [Fact]
    public void Superseded_NeverCollapsesRemoteSessionsTogether()
    {
        var now = DateTime.UtcNow;
        var found = new List<SessionManager.ScanEntry>
        {
            Remote("job-hunter", now.AddMinutes(-5)),
            Remote("resumes", now.AddMinutes(-1)),
            Remote("inbox", now)
        };

        var stale = SessionManager.Superseded(found, NeverLive);

        Assert.Empty(stale);
    }

    // A remote session must never be handed a local session's terminal. The orb
    // would then look clickable-through to a pane on this machine, and clicking
    // it would land somewhere plausible and wrong — which this file's own
    // comment on the codex-exec case already calls worse than a dead click.
    [Fact]
    public void InheritTerminalInfo_DoesNotDonateALocalTerminalToARemoteSession()
    {
        var now = DateTime.UtcNow;

        var local = new SessionManager.ScanEntry(
            "local-session",
            new SessionStatus
            {
                Source = SessionSource.ClaudeCode,
                SessionPid = 4242,
                TmuxPane = "%7",
                TmuxSocket = "/private/tmp/tmux-501/default",
                TmuxBin = "/opt/homebrew/bin/tmux",
                Tty = "ttys001"
            },
            now);

        var remote = Remote("job-hunter", now);

        SessionManager.InheritTerminalInfo(new List<SessionManager.ScanEntry> { local, remote });

        Assert.Equal("", remote.Status.TmuxPane);
        Assert.Equal("", remote.Status.TmuxSocket);
        Assert.Equal("", remote.Status.Tty);
    }

    // The two states an orb draws, from the peer list's own vocabulary. Anything
    // unrecognised reads as idle rather than working: an orb spinning forever
    // because a label changed upstream is worse than one that never spins.
    [Theory]
    [InlineData("running", "generating")]
    [InlineData("idle", "idle")]
    [InlineData("something-new", "idle")]
    public void RemoteStatus_MapsOntoTheTwoStatesAnOrbDraws(string peerStatus, string expected)
    {
        var remote = new RemoteControlSessions.Remote("job-hunter", "94f106", peerStatus, DateTime.UtcNow, ".claude");

        Assert.Equal(expected, remote.Working ? "generating" : "idle");
    }

    // The badge exists so a remote orb is identifiable before it is clicked —
    // clicking one opens a chat where almost every other orb jumps to a
    // terminal, and finding that out by clicking is the difference between the
    // app feeling consistent and feeling broken.
    [Fact]
    public void RemoteKind_IsDistinctFromTheGatewayKinds()
    {
        Assert.NotEqual(SessionKind.Remote, SessionKind.Channel);
        Assert.NotEqual(SessionKind.Remote, SessionKind.Direct);
        Assert.NotEqual(SessionKind.Remote, SessionKind.Cron);
        Assert.NotEqual(SessionKind.Remote, SessionKind.Unknown);
    }
}
