using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// The scan's subprocess questions, through a real SessionManager: who owns
// each claimed tmux pane, what the daemon lists, who is attached, and which
// `claude agents` window watches a directory.
//
// These were answered on the UI thread, one pane at a time, until profiling a
// real machine found the dispatcher about 66% busy and inside
// TerminalFocuser.TmuxPaneOwner in six snapshots of six. ScheduleScan now
// answers them in its background half, and the reconciliation half on the UI
// thread only reads them. The first case here is the regression check for that
// move. The others check that moving the questions kept the answers in use:
// a stale pane claim is still dropped, a claim nobody could verify still is
// not, and a pid-less lead still adopts its viewer's pane.
//
// Every seam is handed over, so nothing here runs tmux, ps, lsof or `claude`.
[Collection("Settings")]
public class ScanProbeScanTests
{
    private static readonly int LivePid = Environment.ProcessId;

    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } =
            Path.Combine(Path.GetTempPath(), "cb-probes-" + Guid.NewGuid());

        public Scratch() => Directory.CreateDirectory(Dir);

        public void Write(
            string sessionId, int pid, string termProgram = "", string tmuxPane = "",
            string cwd = "/Users/user/project", DateTime? written = null)
        {
            var path = Path.Combine(Dir, sessionId + ".txt");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                State = "idle",
                Cli = "",
                Title = "",
                Cwd = cwd,
                SessionPid = pid,
                TermProgram = termProgram,
                Tty = termProgram.Length > 0 ? "/dev/ttys004" : "",
                TmuxPane = tmuxPane,
            }));

            if (written is not null) File.SetLastWriteTimeUtc(path, written.Value);
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static SessionManager Manager(
        Scratch scratch,
        Func<IReadOnlyList<SessionStatus>, IReadOnlyDictionary<TmuxPaneKey, string?>> paneOwners,
        Func<Dictionary<string, string>?>? jobListing = null,
        Func<HashSet<string>?>? attachClients = null,
        Func<string, AgentViewer?>? agentViewer = null)
    {
        ClaudeBuddySettings.ClaudeCodeEnabled = true;
        ClaudeBuddySettings.CodexEnabled = true;

        return new SessionManager(
            scratch.Dir,
            jobListing ?? (() => new Dictionary<string, string>(StringComparer.Ordinal)),
            attachClients ?? (() => new HashSet<string>(StringComparer.Ordinal)),
            dependents: _ => SessionDependents.Nothing,
            paneOwners: paneOwners,
            agentViewer: agentViewer ?? (_ => null));
    }

    private static IReadOnlyDictionary<TmuxPaneKey, string?> Owners(
        IReadOnlyList<SessionStatus> claims, string? owner) =>
        claims.ToDictionary(TmuxPaneKey.Of, _ => owner);

    [AvaloniaFact]
    public async Task ScheduleScanAsksEverySubprocessQuestionOffTheUIThread()
    {
        // One file for each question: a tmux-hosted terminal session (pane
        // ownership) and a pid-less session with no terminal, which makes the
        // pass worth asking the daemon and the attach scan about and wants a
        // viewer for its directory.
        using var scratch = new Scratch();
        scratch.Write("in-tmux", LivePid, termProgram: "iTerm.app", tmuxPane: "%1");
        scratch.Write("pidless", 0, cwd: "/work/lead");

        var asked = new List<(string Question, bool OnUIThread)>();
        void Record(string question)
        {
            lock (asked) asked.Add((question, Dispatcher.UIThread.CheckAccess()));
        }

        var manager = Manager(
            scratch,
            paneOwners: claims => { Record("panes"); return Owners(claims, null); },
            jobListing: () => { Record("daemon"); return new Dictionary<string, string>(); },
            attachClients: () => { Record("attached"); return new HashSet<string>(); },
            agentViewer: _ => { Record("viewer"); return null; });

        Assert.True(Dispatcher.UIThread.CheckAccess());
        await manager.ScheduleScan();

        Assert.Equal(new[] { "attached", "daemon", "panes", "viewer" },
            asked.Select(a => a.Question).OrderBy(q => q, StringComparer.Ordinal));
        Assert.All(asked, a => Assert.False(a.OnUIThread, a.Question + " was asked on the UI thread"));

        // And the scan it fed still finished on the UI thread.
        Assert.NotNull(manager.StatusFor("in-tmux"));
    }

    [AvaloniaFact]
    public async Task TheDispatcherKeepsRunningWorkWhileTheScanIsWaitingOnASubprocess()
    {
        // What the user felt: a menu, a drag or a chat panel waiting on `ps`.
        // Deterministic rather than timed. The pane question posts a job to the
        // dispatcher from inside itself and does not return until that job has
        // run. With the question on the background thread, the dispatcher is
        // free and the job runs straight away. With it on the UI thread, the job
        // cannot run until the question returns, so the wait times out and the
        // assertion names it. The ten seconds bounds a failure, never a pass.
        //
        // Posted from inside the question on purpose. A job the test posted
        // right after calling ScheduleScan would be queued ahead of the scan's
        // UI-thread continuation, so it would run first even if every question
        // were asked in that continuation, and the test would pass against the
        // very bug it exists for.
        using var scratch = new Scratch();
        scratch.Write("in-tmux", LivePid, termProgram: "iTerm.app", tmuxPane: "%1");

        using var dispatcherRan = new ManualResetEventSlim();
        var ranWhileAsking = false;

        var manager = Manager(scratch, paneOwners: claims =>
        {
            Dispatcher.UIThread.Post(dispatcherRan.Set);
            ranWhileAsking = dispatcherRan.Wait(TimeSpan.FromSeconds(10));
            return Owners(claims, null);
        });

        await manager.ScheduleScan();

        Assert.True(ranWhileAsking, "a dispatcher job waited for the scan's subprocess question");
    }

    [AvaloniaFact]
    public void AStalePaneClaimIsDroppedWhenItsPanesVerifiedOwnerIsInTheSameScan()
    {
        // "old" claims %9, and the pane's live process names "new". Both files
        // carry this process's pid, which is the only pid a test can be sure is
        // alive, and "old" is the newer file, so without reconciliation the
        // superseded rule keeps "old" and drops "new". With it, "old" goes before
        // that rule runs, and "new" inherits the pane it is actually in.
        using var scratch = new Scratch();
        scratch.Write("old", LivePid, termProgram: "iTerm.app", tmuxPane: "%9");
        scratch.Write("new", LivePid, written: DateTime.UtcNow.AddMinutes(-1));

        var manager = Manager(scratch, paneOwners: claims => Owners(claims, "new"));
        manager.ScanAndUpdate();

        Assert.Null(manager.StatusFor("old"));
        Assert.Equal("%9", manager.StatusFor("new")!.TmuxPane);
    }

    [AvaloniaFact]
    public void APaneClaimNobodyCouldVerifyChangesNothing()
    {
        // The paired control for the case above, with the same files and an
        // owner of "unknown". Nothing is removed on an absent answer, so the
        // superseded rule decides exactly as it would have with no probe at all.
        using var scratch = new Scratch();
        scratch.Write("old", LivePid, termProgram: "iTerm.app", tmuxPane: "%9");
        scratch.Write("new", LivePid, written: DateTime.UtcNow.AddMinutes(-1));

        var manager = Manager(scratch, paneOwners: claims => Owners(claims, null));
        manager.ScanAndUpdate();

        Assert.Equal("%9", manager.StatusFor("old")!.TmuxPane);
        Assert.Null(manager.StatusFor("new"));
    }

    [AvaloniaFact]
    public void APidlessSessionAdoptsTheViewerForItsDirectory()
    {
        // Adoption used to be AgentTeamViewer.TryAdopt, walking ps and lsof on
        // the UI thread. The walk now happens in the background half, and this
        // is the UI half applying its answer: the session gets the viewer's
        // pane and tty, and no TmuxBin, which is how the click path knows the
        // pane is a guess.
        //
        // Listed by the daemon as a live job, since a pid-less Claude Code
        // session the daemon rules out is dropped as NotALiveJob whatever it
        // adopts.
        using var scratch = new Scratch();
        scratch.Write("pidless", 0, cwd: "/work/lead/");

        var viewersAsked = new List<string>();
        var manager = Manager(
            scratch,
            paneOwners: claims => Owners(claims, null),
            jobListing: () => new Dictionary<string, string> { ["pidless"] = "working" },
            agentViewer: cwd =>
            {
                viewersAsked.Add(cwd);
                return new AgentViewer("/tmp/viewer", "%4", "ttys009");
            });
        manager.ScanAndUpdate();

        Assert.Equal(new[] { "/work/lead" }, viewersAsked);
        var status = manager.StatusFor("pidless");
        Assert.NotNull(status);
        Assert.Equal("%4", status!.TmuxPane);
        Assert.Equal("/tmp/viewer", status.TmuxSocket);
        Assert.Equal("ttys009", status.Tty);
        Assert.Equal("", status.TmuxBin);
    }

    [AvaloniaFact]
    public void AMachineOfTerminalSessionsSpendsOnlyThePaneQuestion()
    {
        // The daemon, the attach scan and the viewer hunt are each counted,
        // because the point is that they do not happen. A pass with nothing
        // background-ish on it paid for none of them before this change either.
        using var scratch = new Scratch();
        scratch.Write("in-tmux", LivePid, termProgram: "iTerm.app", tmuxPane: "%1");

        var other = 0;
        var panes = 0;
        var manager = Manager(
            scratch,
            paneOwners: claims => { panes++; return Owners(claims, null); },
            jobListing: () => { other++; return null; },
            attachClients: () => { other++; return null; },
            agentViewer: _ => { other++; return null; });
        manager.ScanAndUpdate();

        Assert.Equal(1, panes);
        Assert.Equal(0, other);
    }
}
