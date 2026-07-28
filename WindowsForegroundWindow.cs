using System.Runtime.InteropServices;

namespace ClaudeBuddy
{
    // Shared by TerminalFocuser (click an orb, focus its terminal) and
    // ClaudeDesktopManager (click a running profile, focus its window) — both
    // want the same "bring this window to the front" behaviour, so it lives
    // here once rather than twice.
    internal static class WindowsForegroundWindow
    {
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        // Restores the window if minimized, then forces it to the
        // foreground. Returns false for a zero handle so callers can treat
        // that as "nothing to focus" without a separate check.
        public static bool BringToFront(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
            Force(hwnd);
            return true;
        }

        // Windows denies SetForegroundWindow from anything that isn't already
        // foreground — found by clicking a synthetic orb and watching a
        // minimized Windows Terminal window simply stay minimized with no
        // error. Attaching this thread's input queue to the current
        // foreground thread for the duration of the call is the standard
        // workaround; harmless if it's already us or the attach fails.
        private static void Force(IntPtr hwnd)
        {
            var foreground = GetForegroundWindow();
            var foregroundThread = GetWindowThreadProcessId(foreground, out _);
            var currentThread = GetCurrentThreadId();

            var attached = foregroundThread != currentThread
                && AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }
}
