using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // The small always-on-top window that appears just below-and-right of an
    // orb on hover. Two buttons today: an arrange button (always visible)
    // and a mic button (only when voice input is enabled), stacked along
    // the same diagonal away from the orb.
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

        // Where the first button's centre sits inside this window, in DIPs.
        // The arrange button is at Margin="2,2,0,0" in Root, so it spans
        // (2,2)-(26,26) and its centre is (14,14). OrbWindow uses this to
        // line the fly-out animation's start up with the orb — this window's
        // own geometry, stated here rather than guessed at from the other
        // side.
        public const double ButtonCentreOffset = 14;

        private static readonly IBrush ArrangeNormalFill = new SolidColorBrush(Color.Parse("#E0202024"));
        private static readonly IBrush ArrangeActiveFill = new SolidColorBrush(Color.Parse("#E0B8860B"));

        public event Action? MicClicked;
        public event Action? ArrangeClicked;

        public OrbFlyout()
        {
            InitializeComponent();

            ArrangeButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                ArrangeClicked?.Invoke();
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

        public void SetMicVisible(bool visible)
        {
            MicButton.IsVisible = visible;
            var size = visible ? 52 : 26;
            Root.Width = size;
            Root.Height = size;
            Width = size;
            Height = size;
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

        // Animates from the orb's own position to its resting spot near it —
        // the "flies out" motion the feature is named for — rather than
        // just appearing there. Both points are physical screen pixels,
        // already computed by the caller (OrbWindow.EnsureFlyoutShown) via
        // PointToScreen — this window has no reason to know the orb's own
        // geometry, just where it's coming from and going to.
        // `owner` is only used for z-order: the fly-out is meant to look like it
        // comes out from under the orb, so this window spends the animation
        // behind it and returns to the front on arrival. It must not stay
        // behind — see PlaceInFront for the clicks that costs.
        public void ShowNear(PixelPoint from, PixelPoint to, Window owner)
        {
            if (IsVisible)
            {
                // Already up (recording kept it visible through a hover
                // that came back) — just track, no need to replay the
                // fly-out motion for a window that never left. Deliberately
                // no z-order change either: it is already in front, and
                // dropping it behind the orb here is what would leave a
                // window nothing ever raises again.
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

                // Arrived: back in front, so the mic is fully clickable where it
                // has come to rest rather than only where it clears the orb's
                // window. Done here rather than by the caller because this is
                // the only place that knows the motion is over.
                this.PlaceInFront();
            };
            _flyTimer.Start();
        }
    }
}
