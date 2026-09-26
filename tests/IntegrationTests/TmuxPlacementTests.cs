using System.Diagnostics;
using Xunit;

namespace ClaudeBuddy.Tests;

// TMUX_TMPDIR and TMUX are process-wide, and every tmux this process runs reads
// them, so nothing else may run while a test here has them pointed at its own
// server.
[CollectionDefinition("TmuxServerEnv", DisableParallelization = true)]
public sealed class TmuxServerEnvCollection { }

// Where New chat and an orb attach actually land, on a real tmux.
//
// The unit tests pin which placement each caller asks for and what arguments
// that placement becomes. Neither proves what tmux does with them: that a
// new-window really lands in the client's session and moves the client there,
// or that the split really lands in the window the client is on. Those are
// tmux's answers, and they are what the user sees.
//
// Isolated rather than careful: TMUX_TMPDIR moves the *default* server's socket,
// so TerminalLauncher — which always talks to the default server — talks to a
// private one here, and whoever's tmux is running on the machine is never
// touched. TMUX is cleared too, because a tmux run from inside tmux follows it
// instead. The directory is short on purpose: a Unix socket path over 104 bytes
// is refused with "File name too long", and a scratch directory under the
// runner's temp path can exceed that once tmux adds its own tmux-<uid>/default.
[Collection("TmuxServerEnv")]
public sealed class TmuxPlacementTests : IDisposable
{
    private const string Session = "cbtest";

    private readonly string _dir = "/tmp/cbt-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _tmux = TerminalLauncher.ResolveTmux() ?? "tmux";
    private readonly string? _savedTmux = Environment.GetEnvironmentVariable("TMUX");
    private readonly string? _savedTmpDir = Environment.GetEnvironmentVariable("TMUX_TMPDIR");
    private Process? _client;

    public TmuxPlacementTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("TMUX", null);
        Environment.SetEnvironmentVariable("TMUX_TMPDIR", _dir);
        Tmux("new-session", "-d", "-s", Session, "-x", "200", "-y", "50", "-c", "/tmp");
    }

    public void Dispose()
    {
        try { Tmux("kill-server"); } catch (InvalidOperationException) { }

        if (_client is { HasExited: false }) _client.Kill(entireProcessTree: true);
        _client?.Dispose();

        Environment.SetEnvironmentVariable("TMUX", _savedTmux);
        Environment.SetEnvironmentVariable("TMUX_TMPDIR", _savedTmpDir);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // A client attached through a pty, which is what makes the server "one
    // the user is looking at". stdin stays open, or script(1) would see EOF and
    // detach at once.
    private void AttachClient()
    {
        var psi = new ProcessStartInfo("/usr/bin/script")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-q", "/dev/null", _tmux, "attach", "-t", Session }) psi.ArgumentList.Add(a);

        _client = Process.Start(psi)!;
        _client.OutputDataReceived += (_, _) => { };
        _client.BeginOutputReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Tmux("list-clients", "-F", "#{client_session}").Trim() != Session)
        {
            Assert.True(DateTime.UtcNow < deadline, "the tmux client never attached");
            Thread.Sleep(100);
        }
    }

    private string Tmux(params string[] args)
    {
        var psi = new ProcessStartInfo(_tmux)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        if (p.ExitCode != 0) throw new InvalidOperationException("tmux " + string.Join(" ", args) + ": " + p.StandardError.ReadToEnd());
        return stdout;
    }

    private string[] Windows() =>
        Tmux("list-windows", "-t", Session, "-F", "#{window_index}:#{window_panes}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private string ClientWindow() => Tmux("list-clients", "-F", "#{window_index}").Trim();

    private string WindowOf(string pane) => Tmux("display-message", "-p", "-t", pane, "#{window_index}").Trim();

    [MacTmuxFact]
    public void ANewChatOpensItsOwnWindowAndLeavesTheUsersAlone()
    {
        AttachClient();
        var before = ClientWindow();

        var pane = TerminalLauncher.PlaceInOwnTmuxWindow("exec /bin/sleep 30", "/tmp");

        Assert.NotNull(pane);
        Assert.StartsWith("%", pane);
        Assert.Equal(new[] { before + ":1", WindowOf(pane!) + ":1" }, Windows());
        Assert.NotEqual(before, WindowOf(pane!));
        Assert.Equal(WindowOf(pane!), ClientWindow());
    }

    // Round 6a's split, unchanged: an orb attach lands in the window the client
    // is on, as a second pane, and no window is created.
    [MacTmuxFact]
    public void AnOrbAttachStillSplitsTheUsersWindow()
    {
        AttachClient();
        var before = ClientWindow();

        var pane = TerminalLauncher.PlaceInTmux("exec /bin/sleep 30", "/tmp");

        Assert.NotNull(pane);
        Assert.Equal(before, WindowOf(pane!));
        Assert.Equal(new[] { before + ":2" }, Windows());
    }

    private string PathOf(string pane) =>
        Tmux("display-message", "-p", "-t", pane, "#{pane_current_path}").Trim();

    private bool PaneAlive(string pane) =>
        Tmux("list-panes", "-a", "-F", "#{pane_id}").Split('\n').Contains(pane);

    // tmux format-expands -c, so this directory — which exists — became
    // `…/fmtcbtestx`, which does not, and tmux started the pane in $HOME with
    // the CLI running there. The cd guard in the command lands it exactly.
    [MacTmuxFact]
    public void BothPlacementsLandInADirectoryNamedLikeATmuxFormat()
    {
        AttachClient();
        var dir = Path.Combine(_dir, "fmt#{session_name}x");
        Directory.CreateDirectory(dir);

        var chat = TerminalLauncher.PlaceInOwnTmuxWindow("exec /bin/sleep 30", dir);
        var attach = TerminalLauncher.PlaceInTmux("exec /bin/sleep 30", dir);

        Assert.Equal(RealPath(dir), PathOf(chat!));
        Assert.Equal(RealPath(dir), PathOf(attach!));
    }

    // A cwd that has gone runs nothing: the guard exits, the pane closes, and
    // no CLI starts in $HOME in its place.
    [MacTmuxFact]
    public void AMissingDirectoryRunsNothing()
    {
        AttachClient();

        var pane = TerminalLauncher.PlaceInOwnTmuxWindow("exec /bin/sleep 30", Path.Combine(_dir, "gone"));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (pane is not null && PaneAlive(pane))
        {
            Assert.True(DateTime.UtcNow < deadline, "the pane for a missing directory stayed open");
            Thread.Sleep(100);
        }
        Assert.Single(Windows());
    }

    // /tmp is a link to /private/tmp, and tmux reports the resolved path.
    private static string RealPath(string path) =>
        new DirectoryInfo(path).FullName.StartsWith("/tmp/", StringComparison.Ordinal)
            ? "/private" + new DirectoryInfo(path).FullName
            : new DirectoryInfo(path).FullName;

    // A server with no client is nowhere the user is looking, so New chat
    // declines it — the caller then opens a terminal window — and creates
    // nothing in it.
    [MacTmuxFact]
    public void WithNoClientANewChatLeavesTmuxAlone()
    {
        Assert.Null(TerminalLauncher.PlaceInOwnTmuxWindow("exec /bin/sleep 30", "/tmp"));
        Assert.Single(Windows());
    }
}
