using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClaudeBuddy
{
    // Asking a tray-resident Electron app to quit on Windows.
    //
    // WM_CLOSE is not enough. Claude Desktop, like most Electron chat apps,
    // treats closing its window as hide-to-tray, so Process.CloseMainWindow()
    // makes the window vanish and leaves the app running — measured on a real
    // Windows box, where Quit reliably fell through to Force quit.
    //
    // Windows' own shutdown sequence is WM_QUERYENDSESSION, then WM_ENDSESSION,
    // then WM_CLOSE, then a kill. WM_CLOSE is the *weakest* of those, which is
    // why it does the least here. Electron handles WM_ENDSESSION (and, as of
    // writing, still ignores WM_QUERYENDSESSION — electron/electron#44598), so
    // WM_ENDSESSION is the message that actually ends the app while letting it
    // shut down on its own terms.
    //
    // Both messages go to every top-level window the process owns rather than to
    // Process.MainWindowHandle, because that property only finds *visible*
    // windows: once the app has hidden itself to the tray there is no main window
    // handle left to escalate to, and the hidden one still receives messages.
    [SupportedOSPlatform("windows")]
    internal static class WindowsAppQuit
    {
        private const uint WM_QUERYENDSESSION = 0x0011;
        private const uint WM_ENDSESSION = 0x0016;

        private const uint SMTO_ABORTIFHUNG = 0x0002;

        // Long enough for a busy renderer to service its message queue, short
        // enough that a wedged app doesn't hold the calling thread for long.
        private const uint SendTimeoutMs = 4000;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint message,
            IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        // Every top-level window belonging to this process, visible or not.
        private static List<IntPtr> TopLevelWindows(int pid)
        {
            var found = new List<IntPtr>();

            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var owner);
                if (owner == (uint)pid) found.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            return found;
        }

        // Ask the app to end as though the session were ending. Returns false if
        // there was nothing to ask — no windows — which is the caller's cue that
        // only a force quit is left.
        public static bool RequestEndSession(int pid)
        {
            try
            {
                var windows = TopLevelWindows(pid);
                if (windows.Count == 0) return false;

                foreach (var hwnd in windows)
                {
                    // Sent for protocol completeness even though Electron ignores
                    // it; an app that does handle it gets the pair in the order
                    // Windows itself would send them.
                    SendMessageTimeout(hwnd, WM_QUERYENDSESSION, IntPtr.Zero, IntPtr.Zero,
                        SMTO_ABORTIFHUNG, SendTimeoutMs, out _);

                    // wParam TRUE: the session really is ending, so shut down.
                    SendMessageTimeout(hwnd, WM_ENDSESSION, new IntPtr(1), IntPtr.Zero,
                        SMTO_ABORTIFHUNG, SendTimeoutMs, out _);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool HasExited(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true; // already gone
            }
            catch
            {
                return false;
            }
        }
    }
}
