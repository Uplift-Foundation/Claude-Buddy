using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ClaudeBuddy.Tests;

// The click path for a session Claude Desktop hosts (CB-265): which process is
// the app to raise, and every decision that keeps a Desktop-hosted session off
// the terminal paths that cannot reach it.
//
// The fixture is the real chain measured on a Mac on 2026-10-14, not an invented
// one: `claude` (8251) <- `Contents/Helpers/disclaimer` (8250) <- the Electron
// main `Contents/MacOS/Claude` (4392) <- launchd (1). The bundle is a profile's
// cloned copy under Application Support, so the paths contain spaces — which is
// the reason none of the rules here splits a command on whitespace.
public class DesktopSessionFocusTests
{
    private const string Bundle =
        "/Users/warren/Library/Application Support/Orbweaver/bundles/work/Claude.app";

    // `ps -eo pid=,ppid=,args=` shaped, run through the production parser so the
    // fixture also proves the spaced paths survive it.
    private static readonly string RealChain =
        $"    1     0 /sbin/launchd\n" +
        $" 4392     1 {Bundle}/Contents/MacOS/Claude\n" +
        $" 4400  4392 {Bundle}/Contents/Frameworks/Claude Helper (Renderer).app/Contents/MacOS/Claude Helper (Renderer) --type=renderer\n" +
        $" 8250  4392 {Bundle}/Contents/Helpers/disclaimer --pgroup -- " +
        "/Users/warren/Library/Application Support/Claude/claude-code/2.1.0/abc123/claude.app/Contents/MacOS/claude " +
        "--output-format stream-json --input-format stream-json --permission-prompt-tool=stdio\n" +
        " 8251  8250 /Users/warren/Library/Application Support/Claude/claude-code/2.1.0/abc123/claude.app/Contents/MacOS/claude " +
        "--output-format stream-json --input-format stream-json --permission-prompt-tool=stdio --setting-sources=user,project,local\n";

    private static List<SessionDependents.ProcessRow> Table() => SessionDependents.ParsePs(RealChain);

    private static bool Mac(string command) => DesktopSessionFocus.LooksLikeDesktopMain(command, onWindows: false);

    private static SessionStatus Desktop(int pid = 8251) => new()
    {
        Cli = "claude",
        Entrypoint = SessionStatus.DesktopEntrypoint,
        SessionPid = pid,
    };

    // --- HostOf ----------------------------------------------------------------

    [Fact]
    public void TheWalkClimbsPastTheDisclaimerToTheElectronMain()
    {
        Assert.Equal(4392, DesktopSessionFocus.HostOf(Table(), 8251, Mac));
    }

    // Starting above the session: the session's own row is never the answer even
    // if it would satisfy the predicate.
    [Fact]
    public void TheSessionItselfIsNeverTheHost()
    {
        Assert.Equal(0, DesktopSessionFocus.HostOf(Table(), 8251, _ => false));

        var seen = new List<string>();
        DesktopSessionFocus.HostOf(Table(), 8251, c => { seen.Add(c); return false; });
        Assert.DoesNotContain(seen, c => c.Contains("--setting-sources=user,project,local"));
    }

    [Fact]
    public void ANonPositiveOrAbsentPidFindsNothing()
    {
        Assert.Equal(0, DesktopSessionFocus.HostOf(Table(), 0, Mac));
        Assert.Equal(0, DesktopSessionFocus.HostOf(Table(), -3, Mac));
        Assert.Equal(0, DesktopSessionFocus.HostOf(Table(), 99999, Mac));
        Assert.Equal(0, DesktopSessionFocus.HostOf(new List<SessionDependents.ProcessRow>(), 8251, Mac));
    }

    // The chain simply ends: the parent is not in the table (or is pid 0).
    [Fact]
    public void AChainThatEndsWithoutAHostFindsNothing()
    {
        var table = Table().Where(r => r.Pid != 4392).ToList();
        Assert.Equal(0, DesktopSessionFocus.HostOf(table, 8251, Mac));

        var orphan = new List<SessionDependents.ProcessRow> { new(500, 0, "/bin/claude") };
        Assert.Equal(0, DesktopSessionFocus.HostOf(orphan, 500, Mac));
    }

