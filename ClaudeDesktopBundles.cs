using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClaudeBuddy
{
    // Per-profile Dock icons, by giving each profile its own copy of Claude.app
    // with a tinted icon.
    //
    // The copy is an APFS clone (cp -Rc), so a 753 MB bundle costs ~0 disk and
    // ~0.3s. The icon is a *custom Finder icon*, which lives in an "Icon\r" file
    // at the bundle root and a com.apple.FinderInfo xattr — both OUTSIDE
    // Contents/, which is what the code signature seals. That is the whole trick:
    //
    //   codesign --verify        passes
    //   spctl assessment         still "Notarized Developer ID"
    //   CDHash                   byte-identical to Anthropic's original
    //
    // An identical CDHash is why this is safe to do: the running code identity is
    // unchanged, so the "Claude Safe Storage" keychain ACL still matches (stored
    // logins keep decrypting) and existing TCC grants still apply. Re-signing the
    // bundle — which is what modifying Info.plist would force — is what breaks
    // all of that. So the bundle name and CFBundleName are deliberately left
    // alone; every clone still calls itself "Claude", and colour is the whole
    // identity signal. Only `codesign --verify --strict` objects, over the xattr.
    //
    // Each clone is named exactly "Claude.app" inside a per-profile parent
    // directory, because MacOSProcessScan matches the main process on the path
    // suffix "/Claude.app/Contents/MacOS/Claude". Naming the bundle after the
    // profile would silently break running-detection for every cloned instance.
    internal static class ClaudeDesktopBundles
    {
        // False when the last icon write was refused by macOS.
        public static bool IconApplied { get; private set; } = true;

        private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // A cache, not configuration: everything here is regenerable from
        // /Applications/Claude.app and the profile list, and deleting it only
        // costs the coloured icons.
        //
        // CLAUDE_BUDDY_BUNDLE_ROOT redirects it, the same scratch-override
        // pattern as CLAUDE_BUDDY_PROFILE_ROOT in ClaudeDesktopManager and
        // CLAUDE_BUDDY_SETTINGS_DIR in ClaudeBuddySettings. Without it the only
        // way to test what is in this file is to write into the real
        // ~/Library/Application Support/ClaudeBuddy/bundles — the actual cache,
        // on the machine running the tests, holding real cloned .app bundles
        // whose icons a user is looking at. That the override did not exist is
        // why nothing here was covered.
        public static string Root =>
            Environment.GetEnvironmentVariable("CLAUDE_BUDDY_BUNDLE_ROOT") is { Length: > 0 } scratch
                ? scratch
                : Path.Combine(
                    Home, "Library", "Application Support", "ClaudeBuddy", "bundles");

        public static string DirectoryFor(string profileFolder) => Path.Combine(Root, profileFolder);

        public static string PathFor(string profileFolder) =>
            Path.Combine(DirectoryFor(profileFolder), "Claude.app");

        public static bool Exists(string profileFolder) =>
            OperatingSystem.IsMacOS() && Directory.Exists(PathFor(profileFolder));

        // Returns the clone's path, creating or refreshing it as needed, or null
        // if anything went wrong — callers fall back to launching the real bundle,
        // so a failure here costs the colour and nothing else.
        // Excluded from coverage: copies a real .app bundle on disk and shells out
        // to codesign.
        [ExcludeFromCodeCoverage]
        public static string? Ensure(string profileFolder, string sourceApp, Color tint)
        {
            if (!OperatingSystem.IsMacOS()) return null;

            try
            {
                var clone = PathFor(profileFolder);

                if (Directory.Exists(clone)
                    && !IsStale(clone, sourceApp)
                    && ColourMatches(profileFolder, tint))
                {
                    return clone;
                }

                Directory.CreateDirectory(DirectoryFor(profileFolder));
                if (Directory.Exists(clone)) DeleteDirectory(clone);

                // -c asks for a clonefile(2) copy; without it this would really
                // copy 753 MB.
                if (!Run("/bin/cp", "-Rc", sourceApp, clone)) return null;
                if (!Directory.Exists(clone)) return null;

                ApplyTintedIcon(clone, sourceApp, profileFolder, tint);
                return clone;
            }
            catch
            {
                return null;
            }
        }

        // Squirrel updates /Applications/Claude.app only, so clones go stale and
        // would keep running an old version indefinitely. Compare bundle versions
        // rather than mtimes, which cp -Rc preserves.
        // Excluded from coverage: compares bundle versions read by plutil.
        [ExcludeFromCodeCoverage]
        private static bool IsStale(string clone, string sourceApp)
        {
            var cloneVersion = BundleVersion(clone);
            var sourceVersion = BundleVersion(sourceApp);
            if (cloneVersion is null || sourceVersion is null) return true;
            return !string.Equals(cloneVersion, sourceVersion, StringComparison.Ordinal);
        }

        internal static bool ColourMatches(string profileFolder, Color tint)
        {
            try
            {
                var marker = Path.Combine(DirectoryFor(profileFolder), "icon-colour");
                if (!File.Exists(marker)) return false;
                if (File.ReadAllText(marker).Trim() != tint.ToString()) return false;

                // The marker says which colour was *intended*; the Icon\r file
                // is whether one actually went on. Checking both is what repairs
                // a clone left behind by the older ordering bug, which wrote the
                // marker before calling NSWorkspace setIcon: and so recorded a
                // refusal as a success. Those clones cannot heal on their own —
                // the marker matches for ever, so Ensure() never rebuilds and
                // the tint never retries even once the user grants App
                // Management.
                //
                // Cheap: one File.Exists on a path we already have.
                return HasCustomIcon(PathFor(profileFolder));
            }
            catch
            {
                return false;
            }
        }

        // A custom Finder icon lives in a file named "Icon" followed by a
        // carriage return at the bundle root — outside Contents/, which is what
        // keeps the code signature intact. Its absence is the only reliable
        // evidence that setIcon: did not take: the FinderInfo xattr can be left
        // set with no icon resource behind it, which is exactly the state a
        // refused write leaves.
        // Excluded from coverage for its catch, which is not reachable: on both
        // platforms File.Exists answers false for a path it cannot evaluate
        // rather than throwing — including, on Windows, one containing the
        // carriage return this looks for.
        //
        // Kept because the path is built from a profile folder name, which is a
        // directory on disk rather than anything this app validates, and because
        // the cost of being wrong is an exception on the scan path rather than a
        // missing icon. What it answers is covered both ways —
        // ClaudeDesktopBundleIconTests for the name, BundleCacheLayoutTests for
        // what ColourMatches does with it.
        [ExcludeFromCodeCoverage]
        internal static bool HasCustomIcon(string bundlePath)
        {
            try { return File.Exists(Path.Combine(bundlePath, "Icon\r")); }
            catch { return false; }
        }

        public static bool IsStaleFor(string profileFolder, string sourceApp) =>
            Exists(profileFolder) && IsStale(PathFor(profileFolder), sourceApp);

        // Excluded from coverage: reads a binary Info.plist through plutil.
        [ExcludeFromCodeCoverage]
        private static string? BundleVersion(string appPath) =>
            PlistValue(Path.Combine(appPath, "Contents", "Info.plist"), "CFBundleVersion");

        // Excluded from coverage: runs plutil against a real plist.
        [ExcludeFromCodeCoverage]
        private static string? PlistValue(string plist, string key)
        {
            // Info.plist is a binary plist; plutil reads either form.
            return RunCapture("/usr/bin/plutil", "-extract", key, "raw", "-o", "-", plist)?.Trim();
        }

        // Excluded from coverage: unpacks an .icns with iconutil and writes it
        // back into a bundle; the pixel maths it calls is WriteTinted, which is
        // tested.
        [ExcludeFromCodeCoverage]
        private static void ApplyTintedIcon(string clone, string sourceApp, string profileFolder, Color tint)
        {
            var work = DirectoryFor(profileFolder);
            var iconFile = PlistValue(Path.Combine(sourceApp, "Contents", "Info.plist"), "CFBundleIconFile")
                           ?? "electron";
            if (!iconFile.EndsWith(".icns", StringComparison.OrdinalIgnoreCase)) iconFile += ".icns";

            var source = Path.Combine(sourceApp, "Contents", "Resources", iconFile);
            if (!File.Exists(source)) return;

            var flat = Path.Combine(work, "icon-source.png");
            var tinted = Path.Combine(work, "icon-tinted.png");

            // 512 is plenty for a Dock tile and keeps the pixel pass quick.
            if (!Run("/usr/bin/sips", "-s", "format", "png", "-Z", "512", source, "--out", flat)) return;

            WriteTinted(flat, tinted, tint);

            // A false here means macOS refused the write — see the note on
            // Retint. Worth knowing about rather than leaving the user with a
            // wrong-coloured Dock tile and no explanation.
            IconApplied = MacOSCustomIcon.Set(tinted, clone);

            // Record what colour this clone was built with — but only if the
            // icon actually went on. Ensure() treats a *matching* marker as
            // "nothing to do", so writing it before the call above recorded a
            // refusal as a success: the clone was then considered correctly
            // coloured for ever, and the tint was never retried even after the
            // user granted App Management. That is not hypothetical — it is how
            // this was found, with a marker dated minutes earlier sitting beside
            // a bundle that had no Icon\r file and no FinderInfo xattr at all.
            //
            // Leaving the marker absent instead costs one clone rebuild on the
            // next launch, which is an APFS clone: ~0.3s and ~0 disk.
            if (IconApplied)
            {
                try { File.WriteAllText(Path.Combine(work, "icon-colour"), tint.ToString()); } catch { }
            }

            try { File.Delete(flat); } catch { }
        }

        // Recolours by luminance: dark pixels go toward the tint, light pixels
        // toward white, alpha untouched. Keeps Claude's mark legible instead of
        // flat-filling it, and preserves the rounded-corner alpha that makes it
        // look like a real app icon.
        // internal: pure pixel maths over two files, and the one part of this
        // file that decides what the user actually sees. The comment inside about
        // undoing premultiplication is the kind of claim worth a test — muddy
        // icon edges are hard to notice and impossible to attribute.
        internal static void WriteTinted(string sourcePng, string destinationPng, Color tint)
        {
            using var source = new Bitmap(sourcePng);
            var size = source.PixelSize;
            var stride = size.Width * 4;
            var pixels = new byte[stride * size.Height];

            var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                source.CopyPixels(new PixelRect(0, 0, size.Width, size.Height),
                    pinned.AddrOfPinnedObject(), pixels.Length, stride);

                for (var i = 0; i < pixels.Length; i += 4)
                {
                    var alpha = pixels[i + 3];
                    if (alpha == 0) continue;

                    // Bitmaps arrive premultiplied; undo that before touching the
                    // colour or the edges of the icon come out muddy.
                    var scale = 255.0 / alpha;
                    var c0 = pixels[i] * scale;
                    var c1 = pixels[i + 1] * scale;
                    var c2 = pixels[i + 2] * scale;

                    var luma = (c2 * 299 + c1 * 587 + c0 * 114) / 1000.0;

                    double r, g, b;
                    if (luma < 128)
                    {
                        var k = luma / 128.0;
                        r = tint.R * k;
                        g = tint.G * k;
                        b = tint.B * k;
                    }
                    else
                    {
                        var k = (luma - 128) / 127.0;
                        r = tint.R + (255 - tint.R) * k;
                        g = tint.G + (255 - tint.G) * k;
                        b = tint.B + (255 - tint.B) * k;
                    }

                    var premul = alpha / 255.0;
                    pixels[i] = (byte)Math.Clamp(b * premul, 0, 255);
                    pixels[i + 1] = (byte)Math.Clamp(g * premul, 0, 255);
                    pixels[i + 2] = (byte)Math.Clamp(r * premul, 0, 255);
                }
            }
            finally
            {
                pinned.Free();
            }

            using var output = new WriteableBitmap(size, source.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var frame = output.Lock())
            {
                Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
            }

            output.Save(destinationPng);
        }

        // Changing a profile's colour has to change its Dock icon, and the icon
        // is baked into the clone when the clone is made.
        //
        // Re-setting the icon on an existing bundle is what you'd expect to do,
        // and it does not work: on current macOS, writing into an app bundle you
        // did not just create trips the *App Management* privacy permission, and
        // NSWorkspace.setIcon fails — silently, leaving the FinderInfo flag set
        // with no Icon resource behind it, so the Dock keeps showing a stale
        // cached icon. Rebuilding the clone instead re-runs the path that already
        // works (create, then set the icon on something we just made) and needs no
        // permission. An APFS clone costs ~0.3s and ~0 disk, so this is cheap.
        // Excluded from coverage: rewrites a real bundle icon.
        [ExcludeFromCodeCoverage]
        public static bool Retint(string profileFolder, string sourceApp, Color tint)
        {
            if (!OperatingSystem.IsMacOS()) return false;
            if (!Exists(profileFolder)) return false;

            try
            {
                Remove(profileFolder);
                return Ensure(profileFolder, sourceApp, tint) is not null;
            }
            catch
            {
                return false;
            }
        }

        // Excluded from coverage: deletes a real bundle from disk.
        [ExcludeFromCodeCoverage]
        public static void Remove(string profileFolder)
        {
            if (!OperatingSystem.IsMacOS()) return;

            // Unregister before deleting, while the path still resolves.
            //
            // Deleting the directory does not remove the bundle from the
            // LaunchServices database, and a clone claims `claude:` and the
            // MSAL sign-in scheme exactly as the real Claude.app does. A
            // registration for a bundle that no longer exists therefore stays
            // in the running for those schemes indefinitely — the machine this
            // was found on still listed bundles/Claude-Profile-1 months after
            // the directory went away — which is what made the wrong-profile
            // behaviour look intermittent rather than deterministic.
            Unregister(PathFor(profileFolder));

            try { DeleteDirectory(DirectoryFor(profileFolder)); } catch { }
        }

        // lsregister is not API and has no supported equivalent: the public
        // LaunchServices surface can register a bundle
        // (LSRegisterURL) but has never been able to remove one. It has lived
        // at this path since 10.5, and a failure here costs a stale database
        // entry rather than anything the user can see immediately, so it is
        // best-effort by design.
        private static readonly string LsRegister =
            "/System/Library/Frameworks/CoreServices.framework/Frameworks/"
            + "LaunchServices.framework/Support/lsregister";

        internal static void Unregister(string bundlePath)
        {
            if (!OperatingSystem.IsMacOS()) return;
            if (!File.Exists(LsRegister)) return;

            try { Run(LsRegister, "-u", bundlePath); } catch { }
        }

        // Excluded from coverage: deletes a real directory tree.
        [ExcludeFromCodeCoverage]
        private static void DeleteDirectory(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }

        // ---- process helpers (local, same reasoning as the manager's) --------

        // Excluded from coverage: starts a subprocess.
        [ExcludeFromCodeCoverage]
        private static bool Run(string executable, params string[] arguments) =>
            RunCapture(executable, arguments) is not null;

        // Excluded from coverage: starts a subprocess and reads its output.
        [ExcludeFromCodeCoverage]
        private static string? RunCapture(string executable, params string[] arguments)
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process is null) return null;

                // Both reads in flight before the wait, or the timeout is
                // unreachable and a chatty child can deadlock on stderr.
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                // cp -Rc of a 753 MB bundle is a clone, not a copy, so this is
                // generous rather than tight.
                if (!process.WaitForExit(20_000))
                {
                    try { process.Kill(true); } catch { }
                    return null;
                }

                var text = stdout.GetAwaiter().GetResult();
                stderr.GetAwaiter().GetResult();
                return process.ExitCode == 0 ? text : null;
            }
            catch
            {
                return null;
            }
        }
    }

    // [[NSWorkspace sharedWorkspace] setIcon:forFile:options:] — the supported
    // way to set a custom Finder icon, and the only part of this that touches
    // the bundle at all.
    // Excluded from coverage, as a class: every member is either a DllImport of
    // objc_msgSend or the one method that calls them. Set() allocates an NSImage
    // from a path and asks NSWorkspace's sharedWorkspace to
    // setIcon:forFile:options: — there is no NSWorkspace under a headless runner,
    // and on Windows the whole class is unreachable behind Set()'s own IsMacOS
    // guard, which is the one line of it a test can observe.
    [ExcludeFromCodeCoverage]
    internal static class MacOSCustomIcon
    {
        private const string Objc = "/usr/lib/libobjc.A.dylib";

        [DllImport(Objc)]
        private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Objc)]
        private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        private static extern IntPtr msgSend(IntPtr receiver, IntPtr selector);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        private static extern IntPtr msgSend_str(IntPtr receiver, IntPtr selector,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        private static extern IntPtr msgSend_ptr(IntPtr receiver, IntPtr selector, IntPtr arg);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool msgSend_setIcon(IntPtr receiver, IntPtr selector,
            IntPtr image, IntPtr file, ulong options);

        public static bool Set(string imagePath, string targetPath)
        {
            if (!OperatingSystem.IsMacOS()) return false;

            try
            {
                var nsString = objc_getClass("NSString");
                var fromUtf8 = sel_registerName("stringWithUTF8String:");

                var imageNs = msgSend_str(nsString, fromUtf8, imagePath);
                var targetNs = msgSend_str(nsString, fromUtf8, targetPath);
                if (imageNs == IntPtr.Zero || targetNs == IntPtr.Zero) return false;

                var image = msgSend(objc_getClass("NSImage"), sel_registerName("alloc"));
                image = msgSend_ptr(image, sel_registerName("initWithContentsOfFile:"), imageNs);
                if (image == IntPtr.Zero) return false;

                var workspace = msgSend(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
                if (workspace == IntPtr.Zero) return false;

                return msgSend_setIcon(workspace, sel_registerName("setIcon:forFile:options:"),
                    image, targetNs, 0);
            }
            catch
            {
                return false;
            }
        }
    }
}
