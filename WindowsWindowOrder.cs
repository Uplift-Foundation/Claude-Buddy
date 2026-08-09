using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace ClaudeBuddy
{
    // Relative z-order between two of this app's own windows, which Avalonia
    // has no API for: Topmost is a band, not an ordering, and both the orb and
    // its mic flyout are in it.
    internal static class WindowsWindowOrder
    {
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint flags);

        // Puts `window` immediately behind `other`, without moving, resizing or
        // activating it — and without leaving the topmost band, since inserting
        // relative to a window that is already topmost keeps that status.
        //
        // Needed because Windows puts a newly shown topmost window at the *front*
        // of the topmost band. The mic flyout is shown at the instant its
        // fly-out animation starts, from a position concentric with the orb, so
        // for the 160ms that animation runs the mic drew on top of the orb it is
        // supposed to be sliding out from underneath. Left alone it ends up
        // behind the orb anyway once other things touch the z-order, which is
        // why this only ever looked wrong at the start of the motion.
        //
        // Windows-only on purpose. The equivalent on macOS is NSWindow's
        // orderWindow:relativeTo:, but the ordering there was never observed to
        // be wrong, and this machine can't test a change to it — so macOS keeps
        // the behaviour it already has rather than gaining an unverified call
        // that could hide the flyout outright.
        public static void PlaceJustBehind(this Window window, Window other)
        {
            if (!OperatingSystem.IsWindows()) return;

            var self = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            var above = other.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (self == IntPtr.Zero || above == IntPtr.Zero) return;

            SetWindowPos(self, above, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }

        // Back to the front of the topmost band — where showing the window put
        // it in the first place, and where it has to end up.
        //
        // Being behind the orb is only wanted while the fly-out animation runs.
        // Left there afterwards it costs clicks: the orb's window is 120x56 of
        // hit-testable HWND whatever its content does (see the comment on Root
        // in OrbWindow.axaml), the mic comes to rest inside that rectangle, and
        // a click on the overlap goes to whichever window is in front. Measured
        // with WindowFromPoint over the mic's circle: with the flyout behind,
        // the orb owned the mic's top rows and those clicks did nothing.
        private static readonly IntPtr HwndTopmost = new(-1);

        public static void PlaceInFront(this Window window)
        {
            if (!OperatingSystem.IsWindows()) return;

            var self = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (self == IntPtr.Zero) return;

            SetWindowPos(self, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }
}
