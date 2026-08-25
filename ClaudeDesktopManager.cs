using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
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
        string ThemeMode,
        // How many processes are using this profile directory. Should always be
        // 0 or 1; more than that is the concurrent-access case that corrupts
        // leveldb and SQLite, and the menu says so rather than hiding it behind
        // a single "running" row.
        int InstanceCount = 1);

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
        private const int ForceQuitOfferMs = 60_000;
        private const int ErrorMs = 20_000;

        // How long Quit waits, on Windows, for the close request it just sent
        // to actually end the app before terminating the tree itself. Measured
        // safe on a real profile (docs/windows-quit-focus-findings.md item 2):
        // three kill/relaunch cycles, no corruption. Long enough that a build
        // which does honour the close request gets a real chance; short enough
        // that Quit still reads as quitting, not hanging.
        private const int WindowsQuitGraceMs = 2_500;

        private const int ProcessTimeoutMs = 5_000;

        // Directories that mark a folder as a real Claude Desktop profile.
        private static readonly string[] MarkerFiles =
            { "config.json", "Cookies", "Local State", "Preferences", "ant-did" };

        private static readonly string[] MarkerDirectories =
            { Path.Combine("Local Storage", "leveldb"), "Crashpad" };

        internal sealed record Transient(ProfileActivity Kind, long Deadline, string? Message);

        // First pid seen for a profile directory, and how many processes are on
        // it. Count > 1 is the concurrent-access hazard, not a normal state.
        internal readonly record struct InstanceGroup(int Pid, int Count);

        // DefaultDirectory resolves symlinks, so it touches the filesystem.
        // It's captured here rather than recomputed in Compose, which also runs
        // on the UI thread when a click changes transient state.
        internal sealed record ScanResult(
            bool Installed,
            string DefaultDirectory,
            IReadOnlyList<(string Name, string Directory)> Profiles,
            IReadOnlyDictionary<string, InstanceGroup> Running);

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
        // Excluded from coverage: the whole of it is the machine's own state.
        // AppInstalled() asks whether Claude Desktop is installed on this
        // computer — /Applications on macOS, the AppX package repository in the
        // registry on Windows — and, when it is, ScanProcesses() walks every
        // live process through sysctl(KERN_PROCARGS2) or WMI to find the running
        // instances. A test can arrange neither answer, and on a machine where
        // Claude Desktop *is* installed it would publish that machine's real
        // profile list over whatever a test had just set up. Adopt() below is
        // the seam: it is the same "remember this scan and publish it" step this
        // method reaches, with the scan handed in.
        [ExcludeFromCodeCoverage]
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

        // Excluded from coverage: see KickRefresh. This is the body it runs.
        [ExcludeFromCodeCoverage]
        private static void RefreshCore()
        {
            var installed = AppInstalled();

            IReadOnlyList<(string Name, string Directory)> profiles =
                installed ? Discover() : Array.Empty<(string Name, string Directory)>();
            IReadOnlyDictionary<string, InstanceGroup> running =
                installed ? MapInstances(ScanProcesses()) : EmptyRunning;

            Adopt(new ScanResult(installed, DefaultDirectory(), profiles, running));
        }

        // Remember a scan as the current one and publish what it composes to.
        // Split out of RefreshCore so a caller that already has a scan — a test,
        // or any future non-polling source of one — does not have to reach the
        // machine to install it; Recompose then has something to recompose from,
        // which is the whole reason it can avoid re-scanning on a click.
        internal static void Adopt(ScanResult scan)
        {
            _lastScan = scan;
            Publish(Compose(scan));
        }

        // Excluded from coverage: sysctl(KERN_PROCARGS2) on macOS, WMI on
        // Windows — a walk of every process on the machine running the tests.
        [ExcludeFromCodeCoverage]
        private static IReadOnlyList<ClaudeInstance> ScanProcesses() =>
            OperatingSystem.IsWindows() ? WindowsProcessScan.Scan() : MacOSProcessScan.Scan();

        private static readonly IReadOnlyDictionary<string, InstanceGroup> EmptyRunning =
            new Dictionary<string, InstanceGroup>(StringComparer.Ordinal);

        // Recompose from the last scan without re-scanning — for when a click
        // has changed transient state and the menu should say so immediately.
        internal static void Recompose()
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

        internal static void Publish(DesktopSnapshot next)
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

        internal static string DigestOf(DesktopSnapshot snapshot)
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
                    // InstanceCount belongs here even though it's a count: it's
                    // stable while the processes are, and without it a profile
                    // going from one instance to two would never repaint the
                    // menu, so the duplicate warning would never appear.
                    return $"{p.DisplayName}:{(p.IsRunning ? 1 : 0)}:{p.Activity}:{p.Message}"
                           + $":{p.ThemeMode}:{colour}:{(settings.ShowSwatch ? 1 : 0)}"
                           + $":{p.InstanceCount}";
                })
                .OrderBy(entry => entry, StringComparer.Ordinal));
        }

        internal static DesktopSnapshot Compose(ScanResult scan)
        {
            var now = Environment.TickCount64;
            var defaultDirectory = scan.DefaultDirectory;
            var views = new List<ProfileView>(scan.Profiles.Count);

            foreach (var (name, directory) in scan.Profiles)
            {
                var isRunning = scan.Running.TryGetValue(directory, out var group);
                var (activity, message) = ResolveTransient(directory, isRunning, now);

                var chosenName = ClaudeBuddySettings.For(name).Name;

                views.Add(new ProfileView(
                    chosenName is { Length: > 0 } ? chosenName : DisplayNameFor(name),
                    directory,
                    string.Equals(directory, defaultDirectory, StringComparison.Ordinal),
                    isRunning,
                    isRunning ? group.Pid : 0,
                    activity,
                    message,
                    ReadThemeMode(directory),
                    isRunning ? group.Count : 0));
            }

            return new DesktopSnapshot(scan.Installed, views);
        }

        internal static (ProfileActivity, string?) ResolveTransient(string directory, bool isRunning, long now)
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
                        if (!isRunning)
                        {
                            Transients.Remove(directory);
                            return (ProfileActivity.None, null);
                        }

                        // The offer expires on macOS because a graceful quit
                        // works there, so a lapsed offer just means "ask nicely
                        // again". On Windows nothing can end the app except this
                        // offer, so letting it expire stranded the instance:
                        // the row fell back to Quit, that click could no longer
                        // find a window to close, and there was no route left to
                        // the only thing that does work. Keep offering while it
                        // is alive.
                        if (!OperatingSystem.IsWindows() && now > transient.Deadline)
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

        internal static void SetTransient(string directory, ProfileActivity kind, int lifetimeMs, string? message = null)
        {
            lock (TransientGate)
            {
                Transients[directory] = new Transient(kind, Environment.TickCount64 + lifetimeMs, message);
            }

            Recompose();
        }

        internal static void ClearTransient(string directory)
        {
            lock (TransientGate)
            {
                Transients.Remove(directory);
            }

            Recompose();
        }

        // ---- discovery -----------------------------------------------------

        // Excluded from coverage: answers "is Claude Desktop installed on this
        // machine", which is exactly the thing no test can arrange — it is true
        // on a developer's laptop and false on a CI runner, and an assertion
        // that holds on one is wrong on the other.
        [ExcludeFromCodeCoverage]
        private static bool AppInstalled() =>
            OperatingSystem.IsWindows() ? WindowsAppLookup.ResolveAumid() is not null : AppPath() is not null;

        // macOS only: the bundle path backs cloned, tinted Dock icons, which
        // have no Windows analogue (out of scope — see ClaudeDesktopBundles).
        // Excluded from coverage: same reason as AppInstalled — it probes
        // /Applications and ~/Applications for a real installed bundle.
        [ExcludeFromCodeCoverage]
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

        internal static string DefaultDirectory() =>
            Canonicalise(Path.Combine(ProfileRoot, DefaultProfileFolder))
            ?? Path.Combine(ProfileRoot, DefaultProfileFolder);

        internal static IReadOnlyList<(string Name, string Directory)> Discover()
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

        internal static bool IsSymlink(string path)
        {
            try { return new DirectoryInfo(path).LinkTarget is not null; }
            catch { return false; }
        }

        internal static string? Canonicalise(string path)
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
        internal static bool LooksLikeProfile(string directory)
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

        internal static string DisplayNameFor(string folderName) =>
            folderName == DefaultProfileFolder ? DefaultDisplayName : folderName["Claude-".Length..];

        internal static IReadOnlyDictionary<string, InstanceGroup> MapInstances(
            IReadOnlyList<ClaudeInstance> instances)
        {
            var defaultDirectory = DefaultDirectory();
            var running = new Dictionary<string, InstanceGroup>(StringComparer.Ordinal);

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

                // Count them rather than keeping only the first. Two processes
                // on one profile directory is the failure this whole feature is
                // built to avoid, and TryAdd used to make it invisible: the menu
                // showed a single "running" row and nothing suggested anything
                // was wrong. Keep the first pid — that's the one Focus and Quit
                // act on — and remember how many there were.
                if (running.TryGetValue(directory, out var existing))
                {
                    running[directory] = existing with { Count = existing.Count + 1 };
                }
                else
                {
                    running[directory] = new InstanceGroup(instance.Pid, 1);
                }
            }

            return running;
        }

        // ---- actions -------------------------------------------------------

        // Excluded from coverage: starts Claude Desktop. Every path out of here
        // ends in `open -n` against a real app bundle or in
        // WindowsAppActivation's IApplicationActivationManager, and the clone
        // path first cp -Rc's a 753 MB bundle. Running this in a test would put
        // a second Chromium on somebody's profile directory, which is precisely
        // the corruption the LaunchGate below exists to prevent.
        [ExcludeFromCodeCoverage]
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
                    if (running.TryGetValue(directory, out var group))
                    {
                        ClearTransient(directory);
                        Focus(group.Pid);
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

        // Excluded from coverage: see Launch. Runs /usr/bin/open.
        [ExcludeFromCodeCoverage]
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
        // Excluded from coverage: see Launch. Calls the shell's activation
        // manager against a real installed AppX package.
        [ExcludeFromCodeCoverage]
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static bool LaunchWindows(string directory, bool isDefault)
        {
            var aumid = WindowsAppLookup.ResolveAumid();
            if (aumid is null) return false;

            var arguments = isDefault ? "" : $"--user-data-dir=\"{directory}\"";
            return WindowsAppActivation.TryActivate(aumid, arguments, out _);
        }

        // Excluded from coverage: activates another application's window —
        // NSRunningApplication.activateWithOptions through Apple Events on
        // macOS, ShowWindow/SetForegroundWindow on Windows. Both act on a live
        // pid belonging to a process this app does not own, and on macOS the
        // Apple Event is subject to the machine's Automation consent.
        [ExcludeFromCodeCoverage]
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

        // Not Process.MainWindowHandle: it only reports *visible* windows, so a
        // profile whose window is hidden in the tray — which is where Claude
        // Desktop goes when you close it — had nothing to focus and the click
        // did nothing at all. ShowAndFocus finds the hidden window and shows it.
        // Excluded from coverage: see Focus.
        [ExcludeFromCodeCoverage]
        private static void FocusWindows(int pid)
        {
            try
            {
                WindowsForegroundWindow.ShowAndFocus(pid);
            }
            catch
            {
                // The process may have exited between the scan and the
                // click; focusing is a convenience, never worth an error row.
            }
        }

        // Excluded from coverage: terminates another application. On macOS that
        // is an Apple Event asking Claude Desktop to quit; on Windows it posts
        // WM_CLOSE to its windows and then kills the process tree. Either one
        // run against a live pid on the machine hosting the tests ends somebody
        // else's process, and against a stale pid ends whatever now holds it.
        [ExcludeFromCodeCoverage]
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
                        return;
                    }

                    // Give a close request a real chance before assuming it
                    // won't be honoured. Off the UI thread: this method reaches
                    // here via Dispatcher.UIThread.Post, and sleeping there
                    // freezes the menu and every orb. See WindowsQuitGraceMs and
                    // docs/windows-quit-focus-findings.md item 2 for why
                    // terminating afterward is safe rather than merely
                    // reachable.
                    Task.Run(async () =>
                    {
                        await Task.Delay(WindowsQuitGraceMs);
                        if (!ProcessAlive(pid)) return;
                        ForceQuitWindows(pid);
                    });
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

        // Claude Desktop can't be made to quit gracefully from outside on this
        // build — WM_CLOSE hides it to the tray and WM_ENDSESSION is ignored,
        // both measured on a real installed build. See WindowsAppQuit for what
        // was tried. So Quit's Windows path asks first (this method), waits a
        // couple of seconds off the UI thread for a build that does honour the
        // close request, then terminates the tree itself exactly as Force quit
        // does — verified safe to a live profile in
        // docs/windows-quit-focus-findings.md item 2.
        //
        // Posts WM_CLOSE to every window of the process rather than calling
        // Process.CloseMainWindow(), because that only finds *visible*
        // windows: after a first Quit hid the app, it returned false and the
        // row said "couldn't quit" without ever reaching a state that could
        // terminate the tree. Asking a hidden window works fine.
        // Excluded from coverage: see Quit.
        [ExcludeFromCodeCoverage]
        [SupportedOSPlatform("windows")]
        private static bool QuitWindows(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);

                // Only meaningful while a window is actually on screen; harmless
                // once hidden, when MainWindowHandle is zero.
                WindowsForegroundWindow.BringToFront(process.MainWindowHandle);

                // True only claims we found windows to ask, which is all this
                // ever promised — whether the app honours it is up to the app,
                // and here it reliably doesn't.
                return WindowsAppQuit.RequestClose(pid);
            }
            catch
            {
                return false;
            }
        }

        internal static bool ProcessAlive(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        // Excluded from coverage: see Quit. This is the same thing without the
        // asking.
        [ExcludeFromCodeCoverage]
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

        // Excluded from coverage: Process.Kill(entireProcessTree) against a pid
        // this app does not own.
        [ExcludeFromCodeCoverage]
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
        // Excluded from coverage: rebuilds every clone, which means cp -Rc of the
        // installed bundle per profile plus NSWorkspace.setIcon on each — see
        // ClaudeDesktopBundles, where the same boundary is drawn.
        [ExcludeFromCodeCoverage]
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
        // Excluded from coverage: same as RebuildDockIcons for one profile, and
        // it first scans the machine's live processes to decide whether it is
        // safe to delete the bundle out from under a running instance.
        [ExcludeFromCodeCoverage]
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

        // Excluded from coverage: opens a Finder window on the machine running
        // the tests.
        [ExcludeFromCodeCoverage]
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

        internal static string ReadThemeMode(string directory)
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

        // Excluded from coverage: the *decision* this makes is whether an
        // instance is live on the directory, and it makes it by walking the
        // machine's processes — see ScanProcesses. The rewrite itself, which is
        // the part with the profile's login at stake, is WriteThemeMode below,
        // and that is tested.
        [ExcludeFromCodeCoverage]
        public static void SetTheme(ProfileView profile, string mode)
        {
            if (!SupportedPlatform) return;

            var directory = profile.Directory;

            Task.Run(() =>
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

                var failure = WriteThemeMode(directory, mode);
                if (failure is not null)
                {
                    SetTransient(directory, ProfileActivity.Error, ErrorMs, failure);
                    return;
                }

                KickRefresh();
            });
        }

        // Rewrites one profile's config.json to the requested theme, returning
        // null on success or the message the menu row should show.
        //
        // Split out of SetTheme so it can be tested against a real file without
        // first having to convince a process scan that nothing is running: the
        // scan is the part no test can arrange, and this is the part that can
        // lose somebody their stored login.
        internal static string? WriteThemeMode(string directory, string mode)
        {
            try
            {
                var path = Path.Combine(directory, "config.json");
                var original = File.Exists(path) ? File.ReadAllText(path) : "{}";
                var root = JsonNode.Parse(original) as JsonObject;

                if (root is null) return "config unreadable";

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
                    return "config rewrite unsafe";
                }

                File.Move(temporary, path, overwrite: true);
                return null;
            }
            catch
            {
                return "couldn't set theme";
            }
        }

        // Every top-level key present before must still be present after, with an
        // unchanged serialised value — except userThemeMode, which is the one we
        // meant to change. Cheap insurance against a serialiser quirk silently
        // dropping or rewriting the encrypted token blobs next door.
        internal static bool PreservesKeys(string originalText, string candidatePath, string expectedMode)
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

        // Where a profile's logs could be, most likely first.
        //
        // Split out of RevealLogs because the *list* is the interesting part and
        // opening a Finder or Explorer window is not: which directory Electron
        // writes to depends on whether the instance was launched with an
        // environment override, and this app deliberately launches Default
        // without one. Get that wrong and "Reveal logs" opens the wrong
        // profile's logs, which is worse than opening nothing.
        internal static IEnumerable<string> LogCandidates(string directory, bool isDefault) =>
            LogCandidates(directory, isDefault, OperatingSystem.IsWindows());

        // The platform is an argument rather than a question this asks, so both
        // answers are reachable from either machine. The two are genuinely
        // different rules rather than different paths — see the comments below —
        // and a rule that only one CI leg ever executes is a rule nobody reads
        // until it is wrong.
        internal static IEnumerable<string> LogCandidates(string directory, bool isDefault, bool windows)
        {
            if (windows)
            {
                // Unlike macOS, Electron's userData resolves to the same
                // directory whether or not --user-data-dir was passed —
                // Default's userData is just %APPDATA%\Claude — so there's
                // one candidate rather than a Default/created split.
                return new[] { Path.Combine(directory, "logs") };
            }

            // Only an env-launched instance writes <profile>/Logs; a plain
            // launch — which is what Default deliberately gets — writes
            // Electron's default path instead.
            return isDefault
                ? new[] { Path.Combine(Home, "Library", "Logs", DefaultProfileFolder), directory }
                : new[] { Path.Combine(directory, "Logs"), directory };
        }

        // Excluded from coverage: opens a Finder or Explorer window on the
        // machine running the tests. LogCandidates above is the part with a
        // decision in it.
        [ExcludeFromCodeCoverage]
        public static void RevealLogs(ProfileView profile)
        {
            if (!SupportedPlatform) return;

            var directory = profile.Directory;
            var isDefault = profile.IsDefault;

            Task.Run(() =>
            {
                foreach (var candidate in LogCandidates(directory, isDefault))
                {
                    if (!Directory.Exists(candidate)) continue;
                    OpenFolder(candidate);
                    return;
                }

                RevealProfilesFolder();
            });
        }

        // Excluded from coverage: opens a Finder or Explorer window.
        [ExcludeFromCodeCoverage]
        public static void RevealProfilesFolder()
        {
            if (!SupportedPlatform) return;

            Task.Run(() =>
            {
                var root = ProfileRoot;
                if (Directory.Exists(root)) OpenFolder(root);
            });
        }

        // Excluded from coverage: launches explorer.exe or /usr/bin/open, both
        // of which put a window on the screen of whatever machine is running.
        [ExcludeFromCodeCoverage]
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

        // The name a new profile gets: the first Claude-Profile-N whose
        // directory does not already exist, so the numbering reuses a gap rather
        // than climbing forever.
        //
        // Split out of NewProfile because reusing a gap is a decision with a
        // consequence elsewhere — ClaudeBuddySettings.RemoveProfile's own comment
        // says a name left behind would be inherited by the next profile that
        // reused it, and this is what makes reuse happen.
        internal static string NextProfileName(string root)
        {
            var n = 1;
            while (Directory.Exists(Path.Combine(root, $"Claude-Profile-{n}"))) n++;

            return $"Claude-Profile-{n}";
        }

        // Excluded from coverage: its last act is Launch, so running it starts a
        // real Claude Desktop instance — see Launch. NextProfileName above is the
        // part with a rule in it.
        [ExcludeFromCodeCoverage]
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

                    name = NextProfileName(root);
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

        // Move a profile to the Trash.
        //
        // The one destructive thing this app can do to somebody else's data — a
        // profile directory is a Claude Desktop login, its chat history and its
        // local databases — so three rules, none of them optional.
        //
        // It goes to the Trash rather than being deleted. Recoverable is the
        // whole difference between a mistake and a loss, and the OS already has
        // the right place to put it. The default profile is refused outright:
        // that is Claude Desktop's own data directory, not a profile this app
        // invented, and nothing here should be able to throw it away. A running
        // profile is refused too — deleting the directory out from under a live
        // Electron app corrupts what is left rather than removing it, and the
        // caller is told to quit it first.
        //
        // Returns what happened rather than a bool, so the caller can say which
        // of those it was instead of a shrug.
        internal enum DeleteOutcome { Deleted, RefusedDefault, RefusedRunning, Failed }

        // Every reason this is allowed to refuse, in one place and with nothing
        // destructive behind it. Deleted here means "nothing objects", not
        // "done" — DeleteProfile is what actually moves the directory.
        //
        // Split out for exactly that reason: these are the three rules the long
        // comment above calls non-optional, and a test can check all of them
        // without a profile directory ever reaching anybody's Trash.
        internal static DeleteOutcome CheckDelete(ProfileView profile)
        {
            if (!SupportedPlatform) return DeleteOutcome.Failed;
            if (profile.IsDefault) return DeleteOutcome.RefusedDefault;
            if (profile.IsRunning || profile.InstanceCount > 0 && profile.Pid != 0)
                return DeleteOutcome.RefusedRunning;

            var directory = profile.Directory;
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return DeleteOutcome.Failed;

            // Never anything but a direct child of the profile root. The path
            // arrives from a snapshot this file built, so this cannot currently
            // be wrong — which is exactly when a guard is cheap and the absence
            // of one is a bet on that staying true.
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));
            if (!string.Equals(
                    Path.TrimEndingDirectorySeparator(parent ?? ""),
                    Path.TrimEndingDirectorySeparator(ProfileRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                return DeleteOutcome.Failed;
            }

            return DeleteOutcome.Deleted;
        }

        // What has to go with a profile once its directory has gone: the cloned
        // bundle that gave it a coloured Dock icon, and the name and colour saved
        // against it. Left behind, both would be waiting for a profile that no
        // longer exists — and would be silently inherited by the next profile
        // that happened to reuse the name, which the numbering makes likely
        // rather than far-fetched (see NextProfileName).
        internal static void ForgetProfile(string directory)
        {
            var folder = Path.GetFileName(directory);

            try { ClaudeDesktopBundles.Remove(folder); } catch { }
            ClaudeBuddySettings.RemoveProfile(folder);
        }

        // Excluded from coverage: moves a real directory to the Trash or the
        // Recycle Bin — someone's Claude Desktop login, chat history and local
        // databases. Both halves that can be checked without doing that are
        // CheckDelete and ForgetProfile above, and both are tested; what is left
        // here is the Trash call itself and the order of the three steps.
        [ExcludeFromCodeCoverage]
        public static DeleteOutcome DeleteProfile(ProfileView profile)
        {
            var refusal = CheckDelete(profile);
            if (refusal != DeleteOutcome.Deleted) return refusal;

            if (!Trash(profile.Directory)) return DeleteOutcome.Failed;

            ForgetProfile(profile.Directory);

            KickRefresh();
            return DeleteOutcome.Deleted;
        }

        // To the Trash, using whichever facility the platform calls that.
        // Excluded from coverage: see DeleteProfile. On macOS this is an Apple
        // Event telling Finder to delete a path; on Windows a shell file
        // operation into the Recycle Bin.
        [ExcludeFromCodeCoverage]
        private static bool Trash(string directory)
        {
            if (OperatingSystem.IsMacOS())
            {
                // Finder rather than NSFileManager: this app already talks to
                // Finder through osascript elsewhere, and the scripted delete is
                // exactly the Trash the user knows how to look in.
                var escaped = directory.Replace("\\", "\\\\").Replace("\"", "\\\"");
                return Run("/usr/bin/osascript", "-e",
                    $"tell application \"Finder\" to delete POSIX file \"{escaped}\"");
            }

            if (OperatingSystem.IsWindows()) return RecycleOnWindows(directory);

            return false;
        }

        // The Recycle Bin, through the shell's own file operation.
        //
        // SHFileOperation rather than Directory.Delete, for the reason the macOS
        // side uses Finder: FOF_ALLOWUNDO is what makes this recoverable, and
        // Directory.Delete has no such thing. The double-null terminator is
        // required — pFrom is a list of paths, not a path.
        //
        // Not run on a real Windows machine. See docs/windows-*-findings.md for
        // what that phrase is worth in this repo.
        // Excluded from coverage: see DeleteProfile. SHFileOperation with
        // FOF_ALLOWUNDO against a real path.
        [ExcludeFromCodeCoverage]
        [SupportedOSPlatform("windows")]
        private static bool RecycleOnWindows(string directory)
        {
            const uint FO_DELETE = 0x0003;
            const ushort FOF_ALLOWUNDO = 0x0040;
            const ushort FOF_NOCONFIRMATION = 0x0010;
            const ushort FOF_SILENT = 0x0004;
            const ushort FOF_NOERRORUI = 0x0400;

            try
            {
                var op = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = directory + "\0\0",
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
                };

                return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
            }
            catch
            {
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

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
        // Excluded from coverage: every caller left in this file is excluded for
        // reaching the OS, and this is how they reach it — open(1), osascript(1).
        // Covering it would mean starting one of those processes for no reason
        // other than the number.
        [ExcludeFromCodeCoverage]
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
