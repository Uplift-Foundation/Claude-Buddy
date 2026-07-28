using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    internal enum ProfileActivity
    {
        None,
        Launching,
        Quitting,
        ForceQuitOffered,
        Error
    }

    internal sealed record ProfileView(
        string DisplayName,
        string Directory,
        bool IsDefault,
        bool IsRunning,
        int Pid,
        ProfileActivity Activity,
        string? Message,
        string ThemeMode);

    internal sealed record DesktopSnapshot(bool AppInstalled, IReadOnlyList<ProfileView> Profiles);

    // Running several Claude Desktop instances side by side, one per Anthropic
    // account, and switching between them from the status-bar menu.
    //
    // Claude Desktop signs into one account at a time and keeps that login in
    // its userData directory (Cookies -> sessionKey, config.json ->
    // oauth:tokenCache), not the Keychain — so a second account is a second
    // userData directory, selected with CLAUDE_USER_DATA_DIR. The app honours
    // that variable (app.setPath("userData", ...)) and takes no single-instance
    // lock, so instances genuinely can coexist.
    //
    // Everything here is independent of the session-monitoring side of the app:
    // no SessionStatus, no SessionManager, no OrbWindow. The only seam is
    // TrayController calling Digest() and ClaudeDesktopSection.Append().
    internal static class ClaudeDesktopManager
    {
        private const string BundleId = "com.anthropic.claudefordesktop";
        private const string DefaultProfileFolder = "Claude";
        private const string DefaultDisplayName = "Default";

        // Claude Desktop takes seconds to show a window. Without a sticky
        // "Launching…" the user clicks again and gets a second instance on the
        // same directory — the single most likely real-world failure, and the
        // one that corrupts leveldb/SQLite.
        private const int LaunchWindowMs = 30_000;

        // How long a quit is given before the row offers Force quit instead.
        private const int QuitWindowMs = 20_000;

        // How long WM_CLOSE gets before Windows quit escalates to WM_ENDSESSION.
        // Comfortably inside QuitWindowMs, so the escalation happens while the
        // row still says "Quitting…" rather than after it has given up and
        // offered a force quit.
        private const int WindowsQuitEscalationMs = 1_500;
        private const int ForceQuitOfferMs = 60_000;
        private const int ErrorMs = 20_000;

        private const int ProcessTimeoutMs = 5_000;

        // Directories that mark a folder as a real Claude Desktop profile.
        private static readonly string[] MarkerFiles =
            { "config.json", "Cookies", "Local State", "Preferences", "ant-did" };

        private static readonly string[] MarkerDirectories =
            { Path.Combine("Local Storage", "leveldb"), "Crashpad" };

        private sealed record Transient(ProfileActivity Kind, long Deadline, string? Message);

        // DefaultDirectory resolves symlinks, so it touches the filesystem.
        // It's captured here rather than recomputed in Compose, which also runs
        // on the UI thread when a click changes transient state.
        private sealed record ScanResult(
            bool Installed,
            string DefaultDirectory,
            IReadOnlyList<(string Name, string Directory)> Profiles,
            IReadOnlyDictionary<string, int> Running);

        private static readonly Dictionary<string, Transient> Transients = new(StringComparer.Ordinal);
        private static readonly object TransientGate = new();

        // Only ever one launch in flight, so two clicks in the same tick can't
        // both clear the "is it already running" gate below.
        private static readonly SemaphoreSlim LaunchGate = new(1, 1);

        private static volatile DesktopSnapshot _snapshot = new(false, Array.Empty<ProfileView>());
        private static volatile ScanResult? _lastScan;
        private static volatile string _digest = "cd=off";
        private static int _refreshing;

        public static DesktopSnapshot Snapshot => _snapshot;

        private static bool SupportedPlatform => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

        // Folded into TrayController's rebuild signature. Deliberately carries
        // no pid, timestamp or countdown: anything volatile in here would force
        // a menu rebuild on every 2-second tick.
        public static string Digest() => SupportedPlatform ? _digest : "cd=off";

        // %APPDATA% on Windows, ~/Library/Application Support on macOS.
        // Environment.SpecialFolder.ApplicationData already resolves
        // correctly on both — that's how ClaudeBuddySettings.Directory does
        // it — so this only needs a scratch-override branch, not a platform
        // one.
        public static string ProfileRoot =>
            Environment.GetEnvironmentVariable("CLAUDE_BUDDY_PROFILE_ROOT") is { Length: > 0 } scratch
                ? scratch
                : OperatingSystem.IsWindows()
                    ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                    : Path.Combine(Home, "Library", "Application Support");

        private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // ---- refresh -------------------------------------------------------

        // Cheap to call on every poll tick: at most one scan is ever in flight,
        // and the result is only pushed back to the UI when the digest changes,
        // which is what keeps Refresh() from looping back into another scan.
        public static void KickRefresh()
        {
            if (!SupportedPlatform) return;
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;

            Task.Run(() =>
            {
                try { RefreshCore(); }
                catch { /* a stalled network home must never take the tray down */ }
                finally { Volatile.Write(ref _refreshing, 0); }
            });
        }

        private static void RefreshCore()
        {
            var installed = AppInstalled();

            IReadOnlyList<(string Name, string Directory)> profiles =
                installed ? Discover() : Array.Empty<(string Name, string Directory)>();
            IReadOnlyDictionary<string, int> running =
                installed ? MapInstances(ScanProcesses()) : EmptyRunning;

            var scan = new ScanResult(installed, DefaultDirectory(), profiles, running);
            _lastScan = scan;
            Publish(Compose(scan));
        }

        private static IReadOnlyList<ClaudeInstance> ScanProcesses() =>
            OperatingSystem.IsWindows() ? WindowsProcessScan.Scan() : MacOSProcessScan.Scan();

        private static readonly IReadOnlyDictionary<string, int> EmptyRunning =
            new Dictionary<string, int>(StringComparer.Ordinal);

        // Recompose from the last scan without re-scanning — for when a click
        // has changed transient state and the menu should say so immediately.
        private static void Recompose()
        {
            var scan = _lastScan;
            if (scan is null)
            {
                KickRefresh();
                return;
            }

            Publish(Compose(scan));
        }

        // Two writers: the background scan, and Recompose on the UI thread when
        // a click changes transient state. Serialised so the snapshot and the
        // digest can't come from different composes — the tray reads them
        // separately, and a mismatched pair means a menu that doesn't match its
        // own rebuild signature.
        private static readonly object PublishGate = new();

        private static void Publish(DesktopSnapshot next)
        {
            bool changed;

            lock (PublishGate)
            {
                var digest = DigestOf(next);
                changed = digest != _digest;

                _snapshot = next;
                _digest = digest;
            }

            if (changed) Dispatcher.UIThread.Post(() => TrayController.Instance?.Refresh());
        }

        private static string DigestOf(DesktopSnapshot snapshot)
        {
            if (!snapshot.AppInstalled) return "cd=off";

            return "cd=" + string.Join(",", snapshot.Profiles
                .Select(p =>
                {
                    // Settings-derived values belong in here too: change a colour
                    // or hide a swatch and the menu has to repaint, which it only
                    // does when this string changes.
                    var folder = Path.GetFileName(p.Directory);
                    var settings = ClaudeBuddySettings.For(folder);
                    var colour = ClaudeDesktopColors.NameFor(folder, p.IsDefault);
                    return $"{p.DisplayName}:{(p.IsRunning ? 1 : 0)}:{p.Activity}:{p.Message}"
                           + $":{p.ThemeMode}:{colour}:{(settings.ShowSwatch ? 1 : 0)}";
                })
                .OrderBy(entry => entry, StringComparer.Ordinal));
        }

        private static DesktopSnapshot Compose(ScanResult scan)
        {
            var now = Environment.TickCount64;
            var defaultDirectory = scan.DefaultDirectory;
            var views = new List<ProfileView>(scan.Profiles.Count);

            foreach (var (name, directory) in scan.Profiles)
            {
                var isRunning = scan.Running.TryGetValue(directory, out var pid);
                var (activity, message) = ResolveTransient(directory, isRunning, now);

                var chosenName = ClaudeBuddySettings.For(name).Name;

                views.Add(new ProfileView(
                    chosenName is { Length: > 0 } ? chosenName : DisplayNameFor(name),
                    directory,
                    string.Equals(directory, defaultDirectory, StringComparison.Ordinal),
                    isRunning,
                    isRunning ? pid : 0,
                    activity,
                    message,
                    ReadThemeMode(directory)));
            }

            return new DesktopSnapshot(scan.Installed, views);
        }

        private static (ProfileActivity, string?) ResolveTransient(string directory, bool isRunning, long now)
        {
            lock (TransientGate)
            {
                if (!Transients.TryGetValue(directory, out var transient)) return (ProfileActivity.None, null);

                switch (transient.Kind)
                {
                    case ProfileActivity.Launching:
                        if (isRunning || now > transient.Deadline)
                        {
                            Transients.Remove(directory);
                            return (ProfileActivity.None, null);
                        }
                        return (ProfileActivity.Launching, null);

                    case ProfileActivity.Quitting:
                        if (!isRunning)
                        {
                            Transients.Remove(directory);
                            return (ProfileActivity.None, null);
                        }
                        if (now > transient.Deadline)
                        {
                            // No automatic escalation. SIGTERM isn't graceful
                            // for Electron, and a refusal is often legitimate —
                            // so offer Force quit and make the user mean it.
                            Transients[directory] =
                                new Transient(ProfileActivity.ForceQuitOffered, now + ForceQuitOfferMs, null);
                            return (ProfileActivity.ForceQuitOffered, null);
                        }
                        return (ProfileActivity.Quitting, null);

                    case ProfileActivity.ForceQuitOffered:
                        if (!isRunning || now > transient.Deadline)
                        {
                            Transients.Remove(directory);
                            return (ProfileActivity.None, null);
                        }
                        return (ProfileActivity.ForceQuitOffered, null);

                    default:
                        if (now > transient.Deadline)
                        {
                            Transients.Remove(directory);
                            return (ProfileActivity.None, null);
                        }
                        return (ProfileActivity.Error, transient.Message);
                }
            }
        }

        private static void SetTransient(string directory, ProfileActivity kind, int lifetimeMs, string? message = null)
        {
            lock (TransientGate)
            {
                Transients[directory] = new Transient(kind, Environment.TickCount64 + lifetimeMs, message);
            }

            Recompose();
        }

        private static void ClearTransient(string directory)
        {
            lock (TransientGate)
            {
                Transients.Remove(directory);
            }

            Recompose();
        }

        // ---- discovery -----------------------------------------------------

        private static bool AppInstalled() =>
            OperatingSystem.IsWindows() ? WindowsAppLookup.ResolveAumid() is not null : AppPath() is not null;

        // macOS only: the bundle path backs cloned, tinted Dock icons, which
        // have no Windows analogue (out of scope — see ClaudeDesktopBundles).
        private static string? AppPath()
        {
            foreach (var candidate in new[]
                     {
                         "/Applications/Claude.app",
                         Path.Combine(Home, "Applications", "Claude.app")
                     })
            {
                if (Directory.Exists(candidate)) return candidate;
            }

            return null;
        }

        private static string DefaultDirectory() =>
            Canonicalise(Path.Combine(ProfileRoot, DefaultProfileFolder))
            ?? Path.Combine(ProfileRoot, DefaultProfileFolder);

        private static IReadOnlyList<(string Name, string Directory)> Discover()
        {
            var found = new List<(string Name, string Directory)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            string[] entries;
            try { entries = Directory.GetDirectories(ProfileRoot); }
            catch { return found; }

            // Directory order is whatever the filesystem feels like, and the
            // dedupe below is first-one-wins — so without this, a symlinked
            // alias could beat the real directory to the row and supply the
            // display name. Real directories first, then by name.
            Array.Sort(entries, (a, b) =>
            {
                var aLink = IsSymlink(a);
                var bLink = IsSymlink(b);
                if (aLink != bLink) return aLink ? 1 : -1;
                return string.Compare(a, b, StringComparison.Ordinal);
            });

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);

                // Case-sensitive on purpose. The app's own directories are
                // exactly "Claude" and "Claude-*"; matching case-insensitively
                // on a case-insensitive volume sweeps in unrelated vendors.
                if (name != DefaultProfileFolder && !name.StartsWith("Claude-", StringComparison.Ordinal)) continue;

                // "Claude-3p" is Claude Desktop's *own* sidecar config
                // directory (configLibrary/, deploymentMode), which a normally
                // launched instance reads and writes. Offering it as a profile
                // would point a second Chromium at a live directory.
                if (name.EndsWith("-3p", StringComparison.Ordinal)) continue;

                // The unpackaged-build suffix.
                if (name.EndsWith("-dev", StringComparison.Ordinal)) continue;

                var directory = Canonicalise(entry);
                if (directory is null) continue;
                if (!LooksLikeProfile(directory)) continue;

                // Without this, a symlink or a case variant yields two menu
                // rows for one directory and defeats the launch guard.
                if (!seen.Add(directory)) continue;

                found.Add((name, directory));
            }

            var defaultDirectory = DefaultDirectory();
            found.Sort((a, b) =>
            {
                var aDefault = string.Equals(a.Directory, defaultDirectory, StringComparison.Ordinal);
                var bDefault = string.Equals(b.Directory, defaultDirectory, StringComparison.Ordinal);
                if (aDefault != bDefault) return aDefault ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            return found;
        }

        private static bool IsSymlink(string path)
        {
            try { return new DirectoryInfo(path).LinkTarget is not null; }
            catch { return false; }
        }

        private static string? Canonicalise(string path)
        {
            try
            {
                var info = new DirectoryInfo(path);
                if (!info.Exists) return null;

                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                var full = Path.GetFullPath(target?.FullName ?? info.FullName);
                return full.Length > 1 ? full.TrimEnd('/') : full;
            }
            catch
            {
                return null;
            }
        }

        // Accept a real profile, or an empty directory — New profile creates
        // them empty, so a brand-new one has to be adoptable. Anything else
        // called "Claude-something" is somebody else's folder.
        private static bool LooksLikeProfile(string directory)
        {
            try
            {
                var populated = Directory.EnumerateFileSystemEntries(directory).Any();
                if (!populated) return true;

                var hits = MarkerFiles.Count(marker => File.Exists(Path.Combine(directory, marker)))
                         + MarkerDirectories.Count(marker => Directory.Exists(Path.Combine(directory, marker)));

                return hits >= 2;
            }
            catch
            {
                return false;
            }
        }

        private static string DisplayNameFor(string folderName) =>
            folderName == DefaultProfileFolder ? DefaultDisplayName : folderName["Claude-".Length..];

        private static IReadOnlyDictionary<string, int> MapInstances(
            IReadOnlyList<ClaudeInstance> instances)
        {
            var defaultDirectory = DefaultDirectory();
            var running = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var instance in instances)
            {
                string? directory;

                if (instance.UserDataDir is null)
                {
                    // No override in the environment means the app resolved its
                    // own default location — which is what a Dock launch does,
                    // and what we deliberately do for the Default profile.
                    directory = defaultDirectory;
                }
                else
                {
                    directory = Canonicalise(instance.UserDataDir);
                    if (directory is null)
                    {
                        try { directory = Path.GetFullPath(instance.UserDataDir).TrimEnd('/'); }
                        catch { continue; }
                    }
                }

                running.TryAdd(directory, instance.Pid);
            }

            return running;
        }

        // ---- actions -------------------------------------------------------

        public static void Launch(ProfileView profile)
        {
            if (!SupportedPlatform) return;

            var directory = profile.Directory;
            var isDefault = profile.IsDefault;

            SetTransient(directory, ProfileActivity.Launching, LaunchWindowMs);

            Task.Run(() =>
            {
                LaunchGate.Wait();
                try
                {
                    // Authoritative re-check inside the gate. Concurrent
                    // Chromium access to one userData directory corrupts
                    // leveldb and SQLite, and this app takes no single-instance
                    // lock of its own, so this is the last line of defence.
                    var running = MapInstances(ScanProcesses());
                    if (running.TryGetValue(directory, out var pid))
                    {
                        ClearTransient(directory);
                        Focus(pid);
                        return;
                    }

                    var launched = OperatingSystem.IsWindows()
                        ? LaunchWindows(directory, isDefault)
                        : LaunchMac(directory, isDefault);

                    if (!launched)
                    {
                        SetTransient(directory, ProfileActivity.Error, ErrorMs, "couldn't launch");
                    }
                }
                catch
                {
                    SetTransient(directory, ProfileActivity.Error, ErrorMs, "couldn't launch");
                }
                finally
                {
                    LaunchGate.Release();
                    KickRefresh();
                }
            });
        }

        private static bool LaunchMac(string directory, bool isDefault)
        {
            // The Default profile is launched *without* the variable.
            // Setting it suppresses the app's own resolution of its
            // sidecar config directory, so a tray launch could
            // re-trigger the deployment-mode chooser on an already
            // configured profile — and it would start a second log
            // history under <profile>/Logs.
            // A cloned bundle with a tinted icon, so this instance gets
            // its own colour in the Dock. Only for created profiles:
            // Default deliberately stays the bundle you installed, icon
            // and all. A failure here just means no colour — we fall back
            // to the real bundle rather than not launching.
            var folder = Path.GetFileName(directory);
            var profileSettings = ClaudeBuddySettings.For(folder);

            // Default gets a tinted clone too, but only once you've
            // actually picked a colour for it. Left on "auto" it launches
            // the bundle you installed, with Anthropic's icon — changing
            // that unasked would be presumptuous, and it's also what you
            // see when you launch Claude from the Dock yourself.
            var wantsClone = profileSettings.TintDockIcon
                             && (!isDefault || profileSettings.Color is { Length: > 0 });

            var clone = wantsClone
                ? ClaudeDesktopBundles.Ensure(
                    folder,
                    AppPath() ?? "/Applications/Claude.app",
                    ClaudeDesktopColors.For(folder, isDefault))
                : null;

            // -n on every path. Without it, `open` does not start anything
            // when *any* instance of the bundle is already running —
            // LaunchServices just activates that one — so launching
            // Default while a profile was up would bring the profile's
            // window forward and Default would never start. Safe because
            // the gate above has just confirmed, from a fresh scan, that
            // this directory has no live instance; an env-var-less
            // instance maps to Default there, so a Dock-launched Default
            // is caught too.
            //
            // Clones are addressed by path, not bundle id: several bundles
            // now share com.anthropic.claudefordesktop, so -b would be
            // ambiguous.
            var target = clone is not null
                ? new[] { "-n", "-a", clone }
                : new[] { "-n", "-b", BundleId };

            // Default is launched without CLAUDE_USER_DATA_DIR whether or
            // not it runs from a clone, so the app resolves its own
            // userData and sidecar config exactly as a Dock launch does.
            var arguments = isDefault
                ? target
                : target.Concat(new[] { "--env", "CLAUDE_USER_DATA_DIR=" + directory }).ToArray();

            // open(1) rather than starting Contents/MacOS/Claude
            // directly: a direct child would inherit Claude Buddy's
            // whole environment, land in its process group (so Ctrl-C
            // during a dotnet run would SIGHUP every instance), and
            // have its privacy prompts attributed to Claude Buddy,
            // whose ad-hoc signature changes on every build.
            return Run("/usr/bin/open", arguments);
        }

        // Default is launched with no arguments at all — passing
        // --user-data-dir pointed at the app's own default directory is not
        // the same thing to Chromium as omitting the flag, and risks
        // re-triggering the deployment-mode chooser the same way an
        // unnecessary CLAUDE_USER_DATA_DIR does on macOS (see LaunchMac).
        // A created profile gets the flag pointed at its own directory.
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static bool LaunchWindows(string directory, bool isDefault)
        {
            var aumid = WindowsAppLookup.ResolveAumid();
            if (aumid is null) return false;

            var arguments = isDefault ? "" : $"--user-data-dir=\"{directory}\"";
            return WindowsAppActivation.TryActivate(aumid, arguments, out _);
        }

        public static void Focus(int pid)
        {
            if (!SupportedPlatform || pid <= 0) return;

            if (OperatingSystem.IsWindows())
            {
                Dispatcher.UIThread.Post(() => FocusWindows(pid));
            }
            else
            {
                Dispatcher.UIThread.Post(() => MacOSAppActivation.Activate(pid));
            }
        }

        private static void FocusWindows(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                WindowsForegroundWindow.BringToFront(process.MainWindowHandle);
            }
            catch
            {
                // The process may have exited between the scan and the
                // click; focusing is a convenience, never worth an error row.
            }
        }

        public static void Quit(ProfileView profile)
        {
            if (!SupportedPlatform || profile.Pid <= 0) return;

            var pid = profile.Pid;
            var directory = profile.Directory;

            SetTransient(directory, ProfileActivity.Quitting, QuitWindowMs);

            Dispatcher.UIThread.Post(() =>
            {
                if (OperatingSystem.IsWindows())
                {
                    if (!QuitWindows(pid))
                    {
                        SetTransient(directory, ProfileActivity.Error, ErrorMs, "couldn't quit");
                    }
                    return;
                }

                // Activate first, so an "unsaved work" sheet ends up on screen
                // instead of behind whatever you were looking at.
                MacOSAppActivation.Activate(pid);

                if (!MacOSAppActivation.Terminate(pid))
                {
                    SetTransient(directory, ProfileActivity.Error, ErrorMs, "allow Automation to quit");
                }
            });
        }

        // Windows has no AppleScript equivalent for "please quit", so this is a
        // two-step escalation rather than one call.
        //
        // WM_CLOSE first, via CloseMainWindow(), because it's the gentlest thing
        // that exists and an app which does quit on it gets to run its own close
        // handling (including any beforeunload prompt). Claude Desktop is not
        // such an app — it hides to the tray and keeps running, which made Quit
        // fall through to Force quit every time on a real Windows box.
        //
        // So if it's still alive shortly after, ask again the way Windows asks
        // during shutdown. See WindowsAppQuit: Electron acts on WM_ENDSESSION,
        // and that message reaches the hidden window WM_CLOSE just created.
        //
        // The escalation waits off the UI thread. Quit() posts this to the
        // dispatcher, and sleeping there would freeze the menu and every orb.
        // Attributed rather than relying on the caller's OperatingSystem.IsWindows()
        // check: the escalation below runs inside a Task.Run closure, and the
        // analyzer can't see a guard through one. Without this, CA1416 correctly
        // reports the user32 P/Invokes as reachable on every platform.
        [SupportedOSPlatform("windows")]
        private static bool QuitWindows(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                WindowsForegroundWindow.BringToFront(process.MainWindowHandle);
                var closed = process.CloseMainWindow();

                Task.Run(async () =>
                {
                    await Task.Delay(WindowsQuitEscalationMs).ConfigureAwait(false);
                    if (!WindowsAppQuit.HasExited(pid)) WindowsAppQuit.RequestEndSession(pid);
                });

                // The transient "Quitting…" row and its force-quit fallback are
                // driven by whether the process is still there when the window
                // expires, so a true here is only a claim that we asked — which
                // is all this ever promised.
                return closed;
            }
            catch
            {
                return false;
            }
        }

        public static void ForceQuit(ProfileView profile)
        {
            if (!SupportedPlatform || profile.Pid <= 0) return;

            var pid = profile.Pid;
            var directory = profile.Directory;

            SetTransient(directory, ProfileActivity.Quitting, QuitWindowMs);

            Dispatcher.UIThread.Post(() =>
            {
                var ok = OperatingSystem.IsWindows() ? ForceQuitWindows(pid) : MacOSAppActivation.ForceTerminate(pid);

                if (!ok)
                {
                    SetTransient(directory, ProfileActivity.Error, ErrorMs, "couldn't force quit");
                }
            });
        }

        private static bool ForceQuitWindows(int pid)
        {
            try
            {
                Process.GetProcessById(pid).Kill(entireProcessTree: true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---- Dock icon bundles ---------------------------------------------

        // Squirrel only ever updates the bundle in /Applications, so clones go
        // stale after a Claude update and would keep running the old version.
        // Rebuilding is a clone plus an icon, so it's cheap enough to just redo
        // for every profile that has one.
        public static void RebuildDockIcons()
        {
            if (!OperatingSystem.IsMacOS()) return;

            Task.Run(() =>
            {
                var source = AppPath();
                if (source is null) return;

                foreach (var profile in Snapshot.Profiles)
                {
                    if (profile.IsDefault) continue;

                    var folder = Path.GetFileName(profile.Directory);
                    ClaudeDesktopBundles.Remove(folder);
                    ClaudeDesktopBundles.Ensure(
                        folder, source, ClaudeDesktopColors.For(folder, isDefault: false));
                }

                KickRefresh();
            });
        }

        // Called when a profile's colour changes: the clone's Dock icon was baked
        // at creation time and would otherwise keep the old colour until the next
        // rebuild.
        public static void RecolourDockIcon(string folder)
        {
            if (!OperatingSystem.IsMacOS()) return;

            Task.Run(() =>
            {
                var source = AppPath();
                if (source is null) return;

                var directory = Path.Combine(ProfileRoot, folder);

                // Recolouring rebuilds the clone, and deleting a bundle out from
                // under a running instance is asking for trouble — it survives in
                // practice, because the open inodes stay alive, but anything the
                // app loads lazily afterwards would be gone. So defer: the clone
                // records the colour it was built with, and Ensure() treats a
                // mismatch as stale, so the next launch picks it up.
                if (MapInstances(MacOSProcessScan.Scan()).ContainsKey(directory))
                {
                    SetTransient(directory, ProfileActivity.Error, ErrorMs, "icon changes on relaunch");
                    return;
                }

                var isDefault = string.Equals(directory, DefaultDirectory(), StringComparison.Ordinal);

                // On "auto" there is nothing to tint Default with — it goes back
                // to the installed bundle, so drop any clone it had.
                if (isDefault && ClaudeBuddySettings.For(folder).Color is not { Length: > 0 })
                {
                    ClaudeDesktopBundles.Remove(folder);
                    return;
                }

                ClaudeDesktopBundles.Retint(
                    folder, source, ClaudeDesktopColors.For(folder, isDefault));
            });
        }

        public static void RevealDockIconBundles()
        {
            if (!OperatingSystem.IsMacOS()) return;

            Task.Run(() =>
            {
                var root = ClaudeDesktopBundles.Root;
                Directory.CreateDirectory(root);
                Run("/usr/bin/open", root);
            });
        }

        // ---- theme ---------------------------------------------------------

        // Claude Desktop keeps its light/dark choice in each profile's own
        // config.json, so it is already per-profile — setting different values
        // makes the app windows themselves distinguishable, which is the only
        // in-app differentiation available (there is no accent-colour concept
        // anywhere in the app).
        public const string SystemTheme = "system";

        private static string ReadThemeMode(string directory)
        {
            try
            {
                var path = Path.Combine(directory, "config.json");
                if (!File.Exists(path)) return SystemTheme;

                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                return document.RootElement.TryGetProperty("userThemeMode", out var value)
                       && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? SystemTheme
                    : SystemTheme;
            }
            catch
            {
                return SystemTheme;
            }
        }

        public static void SetTheme(ProfileView profile, string mode)
        {
            if (!SupportedPlatform) return;

            var directory = profile.Directory;

            Task.Run(() =>
            {
                try
                {
                    // A running instance rewrites config.json from memory when it
                    // exits, which would silently discard this — and two writers
                    // on one file can leave it unparseable, which would cost the
                    // profile its stored login. Re-check authoritatively rather
                    // than trusting the menu's snapshot.
                    if (MapInstances(ScanProcesses()).ContainsKey(directory))
                    {
                        SetTransient(directory, ProfileActivity.Error, ErrorMs, "quit it first");
                        return;
                    }

                    var path = Path.Combine(directory, "config.json");
                    var original = File.Exists(path) ? File.ReadAllText(path) : "{}";
                    var root = JsonNode.Parse(original) as JsonObject;

                    if (root is null)
                    {
                        SetTransient(directory, ProfileActivity.Error, ErrorMs, "config unreadable");
                        return;
                    }

                    root["userThemeMode"] = mode;

                    // Write beside the target and rename over it: a crash midway
                    // through an in-place write would leave the profile without a
                    // parseable config, taking its oauth token cache with it.
                    // UTF-8 without a BOM, matching what the app itself writes.
                    var temporary = path + ".claude-buddy.tmp";
                    File.WriteAllText(temporary, root.ToJsonString(), new UTF8Encoding(false));

                    // This file holds the profile's login. Prove the rewrite kept
                    // every key before letting it replace the original, and throw
                    // the candidate away rather than the real thing if it didn't.
                    if (!PreservesKeys(original, temporary, mode))
                    {
                        try { File.Delete(temporary); } catch { }
                        SetTransient(directory, ProfileActivity.Error, ErrorMs, "config rewrite unsafe");
                        return;
                    }

                    File.Move(temporary, path, overwrite: true);
                }
                catch
                {
                    SetTransient(directory, ProfileActivity.Error, ErrorMs, "couldn't set theme");
                    return;
                }

                KickRefresh();
            });
        }

        // Every top-level key present before must still be present after, with an
        // unchanged serialised value — except userThemeMode, which is the one we
        // meant to change. Cheap insurance against a serialiser quirk silently
        // dropping or rewriting the encrypted token blobs next door.
        private static bool PreservesKeys(string originalText, string candidatePath, string expectedMode)
        {
            try
            {
                using var before = JsonDocument.Parse(originalText);
                using var after = JsonDocument.Parse(File.ReadAllBytes(candidatePath));

                if (before.RootElement.ValueKind != JsonValueKind.Object) return false;
                if (after.RootElement.ValueKind != JsonValueKind.Object) return false;

                foreach (var property in before.RootElement.EnumerateObject())
                {
                    if (!after.RootElement.TryGetProperty(property.Name, out var written)) return false;
                    if (property.NameEquals("userThemeMode")) continue;
                    if (written.GetRawText() != property.Value.GetRawText()) return false;
                }

                return after.RootElement.TryGetProperty("userThemeMode", out var themeValue)
                       && themeValue.ValueKind == JsonValueKind.String
                       && themeValue.GetString() == expectedMode;
            }
            catch
            {
                return false;
            }
        }

        public static void RevealLogs(ProfileView profile)
        {
            if (!SupportedPlatform) return;

            var directory = profile.Directory;
            var isDefault = profile.IsDefault;

            Task.Run(() =>
            {
                IEnumerable<string> candidates;

                if (OperatingSystem.IsWindows())
                {
                    // Unlike macOS, Electron's userData resolves to the same
                    // directory whether or not --user-data-dir was passed —
                    // Default's userData is just %APPDATA%\Claude — so there's
                    // one candidate rather than a Default/created split.
                    candidates = new[] { Path.Combine(directory, "logs") };
                }
                else
                {
                    // Only an env-launched instance writes <profile>/Logs; a
                    // plain launch — which is what Default deliberately gets —
                    // writes Electron's default path instead.
                    candidates = isDefault
                        ? new[] { Path.Combine(Home, "Library", "Logs", DefaultProfileFolder), directory }
                        : new[] { Path.Combine(directory, "Logs"), directory };
                }

                foreach (var candidate in candidates)
                {
                    if (!Directory.Exists(candidate)) continue;
                    OpenFolder(candidate);
                    return;
                }

                RevealProfilesFolder();
            });
        }

        public static void RevealProfilesFolder()
        {
            if (!SupportedPlatform) return;

            Task.Run(() =>
            {
                var root = ProfileRoot;
                if (Directory.Exists(root)) OpenFolder(root);
            });
        }

        private static void OpenFolder(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
                    {
                        UseShellExecute = false
                    });
                }
                catch { }
            }
            else
            {
                Run("/usr/bin/open", path);
            }
        }

        public static void NewProfile()
        {
            if (!SupportedPlatform) return;

            Task.Run(() =>
            {
                string directory;
                string name;

                try
                {
                    var root = ProfileRoot;
                    Directory.CreateDirectory(root);

                    var n = 1;
                    while (Directory.Exists(Path.Combine(root, $"Claude-Profile-{n}"))) n++;

                    name = $"Claude-Profile-{n}";
                    directory = Path.Combine(root, name);
                    Directory.CreateDirectory(directory);
                }
                catch
                {
                    return;
                }

                var canonical = Canonicalise(directory) ?? directory;

                // Launch straight away rather than waiting for the next scan to
                // notice it — the whole point of the click is to sign in.
                Launch(new ProfileView(
                    DisplayNameFor(name), canonical, IsDefault: false,
                    IsRunning: false, Pid: 0, ProfileActivity.None, Message: null,
                    ThemeMode: SystemTheme));
            });
        }

        // ---- process runner ------------------------------------------------

        // Local rather than shared with TerminalFocuser.TryRun, which is private
        // to the session-monitoring side of the app and does the same thing.
        // Keeping this feature's only dependency on the tray menu is what makes
        // it deletable in one revert; a shared runner would mean editing an
        // unrelated file to widen a helper's visibility. The two agree on the
        // part that matters: both reads have to be in flight *before* the wait,
        // or the timeout is unreachable (a blocking read returns when the pipe
        // closes, which a wedged child never does) and an undrained stderr can
        // deadlock a chatty one once its pipe buffer fills.
        private static bool Run(string executable, params string[] arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

                using var process = Process.Start(startInfo);
                if (process is null) return false;

                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(ProcessTimeoutMs))
                {
                    try { process.Kill(true); } catch { /* already gone */ }
                    return false;
                }

                Task.WaitAll(new Task[] { stdout, stderr }, 1_000);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
