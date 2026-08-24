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
    // context), which is treated the same as locked: either way there's no
    // display to drive.
    // Excluded from coverage: CGSessionCopyCurrentDictionary, which reports
    // whether the login session's screen is locked. A CI runner has no
    // interactive session for the answer to be about, and WaitForUnlock is a
    // poll around it that would exit immediately with nothing asserted.
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

        // Null dictionary, or the key present and true. A missing key means
        // unlocked — the key is only published while the screen is locked.
        public static bool IsScreenLocked()
        {
            if (!OperatingSystem.IsMacOS()) return false;

            var session = IntPtr.Zero;
            var key = IntPtr.Zero;

            try
            {
                session = CGSessionCopyCurrentDictionary();
                if (session == IntPtr.Zero) return true;

                key = CFStringCreateWithCString(IntPtr.Zero, "CGSSessionScreenIsLocked",
                    KCFStringEncodingUtf8);
                if (key == IntPtr.Zero) return false;

                var value = CFDictionaryGetValue(session, key);
                return value != IntPtr.Zero && CFBooleanGetValue(value);
            }
            catch
            {
                // Never let a probe be the reason the app doesn't start.
                return false;
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
        // on. Returns false if the cap expired while still locked — the caller
        // starts anyway, because a wrong answer here must not be able to keep the
        // app from ever running. Waiting is the right behaviour rather than a
        // compromise: nobody can see a menu-bar icon on a locked screen, so
        // there is nothing to lose by being late, and the alternative is a
        // process that died at login and is silently absent afterwards.
        public static bool WaitForUnlock(TimeSpan cap, TimeSpan interval)
        {
            if (!OperatingSystem.IsMacOS()) return true;
            if (!IsScreenLocked()) return true;

            var deadline = DateTime.UtcNow + cap;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(interval);
                if (!IsScreenLocked()) return true;
            }

            return false;
        }
    }
}
