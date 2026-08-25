using System.Text;
using Xunit;

namespace ClaudeBuddy.Tests
{
    // The three parsers that read a macOS process's own command line and
    // environment out of a KERN_PROCARGS2 buffer.
    //
    // These are pure byte-array walks and always were, which is why they are
    // `internal static` and take a buffer rather than a pid — the sysctl that
    // fills the buffer lives next door in `ReadUserDataDir`/`ArgumentValues`,
    // and is the only part a headless runner cannot reach. That split predates
    // this file; nothing about it was changed to make these testable. They were
    // simply never tested.
    //
    // They are worth testing on their own merits rather than for a coverage
    // number. `ParseArgumentValues` is how AgentTeam links a team member to its
    // lead: it reads `--parent-session-id` off the live process, deliberately
    // rather than out of the transcript, so that a team which has gone quiet
    // still draws its arrows. If it mis-walks the buffer the app draws the wrong
    // team shape, and CLAUDE.md's standing instruction is to read a wrong team
    // shape on the board as a bug report about the app. `ParseUserDataDir` finds
    // which profile a Claude Desktop instance is running, and getting that wrong
    // means the overlay attaches to the wrong window.
    //
    // The layout being parsed, from the file's own comment:
    //
    //     [int32 argc][exec path\0][\0 padding][argv 0..argc-1, each \0][env, each \0]
    //
    // `Buffer` below builds exactly that, including the alignment padding after
    // the exec path, which is the part a hand-written fixture is most likely to
    // get wrong — and the part the parsers explicitly skip.
    public class ProcArgsParsingTests
    {
        // A real KERN_PROCARGS2 answer, assembled the way the kernel assembles
        // one. `padding` is variable in life (the kernel aligns the first
        // argument), so it is a parameter here: a parser that assumed a fixed
        // offset would pass with one value and fail with another.
        private static byte[] Buffer(
            int argc, string execPath, IEnumerable<string> argv, IEnumerable<string> env,
            int padding = 3, bool terminate = true)
        {
            var bytes = new List<byte>();
            bytes.AddRange(BitConverter.GetBytes(argc));
            bytes.AddRange(Encoding.UTF8.GetBytes(execPath));
            bytes.Add(0);
            for (var i = 0; i < padding; i++) bytes.Add(0);

            foreach (var arg in argv)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(arg));
                bytes.Add(0);
            }

