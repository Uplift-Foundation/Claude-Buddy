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
        // there. The path CB-28 was written against was
        // RemoteControlSessions.StartAsync reaching its
        // `Dispatcher.UIThread.Post(EnsureTimer)` after an awaited
        // `bridge.StartAsync()`, so that post ran on the pool — and **that
        // path no longer exists.** The bridge went with the relay, and
        // `StartAsync` and `EnsureTimer` now survive only in comments; see
        // ServePump.cs's header, which has the rest of what went with it.
        //
        // The hazard did not go with it, which is why this still has to happen
        // first. `serveOnLaunch` starts OpenClawSessions and
        // ClaudeCloudSessions on `Task.Run` loops and PeerSessions on plain
        // Timers, and all three reach `Dispatcher.UIThread.Post` from a pool
        // thread to push their results at the UI. Any one of them is the same
        // race in the same shape, against a different caller.
        //
        // On an ordinary machine Main is already inside
        // StartWithClassicDesktopLifetime by then and has claimed the
        // dispatcher; on a machine whose screen never unlocks Main is asleep
        // in WaitForUnlock and cannot have. That is the whole of the race, and
        // it is why the crash was always at the two-hour mark — back when two
        // hours was when that sleep ended. A reported lock now runs to a
        // twelve-hour cap instead (see ScreenLockWait), so the window in which
        // a pool thread could win is six times longer rather than shorter: the
        // claim below matters more than it did, not less.
        //
        // Idempotent, and cheap enough not to think about: after the first call
        // it is a static field read.
        internal static void ClaimUiThread() => _ = Dispatcher.UIThread;

        // Main's body, with the things it does passed in.
        //
        // The point is the sequence, which is the fix: the claim has to happen
        // before anything that could post from another thread, and everything
        // Buddy starts before the UI is up can. Writing crashes down comes
        // before even that, because the first thing worth recording is a failure
        // in the startup below it — the two crashes that prompted all of this
        // happened inside `startUi` and left nothing behind (CB-44).
        // `serveOnLaunch` brings up the peer link and the gateway and cloud
        // pollers, whose continuations land on the pool; `waitForUnlock` then
        // holds this thread for as long as the screen stays locked, up to
        // twelve hours, which is all the time those continuations need and
        // then some. Starting the UI
        // last is the shape that already existed and is what makes the first
        // three worth ordering at all.
        //
        // `claimSingleInstance` sits between `installCrashLog` and
        // `claimUiThread`, and both sides of that placement matter (CB-178).
        // After `installCrashLog`, so that a genuinely unexpected failure in
        // the claim itself (not the ordinary "someone else holds it" answer —
        // an actual thrown exception) still gets written down. Before
        // everything else, including `claimUiThread`, because a duplicate
        // instance should do *nothing* else: not claim the UI thread, not
        // start a relay, not touch the screen-lock wait. It used to run
        // inside Avalonia startup instead — see App.axaml.cs's history and
        // SingleInstance.cs's header comment — where the loser called
        // `desktop.Shutdown()` after Avalonia had already begun unwinding
        // toward `Dispatcher.MainLoop`, which is what turned "another Buddy
        // is running" into an uncaught `InvalidOperationException` and a
        // SIGABRT. A step that can return `false` and stop the sequence
        // before any of that exists is the fix: nothing after this line runs
        // for the loser, so there is no dispatcher left for anything to shut
        // down.
        //
        // Passed as delegates rather than called directly because every one of
        // them is unrunnable in a test — a real relay, a real screen-lock query,
        // a real named mutex, and a lifetime that owns the process until it
        // exits — while the order (and, now, the short-circuit) is the part
        // that broke and the part a test can hold on to.
        internal static void Run(
            Action installCrashLog,
            Func<bool> claimSingleInstance,
            Action claimUiThread,
            Action serveOnLaunch,
            Action waitForUnlock,
            Action startUi)
        {
            installCrashLog();
            if (!claimSingleInstance())
            {
                // Another live Buddy holds the single-instance claim. Exit
                // the sequence here, with nothing else started and nothing
                // thrown — this return is the whole fix for CB-178: it is a
                // normal return from Main, so the process exits 0.
                return;
            }
            claimUiThread();
            serveOnLaunch();
            waitForUnlock();
            startUi();
        }
    }
}
