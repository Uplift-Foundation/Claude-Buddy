using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ClaudeBuddy
{
    // Is the login session's screen locked?
    //
    // This exists for one reason: Avalonia's macOS render timer is a
    // CVDisplayLink, and CVDisplayLinkStart fails with -6661
    // (kCVReturnInvalidDisplay) while the screen is locked, which aborts the app
    // during startup rather than degrading. Launching into a locked screen is not
    // an edge case — a Login Item starts before you type your password, so every
    // reboot is this scenario.
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
        // The null-dictionary arm is not a guess either: a background job with
        // no window server session is already a recorded measurement in this
        // repository's own notes, which is where the "reads null as locked"
        // behaviour this change corrects was written down. The one reading
        // that has *not* been observed is the key present and true, because
        // observing it means locking the machine somebody is using. It is what
        // this code and Apple's documented key both assume, and it is an
        // assumption.
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
