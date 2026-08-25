using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ClaudeBuddy
{
    // On-screen window frames per process, and which app is frontmost.
    //
    // CGWindowListCopyWindowInfo gives bounds and owner pid with no TCC prompt —
    // only window *titles* and images need Screen Recording, and this never asks
    // for either. That's what makes tinting a window from outside possible
    // without a permission dialog.
    //
    // Frames come back in CoreGraphics global coordinates: points, origin at the
    // top-left of the main display. Avalonia window positions are physical
    // pixels, so callers scale.
    // Excluded from coverage: CGWindowListCopyWindowInfo. The whole file is a
    // query against the window server's own list of on-screen windows, which is
    // exactly what a headless runner does not have — and reading geometry back
    // out of the window server is the reason it exists (see CLAUDE.md on
    // checking where an orb actually sat).
    [ExcludeFromCodeCoverage]
    internal static class MacOSWindowList
    {
        private const string CoreGraphics =
            "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        private const string CoreFoundation =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        private const string Objc = "/usr/lib/libobjc.A.dylib";

        public readonly record struct WindowFrame(
            uint WindowId, double X, double Y, double Width, double Height);

        [StructLayout(LayoutKind.Sequential)]
        private struct CGRect
        {
            public double X, Y, Width, Height;
        }

        private const uint OnScreenOnly = 1;              // kCGWindowListOptionOnScreenOnly
        private const uint ExcludeDesktopElements = 1 << 4; // kCGWindowListExcludeDesktopElements
        private const uint KCFStringEncodingUtf8 = 0x08000100;
        private const int KCFNumberIntType = 9;

        [DllImport(CoreGraphics)]
        private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

        [DllImport(CoreGraphics)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dictionary, out CGRect rect);

        [DllImport(CoreFoundation)]
        private static extern long CFArrayGetCount(IntPtr array);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr reference);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

        [DllImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool CFNumberGetValue(IntPtr number, int type, out int value);

        [DllImport(Objc)]
        private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Objc)]
        private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        private static extern IntPtr msgSend(IntPtr receiver, IntPtr selector);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        private static extern int msgSend_i32(IntPtr receiver, IntPtr selector);

        // The dictionary keys are ordinary CFStrings, and CFDictionary compares
        // keys with CFEqual, so strings we make ourselves match the framework's
        // constants without having to dlsym them.
        private static readonly Lazy<IntPtr> KeyOwnerPid = Key("kCGWindowOwnerPID");
        private static readonly Lazy<IntPtr> KeyBounds = Key("kCGWindowBounds");
        private static readonly Lazy<IntPtr> KeyLayer = Key("kCGWindowLayer");
        private static readonly Lazy<IntPtr> KeyNumber = Key("kCGWindowNumber");

        private static Lazy<IntPtr> Key(string name) =>
            new(() => CFStringCreateWithCString(IntPtr.Zero, name, KCFStringEncodingUtf8));

        // Normal document windows of one process. Layer 0 filters out panels,
        // sheets and the like, which would otherwise each get their own tint.
        public static List<WindowFrame> ForPid(int pid)
        {
            var frames = new List<WindowFrame>();
            if (!OperatingSystem.IsMacOS() || pid <= 0) return frames;

            var list = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktopElements, 0);
            if (list == IntPtr.Zero) return frames;

            try
            {
                var count = CFArrayGetCount(list);
                for (long i = 0; i < count; i++)
                {
                    var window = CFArrayGetValueAtIndex(list, i);
                    if (window == IntPtr.Zero) continue;

                    if (!TryInt(window, KeyOwnerPid.Value, out var owner) || owner != pid) continue;
                    if (TryInt(window, KeyLayer.Value, out var layer) && layer != 0) continue;

                    var bounds = CFDictionaryGetValue(window, KeyBounds.Value);
                    if (bounds == IntPtr.Zero) continue;
                    if (!CGRectMakeWithDictionaryRepresentation(bounds, out var rect)) continue;
                    if (rect.Width < 80 || rect.Height < 80) continue; // ignore slivers

                    TryInt(window, KeyNumber.Value, out var number);
                    frames.Add(new WindowFrame((uint)number, rect.X, rect.Y, rect.Width, rect.Height));
                }
            }
            finally
            {
                CFRelease(list);
            }

            return frames;
        }

        private static bool TryInt(IntPtr dictionary, IntPtr key, out int value)
        {
            value = 0;
            var number = CFDictionaryGetValue(dictionary, key);
            return number != IntPtr.Zero && CFNumberGetValue(number, KCFNumberIntType, out value);
        }

        // NSWorkspace.frontmostApplication.processIdentifier — the overlay only
        // shows for the instance actually in front, so it can never sit on top of
        // an unrelated app's window.
        public static int FrontmostPid()
        {
            if (!OperatingSystem.IsMacOS()) return 0;

            try
            {
                var workspace = msgSend(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
                if (workspace == IntPtr.Zero) return 0;

                var app = msgSend(workspace, sel_registerName("frontmostApplication"));
                if (app == IntPtr.Zero) return 0;

                return msgSend_i32(app, sel_registerName("processIdentifier"));
            }
            catch
            {
                return 0;
            }
        }
    }
}
