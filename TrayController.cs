using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClaudeBuddy
{
    // The menu-bar / notification-area presence: one status item whose icon
    // reflects the most urgent state across all sessions (waiting beats
    // generating beats idle), plus a menu that lists the live sessions and
    // lets you jump to any of their terminals without hunting for its orb.
    //
    // This is also the app's only permanent, always-there control surface —
    // there's no Dock icon and no window when nothing is running, so "Quit"
    // and the orb visibility toggle live here rather than only on an orb's
    // right-click menu (which is unreachable when there are zero orbs).
    internal sealed class TrayController
    {
        private readonly TrayIcon _tray;

        // One menu instance for the app's lifetime, repopulated in place.
        // Assigning a *new* NativeMenu to an already-exported TrayIcon throws
        // on macOS ("The menu being updated does not match") — Avalonia's
        // native exporter caches the menu it was handed and only tracks
        // changes to that same object's Items.
        private readonly NativeMenu _menu = new();

        // Keyed by state, not by colour: there are only ever three live entries,
        // and a colour change clears the lot (see ReapplyStateColors).
        //
        // This cache is what keeps the re-tint's pixel loop off the 2s tick.
        // Apply() is signature-gated, Rebuild calls UpdateIcon, and UpdateIcon
        // asks LoadIcon every time — so remove either the gate or this cache and
        // three PNGs start getting decoded and recoloured twice a second.
        private readonly Dictionary<string, WindowIcon> _iconCache = new();

        // Rebuilding a NativeMenu is visible on macOS (it can dismiss an open
        // menu), and ScanAndUpdate runs every 2s, so only touch the menu when
        // something a user could actually see has changed.
        private string _lastSignature = "";

        // The Claude Desktop section refreshes off its own background probe, so
        // it needs a way back in that doesn't involve SessionManager.
        internal static TrayController? Instance { get; private set; }

        private IReadOnlyList<SessionEntry> _lastSessions = Array.Empty<SessionEntry>();

        // Rebuild() clears Items, which on macOS dismisses a menu that's
        // currently open — and people linger over submenus. Hold changes until
        // it closes rather than yanking the menu out from under the pointer.
        //
        // The deadline is a safety valve, not a feature: if the platform ever
        // raises Opening without a matching Closed, the menu would otherwise
        // freeze permanently. A menu genuinely left open this long is rare, and
        // a rebuild under the pointer beats a menu that stops updating.
        private static readonly TimeSpan MenuOpenDeadline = TimeSpan.FromSeconds(60);

        private bool _menuOpen;
        private bool _pendingRebuild;
        private long _menuOpenedAt;

        public TrayController()
        {
            Instance = this;

            _tray = new TrayIcon
            {
                Icon = LoadIcon("idle"),
                ToolTipText = "Claude Buddy",
                IsVisible = true,
                Menu = _menu
            };

            _menu.Opening += (_, _) =>
            {
                _menuOpen = true;
                _menuOpenedAt = Environment.TickCount64;
                ClaudeDesktopManager.KickRefresh();
            };

            _menu.Closed += (_, _) =>
            {
                _menuOpen = false;
                if (!_pendingRebuild) return;

                _pendingRebuild = false;
                Refresh();
            };

            if (Application.Current is { } app)
            {
                TrayIcon.SetIcons(app, new TrayIcons { _tray });
            }

            // Tints the frontmost Claude Desktop window in its profile's colour.
            // Owns its own timer; nothing else here depends on it.
            ClaudeDesktopOverlay.Start();

            Rebuild(Array.Empty<SessionEntry>());
        }

        public readonly record struct SessionEntry(string SessionId, SessionStatus Status);

        public void Update(IReadOnlyList<SessionEntry> sessions)
        {
            _lastSessions = sessions;

            // The 2s poll is what keeps the Claude Desktop section honest: the
            // probe runs off the UI thread and only calls back when its digest
            // changes, so the menu-open hook below is an improvement on
            // latency, not something the section depends on for correctness.
            ClaudeDesktopManager.KickRefresh();

            Apply(sessions);
        }

        // Re-render from the last session list we were handed.
        // ClaudeDesktopManager posts this to the UI thread when its snapshot
        // changes; it deliberately doesn't kick another probe, which would loop.
        internal void Refresh() => Apply(_lastSessions);

        private void Apply(IReadOnlyList<SessionEntry> sessions)
        {
            var signature = string.Join("|",
                                sessions.Select(s => $"{s.SessionId}:{s.Status.State}:{s.Status.Cwd}:{s.Status.Title}"))
                            + $"|orbs={SessionManager.Instance?.OrbsVisible}"
                            + $"|{ClaudeDesktopManager.Digest()}";
            if (signature == _lastSignature) return;

            // The icon is the urgent half of this — it goes amber when a
            // session needs you — and changing it doesn't disturb an open menu,
            // so it's never held back.
            UpdateIcon(sessions);

            // The menu is. Leave _lastSignature stale on purpose so the change
            // isn't lost; Closed replays it.
            if (_menuOpen && Environment.TickCount64 - _menuOpenedAt < MenuOpenDeadline.TotalMilliseconds)
            {
                _pendingRebuild = true;
                return;
            }

            _menuOpen = false;
            _pendingRebuild = false;

            _lastSignature = signature;

            Rebuild(sessions);
        }

        private void UpdateIcon(IReadOnlyList<SessionEntry> sessions)
        {
            var waiting = sessions.Count(s => s.Status.State == "waiting");
            var generating = sessions.Count(s => s.Status.State == "generating");

            _tray.Icon = LoadIcon(waiting > 0 ? "waiting" : generating > 0 ? "generating" : "idle");
            _tray.ToolTipText = Summary(sessions.Count, waiting, generating);
        }

        // A colour change doesn't change the session list, so Apply's signature is
        // unchanged and nothing would repaint. Drop the cached icons and set one
        // again directly — the same explicit-kick shape as
        // ClaudeDesktopManager.KickRefresh.
        //
        // UpdateIcon assigns the icon unconditionally, so this repaints even with
        // zero sessions. That matters: with nothing running, the menu-bar icon is
        // the only live preview of the idle colour.
        internal void ReapplyStateColors()
        {
            _iconCache.Clear();
            UpdateIcon(_lastSessions);
        }

        private void Rebuild(IReadOnlyList<SessionEntry> sessions)
        {
            UpdateIcon(sessions);

            var menu = _menu;
            menu.Items.Clear();

            if (sessions.Count == 0)
            {
                // Not "No Claude Code sessions" any more: with the OpenClaw
                // feature on, an empty menu can mean no sessions of either kind,
                // and naming only one of them reads as a bug in the other.
                menu.Add(new NativeMenuItem(
                    ClaudeBuddySettings.OpenClawEnabled ? "No sessions" : "No Claude Code sessions")
                { IsEnabled = false });
            }
            else
            {
                // Two sessions that resolve to the same name would otherwise
                // produce identical menu entries, which is worse than useless —
                // you can't tell which terminal a click will take you to.
                var ambiguous = sessions.GroupBy(DisplayName)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToHashSet();

                foreach (var session in sessions)
                {
                    var item = new NativeMenuItem(SessionLabel(session, ambiguous.Contains(DisplayName(session))));
                    var status = session.Status;
                    var id = session.SessionId;
                    item.Click += (_, _) => TerminalFocuser.Focus(status, null, id);
                    menu.Add(item);
                }
            }

            ClaudeDesktopSection.Append(menu);

            menu.Add(new NativeMenuItemSeparator());

            var orbsItem = new NativeMenuItem("Show orbs")
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = SessionManager.Instance?.OrbsVisible ?? true
            };
            orbsItem.Click += (_, _) =>
                SessionManager.Instance?.SetOrbsVisible(!SessionManager.Instance.OrbsVisible);
            menu.Add(orbsItem);

            var resetItem = new NativeMenuItem("Reset all sessions to idle")
            {
                IsEnabled = sessions.Count > 0
            };
            resetItem.Click += (_, _) => SessionManager.Instance?.ResetAllSessionsToIdle();
            menu.Add(resetItem);

            menu.Add(new NativeMenuItemSeparator());

            var settingsItem = new NativeMenuItem("Settings…");
            settingsItem.Click += (_, _) => SettingsWindow.Toggle();
            menu.Add(settingsItem);

            var quitItem = new NativeMenuItem("Quit Claude Buddy");
            quitItem.Click += (_, _) =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            };
            menu.Add(quitItem);
        }

        private static string Summary(int total, int waiting, int generating)
        {
            if (total == 0) return "Claude Buddy — no sessions";

            var parts = new List<string> { total == 1 ? "1 session" : $"{total} sessions" };
            if (waiting > 0) parts.Add($"{waiting} needs you");
            if (generating > 0) parts.Add($"{generating} working");
            return "Claude Buddy — " + string.Join(", ", parts);
        }

        // An agent's name within its team if it has one, else the chat name if
        // Claude Code has named the session, else its folder. The agent name
        // comes first because a team's members all inherit the team session's
        // title, and four identical rows only differed by the id this menu
        // appends when it can't tell them apart.
        private static string DisplayName(SessionEntry session)
        {
            if (!string.IsNullOrEmpty(session.Status.Agent)) return session.Status.Agent;
            if (!string.IsNullOrEmpty(session.Status.Title)) return session.Status.Title;

            var cwd = session.Status.Cwd;
            if (string.IsNullOrEmpty(cwd)) return session.SessionId;

            var folder = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(folder) ? cwd : folder; // cwd was a filesystem root
        }

        private const int MaxLabelLength = 44;

        private static string SessionLabel(SessionEntry session, bool disambiguate)
        {
            var folder = DisplayName(session);

            // Chat names are sentence-ish and can run long; a menu that wide
            // covers half the screen. Cut at a word boundary when there's one
            // nearby — "...and Mac…" reads better than "...and Mac launch…".
            if (folder.Length > MaxLabelLength)
            {
                var cut = folder[..(MaxLabelLength - 1)];
                var space = cut.LastIndexOf(' ');
                if (space >= MaxLabelLength / 2) cut = cut[..space];
                folder = cut.TrimEnd() + "…";
            }

            if (disambiguate && session.SessionId.Length >= 4)
            {
                folder += $" ({session.SessionId[..4]})";
            }

            var state = session.Status.State switch
            {
                "waiting" => "needs you",
                "generating" => "working",
                _ => "idle"
            };
            return $"{folder} — {state}";
        }

        private WindowIcon LoadIcon(string state)
        {
            if (_iconCache.TryGetValue(state, out var cached)) return cached;

            var icon = Tinted(state)
                       ?? new WindowIcon(AssetLoader.Open(
                           new Uri($"avares://ClaudeBuddy/Assets/tray-{state}.png")));
            _iconCache[state] = icon;
            return icon;
        }

        // Recolours the baked tray artwork to a chosen state colour.
        //
        // The three PNGs are single-colour alpha masks and always have been:
        // make-icons.py's tray_shader returns one constant RGB and varies only
        // alpha (opaque in the ring, 0.30 in the core, nothing outside), so every
        // pixel with any alpha in tray-idle.png carries exactly #5B7A94.
        // Substituting the RGB and keeping the alpha therefore reproduces the
        // icon *exactly*, down to the supersampled rim — which redrawing the
        // annulus with a DrawingContext would not. That would mean a second copy
        // of the Python's geometry here, antialiased by Skia instead of
        // supersampled, so the icon's shape would visibly change the moment
        // someone picked a colour.
        //
        // Unlike ClaudeDesktopBundles.WriteTinted there's no un-premultiply round
        // trip to do: the new colour times the existing alpha *is* the new
        // premultiplied pixel, so nothing is lost at the antialiased edge.
        //
        // Null means "use the file as it is" — either this state has never been
        // recoloured, in which case the baked PNG already is that colour, or
        // something about the re-tint failed, and then the baked PNG is a
        // graceful answer: the icon still says which state we're in, just not in
        // the chosen hue.
        private static WindowIcon? Tinted(string state)
        {
            if (OrbColors.IsDefault(state)) return null;

            var color = OrbColors.For(state);

            try
            {
                using var source = new Bitmap(AssetLoader.Open(
                    new Uri($"avares://ClaudeBuddy/Assets/tray-{state}.png")));

                // 64x64, which is what make-icons.py renders so the menu bar has
                // retina pixels to downsample from.
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

                        pixels[i] = (byte)(color.B * alpha / 255);
                        pixels[i + 1] = (byte)(color.G * alpha / 255);
                        pixels[i + 2] = (byte)(color.R * alpha / 255);
                    }
                }
                finally
                {
                    pinned.Free();
                }

                using var tinted = new WriteableBitmap(
                    size, source.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var frame = tinted.Lock())
                {
                    Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
                }

                // Through a stream because that's the WindowIcon constructor this
                // file already proves exists. Encoding 64x64 to PNG in memory
                // costs well under a millisecond, and it happens three times per
                // colour change rather than once per tray tick.
                var encoded = new MemoryStream();
                tinted.Save(encoded);
                encoded.Position = 0;
                return new WindowIcon(encoded);
            }
            catch
            {
                return null;
            }
        }
    }
}
