using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // The small always-on-top window that appears below an orb on hover,
    // with its buttons arranged in a semicircular arc. The arrange button
    // (always visible) and mic button (voice-input-gated) sit at the
    // 7-o'clock and 5-o'clock positions respectively — symmetric below
    // the orb, looking like a radial menu.
    //
    // Owned one-per-orb by OrbWindow, created lazily on first hover rather
    // than in every orb's constructor, so an orb nobody ever hovers never
    // pays for a second window.
    public partial class OrbFlyout : Window
    {
        // One-shot, not the shared low-fps ticker OrbWindow's pulse uses —
        // that exists to keep *continuous* animation cheap across many
        // simultaneously-pulsing orbs, which doesn't apply to a ~160ms
        // one-off that only runs while a hover is actively landing.
        private const int FlyMs = 160;
        private static readonly TimeSpan FlyTick = TimeSpan.FromMilliseconds(1000.0 / 60);

        private DispatcherTimer? _flyTimer;
        private PixelPoint _flyFrom;
        private PixelPoint _flyTo;
        private long _flyStartedAt;

        private static readonly IBrush ArrangeNormalFill = new SolidColorBrush(Color.Parse("#E0202024"));
        private static readonly IBrush ArrangeActiveFill = new SolidColorBrush(Color.Parse("#E0B8860B"));

        public event Action? MicClicked;
        public event Action? ArrangeClicked;
        public event Action? SettingsClicked;

        public OrbFlyout()
        {
            InitializeComponent();

            ArrangeButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                ArrangeClicked?.Invoke();
            };

            SettingsButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                SettingsClicked?.Invoke();
            };

            MicButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                MicClicked?.Invoke();
            };

            Opened += (_, _) =>
            {
                this.ShowOnAllSpaces();

                // Same fix OrbWindow needs, for the same reason (a
                // background app's first click on an inactive window is
                // otherwise eaten by macOS) — harmless to call again here,
                // since MacOSWindowExtensions installs it once, class-wide,
                // no matter which window asks first.
                this.AcceptFirstClick();
            };
        }

        // Three-button layout (with mic): 94x28.
        // Two-button layout (without mic): 60x28.
        public void SetMicVisible(bool visible)
        {
            MicButton.IsVisible = visible;
            if (visible)
            {
                Root.Width = 94;
                Root.Height = 28;
                Width = 94;
                Height = 28;
            }
            else
            {
                Root.Width = 60;
                Root.Height = 28;
                Width = 60;
                Height = 28;
            }
        }

        // Dark goldenrod fill when orbs are arranged (indicating "click to
        // return"), the usual dark fill otherwise.
        public void SetArranged(bool arranged)
        {
            ArrangeFill.Fill = arranged ? ArrangeActiveFill : ArrangeNormalFill;
        }

        // True while the pointer is anywhere over this window — OrbWindow
        // checks this before deciding whether to actually hide, so moving
        // between the orb and this flyout (or just sitting on the flyout
        // itself) never triggers a hide meant for "the pointer left both".
        public bool IsPointerOverFlyout => Root.IsPointerOver;

        // Animates from the orb's centre to the resting spot below it —
        // slides straight down so both buttons appear to emerge from behind
        // the orb. Both points are physical screen pixels, already computed
        // by the caller (OrbWindow.EnsureFlyoutShown) via PointToScreen.
        // `owner` is only used for z-order: the flyout starts behind the
        // orb during animation and moves in front on arrival.
        public void ShowNear(PixelPoint from, PixelPoint to, Window owner)
        {
            if (IsVisible)
            {
                // Already up (recording kept it visible through a hover
                // that came back) — just track, no need to replay the
                // fly-out motion for a window that never left.
                Position = to;
                return;
            }

            Position = from;
            Opacity = 0;
            Show();

            // After Show(), which is both what gives this window a handle to
            // order and what put it in front of the orb to begin with.
            this.PlaceJustBehind(owner);

            AnimateTo(from, to);
        }

        // `new`, not an override — WindowBase.Hide() isn't virtual — but
        // every caller reaches this one anyway: OrbWindow only ever holds a
        // reference typed as OrbFlyout, never as the base Window, so the
        // compile-time type is what picks the overload here.
        public new void Hide()
        {
            _flyTimer?.Stop();
            base.Hide();
        }

        private void AnimateTo(PixelPoint from, PixelPoint to)
        {
            _flyTimer?.Stop();
            _flyFrom = from;
            _flyTo = to;
            _flyStartedAt = Environment.TickCount64;

            _flyTimer = new DispatcherTimer { Interval = FlyTick };
            _flyTimer.Tick += (_, _) =>
            {
                var elapsed = Environment.TickCount64 - _flyStartedAt;
                var t = Math.Min(1.0, elapsed / (double)FlyMs);

                // Ease-out cubic: quick off the mark, settling gently into
                // place rather than arriving with a jolt.
                var eased = 1 - Math.Pow(1 - t, 3);

                Position = new PixelPoint(
                    (int)Math.Round(_flyFrom.X + (_flyTo.X - _flyFrom.X) * eased),
                    (int)Math.Round(_flyFrom.Y + (_flyTo.Y - _flyFrom.Y) * eased));
                Opacity = eased;

                if (t < 1.0) return;

                _flyTimer!.Stop();
                Position = _flyTo;
                Opacity = 1;

                // Arrived: back in front, so buttons are fully clickable
                // rather than only where they clear the orb's window.
                this.PlaceInFront();
            };
            _flyTimer.Start();
        }
    }
}
