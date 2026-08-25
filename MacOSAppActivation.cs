using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ClaudeBuddy
{
    // Talking to one *instance* of an app rather than to its bundle.
    //
    // `open -a` and `tell application "Claude"` both address the bundle, so
    // with several Claude Desktop instances running they pick whichever one
    // macOS feels like — useless for a profile switcher. NSRunningApplication
    // is addressed by pid, which is exactly what the profile scan hands us.
    //
    // Every method here must be called on the UI thread. That's AppKit's main
    // thread, which has an autorelease pool around each turn of the run loop;
    // +runningApplicationWithProcessIdentifier: returns an autoreleased object,
    // and a bare Task.Run thread has no pool to put it in.
    // Excluded from coverage: objc_msgSend into NSRunningApplication, looked up
    // by pid. Activating, terminating and asking whether an app died are all
    // answers AppKit gives about a real running app, and a headless runner has
    // no AppKit session to give them.
    [ExcludeFromCodeCoverage]
    internal static class MacOSAppActivation
    {
        private const string Libobjc = "/usr/lib/libobjc.A.dylib";

        // NSApplicationActivateAllWindows. Deliberately *not* 3
        // (| NSApplicationActivateIgnoringOtherApps): that flag has been a
        // no-op since macOS 14, so passing it just adds noise.
        private const ulong ActivateAllWindows = 1UL;

        [DllImport(Libobjc)]
        private static extern IntPtr objc_getClass(string name);

        [DllImport(Libobjc)]
        private static extern IntPtr sel_registerName(string name);

        // +runningApplicationWithProcessIdentifier: is a class method
        // ("@20@0:8i16"), so the receiver is the class pointer, and pid_t is a
        // 32-bit int — not an IntPtr.
        [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_pid(IntPtr receiver, IntPtr selector, int pid);

        // Objective-C BOOL is one byte. A bare C# bool marshals as a 4-byte
        // Win32 BOOL, which would read three bytes of whatever else happened to
        // be in the return register — hence U1 on every BOOL-returning import.
        [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool objc_msgSend_bool(IntPtr receiver, IntPtr selector);

        [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool objc_msgSend_bool_ulong(IntPtr receiver, IntPtr selector, ulong arg);

        // Brings that one instance forward. False if the pid isn't a running
        // application (it exited between the scan and the click).
        public static bool Activate(int pid)
        {
            var app = AppForPid(pid);
            if (app == IntPtr.Zero) return false;

            return objc_msgSend_bool_ulong(app, sel_registerName("activateWithOptions:"), ActivateAllWindows);
        }

        // -terminate sends a quit Apple Event, so it is gated by Automation
        // permission and can legitimately be refused by the target (Claude
        // Desktop's Cowork VM and local-agent sessions can veto a quit). The
        // BOOL return is the only signal either way, so callers must check it.
        public static bool Terminate(int pid)
        {
            var app = AppForPid(pid);
            if (app == IntPtr.Zero) return false;

            return objc_msgSend_bool(app, sel_registerName("terminate"));
        }

        // The escalation, only ever behind a second deliberate click.
        public static bool ForceTerminate(int pid)
        {
            var app = AppForPid(pid);
            if (app == IntPtr.Zero) return false;

            return objc_msgSend_bool(app, sel_registerName("forceTerminate"));
        }

        public static bool IsTerminated(int pid)
        {
            var app = AppForPid(pid);
            if (app == IntPtr.Zero) return true;

            return objc_msgSend_bool(app, sel_registerName("isTerminated"));
        }

        private static IntPtr AppForPid(int pid)
        {
            if (!OperatingSystem.IsMacOS() || pid <= 0) return IntPtr.Zero;

            var cls = objc_getClass("NSRunningApplication");
            if (cls == IntPtr.Zero) return IntPtr.Zero;

            return objc_msgSend_pid(cls, sel_registerName("runningApplicationWithProcessIdentifier:"), pid);
        }
    }
}
