using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
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

    // None of these care about the daemon's job list, so they all pass a stub
    // that says nothing is a live background job — equivalent to Superseded's
    // behaviour before that check existed.
    private static readonly Func<string, bool> NeverLive = _ => false;

    [Fact]
    public void Superseded_MarksOlderOfSamePidAndSourceStale()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
        var t1 = t0.AddMinutes(5);
        var older = Entry("older", pid: 100, SessionSource.ClaudeCode, t0);
        var newer = Entry("newer", pid: 100, SessionSource.ClaudeCode, t1);

        var stale = SessionManager.Superseded(new List<SessionManager.ScanEntry> { older, newer }, NeverLive);

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

        var stale = SessionManager.Superseded(new List<SessionManager.ScanEntry> { a, b }, NeverLive);

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

        var stale = SessionManager.Superseded(new List<SessionManager.ScanEntry> { a, b }, NeverLive);

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

        var staleInOrder = SessionManager.Superseded(new List<SessionManager.ScanEntry> { small, large }, NeverLive);
        var staleReversed = SessionManager.Superseded(new List<SessionManager.ScanEntry> { large, small }, NeverLive);

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

        var stale = SessionManager.Superseded(new List<SessionManager.ScanEntry> { claude, codex }, NeverLive);

        Assert.Empty(stale);
    }

    [Fact]
    public void Superseded_OlderEntryConfirmedAsLiveJobIsNotStale()
    {
        // The Agent View case: a background session shares its parent's pid,
        // so it can be the older of two entries under one pid while still
        // being a wholly separate, currently-running conversation. The daemon's
        // job list is what tells the two apart, not the timestamp.
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
        var t1 = t0.AddMinutes(5);
        var backgroundSession = Entry("background", pid: 100, SessionSource.ClaudeCode, t0);
        var foregroundSession = Entry("foreground", pid: 100, SessionSource.ClaudeCode, t1);

        var stale = SessionManager.Superseded(
            new List<SessionManager.ScanEntry> { backgroundSession, foregroundSession },
            isLiveJob: id => id == "background");

        Assert.Empty(stale);
    }

    [Fact]
    public void Superseded_OlderEntryNotConfirmedLiveStaysStaleEvenWhenSiblingIsALiveJob()
    {
        // A daemon that vouches for one sibling under a pid doesn't blanket-
        // exempt the whole group — each entry is asked for individually.
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
        var t1 = t0.AddMinutes(5);
        var t2 = t0.AddMinutes(10);
        var abandoned = Entry("abandoned", pid: 100, SessionSource.ClaudeCode, t0);
        var liveJob = Entry("live-job", pid: 100, SessionSource.ClaudeCode, t1);
        var newest = Entry("newest", pid: 100, SessionSource.ClaudeCode, t2);

        var stale = SessionManager.Superseded(
            new List<SessionManager.ScanEntry> { abandoned, liveJob, newest },
            isLiveJob: id => id == "live-job");

        Assert.Contains("abandoned", stale);
        Assert.DoesNotContain("live-job", stale);
        Assert.DoesNotContain("newest", stale);
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
        // Titled, because that is the only shape the prefix is observable in
        // now: an untitled session of either CLI keys on its own id, which is
        // already unique across the two.
        var codex = new SessionStatus
        {
            Source = SessionSource.Codex, Cwd = "/Users/warren/project", Title = "build"
        };
        var claude = new SessionStatus
        {
            Source = SessionSource.ClaudeCode, Cwd = "/Users/warren/project", Title = "build"
        };

        Assert.Equal("codex\n/Users/warren/project\nbuild", SessionManager.PositionKeyFor(codex, "id-1"));
        Assert.Equal("/Users/warren/project\nbuild", SessionManager.PositionKeyFor(claude, "id-2"));
    }

    [Fact]
    public void PositionKeyFor_TitledSessionKeysOnDirectoryAndTitle()
    {
        // Byte-for-byte what it always was, which is the compatibility point:
        // every position already saved on a real machine still matches the
        // session it was saved for.
        var titled = new SessionStatus
        {
            Source = SessionSource.ClaudeCode, Cwd = "/Users/warren/evidence", Title = "job-lawyer"
        };

        Assert.Equal("/Users/warren/evidence\njob-lawyer", SessionManager.PositionKeyFor(titled, "id-1"));
    }

    [Fact]
    public void PositionKeyFor_UntitledSessionsInOneDirectoryDoNotShareASlot()
    {
        // CB-10. Adding the title separated two *titled* sessions in one folder
        // and did nothing when the title was empty, so every untitled session
        // fell back to the same bare-cwd key. Measured: three live untitled
        // sessions in Documents/GTD/Evidence sharing one key, so
        // RestoreOrbPosition refused a slot to two of them and three orbs read
        // as two on screen.
        var first = new SessionStatus
        {
            Source = SessionSource.ClaudeCode, Cwd = "/Users/warren/evidence", Title = ""
        };
        var second = new SessionStatus
        {
            Source = SessionSource.ClaudeCode, Cwd = "/Users/warren/evidence", Title = ""
        };

        var a = SessionManager.PositionKeyFor(first, "0e043819");
        var b = SessionManager.PositionKeyFor(second, "0e9677a5");

        Assert.Equal("0e043819", a);
        Assert.Equal("0e9677a5", b);
        Assert.NotEqual(a, b);

        // And neither collides with the directory key a titled sibling uses.
        var titled = new SessionStatus
        {
            Source = SessionSource.ClaudeCode, Cwd = "/Users/warren/evidence", Title = "job-lawyer"
        };
        Assert.NotEqual(SessionManager.PositionKeyFor(titled, "id-3"), a);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void PositionKeyFor_ATitleOfNothingButSpaceCountsAsUntitled(string? title)
    {
        // The rule trims before deciding, so whitespace can't sneak a session
        // back into the colliding branch.
        var status = new SessionStatus
        {
            Source = SessionSource.ClaudeCode, Cwd = "/Users/warren/evidence", Title = title!
        };

        Assert.Equal("id-9", SessionManager.PositionKeyFor(status, "id-9"));
    }

    [Fact]
    public void PositionKeyFor_EmptyCwdReturnsEmptyRegardlessOfTitleOrSource()
    {
        var status = new SessionStatus { Source = SessionSource.Codex, Cwd = "", Title = "has-a-title" };
        Assert.Equal("", SessionManager.PositionKeyFor(status, "id-1"));
    }

    // --- GatherTeams ---------------------------------------------------------
    //
    // The stacking order the tray menu reads top-to-bottom and the orbs are
    // laid out in. Its job is to put a team's members straight behind their
    // lead so the arrows stay short and don't cross the unrelated sessions that
    // happened to start in between — and, just as much, to emit every tracked
    // id exactly once. A dropped id is an orb that silently isn't laid out.

    // The two questions GatherTeams asks, over one table: an id in the table is
    // tracked, and its value is the lead it names — empty for none, and
    // deliberately allowed to be null, which is what SessionStatus.Lead really
    // is for a session with no pid to ask AgentTeam about.
    private static (Func<string, bool>, Func<string, string?>) Team(
        params (string Id, string? Lead)[] rows)
    {
        var map = rows.ToDictionary(r => r.Id, r => r.Lead, StringComparer.Ordinal);
        return (map.ContainsKey, id => map.GetValueOrDefault(id));
    }

    private static List<string> Gather(
        List<string> order, (Func<string, bool> Tracked, Func<string, string?> LeadOf) team) =>
        SessionManager.GatherTeams(order, team.Tracked, team.LeadOf);

    [Fact]
    public void GatherTeams_PullsMembersUpBehindTheirLead()
    {
        // first-seen order interleaves the team with two unrelated sessions;
        // the arrows have to end up short regardless.
        var order = new List<string> { "lead", "stranger-1", "member-a", "stranger-2", "member-b" };

        var gathered = Gather(order, Team(
            ("lead", ""), ("stranger-1", ""), ("stranger-2", ""),
            ("member-a", "lead"), ("member-b", "lead")));

        Assert.Equal(
            new[] { "lead", "member-a", "member-b", "stranger-1", "stranger-2" },
            gathered);
    }

    [Fact]
    public void GatherTeams_LeavesAMemberWhoseLeadIsntOnScreenExactlyWhereItWas()
    {
        // "There's nothing to gather it under." A lead can end, or be filtered
        // out by the lifetime setting, while its member outlives it.
        var order = new List<string> { "stranger", "orphan" };

        var gathered = Gather(order, Team(("stranger", ""), ("orphan", "a-lead-that-ended")));

        Assert.Equal(new[] { "stranger", "orphan" }, gathered);
    }

    [Fact]
    public void GatherTeams_IgnoresAnIdThatNamesItselfAsItsOwnLead()
    {
        // Would otherwise gather the id under itself and emit it twice — or
        // never, depending on which loop reached it first.
        var gathered = Gather(new List<string> { "self" }, Team(("self", "self")));

        Assert.Equal(new[] { "self" }, gathered);
    }

    [Fact]
    public void GatherTeams_SkipsIdsThatAreNoLongerTracked()
    {
        // _order outlives _statuses by one scan in the removal pass, so an id
        // with no status behind it is a real state and not a defensive check.
        var gathered = Gather(new List<string> { "alive", "removed" }, Team(("alive", "")));

        Assert.Equal(new[] { "alive" }, gathered);
    }

    [Fact]
    public void GatherTeams_StillLaysOutASessionWhoseLeadFieldIsNull()
    {
        // Not hypothetical: SessionStatus.Lead is assigned from AgentTeam's
        // answer, and a Claude Code session with no pid — a live background job
        // — has no process to ask, so the field can arrive null rather than
        // empty. Treating "no lead" and "not tracked" as one nullable answer
        // silently dropped exactly those orbs out of the stacking order.
        var gathered = Gather(
            new List<string> { "no-pid", "ordinary" },
            Team(("no-pid", null), ("ordinary", "")));

        Assert.Equal(new[] { "no-pid", "ordinary" }, gathered);
    }

    [Fact]
    public void GatherTeams_EmitsEveryTrackedIdExactlyOnceEvenWhenTeamsNest()
    {
        // A lead that is itself somebody's member would leave its own members
        // unemitted by the main loop; the sweep at the end is what catches
        // them. Nesting isn't a thing Claude Code does today, but a dropped orb
        // would be a silent one.
        var order = new List<string> { "top", "middle", "bottom" };

        var gathered = Gather(order, Team(("top", ""), ("middle", "top"), ("bottom", "middle")));

        Assert.Equal(3, gathered.Count);
        Assert.Equal(gathered.Distinct().Count(), gathered.Count);
        Assert.Contains("top", gathered);
        Assert.Contains("middle", gathered);
        Assert.Contains("bottom", gathered);
    }

    // --- ClampIntoWork -------------------------------------------------------

    [Theory]
    // Inside already, so nothing moves.
    [InlineData(500, 400, 500, 400)]
    // Off each edge in turn: an orb is placed by its top-left corner, so the
    // right/bottom limits are the edge less a whole orb.
    [InlineData(-90, 400, 0, 400)]
    [InlineData(500, -90, 500, 0)]
    [InlineData(5000, 400, 1864, 400)]
    [InlineData(500, 5000, 500, 1024)]
    public void ClampIntoWork_PullsAnOrbBackUntilAllOfItIsOnTheScreen(
        int x, int y, int expectedX, int expectedY)
    {
        var work = new PixelRect(0, 0, 1920, 1080);

        var clamped = SessionManager.ClampIntoWork(new PixelPoint(x, y), work, 56);

        Assert.Equal(new PixelPoint(expectedX, expectedY), clamped);
    }

    [Fact]
    public void ClampIntoWork_RespectsAWorkAreaThatDoesNotStartAtTheOrigin()
    {
        // A second monitor to the left of the primary has negative coordinates,
        // and a menu bar means the primary's work area starts below zero on Y.
        var work = new PixelRect(-1920, 25, 1920, 1055);

        Assert.Equal(
            new PixelPoint(-1920, 25),
            SessionManager.ClampIntoWork(new PixelPoint(-3000, -100), work, 56));
    }

    [Fact]
    public void ClampIntoWork_SurvivesAWorkAreaSmallerThanAnOrb()
    {
        // Math.Clamp throws when its bounds are inverted, which is exactly what
        // `Right - orbSize < X` produces. The Math.Max guards are why this
        // returns a corner instead of taking the app down.
        var tiny = new PixelRect(100, 100, 20, 20);

        Assert.Equal(
            new PixelPoint(100, 100),
            SessionManager.ClampIntoWork(new PixelPoint(500, 500), tiny, 56));
    }

    // --- room ids and titles -------------------------------------------------

    [Fact]
    public void RoomId_NamespacesAwayFromBothClaudeCodeIdsAndGatewayKeys()
    {
        // "Nothing on the gateway answers to it" — it is an orb this app
        // invents, and it shares a dictionary with Claude Code's UUIDs.
        Assert.Equal("openclaw:room:general", SessionManager.RoomId("general"));
    }

    [Theory]
    // A member's title is "<agent> — <channel>"; the room is the channel alone.
    [InlineData("Lilibeth — general", "general")]
    [InlineData("Zara — dev ops", "dev ops")]
    // An em dash with no spaces around it is part of a name, not a separator.
    [InlineData("Lilibeth—general", "Lilibeth—general")]
    // No separator at all: the title is already the room's.
    [InlineData("general", "general")]
    [InlineData("", "")]
    // A title that starts with the separator has no agent half to strip.
    [InlineData(" — general", " — general")]
    public void RoomTitle_KeepsTheChannelHalfOfAMembersTitle(string title, string expected)
    {
        Assert.Equal(expected, SessionManager.RoomTitle(title));
    }
}
