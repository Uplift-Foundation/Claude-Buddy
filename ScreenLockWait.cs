namespace ClaudeBuddy
{
    // Whether startup may build a compositor yet, and for how long it is worth
    // waiting if not.
    //
    // This is the pure half of the screen-lock wait, split out from
    // MacOSScreenLock for the same reason SingleInstance.ShouldProceed was
    // split out of the mutex claim: the decision is the part that was wrong,
    // and it lived inside a class no test can reach. MacOSScreenLock is
    // [ExcludeFromCodeCoverage] in its entirety — it is four DllImports and a
    // poll around them, and a CI runner has no interactive session for the
    // answer to be about — so as long as the rule lived there, the rule was
    // unverifiable by construction.
    //
    // What was actually wrong: the probe collapsed three facts into a bool.
    // `IsScreenLocked()` returned true both when the window server said the
    // screen was locked *and* when there was no window server session at all
    // to ask, and `WaitForUnlock` returned a bool that said "the cap expired
    // while still locked" which Program.cs discarded — `Startup.Run` took the
    // step as an `Action`, so the lambda's value went nowhere and `startUi()`
    // ran into a screen the app had just confirmed was locked. The result is
    // an `InvalidOperationException` out of `AvaloniaNativeRenderTimer`
    // (-6661, kCVReturnInvalidDisplay) during `AppBuilder.Setup()`, which
    // kills the process before `App.OnFrameworkInitializationCompleted` has
    // run: no tray icon, no SessionManager, nothing to catch it with.
    //
    // The three facts are different answers, so they are three states:
    //
    //   NoWindowServerSession — CGSessionCopyCurrentDictionary returned null.
    //       There is no session to ask, which is a daemon or background-job
    //       context. Genuinely unknowable, so this keeps the capped wait and
    //       starts anyway when the cap runs out. That is what the cap's
    //       original comment was actually about: a misread lock state must not
    //       be able to keep Buddy off the menu bar for a whole session.
    //
    //   Unlocked — the dictionary is there and CGSSessionScreenIsLocked is
    //       absent (or present and false). Start immediately.
    //
    //   Locked — the dictionary is there and the key is present and true. The
    //       window server is *telling* us the screen is locked, so this is not
    //       an inference that could be wrong in the direction the cap defends
    //       against; starting here is a guaranteed -6661. So this one waits
    //       with no cap at all.
    //
    // Waiting without a cap is the change, and it is safe for a reason that
    // predates it: ServePump. Everything Buddy does before the UI is up — the
    // relay, PeerSessions, OpenClawSessions, ClaudeCloudSessions — is already
    // started ahead of this wait (CB-24, CB-130) and already covered for the
    // no-dispatcher window by ServePump's off-thread tick, so a machine parked
    // here indefinitely still serves its sessions. The only thing that does
    // not happen is drawing a menu-bar icon onto a screen nobody can see.
    //
    // Rejected: returning from Main instead of waiting. The launch agent is
    // KeepAlive{SuccessfulExit:false}, so a clean exit(0) is precisely the
    // answer that stops it being restarted — Buddy would be silently absent,
    // which is the failure the two-hour cap was written to prevent in the
    // first place. Also rejected: catching the -6661. Avalonia 12.1.1 guards
    // setup behind a static s_setupWasAlreadyCalled, so setup is one-shot per
    // process and there is nothing to retry; and the throw lands before any of
    // the app exists, so a catch would leave a live process with no UI and no
    // route to one. Worse than crashing, because the keep-alive would see a
    // healthy process.
    internal enum ScreenLockState
    {
        // CGSessionCopyCurrentDictionary returned null: no window server
        // session, so no answer. Not the same as locked, which is the whole
        // point of this enum.
        NoWindowServerSession,

        // A session exists and does not report a locked screen.
        Unlocked,

        // A session exists and reports CGSSessionScreenIsLocked true.
        Locked
    }

    // How long to keep waiting, given what the probe said. Named rather than
    // expressed as a bool pair so that the three-way nature of the answer
    // survives being read six months from now — a `(bool start, bool capped)`
    // tuple has a fourth combination that means nothing.
    internal enum ScreenLockWaitPolicy
    {
        // Nothing to wait for.
        StartNow,

        // Wait, but start anyway once the caller's cap has elapsed. For the
        // state where we cannot tell.
        WaitUpToCap,

        // Wait for as long as it takes. For the state where the window server
        // has told us starting would fail.
        WaitWithoutCap
    }

    internal static class ScreenLockWait
    {
        // The rule, one arm per state.
        internal static ScreenLockWaitPolicy PolicyFor(ScreenLockState state) => state switch
        {
            ScreenLockState.Unlocked => ScreenLockWaitPolicy.StartNow,
            ScreenLockState.NoWindowServerSession => ScreenLockWaitPolicy.WaitUpToCap,
            ScreenLockState.Locked => ScreenLockWaitPolicy.WaitWithoutCap,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };

        // The policy with the caller's clock folded in: may startup build a
        // compositor right now?
        //
        // `capExpired` is deliberately ignored for WaitWithoutCap. That is not
        // an oversight to tidy up later — it is the fix. A cap that can expire
        // into a start is only defensible while the lock reading might be
        // wrong, and the state that maps here is the window server's own
        // answer.
        //
        // Takes the policy rather than the state, with the state overload
        // below composing the two, so that every arm — including the
        // out-of-range guard — is reachable from a test. Written the other way
        // round, the guard could never fire, because PolicyFor would have
        // thrown on the way in: an unreachable line in the middle of the one
        // rule this change exists to make checkable.
        internal static bool ShouldStartNow(ScreenLockWaitPolicy policy, bool capExpired) =>
            policy switch
            {
                ScreenLockWaitPolicy.StartNow => true,
                ScreenLockWaitPolicy.WaitUpToCap => capExpired,
                ScreenLockWaitPolicy.WaitWithoutCap => false,
                _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
            };

        // The whole rule, from what the probe said to whether startup may
        // proceed. This is what the poll loop calls.
        internal static bool ShouldStartNow(ScreenLockState state, bool capExpired) =>
            ShouldStartNow(PolicyFor(state), capExpired);

        // The poll loop, with the clock, the sleep and the probe passed in.
        //
        // Every one of those three is unrunnable in a test as itself — a real
        // DllImport into CoreGraphics, and a real two-hour Thread.Sleep — while
        // the loop around them is where the discarded answer was. So they are
        // parameters, the same argument Startup.Run makes for its own six
        // delegates.
        //
        // The probe runs before the first sleep, so an unlocked machine — the
        // overwhelmingly common case — pays one CoreGraphics call and no delay
        // at all. The deadline is computed once from the caller's clock rather
        // than accumulated across iterations, which matters more than it looks:
        // Thread.Sleep is not scheduled during deep sleep, so a machine that
        // slept through its own cap notices only on the next wake and finds the
        // deadline already in the past. That is exactly the 2026-09-22 crash,
        // which landed 1.5 seconds into a DarkWake (corroborated against
        // `pmset -g log`). Under this loop that wake re-probes first: an
        // already-expired deadline starts the UI only if the state still says
        // the answer is unknowable, never if the screen is reported locked.
        internal static void Wait(
            Func<ScreenLockState> probe,
            Func<DateTime> now,
            Action<TimeSpan> sleep,
            TimeSpan cap,
            TimeSpan interval)
        {
            var deadline = now() + cap;

            while (true)
            {
                if (ShouldStartNow(probe(), now() >= deadline)) return;
                sleep(interval);
            }
        }
    }
}
