using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ClaudeBuddy.Tests;

// Which subprocess questions a scan pass asks, and what the reconciliation half
// reads back. The scan used to ask these on the UI thread, one pane at a time;
// ScanProbePlan decides them from the files before the UI half runs, and each
// gate has to admit every case the UI half would read, or that case silently
// gets "unknown". These cases pin both directions: what must be asked, and what
// must not be asked on a machine that never paid for it before.
public class ScanProbesTests
{
    private const string A = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string B = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string C = "cccccccc-cccc-cccc-cccc-cccccccccccc";

    private static readonly Func<int, string> NoTeams = _ => "";

    // --- the plan -------------------------------------------------------------

    [Fact]
    public void AMachineOfOrdinaryTerminalSessionsAsksNothingButItsPanes()
    {
        // Two terminal sessions with their own pids, one in tmux. Nothing
        // background-ish, no shared pid, no team, so the daemon, the attach
        // scan and the viewer hunt are all left alone, exactly as the lazy
        // closure used to leave them.
        var found = Found(
            Terminal(A, pid: 100, pane: "%1"),
            Terminal(B, pid: 200));

        var plan = ScanProbePlan.For(found, NoTeams);

        Assert.Equal(new[] { found[0].Status }, plan.PaneClaims);
        Assert.False(plan.AskTheDaemon);
        Assert.False(plan.AskAttachClients);
        Assert.Empty(plan.ViewerCwds);
    }

    [Fact]
    public void OnlyClaudeCodePaneClaimsAreProbed()
    {
        // ReconcileTmuxPaneClaims reads Claude Code claims and nothing else; a
        // Codex session in tmux is not asked about.
        var found = Found(
            Terminal(A, pid: 100, pane: "%1", source: SessionSource.Codex),
            Terminal(B, pid: 200, pane: ""));

        Assert.Empty(ScanProbePlan.For(found, NoTeams).PaneClaims);
    }

    [Theory]
    [InlineData(0, true)]      // no pid of its own
    [InlineData(100, false)]   // no terminal
    public void ABackgroundIshSessionAsksTheDaemonAndTheAttachScan(int pid, bool knowsATerminal)
    {
        var found = Found(knowsATerminal ? Terminal(A, pid) : NoTerminal(A, pid));

        var plan = ScanProbePlan.For(found, NoTeams);

        Assert.True(plan.AskTheDaemon);
        Assert.True(plan.AskAttachClients);
    }

    [Fact]
    public void TwoClaudeFilesSharingAPidAskBoth()
    {
        // WorthAskingTheDaemon's third shape, a pid shared with another file.
        var plan = ScanProbePlan.For(Found(Terminal(A, 100), Terminal(B, 100)), NoTeams);

        Assert.True(plan.AskTheDaemon);
        Assert.True(plan.AskAttachClients);
    }

    [Fact]
    public void ACodexPairSharingAPidAsksTheDaemonForSupersededButNotTheAttachScan()
    {
        // Superseded asks the listing about any non-newest file of a shared
        // pid, whichever CLI wrote it. The attach scan is gated by worthAsking
        // alone, which is Claude Code's.
        var found = Found(
            Terminal(A, 100, source: SessionSource.Codex),
            Terminal(B, 100, source: SessionSource.Codex));

        var plan = ScanProbePlan.For(found, NoTeams);

        Assert.True(plan.AskTheDaemon);
        Assert.False(plan.AskAttachClients);
    }

    [Fact]
    public void ATeamMemberAsksTheDaemonWhetherItsLeadIsALiveJob()
    {
        // A member's presence asks isLiveJob(lead). Nothing else about this
        // machine is background-ish, so without this arm the listing would not
        // be fetched and the member would read its lead as no job at all.
        var found = Found(Terminal(A, 100), Terminal(B, 200));

        var plan = ScanProbePlan.For(found, pid => pid == 200 ? A : "");

        Assert.True(plan.AskTheDaemon);
        Assert.False(plan.AskAttachClients);
    }

    [Fact]
    public void ASessionNamingItselfOrAnotherCliNamingALeadAsksNothing()
    {
        // A lead's own argv can carry its id; the scan clears Lead then, so it
        // never asks. And a Codex process's lead is never read at all, since
        // membership is only computed for Claude Code.
        var found = Found(
            Terminal(A, 100),
            Terminal(B, 200, source: SessionSource.Codex));

        var plan = ScanProbePlan.For(found, pid => pid == 100 ? A : C);

        Assert.False(plan.AskTheDaemon);
    }

    [Fact]
    public void AViewerIsHuntedForALeadWithNoTerminalAndForAPidlessSession()
    {
        // WantsAgentViewer's two arms: a lead some process names, and the
        // legacy pid-less file. Keyed by directory as AgentTeamViewer caches it,
        // and once per directory however many sessions share it.
        var found = Found(
            NoTerminal(A, 100, cwd: "/work/lead/"),
            Terminal(B, 200),
            NoTerminal(C, 0, cwd: "/work/lead"));

        var plan = ScanProbePlan.For(found, pid => pid == 200 ? A : "");

        Assert.Equal(new[] { "/work/lead" }, plan.ViewerCwds);
    }

    [Fact]
    public void NoViewerIsHuntedForASessionThatCannotWantOne()
    {
        var found = Found(
            // A plain background job: has a pid, leads nothing.
            NoTerminal(A, 100, cwd: "/work/a"),
            // Knows its terminal already.
            Terminal(B, 0, cwd: "/work/b"),
            // Nowhere to look.
            NoTerminal(C, 0, cwd: ""),
            // Not Claude Code: there is no `claude agents` window for Codex.
            NoTerminal("codex-1", 0, cwd: "/work/d", source: SessionSource.Codex));

        Assert.Empty(ScanProbePlan.For(found, NoTeams).ViewerCwds);
    }

