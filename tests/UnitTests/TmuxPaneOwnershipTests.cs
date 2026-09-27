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

    // --- one listing per pass (the scan-stall fix) ----------------------------

    [Fact]
    public void AProcessListingParsesEveryWellFormedRowAndSkipsTornOnes()
    {
        var processes = TmuxPaneOwnershipRules.ParseProcessListing(
            "  10     1 /bin/zsh\n" +
            "   11   10 /Users/w/.local/bin/claude --session-id " + A + "\n" +
            "garbage\n" +
            "x 1 /bin/zsh\n" +
            "12 y /bin/zsh\n" +
            "13 1\n");

        Assert.Equal(
            new[] { new ProcessCommand(10, 1, "/bin/zsh"),
                    new ProcessCommand(11, 10, "/Users/w/.local/bin/claude --session-id " + A) },
            processes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnEmptyProcessListingIsAnEmptyTable(string? listing) =>
        Assert.Empty(TmuxPaneOwnershipRules.ParseProcessListing(listing));

    [Fact]
    public void APaneListingKeysEveryPaneIdToItsPid()
    {
        var panes = TmuxPaneOwnershipRules.ParsePanePids(
            "%1 501\n%12 777\n" +
            "1 900\n" +       // not a pane id
            "%3 abc\n" +      // not a pid
            "%4 0\n" +        // no process
            "%5\n" +          // torn
            "%6 1 2\n");      // too many fields

        Assert.Equal(2, panes.Count);
        Assert.Equal(501, panes["%1"]);
        Assert.Equal(777, panes["%12"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnEmptyPaneListingHasNoPanes(string? listing) =>
        Assert.Empty(TmuxPaneOwnershipRules.ParsePanePids(listing));

    [Fact]
    public void EveryClaimOnOneServerIsAnsweredWithOnePaneListingAndOneProcessListing()
    {
        // The shape of the bug: fifteen claims used to cost fifteen
        // display-messages and fifteen whole `ps` runs. Counted, because the
        // point is the number of calls and an outcome check would pass either
        // way.
        var claims = new[] { Claim("%1"), Claim("%2"), Claim("%3") };
        var paneListings = 0;
        var processListings = 0;

        var owners = TmuxPaneOwnershipRules.OwnersFor(
            claims,
            panesOf: (_, _) =>
            {
                paneListings++;
                return new Dictionary<string, int> { ["%1"] = 10, ["%2"] = 20, ["%3"] = 30 };
            },
            panePidOf: (_, _, _) => throw new InvalidOperationException("not a pane id target"),
            processes: () =>
            {
                processListings++;
                return new[]
                {
                    new ProcessCommand(10, 1, "/bin/zsh"), Claude(11, 10, A),
                    new ProcessCommand(20, 1, "/bin/zsh"), Claude(21, 20, B),
                    new ProcessCommand(30, 1, "/bin/zsh"),
                };
            });

        Assert.Equal(1, paneListings);
        Assert.Equal(1, processListings);
        Assert.Equal(A, owners[TmuxPaneKey.Of(claims[0])]);
        Assert.Equal(B, owners[TmuxPaneKey.Of(claims[1])]);

        // A pane whose process names no session is an answer of "unknown",
        // which is what the per-pane probe said about it too.
        Assert.Null(owners[TmuxPaneKey.Of(claims[2])]);
    }

    [Fact]
    public void EachServerIsListedOnceAndOnlyItsOwnPanesAreReadFromIt()
    {
        // A pane id is only unique on its own server, so %1 on two sockets is
        // two different panes and must be looked up in two different listings.
        var first = Claim("%1", socket: "/tmp/a");
        var second = Claim("%1", socket: "/tmp/b");
        var asked = new List<string>();

        var owners = TmuxPaneOwnershipRules.OwnersFor(
            new[] { first, second, Claim("%1", socket: "/tmp/a") },
            panesOf: (_, socket) =>
            {
                asked.Add(socket);
                return new Dictionary<string, int> { ["%1"] = socket == "/tmp/a" ? 10 : 20 };
            },
            panePidOf: (_, _, _) => null,
            processes: () => new[] { Claude(11, 10, A), Claude(21, 20, B) });

        Assert.Equal(new[] { "/tmp/a", "/tmp/b" }, asked);
        Assert.Equal(A, owners[TmuxPaneKey.Of(first)]);
        Assert.Equal(B, owners[TmuxPaneKey.Of(second)]);
    }

    [Fact]
    public void AGonePaneAndAnUnreachableServerLeaveNoAnswerAndCostNoProcessListing()
    {
        // A pane that has closed is absent from its server's listing, the same
        // fact display-message against it reported by failing. With nothing
        // resolved, there is nothing to look up in the process table, so it is
        // not read.
        var processListings = 0;

        var owners = TmuxPaneOwnershipRules.OwnersFor(
            new[] { Claim("%9"), Claim("%1", socket: "/tmp/no-server") },
            panesOf: (_, socket) => socket == "/tmp/no-server"
                ? null
                : new Dictionary<string, int> { ["%1"] = 10 },
            panePidOf: (_, _, _) => null,
            processes: () => { processListings++; return Array.Empty<ProcessCommand>(); });

        Assert.Empty(owners);
        Assert.Equal(0, processListings);
    }

    [Fact]
    public void AFailedProcessListingLeavesEveryResolvedPaneUnknown()
    {
        var claim = Claim("%1");

        var owners = TmuxPaneOwnershipRules.OwnersFor(
            new[] { claim },
            panesOf: (_, _) => new Dictionary<string, int> { ["%1"] = 10 },
            panePidOf: (_, _, _) => null,
            processes: () => null);

        Assert.Null(owners[TmuxPaneKey.Of(claim)]);
    }

    [Fact]
    public void ATargetThatIsNotAPaneIdIsStillAskedAboutOnItsOwn()
    {
        // The hook records $TMUX_PANE, which is always a pane id; a hand-written
        // file can name `session:window.pane` instead, which list-panes cannot
        // key on. That target gets the single-target probe it always did,
        // rather than quietly becoming "unknown".
        var byName = Claim("work:1.0", tmux: "/opt/tmux", socket: "/tmp/s");
        var unresolvable = Claim("work:2.0");
        var asked = new List<(string, string, string)>();

        var owners = TmuxPaneOwnershipRules.OwnersFor(
            new[] { byName, unresolvable, Claim("") },
            panesOf: (_, _) => throw new InvalidOperationException("no pane id here"),
            panePidOf: (bin, socket, pane) =>
            {
                asked.Add((bin, socket, pane));
                return pane == "work:1.0" ? 10 : null;
            },
            processes: () => new[] { Claude(11, 10, A) });

        Assert.Equal(new[] { ("/opt/tmux", "/tmp/s", "work:1.0"), ("", "", "work:2.0") }, asked);
        Assert.Equal(A, owners[TmuxPaneKey.Of(byName)]);
        Assert.False(owners.ContainsKey(TmuxPaneKey.Of(unresolvable)));
    }

    [Fact]
    public void NoClaimsAsksNeitherTmuxNorPs()
    {
        // TmuxPaneOwners gates an empty list before it gets here; this pins the
        // rule itself for any caller that does not.
        var owners = TmuxPaneOwnershipRules.OwnersFor(
            Array.Empty<SessionStatus>(),
            panesOf: (_, _) => throw new InvalidOperationException("no server to list"),
            panePidOf: (_, _, _) => throw new InvalidOperationException("no target to ask"),
            processes: () => throw new InvalidOperationException("no process table wanted"));

        Assert.Empty(owners);
    }

    [Fact]
    public void APaneKeyTreatsAMissingFieldAsEmpty()
    {
        // Status files are JSON, and a null in one deserializes as null over the
        // property's "" default.
        var status = new SessionStatus { TmuxBin = null!, TmuxSocket = null!, TmuxPane = null! };

        Assert.Equal(new TmuxPaneKey("", "", ""), TmuxPaneKey.Of(status));
    }

    private static SessionStatus Claim(string pane, string tmux = "", string socket = "") =>
        new() { Source = SessionSource.ClaudeCode, TmuxPane = pane, TmuxSocket = socket, TmuxBin = tmux };

    private static SessionManager.ScanEntry Entry(string id, string pane, string socket, string tmux) =>
        new(id, new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TmuxPane = pane,
            TmuxSocket = socket,
            TmuxBin = tmux
        }, DateTime.UtcNow);
}