    [Fact]
    public void ACycleTerminatesWithNothing()
    {
        var table = new List<SessionDependents.ProcessRow>
        {
            new(10, 11, "a"), new(11, 12, "b"), new(12, 11, "c"),
        };

        Assert.Equal(0, DesktopSessionFocus.HostOf(table, 10, _ => false));
    }

    // A pid whose parent is itself is the smallest cycle.
    [Fact]
    public void AProcessThatIsItsOwnParentTerminatesWithNothing()
    {
        var table = new List<SessionDependents.ProcessRow> { new(7, 7, "x") };
        Assert.Equal(0, DesktopSessionFocus.HostOf(table, 7, _ => false));
    }

    // Eight ancestors are examined and no more. The host at the eighth is
    // found; at the ninth it is not — the pair is what pins the cap rather than
    // only the "does not hang" half.
    [Theory]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public void TheWalkExaminesExactlyTheHopCap(int hostDepth, bool found)
    {
        var table = new List<SessionDependents.ProcessRow>();
        for (var i = 0; i <= 10; i++) table.Add(new(100 + i, 101 + i, i == hostDepth ? "HOST" : "other"));
        table.Add(new(111, 1, "other"));

        var pid = DesktopSessionFocus.HostOf(table, 100, c => c == "HOST");

        Assert.Equal(found ? 100 + hostDepth : 0, pid);
    }

    [Fact]
    public void AWindowsTableWalksPastElectronChildrenToTheMainProcess()
    {
        const string app = @"C:\Program Files\WindowsApps\Claude_1.0_x64__abc\app\Claude.exe";
        var table = new List<SessionDependents.ProcessRow>
        {
            new(900, 800, @"C:\Users\w\AppData\Roaming\Claude\claude-code\claude.exe --output-format stream-json"),
            new(800, 700, app + " --type=utility --utility-sub-type=node.mojom.NodeService"),
            new(700, 4, app),
        };

        Assert.Equal(700, DesktopSessionFocus.HostOf(table, 900, c => DesktopSessionFocus.LooksLikeDesktopMain(c, onWindows: true)));
    }

    // --- LooksLikeDesktopMain -------------------------------------------------

    [Fact]
    public void TheMainExecutableIsTheMainProcessAtTheEndOrFollowedByArguments()
    {
        Assert.True(Mac($"{Bundle}/Contents/MacOS/Claude"));
        Assert.True(Mac($"{Bundle}/Contents/MacOS/Claude --some-flag"));
        Assert.True(Mac("/Applications/Claude.app/Contents/MacOS/Claude"));
    }

    // Negative controls: each of these shares most of the path with the real
    // thing and must still be refused.
    [Fact]
    public void HelpersTheDisclaimerAndLookAlikesAreNotTheMainProcess()
    {
        Assert.False(Mac($"{Bundle}/Contents/Frameworks/Claude Helper (Renderer).app/Contents/MacOS/Claude Helper (Renderer) --type=renderer"));
        Assert.False(Mac($"{Bundle}/Contents/Helpers/disclaimer --pgroup -- /x/claude.app/Contents/MacOS/claude --output-format stream-json"));
        Assert.False(Mac("/x/claude.app/Contents/MacOS/claude --output-format stream-json"));
        Assert.False(Mac($"{Bundle}/Contents/MacOS/ClaudeHelper"));
        Assert.False(Mac(""));
    }

    // A first occurrence that is a prefix of something longer must not hide a
    // genuine one later in the same line.
    [Fact]
    public void ALaterGenuineOccurrenceStillCounts()
    {
        Assert.True(Mac("/a/Claude.app/Contents/MacOS/ClaudeX /b/Claude.app/Contents/MacOS/Claude"));
    }

    // The two scans must agree about what the main executable is: this file
    // keeps its own copy of the suffix, so it is pinned to MacOSProcessScan's.
    [Fact]
    public void ItAgreesWithMacOSProcessScanAboutTheMainExecutable()
    {
        var main = $"{Bundle}/Contents/MacOS/Claude";
        var helper = $"{Bundle}/Contents/Frameworks/Claude Helper (Renderer).app/Contents/MacOS/Claude Helper (Renderer)";

        Assert.Equal(Bundle, MacOSProcessScan.BundleFromExecutable(main));
        Assert.True(Mac(main));
        Assert.Null(MacOSProcessScan.BundleFromExecutable(helper));
        Assert.False(Mac(helper));
    }

