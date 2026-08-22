using System;
using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers SessionManager.cs ~295-470: the rules that turn a directory full of
// status files into one orb per real session, and ~1295-1340: the keys used
// to remember a dragged orb's position across restarts.
public class SessionScanRulesTests
{
    private static SessionManager.ScanEntry Entry(
        string id, int pid, SessionSource source, DateTime written,
        string tmuxPane = "", string termProgram = "", string termId = "", int termPid = 0,
        string tmuxSocket = "", string tmuxBin = "", string tty = "")
    {
        var status = new SessionStatus
        {
            SessionPid = pid,
            Source = source,
            TmuxPane = tmuxPane,
            TermProgram = termProgram,
            TermId = termId,
            TermPid = termPid,
            TmuxSocket = tmuxSocket,
            TmuxBin = tmuxBin,
            Tty = tty
        };
        return new SessionManager.ScanEntry(id, status, written);
    }

    // --- Superseded ---------------------------------------------------------

    [Fact]
    public void Superseded_MarksOlderOfSamePidAndSourceStale()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
        var t1 = t0.AddMinutes(5);
        var older = Entry("older", pid: 100, SessionSource.ClaudeCode, t0);
        var newer = Entry("newer", pid: 100, SessionSource.ClaudeCode, t1);

        var stale = SessionManager.Superseded(new List<SessionManager.ScanEntry> { older, newer });

