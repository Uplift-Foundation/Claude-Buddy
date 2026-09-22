using System;
using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.Tests;

public class TmuxPaneOwnershipTests
{
    private const string A = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string B = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    // The CB-177 repro, verbatim: a real Claude Code 2.1.278 agent-team member.
    // --parent-session-id names the *lead's* session, not this pane's own, and
    // it sits one word away from a flag whose suffix is literally "session-id".
    private const string TeamMemberParentSessionId = "9401a866-f86b-4453-98db-b68596f0b8b6";
    private const string TeamMemberArgv =
        "/Users/warrenthompson/.local/share/claude/versions/2.1.278 --agent-id pm-cb177@session-9401a866 " +
        "--agent-name pm-cb177 --team-name session-9401a866 --agent-color blue " +
        "--parent-session-id " + TeamMemberParentSessionId + " --agent-type general-purpose " +
        "--dangerously-skip-permissions --model opus";

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

    // PermitsAction is what HasVerifiedTmuxPane now delegates to. Each case
    // below matches an outcome a real pane probe can hand back, per CB-177.

    [Fact]
    public void PermitsActionAllowsAPositivelyMatchedOwner()
    {
        Assert.True(TmuxPaneOwnershipRules.PermitsAction(A, A));
    }

    [Fact]
    public void PermitsActionRefusesAPositivelyDifferentOwner()
    {
        // Commit 7e9fd51a's case: tmux reused the pane id for another
        // conversation, and the probe named it. This must stay refused.
        Assert.False(TmuxPaneOwnershipRules.PermitsAction(A, B));
    }

    [Fact]
    public void PermitsActionAllowsAPlainInteractiveSessionWithNoSessionIdInArgv()
    {
        // The bare repro from the bug report: an ordinary "claude" invocation
        // carries no --session-id at all, so SessionIdIn must come back null --
        // Unknown, not Mismatch -- and the action must still be permitted.
        var processes = new[] { Shell(), new ProcessCommand(11, 10, "claude") };

        Assert.Null(TmuxPaneOwnershipRules.SessionIdIn(processes, 10));
        Assert.True(TmuxPaneOwnershipRules.PermitsAction(A, TmuxPaneOwnershipRules.SessionIdIn(processes, 10)));
    }

    [Fact]
    public void PermitsActionAllowsATeamMemberWhoseArgvNamesAgentIdNotSessionId()
    {
        // Claude Code 2.1.278's team-member argv from the bug report: it
        // satisfies LooksLikeClaudeBinary via the /claude/versions/ arm, but
        // carries --agent-id/--team-name/--parent-session-id, never
        // --session-id, so it is just as Unknown as the bare invocation above.
        var processes = new[] { Shell(), new ProcessCommand(11, 10, TeamMemberArgv) };

        Assert.Null(TmuxPaneOwnershipRules.SessionIdIn(processes, 10));
        Assert.True(TmuxPaneOwnershipRules.PermitsAction(A, TmuxPaneOwnershipRules.SessionIdIn(processes, 10)));
    }

    [Fact]
    public void TeamMemberArgvIsAcceptedAsClaudeOnlyViaTheVersionsPathArm()
    {
        // Pin *why* the scan above reaches null for an honest reason. The
        // leaf here is "2.1.278" -- no "claude" in it anywhere -- so
        // Path.GetFileName(argv0) is "claude" or "claude.exe" does not fire.
        // It is accepted on the other arm, the literal "/claude/versions/"
        // substring, which is what lets the argv reach the --session-id scan
        // at all instead of being rejected outright at the binary check.
        Assert.True(SessionPresence.LooksLikeClaudeBinary(
            "/Users/warrenthompson/.local/share/claude/versions/2.1.278"));
    }