    [Fact]
    public void AProcessWithNoPidIsNeverAskedForItsLead()
    {
        var asked = new List<int>();

        ScanProbePlan.For(Found(Terminal(A, 0), Terminal(B, 7)), pid => { asked.Add(pid); return ""; });

        Assert.Equal(new[] { 7 }, asked);
    }

    // --- gathering ------------------------------------------------------------

    [Fact]
    public void GatheringAsksExactlyWhatThePlanNames()
    {
        var claim = new SessionStatus { TmuxPane = "%1" };
        var plan = new ScanProbePlan(new[] { claim }, AskTheDaemon: true, AskAttachClients: true,
            ViewerCwds: new[] { "/work/lead", "/work/none" });
        var jobs = new Dictionary<string, string> { [A] = "working" };
        var attached = new HashSet<string> { "0e043819" };
        var viewersAsked = new List<string>();

        var probes = ScanProbes.Gather(
            plan,
            paneOwners: claims =>
            {
                Assert.Same(plan.PaneClaims, claims);
                return new Dictionary<TmuxPaneKey, string?> { [TmuxPaneKey.Of(claim)] = B };
            },
            jobListing: () => jobs,
            attachClients: () => attached,
            viewerFor: cwd =>
            {
                viewersAsked.Add(cwd);
                return cwd == "/work/lead" ? new AgentViewer("/tmp/s", "%4", "ttys009") : null;
            });

        Assert.Equal(B, probes.PaneOwner(claim));
        Assert.Same(jobs, probes.Jobs);
        Assert.Same(attached, probes.AttachClients);
        Assert.Equal(plan.ViewerCwds, viewersAsked);
    }

    [Fact]
    public void GatheringAnEmptyPlanSpendsNothing()
    {
        var plan = new ScanProbePlan(
            Array.Empty<SessionStatus>(), false, false, Array.Empty<string>());

        var probes = ScanProbes.Gather(
            plan,
            paneOwners: _ => throw new InvalidOperationException("no claims to ask about"),
            jobListing: () => throw new InvalidOperationException("the daemon was not wanted"),
            attachClients: () => throw new InvalidOperationException("the process table was not wanted"),
            viewerFor: _ => throw new InvalidOperationException("no viewer was wanted"));

        Assert.Null(probes.Jobs);
        Assert.Null(probes.AttachClients);
        Assert.Null(probes.PaneOwner(new SessionStatus { TmuxPane = "%1" }));
    }

    // --- reading --------------------------------------------------------------

    [Fact]
    public void AClaimNobodyAnsweredForIsUnknown() =>
        Assert.Null(ScanProbes.Nothing.PaneOwner(new SessionStatus { TmuxPane = "%1" }));

    [Fact]
    public void AdoptionPointsASessionAtItsDirectorysViewerAndLeavesTmuxBinEmpty()
    {
        // bin='' is what tells the click path this pane was a guess rather than
        // where the hook found the session.
        var probes = WithViewers(("/work/lead", new AgentViewer("/tmp/s", "%4", "ttys009")));
        var status = new SessionStatus { Cwd = "/work/lead/" };

        Assert.True(probes.AdoptViewer(status));

        Assert.Equal("/tmp/s", status.TmuxSocket);
        Assert.Equal("%4", status.TmuxPane);
        Assert.Equal("ttys009", status.Tty);
        Assert.Equal("", status.TmuxBin);
    }

    [Theory]
    [InlineData("")]              // no directory to match on
    [InlineData("/work/other")]   // never asked about
    [InlineData("/work/empty")]   // asked, and nothing is running there
    public void AdoptionLearnsNothingWithoutAViewer(string cwd)
    {
        var probes = WithViewers(("/work/empty", null));
        var status = new SessionStatus { Cwd = cwd, Tty = "ttys001" };

        Assert.False(probes.AdoptViewer(status));
        Assert.Equal("", status.TmuxPane);
        Assert.Equal("ttys001", status.Tty);
    }

    [Theory]
    [InlineData("/work/lead", "/work/lead")]
    [InlineData("/work/lead///", "/work/lead")]
    public void AViewerKeyIsTheDirectoryWithoutItsTrailingSlashes(string cwd, string key) =>
        Assert.Equal(key, ScanProbes.ViewerKey(cwd));

    // --- helpers --------------------------------------------------------------

    private static ScanProbes WithViewers(params (string Cwd, AgentViewer? Viewer)[] viewers) =>
        ScanProbes.Gather(
            new ScanProbePlan(Array.Empty<SessionStatus>(), false, false,
                viewers.Select(v => v.Cwd).ToList()),
            _ => new Dictionary<TmuxPaneKey, string?>(),
            () => null,
            () => null,
            cwd => viewers.First(v => v.Cwd == cwd).Viewer);

    private static List<SessionManager.ScanEntry> Found(params SessionManager.ScanEntry[] entries) =>
        entries.ToList();

    private static SessionManager.ScanEntry Terminal(
        string id, int pid, string pane = "", string cwd = "/work/project",
        SessionSource source = SessionSource.ClaudeCode) =>
        new(id, new SessionStatus
        {
            Source = source,
            SessionPid = pid,
            Cwd = cwd,
            TermProgram = "iTerm.app",
            TmuxPane = pane,
        }, DateTime.UtcNow);

    private static SessionManager.ScanEntry NoTerminal(
        string id, int pid, string cwd = "/work/project",
        SessionSource source = SessionSource.ClaudeCode) =>
        new(id, new SessionStatus { Source = source, SessionPid = pid, Cwd = cwd }, DateTime.UtcNow);
}
