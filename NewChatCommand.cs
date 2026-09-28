using System.Diagnostics;

namespace ClaudeBuddy
{
    // Which of the three local CLIs CB-168's "start a new chat" dialog can
    // launch. OpenClaw is a fourth entry in the dialog itself, but it never
    // reaches this file: it has no binary to shell out to and no terminal to
    // open — see NewChatAvailability and the OpenClaw arm of the plan.
    internal enum NewChatCli
    {
        ClaudeCode,
        Codex,
        Grok
    }

    // What happened when NewChatLauncher.Launch tried to start one.
    //
    // A launch is reported, never silent (CB-168's acceptance criterion): a
    // click that opens nothing and says nothing is indistinguishable from the
    // app being broken, which is the exact complaint AgentTeamViewer's whole
    // click-ladder history is about.
    internal enum LaunchOutcome
    {
        Launched,
        NotFound,
        SpawnFailed
    }

    // Outcome plus the message the dialog shows inline. Message is always
    // present — even Launched carries one, which is what lets the dialog
    // print an inline confirmation instead of asking the caller to invent
    // wording for the success case.
    internal sealed record LaunchResult(LaunchOutcome Outcome, string Message);

    // The command line and the Windows launch description for one CLI — the
    // parts of "start a new chat" that are pure and decide nothing about the
    // OS. Built on TerminalScripts.ShellQuote/ShellCommandLine, the same
    // quoting rule AgentTeamViewer has carried since `claude attach` was first
    // wired up, so a directory or CLI install path with a space or an
    // apostrophe in it is handled once rather than reinvented here.
    internal static class NewChatCommand
    {
        // The shell command that starts a fresh conversation: the bare binary,
        // quoted, with no verb — `claude attach <id>` names a session to
        // rejoin, and starting a new chat names nothing, it just runs the CLI.
        //
        // No leading "exec" here: TerminalScripts.ShellCommandLine and
        // TerminalLauncher's callers add "exec " themselves where a shell
        // script needs the terminal's own shell to become the CLI rather than
        // wait behind it — see AgentTeamViewer.AttachSession for the pattern
        // this follows.
        //
        // configDir null (the default account, or any CLI but Claude Code —
        // see NewChatLauncher.ConfigDirFor) leaves the command exactly as it
        // was: no assignment prefix at all, never one that names the default
        // directory (CB-42's whole point). Set, it becomes
        // `CLAUDE_CONFIG_DIR='<dir>' '<binary>'`, a plain POSIX variable
        // assignment ahead of the command it applies to — the shell scopes it
        // to that one invocation without this needing `export` or a subshell.
        internal static string For(NewChatCli cli, string binaryPath, string? configDir = null) =>
            configDir is null
                ? TerminalScripts.ShellQuote(binaryPath)
                : "CLAUDE_CONFIG_DIR=" + TerminalScripts.ShellQuote(configDir)
                    + " " + TerminalScripts.ShellQuote(binaryPath);

        // The general Windows launch shape: wt.exe when it's installed
        // (`-d <cwd> cmd.exe /k <exe> [args...]`, which gives the CLI an
        // ordinary tab), cmd.exe when it isn't (`/k <exe> [args...]`, still a
        // visible window). Both pass the exe and its arguments separately,
        // never through a shell string, so a directory or CLI path containing
        // spaces remains one argument — the same guarantee
        // AgentTeamViewer.WindowsAttachStartInfo already gave for `claude
        // attach`.
        //
        // General rather than copied: AgentTeamViewer.WindowsAttachStartInfo
        // now calls this with extraArgs = ["attach", jobId], and
        // WindowsProcessStartInfo below calls it with extraArgs = null (a new
        // chat has no verb) — one argument-layout builder instead of two.
        //
        // Pure: it only builds a ProcessStartInfo and starts nothing, so a
        // test can assert on FileName/ArgumentList/WorkingDirectory directly,
        // the way WindowsAttachLaunchTests already does for the attach case.
        internal static ProcessStartInfo? GeneralWindowsStartInfo(
            string? exe, string cwd, IReadOnlyList<string>? extraArgs, bool useWindowsTerminal)
        {
            if (string.IsNullOrEmpty(exe)) return null;

            var start = new ProcessStartInfo(useWindowsTerminal ? "wt.exe" : "cmd.exe")
            {
                UseShellExecute = true,
                WorkingDirectory = cwd
            };

            if (useWindowsTerminal)
            {
                start.ArgumentList.Add("-d");
                start.ArgumentList.Add(cwd);
                start.ArgumentList.Add("cmd.exe");
            }

            start.ArgumentList.Add("/k");
            start.ArgumentList.Add(exe);

            if (extraArgs is not null)
            {
                foreach (var arg in extraArgs) start.ArgumentList.Add(arg);
            }

            return start;
        }

        // The Windows launch for a new chat specifically: the general builder
        // with no extra arguments, since there is no verb and no session id —
        // just the CLI, in the chosen folder.
        //
        // configDir null reproduces the general builder's output byte for
        // byte. Set, the start info additionally switches to
        // UseShellExecute = false and carries CLAUDE_CONFIG_DIR on
        // Environment — true, wt.exe/cmd.exe's shared default, launches
        // through ShellExecuteEx and ignores ProcessStartInfo.Environment
        // entirely, so the variable would silently never leave this process.
        //
        // **Measured on the `windows` box (192.168.1.24), 26 Sep 2026,
        // confirmed rather than assumed:** a scheduled task run in the
        // logged-on interactive session (console, session 2 — an SSH session
        // lands in session 0 and can start no visible window, so this could
        // not be read directly off an SSH-launched process) set
        // CLAUDE_CONFIG_DIR on its own environment and launched `wt.exe -d
        // ... cmd.exe /c "echo %CLAUDE_CONFIG_DIR%> file"`. The file showed
        // the real value, twice, with two different values across two runs —
        // and a WindowsTerminal.exe/OpenConsole.exe pair was already running
        // (since 23 Sep, well before this test), so the result covers the
        // case that actually worried this ticket: wt.exe's single-instance
        // model, where a new invocation hands its request to an
        // already-running "monarch" process over IPC rather than becoming the
        // tab's parent itself. CLAUDE_CONFIG_DIR was not set as a User or
        // Machine environment variable, which rules out the file simply
        // showing an ambient value that had nothing to do with this
        // mechanism. wt.exe does carry the launching process's environment
        // through to a new tab, monarch delegation included, so a profiled
        // launch keeps exactly the same wt-first-then-cmd.exe preference an
        // unprofiled one already has — no separate routing decision was
        // needed here after all.
        internal static ProcessStartInfo? WindowsProcessStartInfo(
            NewChatCli cli, string? binaryPath, string cwd, bool useWindowsTerminal, string? configDir = null)
        {
            var start = GeneralWindowsStartInfo(binaryPath, cwd, extraArgs: null, useWindowsTerminal);

            if (start is not null && configDir is not null)
            {
                start.UseShellExecute = false;
                start.Environment["CLAUDE_CONFIG_DIR"] = configDir;
            }

            return start;
        }
    }
}
