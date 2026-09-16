using System;
using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.Tests;

public class TmuxPaneOwnershipTests
{
    private const string A = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string B = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    private static ProcessCommand Shell(int pid = 10) => new(pid, 1, "/bin/zsh");
    private static ProcessCommand Claude(int pid, int parent, string id) =>
        new(pid, parent, "/Users/w/.local/bin/claude --session-id " + id);

    [Fact]
    public void DescendantClaudeWithExpectedIdMatches()
    {
        var processes = new[] { Shell(), Claude(11, 10, A) };

        Assert.Equal(TmuxPaneOwnership.Match, TmuxPaneOwnershipRules.For(A, 10, processes));
        Assert.Equal(A, TmuxPaneOwnershipRules.SessionIdIn(processes, 10));
    }

    [Fact]
    public void ReusedPaneWithDifferentIdMismatches()
    {
        Assert.Equal(TmuxPaneOwnership.Mismatch, TmuxPaneOwnershipRules.For(A, 10,
            new[] { Shell(), Claude(11, 10, B) }));
    }

    [Fact]
    public void MissingOrAmbiguousClaudeIdentityIsUnknown()
    {
        Assert.Equal(TmuxPaneOwnership.Unknown, TmuxPaneOwnershipRules.For(A, 10,
            new[] { Shell(), new ProcessCommand(11, 10, "vim notes.txt") }));
        Assert.Equal(TmuxPaneOwnership.Unknown, TmuxPaneOwnershipRules.For(A, 10,
            new[] { Shell(), Claude(11, 10, A), Claude(12, 10, B) }));
    }

    [Fact]
    public void NonClaudeArgumentMentioningSessionIdIsNotAuthority()
    {
        var tool = new ProcessCommand(11, 10, "/bin/echo --session-id " + A);

        Assert.Null(TmuxPaneOwnershipRules.SessionIdIn(new[] { Shell(), tool }, 10));
    }

    [Fact]
    public void ReconciliationDropsStaleClaimAndDonatesPaneToVerifiedOwner()
    {
        var stale = Entry(A, "%9", "/tmp/tmux", "/opt/tmux");
        var current = Entry(B, "", "", "");
        var found = new List<SessionManager.ScanEntry> { stale, current };

        var removed = SessionManager.ReconcileTmuxPaneClaims(found,
            entry => entry.SessionId == A ? B : entry.SessionId);

        Assert.Contains(A, removed);
        Assert.DoesNotContain(B, removed);
        Assert.Equal("%9", current.Status.TmuxPane);
        Assert.Equal("/tmp/tmux", current.Status.TmuxSocket);
        Assert.Equal("/opt/tmux", current.Status.TmuxBin);
    }

    [Fact]
    public void ReconciliationKeepsClaimWhenCurrentOwnerStatusIsAbsentOrProbeIsUnknown()
    {
        var stale = Entry(A, "%9", "/tmp/tmux", "/opt/tmux");

        Assert.Empty(SessionManager.ReconcileTmuxPaneClaims(
            new List<SessionManager.ScanEntry> { stale }, _ => B));
        Assert.Empty(SessionManager.ReconcileTmuxPaneClaims(
            new List<SessionManager.ScanEntry> { stale }, _ => null));
    }

    [Fact]
    public void ReconciliationDoesNotOverwriteTheCurrentOwnersOwnTerminal()
    {
        var stale = Entry(A, "%9", "/old/tmux", "/old/tmux");
        var current = Entry(B, "%12", "/current/tmux", "/current/tmux");

        var removed = SessionManager.ReconcileTmuxPaneClaims(
            new List<SessionManager.ScanEntry> { stale, current },
            entry => entry.SessionId == A ? B : entry.SessionId);

        Assert.Contains(A, removed);
        Assert.Equal("%12", current.Status.TmuxPane);
        Assert.Equal("/current/tmux", current.Status.TmuxSocket);
    }

    private static SessionManager.ScanEntry Entry(string id, string pane, string socket, string tmux) =>
        new(id, new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TmuxPane = pane,
            TmuxSocket = socket,
            TmuxBin = tmux
        }, DateTime.UtcNow);
}