    [Fact]
    public void OnWindowsOnlyThePackagedMainProcessCounts()
    {
        const string app = @"C:\Program Files\WindowsApps\Claude_1.0_x64__abc\app\Claude.exe";

        Assert.True(DesktopSessionFocus.LooksLikeDesktopMain(app, onWindows: true));
        Assert.True(DesktopSessionFocus.LooksLikeDesktopMain(app.ToUpperInvariant(), onWindows: true));
        Assert.False(DesktopSessionFocus.LooksLikeDesktopMain(app + " --type=gpu-process", onWindows: true));
        Assert.False(DesktopSessionFocus.LooksLikeDesktopMain(@"C:\dev\Claude\Claude.exe", onWindows: true));
        Assert.False(DesktopSessionFocus.LooksLikeDesktopMain("", onWindows: true));
    }

    [Fact]
    public void OnWindowsAWindowsAppsPathInALaunchersArgumentsIsNotTheHost()
    {
        const string app = @"C:\Program Files\WindowsApps\Claude_1.0_x64__abc\app\Claude.exe";

        Assert.True(DesktopSessionFocus.LooksLikeDesktopMain($"\"{app}\" --squirrel", onWindows: true));
        Assert.False(DesktopSessionFocus.LooksLikeDesktopMain($"C:\\Windows\\System32\\cmd.exe /c \"{app}\"", onWindows: true));
        Assert.False(DesktopSessionFocus.LooksLikeDesktopMain($"\"C:\\tools\\launcher.exe\" \"{app}\"", onWindows: true));
    }

    [Theory]
    [InlineData("\"C:\\a b\\x.exe\" --flag", "C:\\a b\\x.exe")]
    [InlineData("\"C:\\a b\\x.exe", "C:\\a b\\x.exe")]
    [InlineData("C:\\a b\\x.EXE --flag", "C:\\a b\\x.EXE")]
    [InlineData("  claude --flag", "claude")]
    [InlineData("claude", "claude")]
    public void ExecutableOfTakesTheProgramNotItsArguments(string command, string expected) =>
        Assert.Equal(expected, DesktopSessionFocus.ExecutableOf(command));

    // --- FallbackPid ------------------------------------------------------------

    [Fact]
    public void OnlyASingleRunningInstanceIsAGuessWorthMaking()
    {
        Assert.Equal(0, DesktopSessionFocus.FallbackPid(Array.Empty<ClaudeInstance>()));
        Assert.Equal(4392, DesktopSessionFocus.FallbackPid(new[] { new ClaudeInstance(4392, null) }));
        Assert.Equal(0, DesktopSessionFocus.FallbackPid(new[]
        {
            new ClaudeInstance(4392, null), new ClaudeInstance(5000, "/tmp/other"),
        }));
    }

    // --- Focus ------------------------------------------------------------------

    private static (bool ok, List<int> activated, int acks) Run(
        SessionStatus status,
        IReadOnlyList<SessionDependents.ProcessRow>? table,
        IReadOnlyList<ClaudeInstance> instances,
        bool onWindows = false)
    {
        var activated = new List<int>();
        var acks = 0;
        var ok = DesktopSessionFocus.Focus(
            status, () => acks++, () => table, () => instances, activated.Add, onWindows);
        return (ok, activated, acks);
    }

    [Fact]
    public void FocusRaisesTheHostFoundByTheWalkAndAcknowledges()
    {
        var (ok, activated, acks) = Run(Desktop(), Table(), Array.Empty<ClaudeInstance>());

        Assert.True(ok);
        Assert.Equal(new[] { 4392 }, activated);
        Assert.Equal(1, acks);
    }

    // The walk wins over the fallback even when exactly one instance is
    // running under a different pid.
    [Fact]
    public void TheWalkOutranksTheFallback()
    {
        var (_, activated, _) = Run(Desktop(), Table(), new[] { new ClaudeInstance(5555, null) });

        Assert.Equal(new[] { 4392 }, activated);
    }

