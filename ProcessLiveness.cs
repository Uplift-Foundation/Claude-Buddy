using System.Runtime.InteropServices;

namespace ClaudeBuddy
{
    // "Is this pid still running?", asked a few times every couple of seconds.
    //
    // On Unix that's kill(pid, 0), which touches no memory and allocates
    // nothing — Process.GetProcessById would throw for the answer we want and
    // cost an exception each time an orb is stale. EPERM counts as alive: the
    // process exists, it just isn't ours to signal.
    //
    // A recycled pid reads as alive, which keeps an orb up longer than it should
    // rather than removing a live session's orb. That's the right way round for
    // this to be wrong, and the lifetime timer still catches it unless the
    // lifetime is Forever.
    internal static class ProcessLiveness
    {
        private const int ESRCH = 3;   // no such process

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);

        public static bool IsRunning(int pid)
        {
            if (pid <= 0) return true;   // nothing recorded — caller falls back to the timer

            if (OperatingSystem.IsWindows()) return IsRunningWindows(pid);

            if (kill(pid, 0) == 0) return true;

            var error = Marshal.GetLastWin32Error();
            return error != ESRCH;   // ESRCH is the only answer that means gone
        }

        private static bool IsRunningWindows(int pid)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;   // not running
            }
            catch
            {
                // Anything else (access denied on a process we can see but not
                // open) means it exists.
                return true;
            }
        }
    }
}
