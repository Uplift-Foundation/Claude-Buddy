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
    //       window server is *telling* us the screen is locked, so starting
    //       here is a guaranteed -6661. This one waits far longer than the
    //       unknowable state does — but it is still capped, and CapFor's
    //       comment has the argument for why the uncapped version this change
    //       first shipped was wrong.
    //
    // This state is not a hypothesis. It was confirmed from the unified log
    // for the 2026-09-22 crash: `_powerMonitor.screenLocked yes` alongside
    // `DisplayOn: 0`, `isClamshelled 1` and `isDarkWake 1`, 1.3 seconds before
    // the throw. And it was the *key* arm rather than the null one, which
    // matters because the two are not interchangeable — Buddy is a LaunchAgent
    // with no LimitLoadToSessionType, so it loads into the Aqua session where
    // the session dictionary is non-null (verified by a read-only probe:
    // dictionary present, CGSSessionScreenIsLocked absent,
    // kCGSSessionOnConsoleKey 1, while unlocked). A non-null dictionary plus
    // an OS-reported lock is the key being true. Had it been the null arm
    // instead, the wake would have found a short cap already expired and
    // crashed exactly as before.
    //
    // Waiting far longer on a reported lock is the change, and what makes it
    // survivable is that everything Buddy does without a display is started
    // ahead of this wait and keeps running with no dispatcher. `Startup.Run`
    // calls `serveOnLaunch` before `waitForUnlock`, and that body is three
    // calls, each of which was put there for exactly this case (CB-24,
    // CB-130):
    //
    //   PeerSessions.Start()         — two plain System.Threading.Timers,
    //                                  chosen over DispatcherTimers precisely
    //                                  so they keep firing on a machine whose
    //                                  screen never unlocks. See its own
    //                                  comment at the _connecting/_pumping
    //                                  pair, which says so.
    //   OpenClawSessions.Restart()   — a Task.Run supervisor loop.
    //   ClaudeCloudSessions.Restart()— a Task.Run poll loop.
    //
    // All three touch Dispatcher.UIThread.Post only to push results at the UI,
    // and those posts queue harmlessly until there is a dispatcher to drain
    // them. So a machine parked here for hours still serves its peers, its
    // gateway and its cloud sessions. The only thing that does not happen is
    // drawing a menu-bar icon onto a screen nobody can see.
    //
    // **Deliberately not citing ServePump for this, though it looks like the
    // obvious candidate and an earlier draft of this comment did.**
    // `RemoteControlSessions._servePump` is declared and read but never
    // assigned: every `new ServePump` in the tree is in a test project, so the
    // pump does not run in production at all. The relay it was written to
    // cover is gone too — `RemoteControlSessions` says outright that "the
    // bridge itself is gone and this is the shell it lived in", and
    // `serveOnLaunch` has two blank lines where its start call used to be.
    // Both of those are pre-existing on develop and neither is touched here;
    // they are named so the next person does not rebuild an argument on them,
    // which is what happened while this change was being written.
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

        // Wait far longer, but still not forever. For the state where the
        // window server has told us starting would fail.
        WaitUpToLockedCap
    }

    internal static class ScreenLockWait
    {
        // The rule, one arm per state.
        internal static ScreenLockWaitPolicy PolicyFor(ScreenLockState state) => state switch
        {
            ScreenLockState.Unlocked => ScreenLockWaitPolicy.StartNow,
            ScreenLockState.NoWindowServerSession => ScreenLockWaitPolicy.WaitUpToCap,
            ScreenLockState.Locked => ScreenLockWaitPolicy.WaitUpToLockedCap,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };

        // How long this policy is willing to wait before starting anyway.
        //
        // Two caps rather than one, because the two waiting states are waiting
        // on different things. `WaitUpToCap` is the unknowable state and its
        // cap is short, because a misread lock must not keep Buddy off the
        // menu bar for a session. `WaitUpToLockedCap` is the window server's
        // own answer, so its cap is long enough that it will not fire in any
        // real lock — but it exists, and that is the whole argument below.
        internal static TimeSpan CapFor(
            ScreenLockWaitPolicy policy, TimeSpan cap, TimeSpan lockedCap) =>
            policy switch
            {
                ScreenLockWaitPolicy.StartNow => TimeSpan.Zero,
                ScreenLockWaitPolicy.WaitUpToCap => cap,
                ScreenLockWaitPolicy.WaitUpToLockedCap => lockedCap,
                _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
            };

        // The policy with the caller's clock folded in: may startup build a
        // compositor right now?
        //
        // Both waiting arms read `capExpired` the same way, because CapFor has
        // already chosen *which* cap that is. The difference between them is
        // the length, not the rule.
        //
        // **A reported lock is capped too, and that is a deliberate reversal
        // of this change's first draft.** That draft waited on a reported lock
        // forever, on the reasoning that the window server's own answer cannot
        // be wrong in the direction a cap defends against. The reasoning is
        // sound and the conclusion still was not: it has no answer for the key
        // being stuck true after a real unlock, where the failure is Buddy
        // invisibly absent with no recovery but for someone noticing a process
        // and killing it. Capped, that same case starts the UI; and if the
        // screen really is locked, the -6661 crash is restarted by
        // KeepAlive{SuccessfulExit:false}, re-probes and waits again —
        // self-healing, one log line per twelve hours of continuous lock, and
        // nobody is looking at a locked screen while it happens. This
        // repository rates silent absence worse than a crash, and this is
        // exactly that trade.
        //
        // The cap is long enough that it should never fire. The 2026-09-22
        // lock was confirmed from the unified log — `_powerMonitor.screenLocked
        // yes` with `DisplayOn: 0` and `isClamshelled 1`, 1.3 seconds before
        // the crash — so the state this arm handles is real, and a machine
        // genuinely locked for twelve hours is not one anybody is waiting to
        // see a menu bar on.
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
                ScreenLockWaitPolicy.WaitUpToLockedCap => capExpired,
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
        // at all.
        //
        // Elapsed time is measured from a single `start` taken once, rather
        // than accumulated across iterations, which matters more than it looks:
        // Thread.Sleep is not scheduled during deep sleep, so a machine that
        // slept through its own cap notices only on the next wake and finds the
        // deadline already in the past. That is exactly the 2026-09-22 crash,
        // which landed 1.5 seconds into a DarkWake (confirmed from the unified
        // log, not only from `pmset -g log`). Under this loop that wake
        // re-probes first, and the cap it is measured against is chosen from
        // what the *fresh* probe says: a wake into a still-locked screen gets
        // the long cap, so an already-expired two-hour deadline cannot start
        // the UI into a -6661 the way it did.
        //
        // **Each waiting state gets its own clock, and that is a bug fix rather
        // than a refinement.** An earlier version measured both caps from one
        // `start` taken at entry, which meant a long wait under one cap spent
        // the other cap's budget without ever being in that state. Three hours
        // locked, then a single transient NoWindowServerSession reading, and
        // `now() - start` is already past the two-hour cap — so it starts
        // instantly into a context with no window server, which is the very
        // -6661 this file exists to prevent. A DarkWake is exactly where a
        // session dictionary might read differently for a beat, so that is not
        // a hypothetical path.
        //
        // **Latched once and never moved, rather than restarted on every
        // change.** Restarting the clock whenever the state changes sounds
        // like the same fix and is a worse one: a reading that oscillates
        // between Locked and NoWindowServerSession would reset its budget on
        // every poll and never expire either cap, which is the permanent
        // silent absence the caps exist to make impossible. Latching only ever
        // accrues, so both caps still fire, while a state that has genuinely
        // just appeared is measured from when it appeared.
        //
        // This is also what keeps the DarkWake property the previous version
        // had. A machine that sleeps through its own cap is not scheduled, so
        // it notices only on the next wake and finds the deadline already
        // behind it — the 2026-09-22 crash landed 1.5 seconds into one. Because
        // the latch is a fixed instant rather than a rolling one, that wake
        // compares against when the state was first seen and acts on it, rather
        // than resetting its budget and waiting all over again.
        internal static void Wait(
            Func<ScreenLockState> probe,
            Func<DateTime> now,
            Action<TimeSpan> sleep,
            TimeSpan cap,
            TimeSpan lockedCap,
            TimeSpan interval)
        {
            var start = now();
            DateTime? lockedSince = null;
            DateTime? unknownSince = null;
            var firstReading = true;

            while (true)
            {
                var policy = PolicyFor(probe());
                var readAt = now();

                // The first reading is latched at `start` rather than at its
                // own clock read, so a wait that begins with its deadline
                // already behind it sees that on the first pass instead of
                // sleeping once for nothing.
                var latch = firstReading ? start : readAt;
                firstReading = false;

                DateTime since;
                switch (policy)
                {
                    case ScreenLockWaitPolicy.WaitUpToLockedCap:
                        lockedSince ??= latch;
                        since = lockedSince.Value;
                        break;
                    case ScreenLockWaitPolicy.WaitUpToCap:
                        unknownSince ??= latch;
                        since = unknownSince.Value;
                        break;
                    default:
                        // StartNow, whose cap is zero — the latch is immaterial.
                        since = latch;
                        break;
                }

                var expired = readAt - since >= CapFor(policy, cap, lockedCap);

                if (ShouldStartNow(policy, expired)) return;
                sleep(interval);
            }
        }
    }
}