    [Fact]
    public void AnUnreadableTableFallsBackToTheOneRunningInstance()
    {
        var (ok, activated, acks) = Run(Desktop(), null, new[] { new ClaudeInstance(5555, null) });

        Assert.True(ok);
        Assert.Equal(new[] { 5555 }, activated);
        Assert.Equal(1, acks);
    }

    [Fact]
    public void AWalkThatFindsNothingFallsBackToTheOneRunningInstance()
    {
        var (_, activated, _) = Run(Desktop(pid: 99999), Table(), new[] { new ClaudeInstance(5555, null) });

        Assert.Equal(new[] { 5555 }, activated);
    }

    // The negative controls: nothing found anywhere, or an ambiguous fallback,
    // does nothing at all and does not claim to have.
    [Fact]
    public void WhenNothingCanBeFoundNothingIsRaisedAndNothingIsAcknowledged()
    {
        var (ok, activated, acks) = Run(Desktop(pid: 99999), Table(), Array.Empty<ClaudeInstance>());

        Assert.False(ok);
        Assert.Empty(activated);
        Assert.Equal(0, acks);
    }

    [Fact]
    public void TwoRunningInstancesAreNeverGuessedBetween()
    {
        var (ok, activated, acks) = Run(Desktop(pid: 99999), null,
            new[] { new ClaudeInstance(1, null), new ClaudeInstance(2, null) });

        Assert.False(ok);
        Assert.Empty(activated);
        Assert.Equal(0, acks);
    }

    [Fact]
    public void FocusWorksWithoutAnAcknowledgeCallback()
    {
        var activated = new List<int>();

        var ok = DesktopSessionFocus.Focus(
            Desktop(), acknowledge: null, () => Table(), () => Array.Empty<ClaudeInstance>(),
            activated.Add, onWindows: false);

        Assert.True(ok);
        Assert.Equal(new[] { 4392 }, activated);
    }

    [Fact]
    public void FocusOnWindowsUsesTheWindowsRule()
    {
        const string app = @"C:\Program Files\WindowsApps\Claude_1.0_x64__abc\app\Claude.exe";
        var table = new List<SessionDependents.ProcessRow>
        {
            new(900, 700, @"C:\Users\w\claude.exe --output-format stream-json"),
            new(700, 4, app),
        };

        var (_, activated, _) = Run(Desktop(pid: 900), table, Array.Empty<ClaudeInstance>(), onWindows: true);

        Assert.Equal(new[] { 700 }, activated);

        // The same table read with the macOS rule finds nothing: the platform
        // argument is what decides, not the shape of the table.
        var (ok, _, _) = Run(Desktop(pid: 900), table, Array.Empty<ClaudeInstance>(), onWindows: false);
        Assert.False(ok);
    }

    // --- ClickRouting -----------------------------------------------------------

    [Fact]
    public void OnlyADesktopHostedSessionOpensClaudeDesktop()
    {
        Assert.True(ClickRouting.OpensClaudeDesktop(Desktop()));

        Assert.False(ClickRouting.OpensClaudeDesktop(null));
        Assert.False(ClickRouting.OpensClaudeDesktop(new SessionStatus { Cli = "claude", Entrypoint = "cli" }));
        Assert.False(ClickRouting.OpensClaudeDesktop(new SessionStatus { Cli = "claude" }));
        Assert.False(ClickRouting.OpensClaudeDesktop(
            new SessionStatus { Source = SessionSource.Codex, Cli = "codex", Entrypoint = SessionStatus.DesktopEntrypoint }));
    }

    // Both a pane-alive-but-detached claim and a background shape would answer
    // something for an ordinary session; for a Desktop one every answer is None.
    [Fact]
    public void ADesktopSessionHasNoTerminalFallbackAndNothingToAttach()
    {
        var status = Desktop();
        status.Shape = LocalSessionShape.Background;
        status.TmuxPane = "%1";
        status.TmuxBin = "/usr/bin/tmux";

        Assert.Equal(ClickFallback.None, ClickRouting.FallbackFor(status, "s1", paneAliveButDetached: true));
        Assert.Equal(ClickFallback.None, ClickRouting.FallbackFor(status, "s1", paneAliveButDetached: false));
        Assert.False(ClickRouting.AttachWouldReach(status, "s1"));

        // Control: the same shape without the entrypoint does attach.
        var twin = new SessionStatus { Cli = "claude", Shape = LocalSessionShape.Background, SessionPid = 5 };
        Assert.True(ClickRouting.AttachWouldReach(twin, "s1"));
    }

