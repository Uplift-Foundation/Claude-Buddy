using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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

        private readonly Dictionary<string, WindowIcon> _iconCache = new();

        // Rebuilding a NativeMenu is visible on macOS (it can dismiss an open
        // menu), and ScanAndUpdate runs every 2s, so only touch the menu when
        // something a user could actually see has changed.
        private string _lastSignature = "";

        public TrayController()
        {
            _tray = new TrayIcon
            {
                Icon = LoadIcon("idle"),
                ToolTipText = "Claude Buddy",
                IsVisible = true,
                Menu = _menu
            };

            if (Application.Current is { } app)
            {
                TrayIcon.SetIcons(app, new TrayIcons { _tray });
            }

            Rebuild(Array.Empty<SessionEntry>());
        }

        public readonly record struct SessionEntry(string SessionId, SessionStatus Status);

        public void Update(IReadOnlyList<SessionEntry> sessions)
        {
            var signature = string.Join("|",
                                sessions.Select(s => $"{s.SessionId}:{s.Status.State}:{s.Status.Cwd}:{s.Status.Title}"))
                            + $"|orbs={SessionManager.Instance?.OrbsVisible}";
            if (signature == _lastSignature) return;
            _lastSignature = signature;

            Rebuild(sessions);
        }

        private void Rebuild(IReadOnlyList<SessionEntry> sessions)
        {
            var waiting = sessions.Count(s => s.Status.State == "waiting");
            var generating = sessions.Count(s => s.Status.State == "generating");

            _tray.Icon = LoadIcon(waiting > 0 ? "waiting" : generating > 0 ? "generating" : "idle");
            _tray.ToolTipText = Summary(sessions.Count, waiting, generating);

            var menu = _menu;
            menu.Items.Clear();

            if (sessions.Count == 0)
            {
                menu.Add(new NativeMenuItem("No Claude Code sessions") { IsEnabled = false });
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
                    item.Click += (_, _) => TerminalFocuser.Focus(status);
                    menu.Add(item);
                }
            }

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

        // Chat name if Claude Code has named the session, else its folder.
        private static string DisplayName(SessionEntry session)
        {
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

            var icon = new WindowIcon(AssetLoader.Open(
                new Uri($"avares://ClaudeBuddy/Assets/tray-{state}.png")));
            _iconCache[state] = icon;
            return icon;
        }
    }
}