    [Fact]
    public void ParentSessionIdNeverBecomesAuthorityForThePaneEvenWhenItMatchesTheExpectedId()
    {
        // The dangerous failure the PM flagged: SessionIdFrom's word match
        // ("words[i] == "--session-id"") must never loosen into a Contains,
        // an EndsWith, or a "strip the dashes" tidy-up, because
        // --parent-session-id sits one word away from a real UUID -- the
        // lead's, not this pane's -- and a loosened match would let it leak in
        // as if it were the pane's own --session-id.
        var processes = new[] { Shell(), new ProcessCommand(11, 10, TeamMemberArgv) };

        var owner = TmuxPaneOwnershipRules.SessionIdIn(processes, 10);
        Assert.Null(owner);
        Assert.NotEqual(TeamMemberParentSessionId, owner);

        // Pin the shape of the trap, not just its absence: an orb whose own
        // expected session id happens to equal the lead's uuid must still
        // read Unknown -- never Match, which is the one verdict a leaked
        // parent id would produce and which SessionIdFrom's honest null
        // currently rules out entirely.
        Assert.Equal(TmuxPaneOwnership.Unknown,
            TmuxPaneOwnershipRules.For(TeamMemberParentSessionId, 10, processes));
        Assert.True(TmuxPaneOwnershipRules.PermitsAction(TeamMemberParentSessionId, owner));

        // And an orb expecting some other id entirely reads Unknown too, never
        // Mismatch -- there is nothing positive here to disagree with.
        Assert.Equal(TmuxPaneOwnership.Unknown, TmuxPaneOwnershipRules.For(A, 10, processes));
    }

    // Which loosenings this actually catches, stated exactly rather than
    // generally, because a test whose reach is overestimated is worse than one
    // whose reach is known. Mutating SessionIdFrom's comparison to
    // Contains("session-id") does turn the fixture above red -- the parent uuid
    // leaks in and the pane resolves to the lead. Mutating it to
    // EndsWith("--session-id") does *not*, and that is not the test being weak:
    // "--parent-session-id" ends in "t-session-id", so the double-dash flag is
    // genuinely not a suffix of it and nothing leaks. The dashes are load-bearing
    // and this pins that, so nobody reads the green as permission to drop them.
    [Fact]
    public void ParentSessionIdSharesNoDoubleDashSuffixWithTheRealFlag()
    {
        Assert.False("--parent-session-id".EndsWith("--session-id", StringComparison.Ordinal));
        Assert.True("--parent-session-id".EndsWith("-session-id", StringComparison.Ordinal));
        Assert.True("--parent-session-id".Contains("session-id", StringComparison.Ordinal));
    }

    // The old line this replaced compared OrdinalIgnoreCase, and a session id
    // that only differs in case is the same conversation -- refusing it would
    // reintroduce the dead click for anyone whose id reaches the rule in a
    // different case than the status file recorded.
    [Fact]
    public void PermitsActionComparesTheOwnerCaseInsensitively()
    {
        Assert.True(TmuxPaneOwnershipRules.PermitsAction(A, A.ToUpperInvariant()));
        Assert.True(TmuxPaneOwnershipRules.PermitsAction(A.ToUpperInvariant(), A));
        Assert.False(TmuxPaneOwnershipRules.PermitsAction(A, B.ToUpperInvariant()));
    }

    [Fact]
    public void PermitsActionAllowsWhenTheProbeItselfProducedNothing()
    {
        // tmux or ps failing outright -- the process list never came back --
        // is exactly as Unknown as a process list with no session id in it.
        Assert.True(TmuxPaneOwnershipRules.PermitsAction(A, null));
        Assert.True(TmuxPaneOwnershipRules.PermitsAction(A, string.Empty));
    }

    [Fact]
    public void PermitsActionAllowsAnAmbiguousPaneWithTwoDifferentDescendantIds()
    {
        // Two distinct ids among the descendants is Unknown, not Mismatch --
        // SessionIdIn already refuses to pick one, so there is nothing positive
        // for PermitsAction to refuse against either.
        var processes = new[] { Shell(), Claude(11, 10, A), Claude(12, 10, B) };

        Assert.Null(TmuxPaneOwnershipRules.SessionIdIn(processes, 10));
        Assert.True(TmuxPaneOwnershipRules.PermitsAction(A, TmuxPaneOwnershipRules.SessionIdIn(processes, 10)));
    }

    [Fact]
    public void PermitsActionAllowsWhenThereIsNoExpectedSessionIdToViolate()
    {
        Assert.True(TmuxPaneOwnershipRules.PermitsAction(null, A));
        Assert.True(TmuxPaneOwnershipRules.PermitsAction(string.Empty, A));
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
