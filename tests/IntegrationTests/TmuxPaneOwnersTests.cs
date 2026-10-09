using System.Diagnostics;
using Xunit;

namespace Orbweaver.Tests;

// TerminalFocuser.TmuxPaneOwners against a real tmux and the real process
// table: the batch the scan now asks from its background half, in place of a
// display-message and a whole `ps` per pane on the UI thread.
//
// The unit tests pin what OwnersFor decides from listings it is handed. What
// they cannot pin is that the listings are the ones tmux and ps actually print:
// that `list-panes -a -F` keys the pane a hook recorded as $TMUX_PANE, that
// pane_pid is the root of the process tree the Claude process sits in, and that
// ps prints the argv SessionIdFrom reads. A wrong format string fails there and
// nowhere else, as "unknown" for every pane, and a scan that learns nothing
// looks exactly like a scan with nothing to learn.
//
// Its own server on its own socket, named with -S on every call, so the
// environment is never touched and the user's tmux is never reached. That is
// also why this needs no collection: unlike TmuxPlacementTests it moves no
// process-wide variable. The socket path is short, because a Unix socket path
// over 104 bytes is refused.
public sealed class TmuxPaneOwnersTests : IDisposable
{
    private const string SessionId = "5c1d0e2f-7a3b-4c8d-9e0f-1a2b3c4d5e6f";

    private readonly string _dir = "/tmp/cbo-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _socket;
    private readonly string _tmux = TerminalLauncher.ResolveTmux() ?? "tmux";

    public TmuxPaneOwnersTests()
    {
        Directory.CreateDirectory(_dir);
        _socket = Path.Combine(_dir, "s");

        // Something whose argv[0] is named claude and carries --session-id, the
        // two things SessionIdFrom requires. A symlink to bash, so the argv is
        // whatever this says; `; :` stops bash exec'ing sleep in its place.
        File.CreateSymbolicLink(Path.Combine(_dir, "claude"), "/bin/bash");
    }

    public void Dispose()
    {
        try { Tmux("kill-server"); } catch (InvalidOperationException) { }
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [MacTmuxFact]
    public void APaneRunningClaudeIsOwnedByItsSessionAndAPlainShellByNobody()
    {
        var claude = Tmux("new-session", "-d", "-P", "-F", "#{pane_id}", "-s", "owners",
            $"{_dir}/claude -c 'sleep 60; :' x --session-id {SessionId}").Trim();
        var shell = Tmux("split-window", "-d", "-P", "-F", "#{pane_id}", "-t", "owners",
            "/bin/sleep 60").Trim();
        WaitForArgv("--session-id " + SessionId);

        var claims = new[] { Claim(claude), Claim(shell), Claim("%9999") };
        var owners = TerminalFocuser.TmuxPaneOwners(claims);

        Assert.Equal(SessionId, owners[TmuxPaneKey.Of(claims[0])]);

        // Resolved to a pid, with nothing under it naming a session: the same
        // "unknown" the per-pane probe gave, and ReconcileTmuxPaneClaims
        // changes nothing on it.
        Assert.True(owners.ContainsKey(TmuxPaneKey.Of(claims[1])));
        Assert.Null(owners[TmuxPaneKey.Of(claims[1])]);

        // A pane id this server never had is absent, as display-message
        // against it failed.
        Assert.False(owners.ContainsKey(TmuxPaneKey.Of(claims[2])));
    }

    [MacTmuxFact]
    public void AServerThatIsNotRunningAnswersNothing()
    {
        var claims = new[] { Claim("%1") };

        Assert.Empty(TerminalFocuser.TmuxPaneOwners(claims));
    }

    [MacTmuxFact]
    public void NoClaimsAsksNothing() =>
        Assert.Empty(TerminalFocuser.TmuxPaneOwners(Array.Empty<SessionStatus>()));

    private SessionStatus Claim(string pane) => new()
    {
        Source = SessionSource.ClaudeCode,
        TmuxBin = _tmux,
        TmuxSocket = _socket,
        TmuxPane = pane,
    };

    // The pane is created before its command has necessarily exec'd, so wait
    // until ps shows the argv the probe is about to look for.
    private static void WaitForArgv(string fragment)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!Run("/bin/ps", "-eo", "args=").Contains(fragment, StringComparison.Ordinal))
        {
            Assert.True(DateTime.UtcNow < deadline, "the pane's process never started");
            Thread.Sleep(100);
        }
    }

    private string Tmux(params string[] args) =>
        Run(_tmux, new[] { "-S", _socket }.Concat(args).ToArray());

    private static string Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException(exe + " " + string.Join(" ", args) + ": " + p.StandardError.ReadToEnd());
        return stdout;
    }
}
