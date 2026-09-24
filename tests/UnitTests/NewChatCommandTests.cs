using Xunit;

namespace ClaudeBuddy.Tests
{
    // The shell command and Windows launch description for CB-168's "start a
    // new chat" — pure, so quoting edge cases (a space, an apostrophe, a
    // Windows .cmd shim) can be checked without opening a real terminal.
    public class NewChatCommandTests
    {
        // --- For: the macOS/Linux shell command --------------------------

        [Fact]
        public void AnOrdinaryPathIsQuotedButUnchanged()
        {
            Assert.Equal("'/usr/local/bin/claude'", NewChatCommand.For(NewChatCli.ClaudeCode, "/usr/local/bin/claude"));
        }

        // A directory or install path with a space in it has to survive as one
        // shell word — the same failure mode AgentTeamViewer's ClaudeCommand
        // guards against for `claude attach`.
        [Fact]
        public void ASpaceInThePathStaysOneWord()
        {
            var command = NewChatCommand.For(NewChatCli.Codex, "/Applications/My Codex/codex");

            Assert.Equal("'/Applications/My Codex/codex'", command);
        }

        // A single quote in the path has to be closed and reopened the shell
        // way (TerminalScripts.ShellQuote's own rule), not merely escaped —
        // otherwise the command breaks out of its quoting.
        [Fact]
        public void AnApostropheInThePathIsEscapedTheShellWay()
        {
            var command = NewChatCommand.For(NewChatCli.Grok, "/Users/o'brien/.grok/bin/grok");

            Assert.Equal("'/Users/o'\\''brien/.grok/bin/grok'", command);
        }

        // No verb: a new chat names no session to rejoin, unlike
        // AgentTeamViewer.ClaudeCommand("attach", jobId).
        [Fact]
        public void TheCommandCarriesNoVerb()
        {
            var command = NewChatCommand.For(NewChatCli.ClaudeCode, "/usr/local/bin/claude");

            Assert.DoesNotContain("attach", command);
            Assert.DoesNotContain(" ", command.Trim('\''));
        }

        // --- GeneralWindowsStartInfo / WindowsProcessStartInfo ------------

        [Fact]
        public void WindowsTerminalLaunchOpensAndRunsTheBareCliWithNoVerb()
        {
            var start = NewChatCommand.WindowsProcessStartInfo(
                NewChatCli.ClaudeCode, @"C:\Program Files\Claude\claude.exe", @"C:\work\with spaces",
                useWindowsTerminal: true);

            Assert.NotNull(start);
            Assert.Equal("wt.exe", start.FileName);
            Assert.True(start.UseShellExecute);
            Assert.Equal(@"C:\work\with spaces", start.WorkingDirectory);
            Assert.Equal(new[]
            {
                "-d", @"C:\work\with spaces", "cmd.exe", "/k",
                @"C:\Program Files\Claude\claude.exe"
            }, start.ArgumentList);
        }

        [Fact]
        public void CmdFallbackAlsoCarriesNoVerb()
        {
            var start = NewChatCommand.WindowsProcessStartInfo(
                NewChatCli.Codex, @"C:\Users\me\.local\bin\codex.cmd", @"C:\work",
                useWindowsTerminal: false);

            Assert.NotNull(start);
            Assert.Equal("cmd.exe", start.FileName);
            Assert.Equal(new[] { "/k", @"C:\Users\me\.local\bin\codex.cmd" }, start.ArgumentList);
        }

        // Grok, in Windows Terminal mode, with both an npm-style .cmd shim
        // (GrokBinary's own comment: a real install can hand back a .cmd
        // rather than a bare .exe) and a folder with a space in it at once —
        // the combination the plan asked to see checked on paper rather than
        // assumed from the other two CLIs' single-condition cases above.
        [Fact]
        public void WindowsTerminalLaunchWithACmdShimAndASpaceInTheFolder()
        {
            var start = NewChatCommand.WindowsProcessStartInfo(
                NewChatCli.Grok, @"C:\Users\me\.grok\bin\grok.cmd", @"C:\Users\me\My Projects",
                useWindowsTerminal: true);

            Assert.NotNull(start);
            Assert.Equal("wt.exe", start.FileName);
            Assert.Equal(@"C:\Users\me\My Projects", start.WorkingDirectory);
            Assert.Equal(new[]
            {
                "-d", @"C:\Users\me\My Projects", "cmd.exe", "/k",
                @"C:\Users\me\.grok\bin\grok.cmd"
            }, start.ArgumentList);
        }

