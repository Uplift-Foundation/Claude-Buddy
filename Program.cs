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
            // The order these four run in is Startup.Run's, and the first of
            // them is the fix for CB-28 — see Startup.ClaimUiThread for why
            // reading one static property earns a step of its own. What each
            // step is *for* is here, next to the thing it calls.
            Startup.Run(
                // Write an unhandled exception down before anything can throw
                // one. Buddy aborted twice on the mini on 28 Aug with nothing on
                // disk to say why, and the .ips reports could not name the
                // exception; CrashLog exists so the next one costs a `cat`
                // rather than a probe (CB-44).
                installCrashLog: CrashLog.Install,

                // Claim Avalonia's UI thread for this thread while it is
                // certain to be free, which is the only moment it is:
                // everything after this line either starts something that
                // posts to the dispatcher from the thread pool, or holds this
                // thread for two hours while it does.
                claimUiThread: Startup.ClaimUiThread,

                // The serve path before the screen-lock wait below, because it
                // needs nothing that wait exists to protect: a relay is tmux,
                // files and subprocesses, with no display anywhere in it. A
                // machine that serves its sessions to other Buddies unattended
                // is exactly a machine whose screen may never unlock —
                // headless, in a cupboard — and parking the relay behind the
                // wait meant it never served at all (CB-24). Does nothing
                // unless remoteControlServeOnLaunch is on; and if this early
                // start fails, SessionManager.Start makes the same call again
                // once the UI is up, because EnsureStarted retries a failed
                // relay.
                serveOnLaunch: () =>
                {
                    

                    // The peer link starts here for exactly the reasons above,
                    // and rather more sharply. It is a socket and a UDP
                    // announcement — no display anywhere in it — and the
                    // machine it matters most on is the one whose screen never
                    // unlocks. Parking it behind the screen-lock wait would
                    // reproduce CB-24 on a transport that has no excuse for it.
                    //
                    // Does nothing unless peerLinkEnabled is on.
                    PeerSessions.Start();
                },

                // Avalonia's macOS render timer is a CVDisplayLink, and
                // CVDisplayLinkStart fails with -6661 (kCVReturnInvalidDisplay)
                // while the screen is locked, which killed startup outright. A
                // Login Item starts before you type your password, so every
                // reboot hit this and the app was simply missing afterwards
                // with no visible reason.
                //
                // Nothing is lost by waiting: a menu-bar icon on a locked
                // screen is invisible either way, and sessions that start while
                // locked still get picked up, because the hook writes status
                // files to disk and SessionManager reads them on its first
                // scan.
                waitForUnlock: () => MacOSScreenLock.WaitForUnlock(LockWait, LockPoll),

                startUi: () => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args));
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                // The orbs are the whole UI; no Dock icon needed on macOS.
                .With(new MacOSPlatformOptions { ShowInDock = false })
                .LogToTrace();
    }
}
