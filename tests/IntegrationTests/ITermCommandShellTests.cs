using System.Diagnostics;
using Xunit;

namespace ClaudeBuddy.Tests;

// ITermCommand's output, run past a real shell the way iTerm2 runs it.
//
// The unit tests in TerminalScriptsTests pin the shape of the string and that
// the base64 decodes back to the line in C#. Neither proves that a real
// `zsh -l -i -c` handed that payload lands in the directory and runs the
// command — which is the whole of what the fix is for, and the seam CLAUDE.md
// says gets an integration test as well as a unit one.
//
// iTerm2 itself cannot run on a CI runner, so its half is taken from what was
// measured on 3.7.3: inside single quotes, `$(…)`, `"`, `%` and `|` arrive
// untouched. The command is `'<shell>' -l -i -c '<payload>'` with no other
// quote in it (TerminalScriptsTests pins that), so its argv is the shell, the
// three flags and the payload, and that is what is exec'd here.
//
// HOME and ZDOTDIR point at a scratch directory, so a developer's own .zshrc
// never runs inside a test and a runner's absent one changes nothing.
public class ITermCommandShellTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(string command, string home)
    {
        const string head = "'/bin/zsh' -l -i -c '";
        Assert.StartsWith(head, command);
        Assert.EndsWith("'", command);
        var payload = command[head.Length..^1];

        var psi = new ProcessStartInfo("/bin/zsh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(payload);
        psi.Environment["HOME"] = home;
        psi.Environment["ZDOTDIR"] = home;

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        process!.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(20_000), "zsh did not exit");

        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    // The directory the probes used on a real iTerm2, less the tab: an
    // apostrophe, double quotes, a literal backslash-n and backslash-paren,
    // a dollar and a tilde — each one something some layer would take.
    private const string HostileLeaf = "it's a \"quoted\" dir \\n\\(x) $HOME ~";

    [MacITermShellFact]
    public void TheShellLandsInTheDirectoryAndRunsTheCommand()
    {
        var home = Directory.CreateTempSubdirectory("cb-iterm-home-").FullName;
        var dir = Path.Combine(home, HostileLeaf);
        Directory.CreateDirectory(dir);

        try
        {
            var line = TerminalScripts.ShellCommandLine(dir, "exec '/bin/pwd' '-P'");
            var (exit, stdout, stderr) = Run(TerminalScripts.ITermCommand("/bin/zsh", line), home);

            Assert.True(exit == 0, $"exit {exit}: {stderr}");
            // pwd -P resolves /var to /private/var, so compare against home
            // resolved the same way.
            Assert.Equal(RealPath(home) + "/" + HostileLeaf, stdout.TrimEnd('\n'));
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    // An argument with a backslash and an apostrophe arrives as one word,
    // intact — the quoting ShellQuote did survives the trip through base64.
    [MacITermShellFact]
    public void AnArgumentArrivesIntact()
    {
        var home = Directory.CreateTempSubdirectory("cb-iterm-home-").FullName;

        try
        {
            var arg = "\\n it's \"q\" $HOME";
            var line = "exec '/usr/bin/printf' '%s|' " + TerminalScripts.ShellQuote(arg) + " 'second'";
            var (exit, stdout, stderr) = Run(TerminalScripts.ITermCommand("/bin/zsh", line), home);

            Assert.True(exit == 0, $"exit {exit}: {stderr}");
            Assert.Equal(arg + "|second|", stdout);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    // The cd guard still guards: a directory that has gone never runs the
    // command somewhere else.
    [MacITermShellFact]
    public void AMissingDirectoryRunsNothing()
    {
        var home = Directory.CreateTempSubdirectory("cb-iterm-home-").FullName;

        try
        {
            var line = TerminalScripts.ShellCommandLine(
                Path.Combine(home, "gone"), "exec '/usr/bin/printf' 'ran'");
            var (exit, stdout, _) = Run(TerminalScripts.ITermCommand("/bin/zsh", line), home);

            Assert.Equal(1, exit);
            Assert.DoesNotContain("ran", stdout);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    private static string RealPath(string path)
    {
        var psi = new ProcessStartInfo("/bin/pwd") { UseShellExecute = false, RedirectStandardOutput = true, WorkingDirectory = path };
        psi.ArgumentList.Add("-P");
        using var p = Process.Start(psi)!;
        var result = p.StandardOutput.ReadToEnd().TrimEnd('\n');
        p.WaitForExit();
        return result;
    }
}
