using System.Diagnostics.CodeAnalysis;
using Avalonia;

namespace ClaudeBuddy
{
    // Excluded from coverage: the process entry point. Main waits on the real
    // screen-lock state and then hands control to
    // StartWithClassicDesktopLifetime, which owns the process for its lifetime;
    // BuildAvaloniaApp configures the one AppBuilder a process gets, and the
    // headless suites build their own (see tests/UiTests' TestAppBuilder). There
    // is no way to run either without ending the test run.
    [ExcludeFromCodeCoverage]
    internal static class Program
    {
        // How long to wait for the screen to unlock before starting anyway. Long
        // enough to cover coming back to the machine after a while, short enough
        // that a misread lock state can't keep the app off the menu bar for a
        // whole session.
        private static readonly TimeSpan LockWait = TimeSpan.FromHours(2);
        private static readonly TimeSpan LockPoll = TimeSpan.FromSeconds(2);

        [STAThread]
        public static void Main(string[] args)
        {
            // Avalonia's macOS render timer is a CVDisplayLink, and
            // CVDisplayLinkStart fails with -6661 (kCVReturnInvalidDisplay) while
            // the screen is locked, which killed startup outright. A Login Item
            // starts before you type your password, so every reboot hit this and
            // the app was simply missing afterwards with no visible reason.
            //
            // Nothing is lost by waiting: a menu-bar icon on a locked screen is
            // invisible either way, and sessions that start while locked still
            // get picked up, because the hook writes status files to disk and
            // SessionManager reads them on its first scan.
            MacOSScreenLock.WaitForUnlock(LockWait, LockPoll);

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                // The orbs are the whole UI; no Dock icon needed on macOS.
                .With(new MacOSPlatformOptions { ShowInDock = false })
                .LogToTrace();
    }
}
