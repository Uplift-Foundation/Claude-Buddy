using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ClaudeBuddy
{
    // Shared by TerminalFocuser (click an orb, focus its terminal) and
    // ClaudeDesktopManager (click a running profile, focus its window) — both
    // want the same "bring this window to the front" behaviour, so it lives
    // here once rather than twice.
    // Excluded from coverage: there is no window-independent line in here.
    // Every entry point either takes a live HWND or walks the desktop's real
    // top-level windows through EnumWindows, and the window manager's answers
    // are the whole input. A headless runner has no window station to ask.
    [ExcludeFromCodeCoverage]
    internal static class WindowsForegroundWindow
    {
        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPlacement
        {
            public int Length;
            public int Flags;
            public int ShowCmd;
            public Point MinPosition;
            public Point MaxPosition;
            public Rect NormalPosition;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        // Restores the window if minimized, shows it if hidden, then forces it
        // to the foreground. Returns false for a zero handle so callers can
        // treat that as "nothing to focus" without a separate check.
        public static bool BringToFront(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            // Order matters: a window can be both hidden and iconic, and
            // showing an iconic window leaves it iconic.
            if (!IsWindowVisible(hwnd)) ShowWindowAsync(hwnd, SW_SHOW);
            if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);

            Force(hwnd);
            return true;
        }

        // Bring a process's window to the front, finding it ourselves rather
        // than through Process.MainWindowHandle.
        //
        // That property only reports *visible* windows, so it returns zero for
        // an app sitting hidden in the tray — which is exactly the state Claude
        // Desktop goes into when you close its window, and it made "Bring to
        // front" silently do nothing for a profile that was still running.
        //
        // Chromium processes own a fair number of top-level windows that are
        // not the app (message-only windows, drag helpers), so candidates must
        // have a title and must not be tool windows.
        //
        // Among what's left, the biggest window wins. Sorting by visibility
        // first looks reasonable and is wrong: Claude Desktop's quick-ask bar is
        // a separate titled top-level window, and it is usually *visible* while
        // the main window sits hidden in the tray — so "prefer visible" brought
        // the little bar forward and left the app where it was. Size separates
        // them unambiguously, and visibility is only a tie-break for two windows
        // of genuinely similar size, where the one already on screen is the one
        // the user meant.
        //
        // Size comes from GetWindowPlacement's normal position rather than
        // GetWindowRect, because that reports the *restored* rectangle: a
        // minimized window's live rect is off-screen junk (around -32000), and a
        // hidden window still needs to be measured at the size it will return
        // to.
        public static bool ShowAndFocus(int pid)
        {
            var candidates = new List<(IntPtr Handle, long Area, int Visibility)>();

            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var owner);
                if (owner != (uint)pid) return true;

                if (GetWindowTextLength(hwnd) == 0) return true;
                if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;

                candidates.Add((
                    hwnd,
                    RestoredArea(hwnd),
                    IsWindowVisible(hwnd) ? (IsIconic(hwnd) ? 1 : 0) : 2));

                return true;
            }, IntPtr.Zero);

            if (candidates.Count == 0) return false;

            // Largest first, then anything within three quarters of it counts as
            // the same size and is decided by which is already on screen.
            var largest = candidates.Max(c => c.Area);
            var threshold = largest * 3 / 4;

            var pick = candidates
                .Where(c => c.Area >= threshold)
                .OrderBy(c => c.Visibility)
                .ThenByDescending(c => c.Area)
                .First();

            return BringToFront(pick.Handle);
        }

        private static long RestoredArea(IntPtr hwnd)
        {
            var placement = new WindowPlacement();
            placement.Length = Marshal.SizeOf<WindowPlacement>();

            if (!GetWindowPlacement(hwnd, ref placement)) return 0;

            var rect = placement.NormalPosition;
            var width = (long)(rect.Right - rect.Left);
            var height = (long)(rect.Bottom - rect.Top);

            return width > 0 && height > 0 ? width * height : 0;
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
