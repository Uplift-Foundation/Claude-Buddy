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
        internal static string For(NewChatCli cli, string binaryPath) =>
            TerminalScripts.ShellQuote(binaryPath);

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
        internal static ProcessStartInfo? WindowsProcessStartInfo(
            NewChatCli cli, string? binaryPath, string cwd, bool useWindowsTerminal) =>
            GeneralWindowsStartInfo(binaryPath, cwd, extraArgs: null, useWindowsTerminal);
    }
}
