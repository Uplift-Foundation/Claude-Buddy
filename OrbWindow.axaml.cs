using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;

namespace ClaudeBuddy
{
    public partial class OrbWindow : Window
    {
        private static readonly Color IdleColor = Color.Parse("#5B7A94");       // calm slate blue
        private static readonly Color GeneratingColor = Color.Parse("#8B6FD1"); // violet
        private static readonly Color WaitingColor = Color.Parse("#E8983B");    // amber

        // A session's /color goes on the orb's border and letter, leaving the
        // fill to mean what it always has.
        //
        // These are Claude Code's own accent colors, which it renders as
        // xterm-256 indices (index = 16 + 36r + 6g + b over the levels
        // 0/95/135/175/215/255). Three are confirmed from what Claude Code
        // actually emitted in a terminal — green is index 35, and the two
        // auto-assigned accents seen in other sessions were 37 and 175. The
        // rest are the same-band cube colors for their hue, i.e. educated
        // guesses; correct one by reading the escape sequence Claude Code
        // writes for that color (`tmux capture-pane -p -e`, look for
        // `38;5;<n>`) if one ever looks off.
        private static readonly Dictionary<string, Color> AgentColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = Color.Parse("#D75F5F"),     // 167
            ["orange"] = Color.Parse("#D7875F"),  // 173
            ["yellow"] = Color.Parse("#D7AF5F"),  // 179
            ["green"] = Color.Parse("#00AF5F"),   // 35  — confirmed
            ["teal"] = Color.Parse("#00AFAF"),    // 37  — confirmed (auto-assigned)
            ["cyan"] = Color.Parse("#00AFAF"),    // 37
            ["blue"] = Color.Parse("#5F87D7"),    // 68
            ["purple"] = Color.Parse("#875FD7"),  // 98
            ["violet"] = Color.Parse("#875FD7"),  // 98
            ["magenta"] = Color.Parse("#D787AF"), // 175 — confirmed (auto-assigned)
            ["pink"] = Color.Parse("#D787AF"),    // 175
            ["gray"] = Color.Parse("#808080"),    // 244
            ["grey"] = Color.Parse("#808080"),    // 244
            ["white"] = Color.Parse("#FFFFFF")
        };

        // What an orb looks like with no /color set: the original faint hairline
        // and near-white letter.
        private static readonly Color PlainStroke = Color.Parse("#22FFFFFF");
        private static readonly Color PlainGlyph = Color.Parse("#DDFFFFFF");

        public string SessionId { get; }

        private string _lastState = "";
        private string _lastColor = "";

        private readonly SolidColorBrush _orbBrush = new(IdleColor);
        private readonly ColorTransition _colorTransition;
        private readonly ScaleTransform _orbScale = new();
        private CancellationTokenSource? _pulseCts;

        public OrbWindow(string sessionId)
        {
            SessionId = sessionId;
            InitializeComponent();

            _colorTransition = new ColorTransition
            {
                Property = SolidColorBrush.ColorProperty,
                Duration = TimeSpan.FromMilliseconds(300),
                Easing = new QuadraticEaseOut()
            };
            _orbBrush.Transitions = new Transitions { _colorTransition };

            Orb.Fill = _orbBrush;
            Glow.Fill = _orbBrush;
            Orb.RenderTransform = _orbScale;

            // Unlike WPF, Loaded fires *after* the first UpdateFrom here, so
            // honor any state that already arrived instead of stomping it.
            Loaded += (_, _) => ApplyState(string.IsNullOrEmpty(_lastState) ? "idle" : _lastState);

            Opened += (_, _) => this.ShowOnAllSpaces();
        }

        public void UpdateFrom(SessionStatus status)
        {
            _lastStatus = status;

            var folder = string.IsNullOrEmpty(status.Cwd)
                ? ""
                : System.IO.Path.GetFileName(status.Cwd.TrimEnd('\\', '/'));

            // The chat's own name is the better label — it says what the
            // session is *doing*, and two sessions in one repo no longer look
            // identical. Falls back to the folder until Claude Code names it.
            var label = string.IsNullOrEmpty(status.Title) ? folder : status.Title;

            ToolTip.SetTip(Root, string.IsNullOrEmpty(status.Cwd)
                ? (string.IsNullOrEmpty(label) ? SessionId : label)
                : $"{label}\n{status.Cwd}");

            Glyph.Text = GlyphFor(label);
            ApplyAccent(status.Color);

            SessionInfoItem.Header = string.IsNullOrEmpty(label) ? SessionId : label;
            SessionPathItem.Header = status.Cwd;
            SessionPathItem.IsVisible = !string.IsNullOrEmpty(status.Title)
                                        && !string.IsNullOrEmpty(status.Cwd);

            if (status.State != _lastState)
            {
                _lastState = status.State;
                if (IsLoaded)
                {
                    ApplyState(status.State);
                }
                // else: Loaded handler applies _lastState once the window is up.
            }
        }

