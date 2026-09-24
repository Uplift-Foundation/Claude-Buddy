using System.Diagnostics.CodeAnalysis;

namespace ClaudeBuddy
{
    // Starts one of the three local CLIs in a terminal, on a chosen folder —
    // CB-168's "start a new chat" action. No new orb plumbing is needed for
    // these three: the CLI's own installed hook writes the status file the
    // moment it starts, and the existing scan draws the orb, the same way it
    // does for a CLI a user started by hand.
    internal static class NewChatLauncher
    {
        // The dialog's own seam. A UI test drives Start without spawning a
        // real terminal, the same FakeChatSession-shaped seam CLAUDE.md asks
        // for elsewhere in this app — see NewChatAvailability.CurrentForTests
        // for the matching seam on the other half of the dialog.
        internal static Func<NewChatCli, string, LaunchResult>? LaunchForTests;

        internal static LaunchResult Launch(NewChatCli cli, string cwd) =>
            LaunchForTests?.Invoke(cli, cwd) ?? RealLaunch(cli, cwd);

        // The real launch: locate the binary fresh (never the cached
        // ClaudeBinary/CodexBinary/GrokBinary.Path — see NewChatAvailability
        // for why a fresh Locate() matters here), build its command, and open
        // a terminal on it through TerminalLauncher.
        //
        // Excluded from coverage as a whole, the same way
        // AgentTeamViewer.AttachSessionOnWindows is: every branch below either
        // starts a real subprocess or calls into TerminalLauncher, which is
        // itself excluded for running `ps`/tmux/osascript/wt.exe. What can be
        // tested independently already is — NewChatCommand's quoting,
        // NewChatAvailability's decision, DisplayName below — leaving nothing
        // pure left uncovered inside this method.
        [ExcludeFromCodeCoverage]
        private static LaunchResult RealLaunch(NewChatCli cli, string cwd)
        {
            var name = DisplayName(cli);
            var binary = LocateFresh(cli);

            if (binary is null)
            {
                return new LaunchResult(
                    LaunchOutcome.NotFound,
                    name + " " + NewChatAvailability.NotFoundReasonSuffix + ".");
            }

            var directory = Directory.Exists(cwd) ? cwd : Environment.CurrentDirectory;

            if (OperatingSystem.IsWindows())
            {
                var started = TerminalLauncher.StartWindowsProcess(useWindowsTerminal =>
                    NewChatCommand.WindowsProcessStartInfo(cli, binary, directory, useWindowsTerminal));

                return started
                    ? new LaunchResult(LaunchOutcome.Launched, name + " started in " + directory + ".")
                    : new LaunchResult(LaunchOutcome.SpawnFailed, "Couldn't open a terminal for " + name + ".");
            }

            if (!OperatingSystem.IsMacOS())
            {
                return new LaunchResult(
                    LaunchOutcome.SpawnFailed, "Starting a new chat isn't supported on this platform yet.");
            }

            // "exec " so the terminal's own shell becomes the CLI rather than
            // waiting behind it — the same reason every AgentTeamViewer
            // launch site prefixes its command the same way.
            var command = "exec " + NewChatCommand.For(cli, binary);

            // Beside the user's tmux first, for the same reason
            // AgentTeamViewer.AttachSession prefers PlaceInTmux over a bare
            // window: someone who lives in tmux gets a pane inside the thing
            // they use to move between windows, rather than a window outside
            // it.
            if (TerminalLauncher.PlaceInTmux(command, directory) is { Length: > 0 })
            {
                return new LaunchResult(LaunchOutcome.Launched, name + " started in " + directory + ".");
            }

            var launched = TerminalLauncher.LaunchInTerminal(TerminalLauncher.TerminalApp(), directory, command);

            return launched
                ? new LaunchResult(LaunchOutcome.Launched, name + " started in " + directory + ".")
                : new LaunchResult(LaunchOutcome.SpawnFailed, "Couldn't open a terminal for " + name + ".");
        }

        [ExcludeFromCodeCoverage]
        private static string? LocateFresh(NewChatCli cli) => cli switch
        {
            NewChatCli.ClaudeCode => ClaudeBinary.Locate(),
            NewChatCli.Codex => CodexBinary.Locate(),
            NewChatCli.Grok => GrokBinary.Locate(),
            _ => null
        };

        // Pure and used by both this file and NewChatAvailability's reason
        // text, so the two never drift into naming a CLI differently.
        internal static string DisplayName(NewChatCli cli) => cli switch
        {
            NewChatCli.ClaudeCode => "Claude Code",
            NewChatCli.Codex => "Codex",
            NewChatCli.Grok => "Grok",
            _ => cli.ToString()
        };
    }
}
