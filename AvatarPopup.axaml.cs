using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // The agent's portrait, four times the size the chat panel shows it at.
    //
    // A window rather than an overlay inside the panel, for the same reason the
    // panel is a window rather than a flyout: the panel is 340pt wide and this
    // is 292 square, so anything drawn inside it would either be cropped or
    // force the panel to resize around a temporary thing.
    //
    // One instance, reused. Two of these open at once has no meaning, and a
    // singleton makes closing the previous one free.
    public partial class AvatarPopup : Window
    {
        private static AvatarPopup? _instance;

        private OpenClawAvatars.Avatar? _avatar;
        private ImageBrush? _brush;
        private int _frame;
        private DispatcherTimer? _timer;

        public AvatarPopup()
        {
            InitializeComponent();

            // Any click anywhere on it closes it — there is nothing in here to
            // interact with, so a click can only mean "done looking".
            PointerPressed += (_, _) => Dismiss();

            KeyDown += (_, e) =>
            {
                if (e.Key is Key.Escape or Key.Space or Key.Enter) Dismiss();
            };

            // Clicking outside is the main way out, and the one the user asked
            // for. Deferred by a post so a click that is on the way to this
            // window's own handler doesn't race the deactivate.
            Deactivated += (_, _) => Dispatcher.UIThread.Post(Dismiss, DispatcherPriority.Background);

            Opened += (_, _) => this.ShowOnAllSpaces();
        }

        public static bool IsOpen => _instance is { IsVisible: true };

        // near is where the portrait was clicked, in screen pixels — the popup
        // opens centred on it, so it grows out of the thing you clicked rather
        // than appearing somewhere unrelated.
        public static void Show(OpenClawAvatars.Avatar avatar, PixelPoint near)
        {
            _instance ??= new AvatarPopup();
            _instance.Present(avatar, near);
        }

        public static void Close()
        {
            if (_instance is { IsVisible: true }) _instance.Dismiss();
        }

        private void Present(OpenClawAvatars.Avatar avatar, PixelPoint near)
        {
            StopAnimation();

            _avatar = avatar;
            _frame = 0;

            _brush ??= new ImageBrush { Stretch = Stretch.UniformToFill };
            _brush.Source = avatar.Frames[0];
            Portrait.Fill = _brush;

            Position = Centred(near);

            if (!IsVisible) Show();
            Activate();

            if (!avatar.IsAnimated) return;

            // Closes over its own timer and avatar — same reason as the panel's:
            // a queued tick must not fire against a portrait that has since been
            // replaced, or a timer that has since been nulled.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(avatar.DelaysMs[0]) };
            var frames = avatar;

            timer.Tick += (_, _) => Advance(frames, timer);

            _timer = timer;
            _timer.Start();
        }

        // One frame on, as a named method rather than the body of the closure it
        // used to be — so a test can advance the animation itself instead of
        // waiting on a real timer.
        //
        // That is not a cosmetic preference. The test that covers this polled for
        // the second frame while the timer ran, and with a three-frame portrait
        // two ticks delivered in one dispatcher drain step straight past it from
        // the first frame to the third: the poll then never matched and the test
        // failed, but only when the rest of the suite was loading the machine
        // enough for ticks to bunch up. Same shape as the pulse ticker's flake
        // and the settings-collection one — a result that depends on what else is
        // running.
        //
        // The frame counter moved to the field that was already declared for it
        // and only ever reset; the closure had been shadowing it with a local.
        // The avatar and timer stay parameters, because the guard below is the
        // point: a queued tick must not fire against a portrait that has since
        // been replaced, or a timer that has since been nulled.
        internal void Advance(OpenClawAvatars.Avatar frames, DispatcherTimer? timer = null)
        {
            if (!ReferenceEquals(_avatar, frames) || _brush is null) return;

            _frame = (_frame + 1) % frames.Frames.Count;
            _brush.Source = frames.Frames[_frame];

            if (timer is not null)
            {
                timer.Interval = TimeSpan.FromMilliseconds(frames.DelaysMs[_frame]);
            }
        }

        // Centred on the click, then pulled back inside the screen it landed on
        // — a portrait opened from an orb near the right edge would otherwise
        // hang off it.
        private PixelPoint Centred(PixelPoint near)
        {
            var screen = Screens.ScreenFromPoint(near) ?? Screens.Primary;
            if (screen is null) return near;

            var scale = screen.Scaling;
            var work = screen.WorkingArea;

            var size = (int)(292 * scale);
            var x = Math.Clamp(near.X - size / 2, work.X, Math.Max(work.X, work.Right - size));
            var y = Math.Clamp(near.Y - size / 2, work.Y, Math.Max(work.Y, work.Bottom - size));

            return new PixelPoint(x, y);
        }

        private void Dismiss()
        {
            if (!IsVisible) return;

            StopAnimation();
            Hide();
        }

        private void StopAnimation()
        {
            _timer?.Stop();
            _timer = null;
        }
    }
}