        // Claude Code's own .cmd shim, in Windows Terminal mode rather than
        // the cmd.exe fallback CmdFallbackAlsoCarriesNoVerb already covers for
        // Codex — the wt arm never actually reads whether the exe carries an
        // extension, but the plan asked for the case named rather than
        // inferred from a different CLI's cmd-fallback test.
        [Fact]
        public void WindowsTerminalLaunchWithClaudeCodesCmdShim()
        {
            var start = NewChatCommand.WindowsProcessStartInfo(
                NewChatCli.ClaudeCode, @"C:\Users\me\.local\bin\claude.cmd", @"C:\work",
                useWindowsTerminal: true);

            Assert.NotNull(start);
            Assert.Equal(new[]
            {
                "-d", @"C:\work", "cmd.exe", "/k", @"C:\Users\me\.local\bin\claude.cmd"
            }, start.ArgumentList);
        }

        // The cmd.exe fallback (no Windows Terminal installed) with a space
        // in the folder — CmdFallbackAlsoCarriesNoVerb already covers a .cmd
        // shim in this mode but with no space in the path; a space is the one
        // condition that mode hadn't been checked against yet. ArgumentList
        // carries the folder as one element regardless — .NET's own argv
        // escaping is what keeps a space from splitting into two arguments,
        // not anything this builder does — but the plan asked for the
        // combination to be named as a case, not left to be true "by
        // construction" with nothing pinning it.
        [Fact]
        public void CmdFallbackWithASpaceInTheFolder()
        {
            var start = NewChatCommand.WindowsProcessStartInfo(
                NewChatCli.Codex, @"C:\Users\me\.local\bin\codex.cmd", @"C:\Users\me\My Projects",
                useWindowsTerminal: false);

            Assert.NotNull(start);
            Assert.Equal(@"C:\Users\me\My Projects", start.WorkingDirectory);
            Assert.Equal(new[] { "/k", @"C:\Users\me\.local\bin\codex.cmd" }, start.ArgumentList);

            // The folder itself only reaches ArgumentList through -d in wt
            // mode; the cmd.exe fallback carries it solely via
            // WorkingDirectory; asserted above, kept here as the case's own
            // point rather than folded into a different-named test.
        }

        [Fact]
        public void ANullBinaryProducesNoStartInfo()
        {
            Assert.Null(NewChatCommand.WindowsProcessStartInfo(
                NewChatCli.Grok, null, @"C:\work", useWindowsTerminal: true));
        }

        [Fact]
        public void AnEmptyBinaryProducesNoStartInfo()
        {
            Assert.Null(NewChatCommand.WindowsProcessStartInfo(
                NewChatCli.Grok, "", @"C:\work", useWindowsTerminal: true));
        }

        // GeneralWindowsStartInfo itself, exercised directly with extra
        // arguments — this is what AgentTeamViewer.WindowsAttachStartInfo now
        // forwards to, and WindowsAttachLaunchTests pins that forwarding still
        // produces the exact original ArgumentList.
        [Fact]
        public void GeneralBuilderAppendsExtraArgumentsAfterTheExe()
        {
            var start = NewChatCommand.GeneralWindowsStartInfo(
                "claude.exe", @"C:\work", new[] { "attach", "b1425d42" }, useWindowsTerminal: false);

            Assert.NotNull(start);
            Assert.Equal(new[] { "/k", "claude.exe", "attach", "b1425d42" }, start.ArgumentList);
        }

        [Fact]
        public void GeneralBuilderWithNoExtraArgumentsAddsNone()
        {
            var start = NewChatCommand.GeneralWindowsStartInfo(
                "claude.exe", @"C:\work", null, useWindowsTerminal: false);

            Assert.Equal(new[] { "/k", "claude.exe" }, start!.ArgumentList);
        }
    }
}
