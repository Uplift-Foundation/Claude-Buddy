using System.Diagnostics;
using Xunit;

namespace ClaudeBuddy.Tests;

// The generated attach script, run past a real shell.
//
// This suite exists for the reason OpenArgumentDeliveryTests next door records,
// and CLAUDE.md states outright: a format someone else defines is covered here
// *as well as* by a unit test, because the two fail differently. The unit tests
// in TerminalScriptsTests assert the structure of our own string — that the
// socket is pinned, that the attach has no target, that the cd is there — and
// every one of those passes whether or not `sh` can parse the result. They are
// assertions about a belief, in a file that never runs one.
//
// What is at stake is a click that appears to do nothing. This script is written
// to disk and handed to a terminal by `open -a`, so a quoting mistake does not
// throw anywhere this app can see it: the terminal opens, the shell prints a
// syntax error into a window that may already have scrolled, and the orb the
// user clicked still has no session in front of it. Exactly the failure the
// whole click-access change exists to remove, arriving through the one line no
// unit test can check.
//
// `sh -n` reads and parses without executing, which is the whole of what is
// wanted: nothing here attaches to a tmux server, opens a terminal, or runs
// tmux at all.
public class TmuxAttachScriptTests
{
    private static void AssertParses(string script)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "cb-attach-" + Guid.NewGuid().ToString("N")[..12] + ".sh");

        try
        {
            File.WriteAllText(path, script);

            var psi = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // -n: parse it, run none of it.
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add(path);

            using var process = Process.Start(psi);
            Assert.NotNull(process);

            var stderr = process!.StandardError.ReadToEnd();
            process.WaitForExit(10_000);

            Assert.True(
                process.ExitCode == 0,
                $"sh -n rejected the generated script (exit {process.ExitCode}): {stderr}\n{script}");
            Assert.Equal("", stderr.Trim());
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [UnixFact]
    public void TheOrdinaryScriptParses()
    {
        AssertParses(TerminalScripts.TmuxAttachScript(
            "/opt/homebrew/bin/tmux", "/tmp/tmux-501/claude-swarm-88341",
            "/Users/warren/Source/Claude-Buddy"));
    }

    // The case the quoting rule exists for, and the one a structural assertion
    // cannot fail on: an apostrophe closes the single-quoted string, and getting
    // the reopen wrong leaves an unterminated quote — which `sh -n` catches and
    // `Assert.Contains` does not.
    [UnixFact]
    public void ADirectoryWithAnApostropheParses()
    {
        AssertParses(TerminalScripts.TmuxAttachScript(
            "/usr/bin/tmux", "/tmp/tmux-501/default", "/Users/warren/warren's stuff"));
    }

    // Spaces, double quotes, a dollar sign and a backtick — every character that
    // would change what the shell does if the quoting let it through. None of
    // them can be a syntax error on their own, which is the point: the assertion
    // is that they arrive as *text* rather than as shell syntax.
    [UnixFact]
    public void ADirectoryFullOfShellMetacharactersParses()
    {
        AssertParses(TerminalScripts.TmuxAttachScript(
            "/usr/bin/tmux", "/tmp/tmux-501/default",
            "/Users/warren/a \"quoted\" $HOME `whoami` dir"));
    }

    // A socket path can carry the same hazards as a cwd — it is a filesystem
    // path the hook copied out of $TMUX — so it goes through the same quoting.
    [UnixFact]
    public void AnAwkwardSocketPathParses()
    {
        AssertParses(TerminalScripts.TmuxAttachScript(
            "/usr/bin/tmux", "/tmp/warren's sockets/claude-swarm-1", "/tmp"));
    }

    // No cwd, so no `cd` line at all: the guard against `cd ''` failing and
    // taking the attach down with it via `|| exit 1`.
    [UnixFact]
    public void TheScriptWithNoCdParses()
    {
        AssertParses(TerminalScripts.TmuxAttachScript("/usr/bin/tmux", null, null));
    }

    // --- the split-window command -------------------------------------------

    // The last element of TmuxSplitArgs is a *shell* command, not an argv: tmux
    // hands it to `sh -c`. So it is the same hazard as the script above one level
    // down — a quoting mistake there does not throw anywhere this app can see,
    // it prints a syntax error into a pane that just appeared and leaves the user
    // looking at a shell instead of their conversation.
    //
    // Asserted by parsing that element on its own, which is what `sh -c` will do
    // to it. The structural half — that it is the last element, that -h and
    // -P/-F are there, that the socket is pinned — is TerminalScriptsTests'.
    private static void AssertCommandParses(string[] args)
    {
        AssertParses("#!/bin/sh\n" + args[^1] + "\n");
    }

    [UnixFact]
    public void TheSplitCommandParses()
    {
        AssertCommandParses(TerminalScripts.TmuxSplitArgs(
            null, "warren:3", "/Users/warren/project",
            "'/Users/warren/.local/bin/claude' attach '0e043819'"));
    }

    // A path with every character that would change what the shell does if the
    // quoting let it through — the same set the script test uses, because the
    // command is built by the same ShellQuote and travels through this builder
    // untouched.
    [UnixFact]
    public void ASplitCommandFullOfShellMetacharactersParses()
    {
        AssertCommandParses(TerminalScripts.TmuxSplitArgs(
            null, "warren:3", "/tmp",
            TerminalScripts.ShellQuote("/Users/warren/a \"quoted\" $HOME `whoami`/claude")
                + " attach " + TerminalScripts.ShellQuote("0e043819")));
    }

    // The new-window form takes the identical command through the identical
    // path, and is the fallback that runs when the user's active window could
    // not be resolved — so it gets the same check rather than being assumed to
    // behave because its sibling does.
    [UnixFact]
    public void TheNewWindowCommandParses()
    {
        AssertCommandParses(TerminalScripts.TmuxNewWindowArgs(
            null, "warren", "/Users/warren/project",
            "'/Users/warren/.local/bin/claude' attach '0e043819'"));
    }
}
