using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    public partial class OrbFlyout : Window
    {
        private const int FlyMs = 160;
        private static readonly TimeSpan FlyTick = TimeSpan.FromMilliseconds(1000.0 / 60);

        // Distance from the orb's centre to each button's centre, in
        // DIPs. Large enough that the side buttons clear the orb's
        // circle with a small gap, without spreading the arc so far
        // the buttons feel detached from the orb they belong to.
        private const double ArcRadius = 40;
        private const double ButtonHalf = 12;

        private DispatcherTimer? _flyTimer;
        private PixelPoint _flyFrom;
        private PixelPoint _flyTo;
        private long _flyStartedAt;

        private static readonly IBrush ArrangeNormalFill = new SolidColorBrush(Color.Parse("#E0202024"));
        private static readonly IBrush ArrangeActiveFill = new SolidColorBrush(Color.Parse("#E0B8860B"));

        private static readonly IBrush SpeakNormalFill = new SolidColorBrush(Color.Parse("#E0202024"));
        private static readonly IBrush SpeakActiveFill = new SolidColorBrush(Color.Parse("#E04A90D9"));

        public event Action? MicClicked;
        public event Action? ArrangeClicked;
        public event Action? SettingsClicked;
        public event Action? SpeakClicked;

        // Where the orb's centre maps to in this window's DIP space.
        // Computed by LayoutArc, read by OrbWindow.EnsureFlyoutShown to
        // position the window so the arc sits concentric with the orb.
        public double ArcOriginX { get; private set; }
        public double ArcOriginY { get; private set; }

        public OrbFlyout()
        {
            InitializeComponent();
            LayoutArc();

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

            SpeakButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                SpeakClicked?.Invoke();
            };

            MicButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                MicClicked?.Invoke();
            };

            Opened += (_, _) =>
            {
                this.ShowOnAllSpaces();
                this.AcceptFirstClick();
            };
        }

        public void SetMicVisible(bool visible)
        {
            MicButton.IsVisible = visible;
            LayoutArc();
        }

        // Computes button positions along a semicircular arc and sizes
        // the Canvas to the tight bounding box around all buttons.
        // Angles are in degrees, measured from the positive X axis with
        // Y pointing down (screen coordinates): 90° is straight down
        // (6 o'clock), <90° swings right (toward 5), >90° swings left
        // (toward 7).
        private void LayoutArc()
        {
            double[] angles;
            Grid[] buttons;

            // Kept symmetric about 90° (straight down) so the arc stays
            // centred under the orb whichever set is showing, and spread
            // wider as buttons are added rather than packed tighter —
            // ArcRadius is fixed, so the spacing between neighbours is
            // what has to give.
            if (MicButton.IsVisible)
            {
                angles = new[] { 135.0, 105.0, 75.0, 45.0 };
                buttons = new[] { ArrangeButton, SettingsButton, SpeakButton, MicButton };
            }
            else
            {
                angles = new[] { 125.0, 90.0, 55.0 };
                buttons = new[] { ArrangeButton, SettingsButton, SpeakButton };
            }

            var cx = new double[angles.Length];
            var cy = new double[angles.Length];
            for (int i = 0; i < angles.Length; i++)
            {
                var rad = angles[i] * Math.PI / 180.0;
                cx[i] = ArcRadius * Math.Cos(rad);
                cy[i] = ArcRadius * Math.Sin(rad);
            }

            double left = double.MaxValue, top = double.MaxValue;
            double right = double.MinValue, bottom = double.MinValue;
            for (int i = 0; i < angles.Length; i++)
            {
                left = Math.Min(left, cx[i] - ButtonHalf);
                right = Math.Max(right, cx[i] + ButtonHalf);
                top = Math.Min(top, cy[i] - ButtonHalf);
                bottom = Math.Max(bottom, cy[i] + ButtonHalf);
            }

            var w = Math.Ceiling(right - left);
            var h = Math.Ceiling(bottom - top);

            Root.Width = w;
            Root.Height = h;
            Width = w;
            Height = h;

            for (int i = 0; i < buttons.Length; i++)
            {
                Canvas.SetLeft(buttons[i], cx[i] - ButtonHalf - left);
                Canvas.SetTop(buttons[i], cy[i] - ButtonHalf - top);
            }

            ArcOriginX = -left;
            ArcOriginY = -top;
        }

        public void SetArranged(bool arranged)
        {
            ArrangeFill.Fill = arranged ? ArrangeActiveFill : ArrangeNormalFill;
        }

        // Blue fill and a stop-square glyph while speech is playing;
        // normal fill and speaker glyph otherwise. The button is its own
        // stop control, so it has to look like whichever it currently is.
        public void SetSpeaking(bool speaking)
        {
            SpeakFill.Fill = speaking ? SpeakActiveFill : SpeakNormalFill;
            SpeakGlyph.Text = speaking ? "⏹" : "\U0001F508";
        }

        public bool IsPointerOverFlyout => Root.IsPointerOver;

        public void ShowNear(PixelPoint from, PixelPoint to, Window owner)
        {
            if (IsVisible)
            {
                Position = to;
                return;
            }

            Position = from;
            Opacity = 0;
            Show();
            this.PlaceJustBehind(owner);
            AnimateTo(from, to);
        }

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
                var eased = 1 - Math.Pow(1 - t, 3);

                Position = new PixelPoint(
                    (int)Math.Round(_flyFrom.X + (_flyTo.X - _flyFrom.X) * eased),
                    (int)Math.Round(_flyFrom.Y + (_flyTo.Y - _flyFrom.Y) * eased));
                Opacity = eased;

                if (t < 1.0) return;

                _flyTimer!.Stop();
                Position = _flyTo;
                Opacity = 1;
                this.PlaceInFront();
            };
            _flyTimer.Start();
        }
    }
}
