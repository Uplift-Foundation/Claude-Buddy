using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace ClaudeBuddy
{
    // Avalonia doesn't expose NSWindow.collectionBehavior, so set it through
    // the native handle: orbs should follow you across Spaces and still show
    // alongside full-screen apps.
    // Excluded from coverage: patches AppKit classes at runtime through the
    // Objective-C runtime — sel_registerName, class_replaceMethod and
    // objc_msgSend against a real NSWindow. The `if (!OperatingSystem.IsMacOS())
    // return;` guards at the top of each method are the only lines a non-macOS
    // test ever reached, and a guard returning early is not the behaviour worth
    // measuring.
    [ExcludeFromCodeCoverage]
    internal static class MacOSWindowExtensions
    {
        private const ulong CanJoinAllSpaces = 1UL << 0;    // NSWindowCollectionBehaviorCanJoinAllSpaces
        private const ulong FullScreenAuxiliary = 1UL << 8; // NSWindowCollectionBehaviorFullScreenAuxiliary

        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern IntPtr sel_registerName(string name);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, ulong arg);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_get(IntPtr receiver, IntPtr selector);

        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern IntPtr object_getClass(IntPtr obj);

        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern IntPtr class_replaceMethod(IntPtr cls, IntPtr name, IntPtr imp,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

        public static void ShowOnAllSpaces(this Window window)
        {
            if (!OperatingSystem.IsMacOS()) return;

            if (window.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle mac && mac.NSWindow != IntPtr.Zero)
            {
                objc_msgSend(mac.NSWindow, sel_registerName("setCollectionBehavior:"),
                    CanJoinAllSpaces | FullScreenAuxiliary);
            }
        }

        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_void(IntPtr receiver, IntPtr selector);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool objc_msgSend_bool(IntPtr receiver, IntPtr selector);

        // Wait for macOS to finish making *us* the active app before trying to
        // activate anything else.
        //
        // Clicking an orb activates Claude Buddy — it is a click on one of its
        // windows, and macOS asks no further questions. That activation is
        // asynchronous, and it lands after the click handler has already run
        // its tmux queries and told the terminal to come forward: the terminal
        // arrives, our activation completes on top of it, and the terminal goes
        // straight back. Reported as "single click does nothing, double click
        // works", and confirmed by watching the terminal flicker forward and
        // return on a single click.
        //
        // A double click works for a reason worth stating: by the second click
        // the app is *already* active, so that click starts no new activation,
        // and the terminal's own stays. Waiting here puts a single click in the
        // same position.
        //
        // Deliberately not `[NSApp deactivate]`, which was the first attempt:
        // giving up active status hands it to whatever app was frontmost on the
        // desktop you were looking at, and macOS follows that app's windows —
        // pulling you back to where you started, the opposite of the point.
        //
        // The wait is capped, and being already active returns immediately, so
        // the common case — clicking an orb while the app is active — costs
        // nothing at all.
        private const int ActivationSettleMs = 600;
        private const int ActivationPollMs = 20;

        public static void WaitForOwnActivation()
        {
            if (!OperatingSystem.IsMacOS()) return;

            try
            {
                var app = objc_msgSend_get(
                    objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                if (app == IntPtr.Zero) return;

                var isActive = sel_registerName("isActive");

                for (var waited = 0; waited < ActivationSettleMs; waited += ActivationPollMs)
                {
                    if (objc_msgSend_bool(app, isActive)) return;
                    Thread.Sleep(ActivationPollMs);
                }

                // Never became active — nothing to wait for, and nothing that
                // is going to steal the terminal back either.
            }
            catch
            {
                // Focusing is a convenience; never let it take the app down.
            }
        }

        // --- first-click delivery ---------------------------------------------
        //
        // macOS swallows the click that activates an inactive app: the window
        // under the pointer comes forward, but its view never sees the
        // mouseDown unless it answers YES to acceptsFirstMouse:. Avalonia's
        // AvnView doesn't, and Claude Buddy is a background app that is almost
        // never the active one — so clicking an orb *did nothing the first
        // time* and only worked on a second click. Reported as "it needs a
        // double click across desktops", which is exactly the shape of this
        // rule: coming from another Space, the app is never already active, so
        // the first click is always the one that gets eaten.
        //
        // Avalonia exposes no hook for it, so the answer is installed onto its
        // view class directly. That reaches every window this app owns, which
        // is what we want — an orb, and the settings window's controls, should
        // both respond to the click you actually made. Done once: the class is
        // shared, and re-installing per window would be the same write repeated.
        //
        // Deliberately not a swizzle that chains to the original: there is
        // nothing worth calling through to. The method is a constant.
        private static bool _firstMouseInstalled;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte AcceptsFirstMouseFn(IntPtr self, IntPtr selector, IntPtr theEvent);

        // Held in a static so the GC can't collect the thunk the runtime is
        // still calling — the classic way this kind of interop dies later, at a
        // moment unrelated to its cause.
        private static readonly AcceptsFirstMouseFn AlwaysYes = (_, _, _) => 1;

        public static void AcceptFirstClick(this Window window)
        {
            if (!OperatingSystem.IsMacOS() || _firstMouseInstalled) return;

            if (window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle mac
                || mac.NSWindow == IntPtr.Zero)
            {
                return;
            }

            var view = objc_msgSend_get(mac.NSWindow, sel_registerName("contentView"));
            if (view == IntPtr.Zero) return;

            var cls = object_getClass(view);
            if (cls == IntPtr.Zero) return;

            // "c@:@" — returns char (Objective-C BOOL), takes self, _cmd, and
            // the NSEvent. class_replaceMethod covers both cases: it adds the
            // method when the class doesn't implement it and overwrites it when
            // it does, so it doesn't matter which Avalonia does.
            class_replaceMethod(cls, sel_registerName("acceptsFirstMouse:"),
                Marshal.GetFunctionPointerForDelegate(AlwaysYes), "c@:@");

            _firstMouseInstalled = true;
        }
    }
}
