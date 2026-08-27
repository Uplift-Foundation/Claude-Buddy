using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ClaudeBuddy
{
    // "Stop this session", asked once, by hand, from an orb's context menu.
    //
    // Structured like ProcessLiveness next door — one question, one libc call on
    // Unix, a Windows arm that does the same thing a different way — and for the
    // same reason: the decision about *whether* to ask belongs somewhere pure
    // and testable (SessionPresence.CanEndSession), so all that is left here is
    // the syscall.
    //
    // SIGTERM, not SIGKILL. The pid is a `claude` process, which handles TERM by
    // finishing its own shutdown — flushing the transcript, running SessionEnd,
    // which deletes the status file and takes the orb with it. SIGKILL would
    // leave both behind and land the cleanup on the sweep instead.
    //
    // That reasoning is the Unix arm's alone, and the Windows arm cannot have
    // it: Kill() is TerminateProcess, which is unconditional and runs no
    // shutdown, so there is no SessionEnd and no `rm -f`. On Windows the orb
    // still goes on the next scan — the pid stops answering, which is all
    // ProcessGone needs — and the file waits for the sweep's grace to expire.
    // Nothing is lost either way; the difference is ten minutes of a file
    // nobody is looking at, and .NET offers no portable "ask it to stop".
    //
    // Only ever the session's own pid. Not TermPid, which is the terminal the
    // user is typing in; not the daemon, which hosts every other background job
    // on the machine. One worker hosts exactly one session — each row of
    // `claude agents --json` names a distinct pid — so this ends exactly what
    // the menu item said it would.
    internal static class SessionTerminator
    {
        private const int SIGTERM = 15;

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);

        // Excluded from coverage: this is a real signal to a real process, and
        // there is no version of testing it that does not end something on the
        // machine running the suite. The decision it carries out is
        // SessionPresence.CanEndSession, which is pure and covered per case; the
        // platform split is the same one ProcessLiveness.IsRunningWindows
        // documents — coverage comes from one platform's run, so the two arms
        // can never both be measured.
        //
        // Failures are swallowed, and a pid that had already exited counts as
        // success: the user asked for the session to be over, and it is. There
        // is nowhere to report a failure to in any case — this app has no dialog
        // vocabulary — and the orb going away on the next scan is the only
        // feedback the gesture has ever needed.
        [ExcludeFromCodeCoverage]
        public static void Terminate(int pid)
        {
            // Belt to CanEndSession's braces. Signalling 0 on Unix means "every
            // process in my process group", which is this app and every helper
            // it has spawned — the one mistake here that could not be walked
            // back, so it is refused twice.
            if (pid <= 0) return;

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // The whole tree, because a Claude Code session owns its MCP
                    // servers as children and they do not exit on their own. Safe
                    // in the direction that matters: the terminal is this
                    // process's *parent*, never a descendant, so it cannot be
                    // caught by this.
                    using var process = System.Diagnostics.Process.GetProcessById(pid);
                    process.Kill(entireProcessTree: true);
                    return;
                }

                kill(pid, SIGTERM);
            }
            catch
            {
                // Gone already, or not ours to signal. Either way there is
                // nothing further to do and nothing to say.
            }
        }
    }
}
