using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClaudeBuddy
{
    // Asking a tray-resident Electron app to close its windows on Windows.
    //
    // Deliberately not named "quit": Claude Desktop cannot be made to quit from
    // outside, and this file's job is only to ask reliably. Two mechanisms were
    // tried on a real installed build and neither ended the app:
    //
    //   * WM_CLOSE — hides to tray and keeps running, like most Electron chat
    //     apps.
    //   * WM_ENDSESSION, the message Windows' own shutdown sequence uses and
    //     which Electron is documented to act on where WM_QUERYENDSESSION is
    //     ignored (electron/electron#44598). Sent to every top-level window of
    //     the process, on two separately launched instances: no effect either
    //     time. Removed rather than left in, because code implying an outcome it
    //     doesn't produce is worse than no code.
    //
    // So Force quit is the only thing that actually ends an instance on Windows,
    // and the job that matters here is making sure the UI can always *reach*
    // that offer. Which is why this posts WM_CLOSE itself instead of calling
    // Process.CloseMainWindow(): MainWindowHandle only finds *visible* windows,
    // so once the first Quit has hidden the app, CloseMainWindow() returns false
    // and the row reported "couldn't quit" without ever reaching the force-quit
    // offer — stranding instances with no route to end them from the app at all.
    // A hidden window still receives messages perfectly well.
    [SupportedOSPlatform("windows")]
    // Excluded from coverage: posts WM_CLOSE to the real top-level windows of
    // a real pid, found through EnumWindows. Nothing here decides anything a
    // test could check without a live window to close.
    [ExcludeFromCodeCoverage]
    internal static class WindowsAppQuit
    {
        private const uint WM_CLOSE = 0x0010;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

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

        // Ask every window of the process to close, and report whether there was
        // anything to ask. Posted rather than sent: the app's own close handling
        // may put up a prompt, and blocking a UI-thread caller on that would
        // freeze this app's menu and every orb.
        //
        // False means the process has no windows at all, which is the caller's
        // signal that asking is pointless and only a force quit remains.
        public static bool RequestClose(int pid)
        {
            try
            {
                var windows = TopLevelWindows(pid);
                if (windows.Count == 0) return false;

                foreach (var hwnd in windows)
                {
                    PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