        // /color identifies *which* session; the fill keeps saying what it's
        // doing. An unknown or missing color name leaves the orb looking the
        // way it always has, so a future addition to Claude Code's palette
        // degrades quietly instead of throwing.
        private void ApplyAccent(string colorName)
        {
            if (colorName == _lastColor) return;
            _lastColor = colorName;

            Color accent = default;
            var known = !string.IsNullOrEmpty(colorName)
                        && AgentColors.TryGetValue(colorName, out accent);

            Orb.Stroke = new SolidColorBrush(known ? accent : PlainStroke);
            Orb.StrokeThickness = known ? 2 : 1;
            Glyph.Foreground = new SolidColorBrush(known ? accent : PlainGlyph);
        }

        private static string GlyphFor(string label)
        {
            label = label.TrimStart();
            if (label.Length == 0) return "•";

            // Never cut a surrogate pair in half — a title starting with an
            // emoji would render as a broken box.
            var first = char.IsHighSurrogate(label[0]) && label.Length > 1 ? label[..2] : label[..1];
            return first.ToUpperInvariant();
        }

        private void ApplyState(string state)
        {
            switch (state)
            {
                case "waiting":
                    AnimateColor(WaitingColor, TimeSpan.FromMilliseconds(300));
                    StartPulse(1.22, TimeSpan.FromMilliseconds(500), new QuadraticEaseOut());
                    break;
                case "generating":
                    AnimateColor(GeneratingColor, TimeSpan.FromMilliseconds(300));
                    StartPulse(1.14, TimeSpan.FromMilliseconds(900), new SineEaseInOut());
                    break;
                default:
                    StopPulse();
                    AnimateColor(IdleColor, TimeSpan.FromMilliseconds(400));
                    StartPulse(1.06, TimeSpan.FromSeconds(2.2), new SineEaseInOut());
                    break;
            }
        }

        private void AnimateColor(Color to, TimeSpan duration)
        {
            _colorTransition.Duration = duration;
            _orbBrush.Color = to;
        }

        private void StartPulse(double to, TimeSpan duration, Easing easing)
        {
            _pulseCts?.Cancel();
            _pulseCts = new CancellationTokenSource();

            var animation = new Animation
            {
                Duration = duration,
                IterationCount = IterationCount.Infinite,
                PlaybackDirection = PlaybackDirection.Alternate,
                Easing = easing,
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0d),
                        Setters =
                        {
                            new Setter(ScaleTransform.ScaleXProperty, 1.0),
                            new Setter(ScaleTransform.ScaleYProperty, 1.0)
                        }
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1d),
                        Setters =
                        {
                            new Setter(ScaleTransform.ScaleXProperty, to),
                            new Setter(ScaleTransform.ScaleYProperty, to)
                        }
                    }
                }
            };

            // Run against the Orb visual; Avalonia's transform animator finds
            // the ScaleTransform via the visual's RenderTransform.
            _ = animation.RunAsync(Orb, _pulseCts.Token);
        }

        private void StopPulse()
        {
            _pulseCts?.Cancel();
            _pulseCts = null;
        }

        // --- Click, dragging & context menu ---
        // Left-press starts as a potential click; it becomes a drag once the
        // pointer moves past a small threshold. A clean click jumps to the
        // session's terminal (macOS, best-effort — see TerminalFocuser).
        // Dragged position is only honored until the next time the active
        // session set changes (add/remove), at which point SessionManager
        // reflows the whole stack. That's an intentional tradeoff to keep
        // the stack tidy as sessions come and go.

        private SessionStatus? _lastStatus;
        private bool _pressed;
        private bool _dragging;
        private PixelPoint _windowStart;
        private PixelPoint _pointerStart;

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _pressed = true;
                _dragging = false;
                _windowStart = Position;
                _pointerStart = this.PointToScreen(e.GetPosition(this));
                e.Pointer.Capture(this);
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_pressed) return;

            var current = this.PointToScreen(e.GetPosition(this));
            var dx = current.X - _pointerStart.X;
            var dy = current.Y - _pointerStart.Y;

            if (!_dragging && Math.Abs(dx) < 6 && Math.Abs(dy) < 6) return;

            _dragging = true;
            Position = new PixelPoint(_windowStart.X + dx, _windowStart.Y + dy);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_pressed) return;

            _pressed = false;
            e.Pointer.Capture(null);

            if (!_dragging)
            {
                TerminalFocuser.Focus(_lastStatus);
            }
        }

        private void ResetIdle_Click(object? sender, RoutedEventArgs e)
        {
            SessionManager.Instance?.ResetSessionToIdle(SessionId);
        }

        private void Exit_Click(object? sender, RoutedEventArgs e)
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
    }
}