    // --- TerminalTyping ---------------------------------------------------------

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ADesktopSessionIsNeverTypedIntoOnAnyPlatform(bool onMacOS, bool onWindows)
    {
        var status = Desktop();
        status.TmuxPane = "%1";
        status.TermProgram = TerminalTyping.ITerm2Program;
        status.TermId = "GUID";

        var route = TerminalTyping.RouteFor(status, onMacOS, onWindows, new TerminalTyping.Tools(true, true, true));

        Assert.Equal(TerminalTyping.Route.None, route);

        // Control: the same coordinates without the entrypoint do route.
        status.Entrypoint = "cli";
        Assert.NotEqual(TerminalTyping.Route.None,
            TerminalTyping.RouteFor(status, onMacOS, onWindows, new TerminalTyping.Tools(true, true, true)));
    }

    [Fact]
    public void ADesktopSessionIsNeverMessagedEvenWithALiveRegistry()
    {
        Assert.Equal(TerminalTyping.Channel.None,
            TerminalTyping.ChannelFor(Desktop(), TerminalTyping.Route.None, registryLive: true));

        var twin = new SessionStatus { Cli = "claude", SessionPid = 5 };
        Assert.Equal(TerminalTyping.Channel.Messaging,
            TerminalTyping.ChannelFor(twin, TerminalTyping.Route.None, registryLive: true));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void WhyNotNamesClaudeDesktopAndItsCodeTab(bool onMacOS, bool onWindows)
    {
        var why = TerminalTyping.WhyNot(Desktop(), onMacOS, onWindows);

        Assert.Equal($"Claude Desktop {TerminalTyping.CantTypePhrase} — reply in its Code tab.", why);
        Assert.Contains(TerminalTyping.CantTypePhrase, why);
    }

    // --- the composer and the panel's notes ---------------------------------------

    [Fact]
    public void TheComposerHintForADesktopSessionWinsOverEveryOtherArm()
    {
        const string hint = "Typing goes through Claude Desktop — click the orb to open it";

        foreach (var (canSend, canDeliver) in new[] { (false, false), (true, false), (false, true) })
        {
            Assert.Equal(hint, LocalCliChatSession.ComposerHintFor(
                canSend, canDeliver, replyEnabled: false,
                LocalSessionShape.Background, OrbPresence.NeedsInput, desktopHosted: true));
        }

        Assert.Equal(hint, LocalCliChatSession.DesktopComposerHint);

        // Control: the same inputs for an ordinary session say something else.
        Assert.Equal("Needs input — attach to reply", LocalCliChatSession.ComposerHintFor(
            false, false, replyEnabled: false,
            LocalSessionShape.Background, OrbPresence.NeedsInput, desktopHosted: false));
    }

    [Fact]
    public void ANoPaneNoteForADesktopSessionIsWhyNotAloneWithNoTerminalAdvice()
    {
        var note = LocalCliChatSession.NoPaneNote(Desktop(), LocalSessionShape.Terminal, onMacOS: true, onWindows: false);

        Assert.Equal(TerminalTyping.WhyNot(Desktop(), true, false), note);
        Assert.DoesNotContain("Reply in the terminal", note);
    }

    // --- TerminalFocuser --------------------------------------------------------

    // The guard returns before it starts any work. The pool work it prevents is
    // asynchronous, so the observable half is that the call returns and the
    // acknowledge callback is never invoked by it; that the path *would* have run
    // for an ordinary session is not asserted here, because running it means
    // real tmux, ps and osascript.
    [Fact]
    public void TheTerminalFocuserDoesNothingForADesktopSession()
    {
        var acknowledged = false;

        TerminalFocuser.Focus(Desktop(), acknowledge: () => acknowledged = true);
        System.Threading.Thread.Sleep(50);

        Assert.False(acknowledged);
    }
}
