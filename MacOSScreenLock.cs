using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ClaudeBuddy
{
    // Is the login session's screen locked?
    //
    // This exists for one reason: Avalonia's macOS render timer is a
    // CVDisplayLink, and creating one fails with -6661
    // (kCVReturnInvalidArgument) while the screen is locked, which aborts the
    // app during startup rather than degrading. Launching into a locked screen
    // is not an edge case — a Login Item starts before you type your password,
    // so every reboot is this scenario.
    //
    // **-6661 is kCVReturnInvalidArgument. This file called it
    // kCVReturnInvalidDisplay for months, and that was an inference rather
    // than a reading.** kCVReturnInvalidDisplay is -6670 — read from CVReturn.h
    // in the macOS 27.0 SDK on this machine, and the same in 26.5, so it is not
    // a renumbering. The *number* was observed; the *name* was supplied to
    // explain it, and "CoreVideo is telling us there is no valid display" is
    // how this became a screen-lock story in the first place. -6661 says an
    // argument was invalid, which points at nothing on its own.
    //
    // **The conclusion survives the correction, and that is the part worth
    // keeping.** zed-industries/zed#63217 — a different project, a different
    // author, filed 2026-08-25, a month before this branch — reports creating
    // a CVDisplayLink returning -6661 specifically while the screen is locked,
    // measured rather than assumed. The lock association is independently
    // reproduced and never rested on the name. That issue misnames the
    // constant identically, which is the durable lesson: **a wrong name that
    // fits the observation is far stickier than one that does not.** Nobody
    // would have kept kCVReturnInvalidDisplay attached to -6661 for long if the
    // behaviour had looked unrelated to displays; it survived in two unrelated
    // codebases precisely because it made sense of something real.
    //
    // Which specific CoreVideo call returns it is deliberately not named here.
    // Avalonia's PlatformRenderTimer.mm is reported to return the result of
    // CVDisplayLinkCreateWithActiveCGDisplays and to discard
    // CVDisplayLinkStart's, which would make the old wording wrong twice over
    // — but that is a code-read of a dependency nobody here has confirmed, so
    // it is not stated as fact.
    //
    // CGSessionCopyCurrentDictionary needs no entitlement and no TCC prompt. It
    // returns null when there is no window server session at all (a daemon
    // context), and that used to be folded in with "locked" — either way there
    // was no display to drive, so a bool seemed to cover it. It does not: the
    // two answers deserve different waits, and collapsing them is what let
    // startup walk into a -6661 on a screen it had just been told was locked.
    // ScreenLockWait's header comment has the whole diagnosis; this file now
    // only reports which of the three states the window server is in and
    // hands the decision there.
    //
    // Excluded from coverage: this whole class is four DllImports and the
    // wiring around them. A CI runner has no interactive session for the
    // answer to be about, so a test of ProbeState could only assert whatever
    // the runner happens to be, and WaitForUnlock is a poll that would exit
    // immediately with nothing asserted. Everything that used to be a
    // decision in here is in ScreenLockWait, which is pure and fully
    // covered.
    [ExcludeFromCodeCoverage]
    internal static class MacOSScreenLock
    {
        private const string CoreGraphics =
            "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        private const string CoreFoundation =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        private const uint KCFStringEncodingUtf8 = 0x08000100;

        [DllImport(CoreGraphics)]
        private static extern IntPtr CGSessionCopyCurrentDictionary();

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr reference);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

        [DllImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool CFBooleanGetValue(IntPtr boolean);

        // Which of the three states the login session is in.
        //
        // A missing key means unlocked — the key is only published while the
        // screen is locked, which was confirmed by execution on this machine
        // while unlocked: the dictionary came back non-null and its keys were
        // CGSSessionUniqueSessionUUID, kCGSSessionAuditIDKey,
        // kCGSSessionGroupIDKey, kCGSSessionLoginwindowSafeLogin,
        // kCGSSessionOnConsoleKey, kCGSSessionSystemSafeBoot,
        // kCGSSessionUserIDKey, kCGSSessionUserNameKey, kCGSessionLoginDoneKey,
        // kCGSessionLongUserNameKey and kSCSecuritySessionID — with
        // CGSSessionScreenIsLocked absent entirely.
        //
        // The locked reading is observed too, and directly. On a Mac mini that
        // was already locked, read over SSH without locking anything:
        // CGSSessionScreenIsLocked = 1, CGSSessionScreenLockedTime present,
        // kCGSSessionOnConsoleKey = 1, dictionary non-null. So the Locked arm
        // is measured rather than inferred, which it was not when this was
        // first written.
        //
        // **NoWindowServerSession is now the least-evidenced of the three, and
        // the reversal is worth recording because this comment used to claim
        // the opposite.** It cited a recorded measurement in this repository's
        // notes that a background job gets a null dictionary — and that does
        // not reproduce: a Background-domain shell and an SSH login both
        // return non-null. The convention files are being corrected separately.
        // Apple documents NULL for a process with no Quartz GUI session at all,
        // so the arm is right in shape; it is a bound on genuine ignorance
        // rather than on an observed behaviour, and its cap is short for that
        // reason.
        //
        // Nor is the daemon case the only way to get NULL. Chromium's
        // curtain_mode_mac.cc documents a *persistent* null for a process that
        // did have a GUI session — reached past an early return for
        // getuid() == 0, so a genuine user session — with "the only known
        // remedy is logout or reboot", unexplained for years and tied to
        // neither sleep nor lid. That is a further argument for this arm's cap
        // being short rather than long.
        //
        // A throw still answers Unlocked rather than Locked, unchanged from
        // before: never let a probe be the reason the app doesn't start. The
        // much longer wait on a reported lock makes that choice matter more,
        // not less — a probe that threw every time would otherwise park
        // startup for half a day before it got anywhere.
        public static ScreenLockState ProbeState()
        {
            if (!OperatingSystem.IsMacOS()) return ScreenLockState.Unlocked;

            var session = IntPtr.Zero;
            var key = IntPtr.Zero;

            try
            {
                session = CGSessionCopyCurrentDictionary();
                if (session == IntPtr.Zero) return ScreenLockState.NoWindowServerSession;

                key = CFStringCreateWithCString(IntPtr.Zero, "CGSSessionScreenIsLocked",
                    KCFStringEncodingUtf8);
                if (key == IntPtr.Zero) return ScreenLockState.Unlocked;

                var value = CFDictionaryGetValue(session, key);
                return value != IntPtr.Zero && CFBooleanGetValue(value)
                    ? ScreenLockState.Locked
                    : ScreenLockState.Unlocked;
            }
            catch
            {
                return ScreenLockState.Unlocked;
            }
            finally
            {
                // CFDictionaryGetValue returns a borrowed reference, so only the
                // two things we created ourselves get released.
                if (key != IntPtr.Zero) CFRelease(key);
                if (session != IntPtr.Zero) CFRelease(session);
            }
        }

        // Block until there's a display worth drawing on, then let startup carry
        // on. Waiting is the right behaviour rather than a compromise: nobody
        // can see a menu-bar icon on a locked screen, so there is nothing to
        // lose by being late, and everything Buddy does without a display —
        // PeerSessions' plain Timers, OpenClawSessions' and
        // ClaudeCloudSessions' Task.Run loops — is already started ahead of
        // this point by serveOnLaunch and keeps running with no dispatcher.
        // ScreenLockWait's header has the full list and says why ServePump is
        // not on it.
        //
        // Returns void, and that is the fix as much as the tri-state is. It
        // used to return "the cap expired while still locked", which
        // Program.cs wired into Startup.Run's `Action waitForUnlock` — so the
        // answer was discarded by the signature, silently, with nothing at the
        // call site to suggest a value existed. Under ScreenLockWait's rule
        // there is no longer any answer to return: every arm ends in "start",
        // whether because the screen unlocked or because the cap for whatever
        // state it is in has run out. The old bool existed only to say "I gave
        // up, start anyway", and a caller was always going to start anyway.
        // A void return cannot be dropped.
        //
        // Deliberately *not* a Func<bool> short-circuit in the shape CB-178
        // gave claimSingleInstance. That fits a duplicate instance, where
        // exiting 0 is the correct end state. Here it would be the opposite:
        // the launch agent is KeepAlive{SuccessfulExit:false}, so a clean exit
        // is exactly what stops Buddy coming back, and a `false` arm would sit
        // in the code as a route to being silently absent. There is no such
        // answer to model, so the type does not offer one.
        public static void WaitForUnlock(TimeSpan cap, TimeSpan lockedCap, TimeSpan interval) =>
            ScreenLockWait.Wait(
                probe: ProbeState,
                now: () => DateTime.UtcNow,
                sleep: Thread.Sleep,
                cap: cap,
                lockedCap: lockedCap,
                interval: interval);
    }
}
