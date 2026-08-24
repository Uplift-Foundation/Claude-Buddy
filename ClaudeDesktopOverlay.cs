using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // Tints the frontmost Claude Desktop window in its profile's colour.
    //
    // This is the only way to put an arbitrary colour on Claude's own UI. The app
    // has no accent-colour concept (its theme is a body class driven by
    // prefers-color-scheme), Chromium dropped --user-stylesheet years ago, and
    // remote debugging — the one route that could inject CSS — is gated behind an
    // Ed25519-signed, path-bound, five-minute CLAUDE_CDP_AUTH token. So instead of
    // changing the app, we draw over it: a click-through borderless window holding
    // a coloured border and a faint wash, positioned on the window's frame.
    //
    // Only ever the *frontmost* instance is tinted. The overlay is topmost, so
    // showing it for a background window would put a coloured rectangle on top of
    // whatever app you were actually using — which is exactly what a prototype
    // did before this gate existed.
    internal static class ClaudeDesktopOverlay
    {
        // A sample — frontmost pid plus the window list — measures at 0.4 ms, so
        // tracking at 60 Hz costs about 2.4% of one core. That's affordable while
        // a window is actually moving and wasteful when it isn't, hence three
        // rates: chase at 60 Hz for a moment after anything changes, 30 Hz while a
        // Claude instance is in front but still — so the *first* sample of a drag
        // is never the bottleneck — and ~1 Hz when it isn't in front at all.
        private static readonly TimeSpan MotionPoll = TimeSpan.FromMilliseconds(16);
        private static readonly TimeSpan ActivePoll = TimeSpan.FromMilliseconds(33);
        private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(900);

        // How long to keep chasing at 60 Hz after the last observed change, so a
        // drag that pauses mid-flight doesn't drop back to the slow rate and lag
        // when it resumes.
        private static readonly TimeSpan MotionLinger = TimeSpan.FromMilliseconds(600);

        private static long _lastMotionAt;

        private static readonly Dictionary<uint, OverlayWindow> Overlays = new();

        // Overlays are parked and reused, never closed. Constructing and
        // destroying these windows as focus moves crashed the app: Avalonia's
        // renderer segfaulted in ~AvnGlRenderingSession while presenting a
        // surface belonging to a window that had just been torn down. Hiding is
        // also far cheaper than rebuilding a native render target.
        private static readonly Stack<OverlayWindow> Parked = new();

        // Enough for a few windows across a few instances; beyond that, extra
        // overlays are simply not reused.
        private const int MaxParked = 6;
        private static DispatcherTimer? _timer;

        public static bool Enabled { get; private set; } = ClaudeBuddySettings.TintActiveWindow;

        // Excluded from coverage: creates real overlay windows and an Avalonia
        // timer over them.
        [ExcludeFromCodeCoverage]
        public static void Start()
        {
            if (!OperatingSystem.IsMacOS() || _timer is not null) return;

            _timer = new DispatcherTimer { Interval = IdlePoll };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        public static void SetEnabled(bool enabled)
        {
            if (Enabled == enabled) return;
            Enabled = enabled;
            ClaudeBuddySettings.TintActiveWindow = enabled;

            if (!enabled) HideAll();
            TrayController.Instance?.Refresh();
        }

        private static void Tick()
        {
            if (!Enabled)
            {
                HideAll();
                return;
            }

            var frontmost = MacOSWindowList.FrontmostPid();
            var profile = ClaudeDesktopManager.Snapshot.Profiles
                .FirstOrDefault(p => p.IsRunning && p.Pid == frontmost && p.Pid != 0);

            // A profile can opt out of the window tint while keeping its swatch
            // and Dock icon, so treat an opted-out instance as "nothing in front".
            if (profile is null
                || !ClaudeBuddySettings.For(Path.GetFileName(profile.Directory)).TintWindow)
            {
                if (_timer is not null) _timer.Interval = IdlePoll;
                HideAll();
                return;
            }

            var colour = ClaudeDesktopColors.For(
                Path.GetFileName(profile.Directory), profile.IsDefault);

            var frames = MacOSWindowList.ForPid(frontmost);
            var live = new HashSet<uint>();
            var moved = false;

            foreach (var frame in frames)
            {
                // Windows on other Spaces are still "on screen" as far as
                // CGWindowList is concerned, but they report coordinates in that
                // Space's frame — far outside any display. Tinting one would put a
                // coloured rectangle at a nonsense position on the Space you are
                // actually looking at.
                if (!OnAVisibleScreen(frame)) continue;

                live.Add(frame.WindowId);

                if (!Overlays.TryGetValue(frame.WindowId, out var overlay))
                {
                    overlay = Parked.Count > 0 ? Parked.Pop() : new OverlayWindow();
                    Overlays[frame.WindowId] = overlay;
                    moved = true;
                }

                if (overlay.Apply(colour, frame)) moved = true;
            }

            var now = Environment.TickCount64;
            if (moved) _lastMotionAt = now;

            if (_timer is not null)
            {
                _timer.Interval = now - _lastMotionAt < MotionLinger.TotalMilliseconds
                    ? MotionPoll
                    : ActivePoll;
            }

            // Windows that closed, moved to another Space, or belong to an
            // instance that is no longer in front.
            foreach (var (id, overlay) in Overlays.ToList())
            {
                if (live.Contains(id)) continue;
                Park(overlay);
                Overlays.Remove(id);
            }
        }

        // Frames are CoreGraphics points; Avalonia screen bounds are physical
        // pixels, hence the divide by scaling. A window only counts if its centre
        // falls inside a display.
        private static bool OnAVisibleScreen(MacOSWindowList.WindowFrame frame)
        {
            var screens = (Application.Current?.ApplicationLifetime
                    as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow?.Screens;

            // No window to ask, so fall back to the overlay windows we own, and
            // failing that assume visible rather than silently tinting nothing.
            screens ??= Overlays.Values.FirstOrDefault()?.Screens;
            if (screens is null) return true;

            var centreX = frame.X + frame.Width / 2;
            var centreY = frame.Y + frame.Height / 2;

            foreach (var screen in screens.All)
            {
                var scale = screen.Scaling;
                var left = screen.Bounds.X / scale;
                var top = screen.Bounds.Y / scale;
                var right = left + screen.Bounds.Width / scale;
                var bottom = top + screen.Bounds.Height / scale;

                if (centreX >= left && centreX < right && centreY >= top && centreY < bottom) return true;
            }

            return false;
        }

        private static void HideAll()
        {
            if (Overlays.Count == 0) return;

            foreach (var overlay in Overlays.Values) Park(overlay);
            Overlays.Clear();
        }

        private static void Park(OverlayWindow overlay)
        {
            overlay.Park();
            if (Parked.Count < MaxParked) Parked.Push(overlay);
        }

        private sealed class OverlayWindow : Window
        {
            private readonly Border _frame;
            private bool _shown;

            public OverlayWindow()
            {
                WindowDecorations = WindowDecorations.None;
                Background = Brushes.Transparent;
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
                Topmost = true;
                ShowInTaskbar = false;
                ShowActivated = false;
                CanResize = false;
                SizeToContent = SizeToContent.Manual;

                _frame = new Border
                {
                    BorderThickness = new Thickness(3),
                    CornerRadius = new CornerRadius(10),
                    IsHitTestVisible = false
                };
                Content = _frame;

                // Deliberately NOT ShowOnAllSpaces, unlike the orbs. This window
                // tracks one specific window's frame, so it belongs on the Space
                // that window is on; joining all Spaces would paint its rectangle
                // onto every other Space too.
                Opened += (_, _) => MakeClickThrough();
            }

            private Color _colour;
            private MacOSWindowList.WindowFrame _applied;

            // Returns whether anything actually changed, which is what drives the
            // poll rate. Everything here is skipped when the frame is unchanged:
            // at 60 Hz a static window should cost only the sample, not a layout
            // pass and a native setFrame every tick.
            // Hidden, not closed — see the note on Parked.
            public void Park()
            {
                Hide();
                _shown = false;
                _applied = default;
            }

            public bool Apply(Color colour, MacOSWindowList.WindowFrame frame)
            {
                if (colour != _colour)
                {
                    _colour = colour;
                    _frame.BorderBrush = new SolidColorBrush(colour);

                    // A wash this faint reads as a tint without costing text
                    // contrast; the border is what identifies the window.
                    _frame.Background = new SolidColorBrush(colour, 0.07);
                }

                if (_shown && frame == _applied) return false;
                _applied = frame;

                var scale = Screens.Primary?.Scaling ?? 1.0;

                Width = frame.Width;
                Height = frame.Height;
                Position = new PixelPoint((int)(frame.X * scale), (int)(frame.Y * scale));

                if (_shown) return true;
                _shown = true;
                Show();
                return true;
            }

            // Without this the tint would swallow every click meant for Claude.
            private void MakeClickThrough()
            {
                if (!OperatingSystem.IsMacOS()) return;

                if (TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle handle
                    && handle.NSWindow != IntPtr.Zero)
                {
                    objc_msgSend_bool(handle.NSWindow, sel_registerName("setIgnoresMouseEvents:"), true);
                }
            }

            [DllImport("/usr/lib/libobjc.A.dylib")]
            private static extern IntPtr sel_registerName(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

            [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
            private static extern void objc_msgSend_bool(IntPtr receiver, IntPtr selector,
                [MarshalAs(UnmanagedType.U1)] bool value);
        }
    }
}
