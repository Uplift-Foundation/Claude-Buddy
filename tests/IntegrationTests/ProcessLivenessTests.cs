using System.Diagnostics;
using Xunit;

namespace ClaudeBuddy.Tests
{
    // "Is this pid still running?", which SessionManager asks a few times every
    // couple of seconds to decide whether an orb's session is still there.
    //
    // In IntegrationTests rather than UnitTests because the only honest way to
    // ask about a pid that has genuinely gone is to start a real process and let
    // it exit. Everything else about it — the guards, the wrong-way-round
    // choice — is decided without one.
    //
    // The rule worth pinning is which way it is wrong when it is wrong. A
    // recycled pid reads as alive, which keeps an orb up longer than it should
    // rather than removing a live session's orb, and the file's comment says
    // that is the right way round for this to fail. Both branches below exist to
    // keep that from being quietly reversed.
    public class ProcessLivenessTests
    {
        // Nothing recorded. The caller falls back to the lifetime timer, so the
        // answer has to be "alive" — answering false here would delete the orb
        // of every session whose status file carries no pid, which includes every
        // background agent and every subagent.
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void APidThatWasNeverRecordedCountsAsAlive(int pid)
        {
            Assert.True(ProcessLiveness.IsRunning(pid));
        }

        // This process, which is definitely running. On Unix this is kill(pid, 0)
        // returning 0; on Windows it is Process.GetProcessById followed by
        // HasExited.
        [Fact]
        public void ThisProcessIsRunning()
        {
            Assert.True(ProcessLiveness.IsRunning(Environment.ProcessId));
        }

        // A process that has exited and been reaped. On Unix kill() then answers
        // ESRCH, which is the one errno that means gone — anything else,
        // including EPERM for a process that exists but is not ours to signal,
        // counts as alive.
        [Fact]
        public void AProcessThatHasExitedIsNotRunning()
        {
            var pid = StartAndReapAShortLivedProcess();

            Assert.False(ProcessLiveness.IsRunning(pid));
        }

        // pid 1 is launchd on macOS and init on Linux: it exists and belongs to
        // root, so signalling it fails with EPERM rather than ESRCH. That is the
        // branch that distinguishes "cannot ask" from "not there", and getting it
        // backwards would silently delete orbs for any session this process
        // cannot signal.
        [UnixFact]
        public void AProcessWeCannotSignalStillCountsAsAlive()
        {
            Assert.True(ProcessLiveness.IsRunning(1));
        }

        // The Windows path is a different implementation of the same question,
        // and its own not-running branch is an ArgumentException from
        // GetProcessById rather than an errno.
        [WindowsFact]
        public void OnWindowsAnExitedProcessIsNotRunning()
        {
            var pid = StartAndReapAShortLivedProcess();

            Assert.False(ProcessLiveness.IsRunning(pid));
        }

        // A pid far above any plausible live one. Not a substitute for the
        // exited-process case above — a pid that was never used and a pid that
        // has been reaped reach the same code by different routes, and this one
        // costs nothing to also check.
        [Fact]
        public void APidThatWasNeverAllocatedIsNotRunning()
        {
            Assert.False(ProcessLiveness.IsRunning(int.MaxValue - 1));
        }

        // Waits for exit *and* disposes, which is what gets the child reaped
        // rather than left as a zombie. A zombie still answers to kill(pid, 0),
        // so without this the exited-process case would pass or fail depending on
        // when the runtime happened to collect the Process object.
        private static int StartAndReapAShortLivedProcess()
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/c exit")
                : new ProcessStartInfo("/bin/sh", "-c \"exit 0\"");

            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            int pid;
            using (var process = Process.Start(psi))
            {
                Assert.NotNull(process);
                pid = process!.Id;
                process.WaitForExit(10_000);
            }

            return pid;
        }
    }
}
