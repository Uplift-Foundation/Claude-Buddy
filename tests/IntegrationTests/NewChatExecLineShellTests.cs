using System.Diagnostics;
using Xunit;

namespace ClaudeBuddy.Tests;

// NewChatCommand.ExecLine, run by real shells (CB-232).
//
// The unit tests in NewChatCommandTests pin the string. That is exactly what
// let CB-232 ship: `exec CLAUDE_CONFIG_DIR='…' 'claude'` was a well-formed,
// correctly quoted string that no shell would run — exec took the assignment
// as the program name, so every named-account launch on macOS died at once
// (127 in zsh, dash and ksh, 126 in sh and bash) while the default account
// worked. Only running the line can catch that, so this does: the line the
// launcher hands a terminal, wrapped the way TerminalScripts.ShellCommandLine
// wraps it for Terminal.app, run with `-c` by every POSIX shell this machine
// has. tmux runs its window command through the user's default shell too,
// which is one of these.
//
// The CLI is a stub script that prints the variable and its own $0, in a
// directory with a space and an apostrophe in its name, so quoting is
// exercised along with the ordering.
public class NewChatExecLineShellTests
{
    private static readonly string[] Shells = { "/bin/sh", "/bin/bash", "/bin/zsh", "/bin/dash", "/bin/ksh" };

    private static IEnumerable<string> PresentShells() => Shells.Where(File.Exists);

    private static string StubCli(string dir)
    {
        var path = Path.Combine(dir, "fake claude");
        File.WriteAllText(path,
            "#!/bin/sh\nprintf '%s|%s\\n' \"${CLAUDE_CONFIG_DIR-<unset>}\" \"$0\"\n");
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(string shell, string line, string home)
    {
        var psi = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(line);
        // The test process may itself be running under a CLAUDE_CONFIG_DIR
        // (this repo's own agent shells routinely are); the default-account
        // case has to start from none.
        psi.Environment.Remove("CLAUDE_CONFIG_DIR");
        psi.Environment["HOME"] = home;
        psi.Environment["ZDOTDIR"] = home;

        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(20_000), shell + " did not exit");
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    private static void InScratch(Action<string, string, string> body)
    {
        var root = Directory.CreateTempSubdirectory("cb232-").FullName;
        try
        {
            var binDir = Path.Combine(root, "it's a bin");
            Directory.CreateDirectory(binDir);
            var cwd = Path.Combine(root, "work dir");
            Directory.CreateDirectory(cwd);
            body(root, StubCli(binDir), cwd);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [UnixFact]
    public void ANamedAccountStartsTheCliWithItsConfigDirInEveryShell()
    {
        InScratch((root, stub, cwd) =>
        {
            var configDir = Path.Combine(root, "o'brien $HOME .claude-work");
            var line = TerminalScripts.ShellCommandLine(
                cwd, NewChatCommand.ExecLine(NewChatCli.ClaudeCode, stub, configDir));

            Assert.NotEmpty(PresentShells());
            foreach (var shell in PresentShells())
            {
                var (exit, stdout, stderr) = Run(shell, line, root);

                Assert.True(exit == 0, $"{shell}: exit {exit}: {stderr}");
                Assert.Equal(configDir + "|" + stub, stdout.TrimEnd('\n'));
            }
        });
    }

    [UnixFact]
    public void TheDefaultAccountStartsTheCliWithNoConfigDirInEveryShell()
    {
        InScratch((root, stub, cwd) =>
        {
            var line = TerminalScripts.ShellCommandLine(
                cwd, NewChatCommand.ExecLine(NewChatCli.ClaudeCode, stub, configDir: null));

            foreach (var shell in PresentShells())
            {
                var (exit, stdout, stderr) = Run(shell, line, root);

                Assert.True(exit == 0, $"{shell}: exit {exit}: {stderr}");
                Assert.Equal("<unset>|" + stub, stdout.TrimEnd('\n'));
            }
        });
    }

    // The negative control: the pre-CB-232 line, built the way RealLaunch
    // used to build it, fails in every shell. Without this, the two tests
    // above passing could mean the harness never ran the line at all.
    [UnixFact]
    public void TheOldExecThenAssignmentLineFailsInEveryShell()
    {
        InScratch((root, stub, cwd) =>
        {
            var old = "exec CLAUDE_CONFIG_DIR=" + TerminalScripts.ShellQuote(Path.Combine(root, ".claude-work"))
                + " " + TerminalScripts.ShellQuote(stub);
            var line = TerminalScripts.ShellCommandLine(cwd, old);

            foreach (var shell in PresentShells())
            {
                var (exit, stdout, _) = Run(shell, line, root);

                Assert.NotEqual(0, exit);
                Assert.DoesNotContain(stub, stdout);
            }
        });
    }
}
