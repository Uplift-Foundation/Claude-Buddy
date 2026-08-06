using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // The small always-on-top window that appears just below-and-right of an
    // orb on hover. Only the voice-dictation mic lives in it today, but it's
    // built to gain siblings under it (see the XAML comment on Root) rather
    // than to hold exactly one button forever.
    //
    // Owned one-per-orb by OrbWindow, created lazily on first hover rather
    // than in every orb's constructor, so an orb nobody ever hovers never
    // pays for a second window.
    public partial class OrbFlyout : Window
    {
        // Below and to the right, touching the orb's own circle rather than
        // its window's square bounding box: the orb is a 36px circle centred
        // in a 56x56 window, so along the down-right diagonal its true edge
        // sits inset from that box's corner (46,46) at roughly
        // 28 + 18*cos(45°) ≈ 41, not 46 itself. Anchoring at the box corner
        // — the first version of this — left a few pixels of dead space the
        // line had to visibly cross before actually touching the orb.
        //
        // (Team-member orbs are drawn smaller — see OrbWindow.MemberScale —
        // so their true edge sits a little further inside this same 41,41
        // point; not accounted for here, since the difference is a couple of
        // pixels and not worth a per-orb-scale offset for a decorative line.)
        private const int OffsetX = 41;
        private const int OffsetY = 41;

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

        public event Action? MicClicked;

        public OrbFlyout()
        {
            InitializeComponent();

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

        // True while the pointer is anywhere over this window — OrbWindow
        // checks this before deciding whether to actually hide, so moving
        // between the orb and this flyout (or just sitting on the flyout
        // itself) never triggers a hide meant for "the pointer left both".
        public bool IsPointerOverFlyout => Root.IsPointerOver;

        // Animates out from the orb's own position to its resting spot —
        // the "flies out" motion the feature is named for — rather than
        // just appearing there. Recomputed from the orb's current screen
        // position each time, not tracked continuously: this is only ever
        // shown while hovering or recording, both of which end before the
        // orb could plausibly have moved again.
        public void ShowNear(PixelPoint orbPosition)
        {
            var target = new PixelPoint(orbPosition.X + OffsetX, orbPosition.Y + OffsetY);

            if (IsVisible)
            {
                // Already up (recording kept it visible through a hover
                // that came back) — just track, no need to replay the
                // fly-out motion for a window that never left.
                Position = target;
                return;
            }

            Position = orbPosition;
            Opacity = 0;
            Show();
            AnimateTo(from: orbPosition, to: target);
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
            };
            _flyTimer.Start();
        }
    }
}