            foreach (var entry in env)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(entry));
                bytes.Add(0);
            }

            // The empty string that ends the block. Left off by one test on
            // purpose, to prove the walk stops at `length` rather than running
            // off the end of a buffer the kernel filled only part of.
            if (terminate) bytes.Add(0);

            return bytes.ToArray();
        }

        private static byte[] Desktop(string? userDataDir, int padding = 3)
        {
            var env = new List<string> { "PATH=/usr/bin", "HOME=/Users/x" };
            if (userDataDir is not null) env.Add("CLAUDE_USER_DATA_DIR=" + userDataDir);
            env.Add("LANG=en_US.UTF-8");

            return Buffer(
                argc: 2,
                execPath: "/Applications/Claude.app/Contents/MacOS/Claude",
                argv: new[] { "/Applications/Claude.app/Contents/MacOS/Claude", "--no-sandbox" },
                env: env,
                padding: padding);
        }

        // --- ParseUserDataDir: the environment block, one named variable ---

        [Fact]
        public void UserDataDirIsReadFromTheEnvironmentBlock()
        {
            var buffer = Desktop("/Users/x/Library/Application Support/Claude-work");

            Assert.Equal(
                "/Users/x/Library/Application Support/Claude-work",
                MacOSProcessScan.ParseUserDataDir(buffer, buffer.Length));
        }

        // The alignment padding after the exec path is not a fixed width, and a
        // parser that guessed it would read the first argument as part of the
        // path. Every padding a kernel might hand back must give the same
        // answer.
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(4)]
        [InlineData(7)]
        public void PaddingAfterTheExecPathDoesNotMoveTheAnswer(int padding)
        {
            var buffer = Desktop("/tmp/profile", padding);

            Assert.Equal("/tmp/profile", MacOSProcessScan.ParseUserDataDir(buffer, buffer.Length));
        }

        [Fact]
        public void NoUserDataDirVariableIsNull()
        {
            var buffer = Desktop(null);

            Assert.Null(MacOSProcessScan.ParseUserDataDir(buffer, buffer.Length));
        }

        // Set but empty is null rather than "". A caller treating "" as a real
        // path would look for a profile directory at the filesystem root.
        [Fact]
        public void AnEmptyUserDataDirIsNull()
        {
            var buffer = Desktop("");

            Assert.Null(MacOSProcessScan.ParseUserDataDir(buffer, buffer.Length));
        }

        // A variable whose name merely *starts* the same way is not the one
        // asked for. The parser matches on the key including its '=', which is
        // what makes this hold.
        [Fact]
        public void ASimilarlyNamedVariableIsNotMistakenForIt()
        {
            var buffer = Buffer(
                argc: 1,
                execPath: "/Applications/Claude.app/Contents/MacOS/Claude",
                argv: new[] { "Claude" },
                env: new[] { "CLAUDE_USER_DATA_DIR_BACKUP=/tmp/wrong" });

            Assert.Null(MacOSProcessScan.ParseUserDataDir(buffer, buffer.Length));
        }

        // --- the length argument is a real bound, not a formality ---

        [Fact]
        public void ABufferShorterThanArgcIsRefused()
        {
            Assert.Null(MacOSProcessScan.ParseUserDataDir(new byte[] { 1, 2 }, 2));
            Assert.Null(MacOSProcessScan.ParseUserDataDir(Array.Empty<byte>(), 0));
        }

        // sysctl reports how much of the buffer it filled, and the buffer is
        // KERN_ARGMAX — typically a megabyte — so almost all of it is stale
        // zeroes. Reading past `length` is the bug this guards.
        [Fact]
        public void NothingPastTheReportedLengthIsRead()
        {
            var real = Desktop("/tmp/visible");
            var oversized = new byte[real.Length + 4096];
            Array.Copy(real, oversized, real.Length);

            // The same variable again, further along, where a walk that ignored
            // `length` would find it after the first one had been cut off.
            var stale = Encoding.UTF8.GetBytes("CLAUDE_USER_DATA_DIR=/tmp/stale\0");
            Array.Copy(stale, 0, oversized, real.Length + 8, stale.Length);

            Assert.Equal(
                "/tmp/visible", MacOSProcessScan.ParseUserDataDir(oversized, real.Length));

            // And with the length cut back before the real answer, the stale
            // copy must not be found either.
            Assert.Null(MacOSProcessScan.ParseUserDataDir(oversized, sizeof(int) + 8));
        }

        // A negative argc is nonsense the kernel will not produce, which is
        // exactly why it is worth pinning: the loop that skips argv would run
        // unbounded.
        [Fact]
        public void ANegativeArgcIsRefused()
        {
            var buffer = Desktop("/tmp/x");
            Array.Copy(BitConverter.GetBytes(-1), buffer, sizeof(int));

            Assert.Null(MacOSProcessScan.ParseUserDataDir(buffer, buffer.Length));
        }

        // --- ParseArgumentValues: argv, and only argv ---

        [Fact]
        public void FlagValuesAreReadFromTheCommandLine()
        {
            var into = new Dictionary<string, string>();
            var buffer = Buffer(
                argc: 7,
                execPath: "/opt/homebrew/bin/claude",
                argv: new[]
                {
                    "claude",
                    "--agent-name", "architect",
                    "--team-name", "coverage",
                    "--parent-session-id", "95eddb0e",
                },
                env: new[] { "PATH=/usr/bin" });

            MacOSProcessScan.ParseArgumentValues(
                buffer, buffer.Length,
                new[] { "--agent-name", "--team-name", "--parent-session-id" }, into);

            Assert.Equal("architect", into["--agent-name"]);
            Assert.Equal("coverage", into["--team-name"]);
            Assert.Equal("95eddb0e", into["--parent-session-id"]);
        }

        [Fact]
        public void UnaskedForFlagsAreNotCollected()
        {
            var into = new Dictionary<string, string>();
            var buffer = Buffer(
                argc: 5,
                execPath: "/opt/homebrew/bin/claude",
                argv: new[] { "claude", "--agent-name", "qa", "--model", "opus" },
                env: Array.Empty<string>());

            MacOSProcessScan.ParseArgumentValues(
                buffer, buffer.Length, new[] { "--agent-name" }, into);

            Assert.Equal("qa", into["--agent-name"]);
            Assert.DoesNotContain("--model", into.Keys);
            Assert.Single(into);
        }

        // The whole reason the argv walk stops at argc: the environment block
        // that follows is user-controlled, and a variable whose value begins
        // with a flag name would otherwise be read as a command-line argument.
        // The file's own comment calls this out; this is the test of it.
        [Fact]
        public void AFlagNameAppearingInTheEnvironmentIsNotReadAsAnArgument()
        {
            var into = new Dictionary<string, string>();
            var buffer = Buffer(
                argc: 1,
                execPath: "/opt/homebrew/bin/claude",
                argv: new[] { "claude" },
                env: new[] { "--parent-session-id", "attacker-supplied" });

            MacOSProcessScan.ParseArgumentValues(
                buffer, buffer.Length, new[] { "--parent-session-id" }, into);

            Assert.Empty(into);
        }

        // A flag in last position has nothing following it. The value must not
        // be taken from the environment block that starts immediately after.
        [Fact]
        public void ATrailingFlagWithNoValueCollectsNothing()
        {
            var into = new Dictionary<string, string>();
            var buffer = Buffer(
                argc: 2,
                execPath: "/opt/homebrew/bin/claude",
                argv: new[] { "claude", "--agent-name" },
                env: new[] { "HOME=/Users/x" });

            MacOSProcessScan.ParseArgumentValues(
                buffer, buffer.Length, new[] { "--agent-name" }, into);

            Assert.Empty(into);
        }

        [Fact]
        public void ArgumentValuesRefusesAnEmptyOrHeadlessBuffer()
        {
            var into = new Dictionary<string, string>();

            MacOSProcessScan.ParseArgumentValues(new byte[] { 9 }, 1, new[] { "--x" }, into);
            Assert.Empty(into);

            // argc of zero means no command line to walk at all, which the
            // argument walk refuses outright — unlike the environment walk,
            // which still has a block to read.
            var noArgs = Buffer(0, "/bin/true", Array.Empty<string>(), new[] { "A=1" });
            MacOSProcessScan.ParseArgumentValues(noArgs, noArgs.Length, new[] { "--x" }, into);
            Assert.Empty(into);
        }

        // --- ParseEnvironmentValues: the same buffer, the other block ---

        [Fact]
        public void NamedEnvironmentVariablesAreCollected()
        {
            var into = new Dictionary<string, string>();
            var buffer = Buffer(
                argc: 2,
                execPath: "/opt/homebrew/bin/claude",
                argv: new[] { "claude", "--resume" },
                env: new[] { "TMUX_PANE=%30", "TERM=xterm-256color", "SHELL=/bin/zsh" });

            MacOSProcessScan.ParseEnvironmentValues(
                buffer, buffer.Length, new[] { "TMUX_PANE", "SHELL" }, into);

            Assert.Equal("%30", into["TMUX_PANE"]);
            Assert.Equal("/bin/zsh", into["SHELL"]);
            Assert.DoesNotContain("TERM", into.Keys);
        }

        // A value containing '=' keeps all of it. Splitting on the last '='
        // instead of the first would mangle exactly the things most likely to
        // contain one — connection strings, flags, base64.
        [Fact]
        public void OnlyTheFirstEqualsSeparatesNameFromValue()
        {
            var into = new Dictionary<string, string>();
            var buffer = Buffer(
                argc: 1, execPath: "/bin/sh", argv: new[] { "sh" },
                env: new[] { "OPTS=--flag=1 --other=2" });

            MacOSProcessScan.ParseEnvironmentValues(
                buffer, buffer.Length, new[] { "OPTS" }, into);

            Assert.Equal("--flag=1 --other=2", into["OPTS"]);
        }

        [Fact]
        public void AnEmptyValueIsKeptAsEmptyRatherThanSkipped()
        {
            var into = new Dictionary<string, string>();
            var buffer = Buffer(
                argc: 1, execPath: "/bin/sh", argv: new[] { "sh" },
                env: new[] { "TMUX_PANE=" });

            MacOSProcessScan.ParseEnvironmentValues(
                buffer, buffer.Length, new[] { "TMUX_PANE" }, into);

            Assert.Equal("", into["TMUX_PANE"]);
        }

        // An entry with no '=' at all, and one that starts with '=' so its name
        // would be empty. Neither is a variable; both are skipped rather than
        // stored under a blank key.
        [Fact]
        public void MalformedEnvironmentEntriesAreSkipped()
        {
            var into = new Dictionary<string, string>();
            var buffer = Buffer(
                argc: 1, execPath: "/bin/sh", argv: new[] { "sh" },
                env: new[] { "NOTAVARIABLE", "=novalue", "GOOD=yes" });

            MacOSProcessScan.ParseEnvironmentValues(
                buffer, buffer.Length, new[] { "NOTAVARIABLE", "", "GOOD" }, into);

            Assert.Equal("yes", into["GOOD"]);
            Assert.Single(into);
        }

        [Fact]
        public void EnvironmentValuesRefusesATruncatedBuffer()
        {
            var into = new Dictionary<string, string>();

            MacOSProcessScan.ParseEnvironmentValues(new byte[] { 1, 2, 3 }, 3, new[] { "A" }, into);
            Assert.Empty(into);

            var negative = Buffer(1, "/bin/sh", new[] { "sh" }, new[] { "A=1" });
            Array.Copy(BitConverter.GetBytes(-5), negative, sizeof(int));
            MacOSProcessScan.ParseEnvironmentValues(negative, negative.Length, new[] { "A" }, into);
            Assert.Empty(into);
        }

        // A buffer the kernel filled only partly, with no terminating empty
        // string. The walk has to stop at `length`; there is nothing else
        // telling it where the block ends.
        [Fact]
        public void AnUnterminatedBlockStillStopsAtTheEnd()
        {
            var into = new Dictionary<string, string>();
            var buffer = Buffer(
                argc: 1, execPath: "/bin/sh", argv: new[] { "sh" },
                env: new[] { "TMUX_PANE=%7" }, terminate: false);

            MacOSProcessScan.ParseEnvironmentValues(
                buffer, buffer.Length, new[] { "TMUX_PANE" }, into);

            Assert.Equal("%7", into["TMUX_PANE"]);
        }
    }
}