        Assert.Contains("older", stale);
        Assert.DoesNotContain("newer", stale);
    }

    [Fact]
    public void Superseded_DifferentPidsAreNeverStale()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
        var t1 = t0.AddMinutes(5);
        var a = Entry("a", pid: 100, SessionSource.ClaudeCode, t0);
        var b = Entry("b", pid: 200, SessionSource.ClaudeCode, t1);

        var stale = SessionManager.Superseded(new List<SessionManager.ScanEntry> { a, b });

        Assert.Empty(stale);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(0, 0)]
    [InlineData(-1, 100)]
    public void Superseded_IgnoresEntriesWithNonPositivePid(int pidA, int pidB)
    {
        // "A pid of 0 means a hook older than the session_pid field ... left
        // alone" — SessionManager.cs, Superseded's own comment. Same for any
        // pid <= 0.
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
        var t1 = t0.AddMinutes(5);
        var a = Entry("a", pidA, SessionSource.ClaudeCode, t0);
        var b = Entry("b", pidB, SessionSource.ClaudeCode, t1);

        var stale = SessionManager.Superseded(new List<SessionManager.ScanEntry> { a, b });

        Assert.Empty(stale);
    }

    [Fact]
    public void Superseded_TiesOnWrittenBreakByOrdinalSmallerSessionIdBeingStale()
    {
        // "The ordinal tie-break only matters if two files somehow share an
        // mtime... so the choice doesn't depend on the order the directory
        // happened to enumerate in." The entry that survives is the one whose
        // SessionId compares ordinally greater; the smaller one is stale.
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
        var small = Entry("aaa", pid: 100, SessionSource.ClaudeCode, t0);
        var large = Entry("bbb", pid: 100, SessionSource.ClaudeCode, t0);

        var staleInOrder = SessionManager.Superseded(new List<SessionManager.ScanEntry> { small, large });
        var staleReversed = SessionManager.Superseded(new List<SessionManager.ScanEntry> { large, small });

        Assert.Contains("aaa", staleInOrder);
        Assert.DoesNotContain("bbb", staleInOrder);
        // Order of enumeration must not change the outcome.
        Assert.Contains("aaa", staleReversed);
        Assert.DoesNotContain("bbb", staleReversed);
    }

    [Fact]
    public void Superseded_SamePidDifferentSourceIsNeverStale()
    {
        // The nested `codex exec` case: a Codex process's hook can record the
        // OS pid of the Claude Code process that spawned it, so two entries
        // can share a real pid while being genuinely different sessions.
        // Keyed by (pid, Source), not pid alone.
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
        var t1 = t0.AddMinutes(5);
        var claude = Entry("claude-session", pid: 100, SessionSource.ClaudeCode, t0);
        var codex = Entry("codex-session", pid: 100, SessionSource.Codex, t1);

        var stale = SessionManager.Superseded(new List<SessionManager.ScanEntry> { claude, codex });

        Assert.Empty(stale);
    }

    // --- InheritTerminalInfo ------------------------------------------------

    [Fact]
    public void InheritTerminalInfo_FillsEmptyEntryFromNewestTerminalKnowingSibling()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
        var t1 = t0.AddMinutes(1);
        var t2 = t0.AddMinutes(2);

        // Older donor candidate — should lose to the newer one below.
        var olderDonor = Entry("older-donor", pid: 100, SessionSource.ClaudeCode, t0,
            tmuxPane: "%3", tmuxSocket: "old-socket", tmuxBin: "/old/tmux", tty: "/dev/ttys001");

        // Newest terminal-knowing entry — this is the real donor.
        var newestDonor = Entry("newest-donor", pid: 100, SessionSource.ClaudeCode, t1,
            termProgram: "iTerm.app", termId: "term-42", termPid: 555,
            tmuxSocket: "default", tmuxPane: "%7", tmuxBin: "/opt/homebrew/bin/tmux",
            tty: "/dev/ttys003");

        // Knows nothing, newer than either donor.
        var blind = Entry("blind", pid: 100, SessionSource.ClaudeCode, t2);

        var found = new List<SessionManager.ScanEntry> { olderDonor, newestDonor, blind };
        SessionManager.InheritTerminalInfo(found);

        Assert.Equal("iTerm.app", blind.Status.TermProgram);
        Assert.Equal("term-42", blind.Status.TermId);
        Assert.Equal(555, blind.Status.TermPid);
        Assert.Equal("default", blind.Status.TmuxSocket);
        Assert.Equal("%7", blind.Status.TmuxPane);
        Assert.Equal("/opt/homebrew/bin/tmux", blind.Status.TmuxBin);
        Assert.Equal("/dev/ttys003", blind.Status.Tty);
    }

    [Fact]
    public void InheritTerminalInfo_LeavesTtyAloneWhenAlreadySetEvenWithoutOtherTerminalFields()
    {
        // Tty alone doesn't count as "knows a terminal" (KnowsATerminal only
        // checks TmuxPane/TermProgram/TermId/TermPid), so an entry with only a
        // Tty still walks into the inheritance branch — but only Tty itself is
        // conditional on being empty; the other six fields are copied from the
        // donor regardless.
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
        var t1 = t0.AddMinutes(1);

        var donor = Entry("donor", pid: 100, SessionSource.ClaudeCode, t0,
            termProgram: "vscode", termId: "term-1", termPid: 9,
            tmuxSocket: "s", tmuxPane: "%1", tmuxBin: "/bin/tmux", tty: "/dev/ttys009");

        var ownTty = Entry("own-tty", pid: 100, SessionSource.ClaudeCode, t1, tty: "/dev/ttys099");

        SessionManager.InheritTerminalInfo(new List<SessionManager.ScanEntry> { donor, ownTty });

        Assert.Equal("/dev/ttys099", ownTty.Status.Tty); // its own tty survives
        Assert.Equal("vscode", ownTty.Status.TermProgram); // everything else is still donated
        Assert.Equal("%1", ownTty.Status.TmuxPane);
    }

    [Fact]
    public void InheritTerminalInfo_LeavesEntryThatAlreadyKnowsATerminalUntouched()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
        var t1 = t0.AddMinutes(1);

        var selfAware = Entry("self-aware", pid: 100, SessionSource.ClaudeCode, t0, termId: "term-own");

        // Newer, and a genuine donor by every field — but must not overwrite
        // an entry that already knows its own terminal.
        var newerDonor = Entry("newer-donor", pid: 100, SessionSource.ClaudeCode, t1,
            termProgram: "iTerm.app", termId: "term-42", termPid: 555,
            tmuxSocket: "default", tmuxPane: "%7", tmuxBin: "/opt/homebrew/bin/tmux",
            tty: "/dev/ttys003");

        SessionManager.InheritTerminalInfo(new List<SessionManager.ScanEntry> { selfAware, newerDonor });

        Assert.Equal("term-own", selfAware.Status.TermId);
        Assert.Equal("", selfAware.Status.TermProgram);
        Assert.Equal("", selfAware.Status.TmuxPane);
        Assert.Equal("", selfAware.Status.Tty);
    }

    [Fact]
    public void InheritTerminalInfo_NeverCrossesIntoADifferentPidOrSourceGroup()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
        var t1 = t0.AddMinutes(1);

        var donor = Entry("donor", pid: 100, SessionSource.ClaudeCode, t0,
            termProgram: "iTerm.app", termId: "term-42", tmuxPane: "%7");

        var differentPid = Entry("different-pid", pid: 200, SessionSource.ClaudeCode, t1);
        var differentSource = Entry("different-source", pid: 100, SessionSource.Codex, t1);

        SessionManager.InheritTerminalInfo(new List<SessionManager.ScanEntry>
        {
            donor, differentPid, differentSource
        });

        Assert.Equal("", differentPid.Status.TermProgram);
        Assert.Equal("", differentSource.Status.TermProgram);
    }

    // --- SourceOf ------------------------------------------------------------

    [Theory]
    [InlineData("codex")]
    [InlineData("CODEX")]
    [InlineData("Codex")]
    public void SourceOf_MatchesCodexCaseInsensitively(string cli)
    {
        var status = new SessionStatus { Cli = cli };
        Assert.Equal(SessionSource.Codex, SessionManager.SourceOf(status));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("claude-code")]
    [InlineData("some-future-cli")]
    public void SourceOf_TreatsAnythingElseAsClaudeCode(string? cli)
    {
        // "Anything unrecognised is Claude Code, deliberately" — SessionManager.cs.
        var status = new SessionStatus { Cli = cli! };
        Assert.Equal(SessionSource.ClaudeCode, SessionManager.SourceOf(status));
    }

    // --- DirectoryKeyFor / PositionKeyFor ------------------------------------

    [Theory]
    [InlineData("/Users/warren/project/", "/Users/warren/project")]
    [InlineData(@"C:\Users\warren\project\", @"C:\Users\warren\project")]
    [InlineData("/Users/warren/project", "/Users/warren/project")]
    [InlineData("", "")]
    public void DirectoryKeyFor_TrimsTrailingSlashOrBackslash(string cwd, string expected)
    {
        var status = new SessionStatus { Cwd = cwd };
        Assert.Equal(expected, SessionManager.DirectoryKeyFor(status));
    }

    [Fact]
    public void PositionKeyFor_NonLocalCliReturnsSessionIdVerbatim()
    {
        var status = new SessionStatus { Source = SessionSource.OpenClaw, Cwd = "/ignored", Title = "ignored" };
        Assert.Equal("some-session-id", SessionManager.PositionKeyFor(status, "some-session-id"));
    }

    [Fact]
    public void PositionKeyFor_LocalCodexSessionGetsCodexPrefixThatClaudeCodeDoesNot()
    {
        var codex = new SessionStatus { Source = SessionSource.Codex, Cwd = "/Users/warren/project" };
        var claude = new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = "/Users/warren/project" };

        Assert.Equal("codex\n/Users/warren/project", SessionManager.PositionKeyFor(codex, "id-1"));
        Assert.Equal("/Users/warren/project", SessionManager.PositionKeyFor(claude, "id-2"));
    }

    [Fact]
    public void PositionKeyFor_AppendsTitleWhenPresentAndOmitsItWhenEmpty()
    {
        var titled = new SessionStatus
        {
            Source = SessionSource.ClaudeCode, Cwd = "/Users/warren/evidence", Title = "job-lawyer"
        };
        var untitled = new SessionStatus
        {
            Source = SessionSource.ClaudeCode, Cwd = "/Users/warren/evidence", Title = ""
        };

        Assert.Equal("/Users/warren/evidence\njob-lawyer", SessionManager.PositionKeyFor(titled, "id-1"));
        Assert.Equal("/Users/warren/evidence", SessionManager.PositionKeyFor(untitled, "id-2"));
    }

    [Fact]
    public void PositionKeyFor_EmptyCwdReturnsEmptyRegardlessOfTitleOrSource()
    {
        var status = new SessionStatus { Source = SessionSource.Codex, Cwd = "", Title = "has-a-title" };
        Assert.Equal("", SessionManager.PositionKeyFor(status, "id-1"));
    }
}
