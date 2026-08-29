using System;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // The order the process starts in, as something other than the inside of
    // Main.
    //
    // It is here because the order is load-bearing and was wrong, in a way no
    // reading of Main could have shown: the first two steps look independent
    // and are not. CB-28.
    internal static class Startup
    {
        // Claim Avalonia's UI-thread dispatcher for the thread that is calling.
        //
        // **Reading this property is the whole operation, and it is not a
        // no-op.** In Avalonia 12.1.1, `Dispatcher.UIThread` falls through to
        // `CurrentDispatcher` when nothing has made a dispatcher yet
        // (Dispatcher.ThreadStorage.cs), and that constructs
        // `new Dispatcher(null)` *on the calling thread*, whose constructor does
        // `s_uiThread ??= this`. Whichever thread touches it first owns the UI
        // thread for the life of the process. Nothing about the call site says
        // so, which is why this has a name and forty lines of comment rather
        // than being a bare `_ =` in Main.
        //
        // What goes wrong without it, measured on this machine against Avalonia
        // 12.1.1 with a throwaway console app (the method is in the CB-28 PR
        // body so anyone can repeat it in a minute):
        //
        //   post from a thread-pool thread, then
        //   AppBuilder…UsePlatformDetect().SetupWithoutStarting()
        //     -> InvalidOperationException: The calling thread cannot access
        //        this object because a different thread owns it.
        //
        //   claim on the main thread first, then the same pool-thread post
        //     -> setup succeeds, and the callback queued before startup runs
        //        once the dispatcher pumps.
        //
        // macOS platform init (AvaloniaNativePlatform.Initialize) calls
        // Dispatcher.InitializeUIThreadDispatcher, whose first act is
        // UIThread.VerifyAccess(). If a pool thread got there first, that throws
        // and takes the process with it.
        //
        // A pool thread does get there first on an unattended machine, and only
        // there. RemoteControlSessions.StartAsync reaches its
        // `Dispatcher.UIThread.Post(EnsureTimer)` only after
        // `await bridge.StartAsync().ConfigureAwait(false)`, so that post runs
        // on the pool. On an ordinary machine Main is already inside
        // StartWithClassicDesktopLifetime by then and has claimed the
        // dispatcher; on a machine whose screen never unlocks Main is asleep in
        // WaitForUnlock for two hours and cannot have. That is the whole of the
        // race, and it is why the crash was always at the two-hour mark.
        //
        // Idempotent, and cheap enough not to think about: after the first call
        // it is a static field read.
        internal static void ClaimUiThread() => _ = Dispatcher.UIThread;

        // Main's body, with the four things it does passed in.
        //
        // The point is the sequence, which is the fix: the claim has to happen
        // before anything that could post from another thread, and everything
        // Buddy starts before the UI is up can. `serveOnLaunch` brings up a
        // relay whose continuations land on the pool; `waitForUnlock` then holds
        // this thread for up to two hours, which is all the time those
        // continuations need. Starting the UI last is the shape that already
        // existed and is what makes the first three worth ordering at all.
        //
        // Passed as delegates rather than called directly because every one of
        // them is unrunnable in a test — a real relay, a real screen-lock query,
        // and a lifetime that owns the process until it exits — while the order
        // is the part that broke and the part a test can hold on to.
        internal static void Run(
            Action claimUiThread,
            Action serveOnLaunch,
            Action waitForUnlock,
            Action startUi)
        {
            claimUiThread();
            serveOnLaunch();
            waitForUnlock();
            startUi();
        }
    }
}
