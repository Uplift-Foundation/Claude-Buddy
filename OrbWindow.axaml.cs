using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Shapes;
using Avalonia.Styling;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    public partial class OrbWindow : Window
    {
        // The three state colours live in OrbColors now — they're settable, and
        // the tray icon reads the same three, so a static field here would have
        // been a second copy for both reasons.
        //
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
        // and near-white letter. PlainLink is the same idea for the team arrow,
        // but brighter — the hairline works because it sits on the orb's own
        // fill, and an arrow has nothing behind it but the desktop.
        private static readonly Color PlainStroke = Color.Parse("#22FFFFFF");
        private static readonly Color PlainGlyph = Color.Parse("#DDFFFFFF");
        private static readonly Color PlainLink = Color.Parse("#FFCCCCCC");

        public string SessionId { get; }

        private string _lastState = "";
        private string _lastColor = "";
        private string _lastGlyphName = "";

        // Colour for the team arrow leaving this orb, when it has one. Follows
        // /color so several members pointing at one lead stay apart; sessions
        // without a colour share the neutral. See TeamLinks.
        public Color LinkColor { get; private set; } = PlainLink;

        // Seeded from the settings-backed colour at field-init time, the same way
        // SessionManager seeds OrbsVisible from ClaudeBuddySettings.ShowOrbs.
        private readonly SolidColorBrush _orbBrush = new(OrbColors.Idle);

        private readonly RadialGradientBrush _glowBrush = new()
        {
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientStops = GlowStops(OrbColors.Idle)
        };

        private readonly ColorTransition _colorTransition;
        private readonly ScaleTransform _orbScale = new();

        // Flat red rather than a fourth entry in OrbColors: this isn't a
        // session state Claude Code reports, it's purely local UI feedback
        // for "the mic is listening", so it has no reason to be user
        // configurable the way idle/generating/waiting are.
        private static readonly Color RecordingColor = Color.Parse("#D93B3B");

        private bool _recording;
        private VoiceRecorder? _recorder;
        private DispatcherTimer? _recordingCap;

        // Created lazily on first hover (see EnsureFlyoutShown), not here —
        // most orbs are never hovered in a given run, and none of them should
        // pay for a second window until one actually is.
        private OrbFlyout? _flyout;

        // Bridges hover between two separate OS windows (the orb and its
        // flyout): a bare PointerExited on either one would hide the flyout
        // the instant the cursor crosses from one window into the other,
        // before it ever reaches the second window. Scheduling the hide and
        // cancelling it if either window reports the pointer back within the
        // grace period turns that into a single smooth handoff instead.
        private DispatcherTimer? _hideFlyoutTimer;

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

            Glow.Fill = _glowBrush;
            Orb.RenderTransform = _orbScale;

            Root.PointerEntered += (_, _) =>
            {
                CancelFlyoutHide();
                EnsureFlyoutShown();
            };
            Root.PointerExited += (_, _) => ScheduleFlyoutHide();

            // Unlike WPF, Loaded fires *after* the first UpdateFrom here, so
            // honor any state that already arrived instead of stomping it.
            Loaded += (_, _) => ApplyState(string.IsNullOrEmpty(_lastState) ? "idle" : _lastState);

            Opened += (_, _) =>
            {
                this.ShowOnAllSpaces();

                // Otherwise the first click on an orb is spent activating the
                // app and never reaches it — see AcceptFirstClick.
                this.AcceptFirstClick();
            };

            // A closed orb must leave the shared ticker or it keeps being ticked.
            Closed += (_, _) => Pulsing.Remove(this);

            // A session going away mid-dictation (the window closing) must
            // not leave a capture thread or a native mic handle running.
            Closed += (_, _) => CancelRecording();

            // The flyout is a second, independent top-level window — it
            // outlives this one unless told otherwise. Stopping the hide
            // timer first, not just closing the flyout, matters because a
            // tick already queued on the dispatcher would otherwise run
            // after this and touch a window that no longer exists.
            Closed += (_, _) =>
            {
                _hideFlyoutTimer?.Stop();
                _flyout?.Close();
            };
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

            // An agent's own name beats all of it. Every member of a team
            // inherits the team session's title, so a team of four drew the
            // same letter four times and said nothing about which agent was
            // which — while the terminal had been calling them MenuUX,
            // Narrative and HitReactSpec the whole time. The title still gets
            // said, in the tooltip, because "which team" is worth knowing too.
            var name = string.IsNullOrEmpty(status.Agent) ? label : status.Agent;

            var described = string.IsNullOrEmpty(status.Agent) || string.IsNullOrEmpty(label)
                ? name
                : $"{status.Agent} · {label}";

            ToolTip.SetTip(Root, string.IsNullOrEmpty(status.Cwd)
                ? (string.IsNullOrEmpty(described) ? SessionId : described)
                : $"{described}\n{status.Cwd}");

            // Above the orb, not at the pointer (Avalonia's default for a
            // tooltip). The mic flyout sits below and to the right, which is
            // exactly where a pointer-placed tooltip lands, and a tooltip is its
            // own always-on-top window — so it covered the mic and swallowed the
            // clicks meant for it. Caught with WindowFromPoint over the mic's
            // circle: a 160x46 tooltip window owned most of it.
            //
            // Placement rather than suppressing the tooltip while the flyout is
            // up: the name and path are worth having on hover whether or not
            // you're reaching for the mic, and moving it costs nothing, while
            // hiding it would trade one annoyance for another.
            ToolTip.SetPlacement(Root, PlacementMode.Top);

            _lastGlyphName = name;
            Glyph.Text = GlyphFor(name);
            ApplyAccent(status.Color);
            SetTeamRole(!string.IsNullOrEmpty(status.Lead));

            SessionInfoItem.Header = string.IsNullOrEmpty(described) ? SessionId : described;
            SessionPathItem.Header = status.Cwd;
            SessionPathItem.IsVisible = !string.IsNullOrEmpty(status.Title)
                                        && !string.IsNullOrEmpty(status.Cwd);

            if (status.State != _lastState)
            {
                _lastState = status.State;
                if (IsLoaded && !_recording)
                {
                    ApplyState(status.State);
                }
                // else if !IsLoaded: Loaded handler applies _lastState once the
                // window is up. Else (_recording): the mic's red pulse owns
                // the orb's colour/motion right now — StopRecording restores
                // whatever _lastState ends up being once dictation finishes,
                // so a state change mid-recording isn't lost, just deferred.
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
            LinkColor = known ? accent : PlainLink;
        }

        // --- agent teams ------------------------------------------------------
        // A team member is drawn smaller than the session that leads it, so a
        // team reads as one lead with its agents rather than as several equal
        // sessions that happen to be next to each other. Deliberately only the
        // *drawing* shrinks: the window stays 56x56, so the stack spacing, the
        // drag target, and every remembered position keep working unchanged,
        // and a member that later loses its team grows back with no relayout.

        private const double MemberScale = 0.72;

        // Half the orb's drawn width, in DIPs — where TeamLinks stops the arrow
        // so it doesn't run under the orb.
        public double OrbRadius { get; private set; } = 18;

        private bool _isTeamMember;

        public void SetTeamRole(bool isTeamMember)
        {
            if (_isTeamMember == isTeamMember) return;
            _isTeamMember = isTeamMember;

            var scale = isTeamMember ? MemberScale : 1.0;

            Orb.Width = Orb.Height = 36 * scale;
            Glow.Width = Glow.Height = 56 * scale;
            Glyph.FontSize = BaseGlyphFontSize * scale;
            OrbRadius = 18 * scale;
        }

        // Smaller with two letters than with one, so the wider glyph still
        // fits inside the same 36px circle rather than crowding its edge.
        private static double BaseGlyphFontSize => ClaudeBuddySettings.TwoLetterGlyphs ? 12.0 : 16.0;

        // Settings' "Two-letter initials" toggle changes how every already-
        // open orb's glyph reads without waiting for that session's next
        // hook update — see SessionManager.ReapplyGlyphs, which calls this
        // on each one. Re-derives from _lastGlyphName rather than the full
        // SessionStatus: nothing else about the orb needs to change, just
        // the text and the font size sitting under it.
        public void ReapplyGlyph()
        {
            Glyph.Text = GlyphFor(_lastGlyphName);
            Glyph.FontSize = BaseGlyphFontSize * (_isTeamMember ? MemberScale : 1.0);
        }

        private static string GlyphFor(string label)
        {
            label = label.TrimStart();
            if (label.Length == 0) return "•";

            if (!ClaudeBuddySettings.TwoLetterGlyphs)
            {
                return FirstGrapheme(label).ToUpperInvariant();
            }

            // Two words get one letter each — the initials a person would
            // write by hand ("Menu UX" -> "Mu") — rather than two letters
            // from the first word alone, which reads as a typo of it
            // ("Menu UX" -> "Me"). A single word falls back to its own
            // first two letters, since there's nothing else to draw from.
            //
            // Upper then lower, not both upper: two capitals side by side
            // reads as an acronym ("MU"), where the point here is a little
            // word-shaped mark ("Mu") — same reason a monogram is "Mu", not
            // "MU". Only the letter case changes; which letters are picked
            // is exactly the same either way.
            var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2)
            {
                return FirstGrapheme(words[0]).ToUpperInvariant() + FirstGrapheme(words[1]).ToLowerInvariant();
            }

            var first = FirstGrapheme(label);
            var rest = label[first.Length..];
            var second = rest.Length > 0 ? FirstGrapheme(rest) : "";
            return first.ToUpperInvariant() + second.ToLowerInvariant();
        }

        // One printable character, or a full surrogate pair if the string
        // starts with one (e.g. an emoji) — never split in half, which is
        // what renders as a broken box instead of the emoji.
        private static string FirstGrapheme(string s) =>
            s.Length > 1 && char.IsHighSurrogate(s[0]) ? s[..2] : s[..1];

        // The colour comes from OrbColors so this switch is about *motion* only —
        // one state-to-colour mapping in the app, not two that can drift apart.
        private void ApplyState(string state)
        {
            var color = OrbColors.For(state);

            switch (state)
            {
                case "waiting":
                    AnimateColor(color, TimeSpan.FromMilliseconds(300), state);
                    StartPulse(1.22, TimeSpan.FromMilliseconds(500), new QuadraticEaseOut());
                    break;
                case "generating":
                    AnimateColor(color, TimeSpan.FromMilliseconds(300), state);
                    StartPulse(1.14, TimeSpan.FromMilliseconds(900), new SineEaseInOut());
                    break;
                default:
                    StopPulse();
                    AnimateColor(color, TimeSpan.FromMilliseconds(400), state);
                    StartPulse(1.06, TimeSpan.FromSeconds(2.2), new SineEaseInOut());
                    break;
            }
        }

        // The halo is a claim on your attention, so only the two states that
        // have something to say make it. Idle is what most orbs are in most of
        // the time, and glowing about it spends the screen's whole attention
        // budget on the one state that wants none of it — the slow breath is
        // enough to say the session is still there, and the fill and hairline
        // still say where it is.
        //
        // A custom idle colour makes the point sharply: a dark one (the default
        // is already nearly black) renders as a smudge that darkens whatever
        // sits under it rather than as light.
        //
        // Asked in one place, from the state alone, because it's read both by
        // ApplyState and by ReapplyStateColors and the two must not drift —
        // the same reason the colours themselves live in OrbColors.
        private static bool GlowsFor(string state) => state is "waiting" or "generating";

        // Changing a colour in settings is not a state change, and UpdateFrom only
        // calls ApplyState when status.State actually differs — so without this an
        // orb would keep its old fill until its session next did something, which
        // for a quiet session is never.
        //
        // Two things it deliberately doesn't do. It doesn't re-run ApplyState:
        // StartPulse resets the breath's phase, so every orb on screen would jerk
        // in step with the pointer. And it barely fades — 60ms, not the 300-400ms
        // a real state change gets — because the picker raises its change event on
        // every pointer move, and a third of a second of easing leaves the orb
        // trailing the cursor, reading as lag rather than as a live preview. At
        // 60ms each frame lands most of the way there and the orb tracks the
        // spectrum. The glow already snaps, since GlowStops is assigned rather
        // than animated, so this also stops the two disagreeing mid-drag.
        private static readonly TimeSpan SettingsColorFade = TimeSpan.FromMilliseconds(60);

        public void ReapplyStateColors()
        {
            // Not up yet: the Loaded handler applies _lastState with the new
            // colours anyway, which also covers an orb created while orbs were
            // hidden.
            if (!IsLoaded) return;

            var state = string.IsNullOrEmpty(_lastState) ? "idle" : _lastState;
            AnimateColor(OrbColors.For(state), SettingsColorFade, state);
        }

        private void AnimateColor(Color to, TimeSpan duration, string state)
        {
            _colorTransition.Duration = duration;
            _orbBrush.Color = to;

            // Hidden rather than made transparent: an invisible ellipse isn't
            // rendered at all, and there's no point rebuilding four gradient
            // stops for something nobody can see.
            Glow.IsVisible = GlowsFor(state);
            if (Glow.IsVisible) _glowBrush.GradientStops = GlowStops(to);
        }

        // Opaque at the centre, gone by the edge — the same falloff a blur gave,
        // without re-blurring 56x56 pixels sixty times a second.
        // The glow's gradient offsets are fractions of the *radius* (28px), and
        // the orb covers the inner 18px — so anything before offset 0.64 is hidden
        // behind the orb and contributes nothing. Hold the colour flat out to
        // there and fade over the visible ring, which is where the blur used to
        // put its bloom.
        private static GradientStops GlowStops(Color color) => new()
        {
            new GradientStop(Color.FromArgb(150, color.R, color.G, color.B), 0.0),
            new GradientStop(Color.FromArgb(150, color.R, color.G, color.B), 0.64),
            new GradientStop(Color.FromArgb(95, color.R, color.G, color.B), 0.82),
            new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0)
        };

        // One shared ticker drives every orb's pulse instead of an Avalonia
        // Animation per window. Avalonia animations run at the display's frame
        // rate, and each frame re-renders the whole (transparent, topmost) orb
        // window — measured at roughly 8% of a core per orb at 60Hz. The pulse is
        // a slow breath, so a much lower rate is indistinguishable and costs a
        // third as much. Hidden orbs are skipped entirely, which the old
        // animation never did: Hide() left it running.
        private const double PulseFps = 20;

        private static readonly List<OrbWindow> Pulsing = new();
        private static DispatcherTimer? _ticker;

        private double _pulseTo = 1.0;
        private double _pulsePeriodMs = 2200;
        private long _pulseStartedAt;

        private void StartPulse(double to, TimeSpan duration, Easing easing)
        {
            // Duration is a half-cycle in the old alternating animation, so a full
            // breath is twice it. Easing is implied by the cosine below.
            _pulseTo = to;
            _pulsePeriodMs = duration.TotalMilliseconds * 2;
            _pulseStartedAt = Environment.TickCount64;

            if (!Pulsing.Contains(this)) Pulsing.Add(this);
            EnsureTicker();
        }

        private static void EnsureTicker()
        {
            // Restart as well as create: the tick handler stops the timer once the
            // last orb stops pulsing, so a returning session has to be able to
            // wake it again.
            if (_ticker is not null)
            {
                if (!_ticker.IsEnabled) _ticker.Start();
                return;
            }

            _ticker = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / PulseFps)
            };
            _ticker.Tick += (_, _) =>
            {
                for (var i = Pulsing.Count - 1; i >= 0; i--) Pulsing[i].TickPulse();
                if (Pulsing.Count == 0) _ticker!.Stop();
            };
            _ticker.Start();
        }

        private void TickPulse()
        {
            // Nothing on screen, nothing to animate — the whole point of the
            // "Show orbs" toggle was to stop this work, and it never did.
            if (!IsVisible)
            {
                _orbScale.ScaleX = _orbScale.ScaleY = 1.0;
                return;
            }

            var phase = (Environment.TickCount64 - _pulseStartedAt) % _pulsePeriodMs / _pulsePeriodMs;
            var eased = (1 - Math.Cos(phase * 2 * Math.PI)) / 2;   // 0 -> 1 -> 0, smooth at both ends
            var scale = 1.0 + (_pulseTo - 1.0) * eased;

            _orbScale.ScaleX = scale;
            _orbScale.ScaleY = scale;
        }

        private void StopPulse()
        {
            Pulsing.Remove(this);
            _orbScale.ScaleX = _orbScale.ScaleY = 1.0;
        }

        // --- Voice dictation mic ---
        // Hover shows a small flyout window below the orb with action
        // buttons in a semicircular arc (see OrbFlyout — its own window,
        // not a control drawn inside this one's 56x56 bounds). The mic
        // button records, transcribes locally via Whisper, and types the
        // result into this session's terminal. See VoiceRecorder,
        // SpeechTranscriber and TerminalFocuser.SendText.

        // Created on first hover, not in the constructor — see the field's
        // own comment for why. A no-op when the feature is off, so nothing
        // here ever constructs a VoiceRecorder — and triggers macOS's
        // mic-permission prompt — for someone who hasn't opted in.
        private void EnsureFlyoutShown()
        {
            if (_flyout is null)
            {
                _flyout = new OrbFlyout();
                _flyout.MicClicked += () =>
                {
                    if (_recording) StopRecording();
                    else StartRecording();
                };
                _flyout.ArrangeClicked += () =>
                {
                    SessionManager.Instance?.ArrangeOrbsInPattern();
                };
                _flyout.SettingsClicked += () =>
                {
                    SettingsWindow.Toggle();
                };

                // The other half of the hover bridge described on
                // _hideFlyoutTimer: entering the flyout must cancel a hide
                // that Root.PointerExited already scheduled, and leaving it
                // must schedule one of its own in case the pointer doesn't
                // land back on the orb either.
                _flyout.PointerEntered += (_, _) => CancelFlyoutHide();
                _flyout.PointerExited += (_, _) => ScheduleFlyoutHide();
            }

            bool micOn = ClaudeBuddySettings.VoiceInputEnabled;
            _flyout.SetMicVisible(micOn);
            _flyout.SetArranged(SessionManager.Instance?.IsArranged ?? false);

            // The flyout sits centred below the orb. Its resting position
            // and the animation's start point both depend on the current
            // layout size (94x28 with mic, 60x28 without), since the start
            // aligns the flyout's centre with the orb's centre and the end
            // puts it just below the orb's circle edge.
            //
            // PointToScreen, not raw arithmetic: Position is physical screen
            // pixels, these are DIP measurements, and the two only line up
            // at 100% display scaling.
            Point target, from;
            if (micOn)
            {
                // Three-button layout (94x28): arrange, settings, mic.
                // Flyout centre is (47, 14).
                target = new Point(OrbCentre - 47, FlyoutRestY);
                from = new Point(OrbCentre - 47, OrbCentre - 14);
            }
            else
            {
                // Two-button layout (60x28): arrange and settings.
                // Flyout centre is (30, 14).
                target = new Point(OrbCentre - 30, FlyoutRestY);
                from = new Point(OrbCentre - 30, OrbCentre - 14);
            }

            _flyout.ShowNear(
                from: this.PointToScreen(from),
                to: this.PointToScreen(target),
                owner: this);
        }

        // Centre of the orb in its own window's DIPs — half of Root's pinned
        // 56x56. Unchanged by MemberScale: a team member is drawn smaller
        // around this same point, never moved off it.
        private const double OrbCentre = 28;

        // The flyout's top edge rests just below the orb's circle edge with
        // a 2px gap: the circle's radius is 18, so its bottom sits at
        // 28 + 18 = 46 in Root DIP space, and 46 + 2 = 48.
        private const double FlyoutRestY = 48;

        // Called by SessionManager when the arrangement state changes, so
        // every orb's flyout (if it exists) reflects whether clicking the
        // arrange button would arrange or restore.
        public void SetFlyoutArranged(bool arranged) => _flyout?.SetArranged(arranged);

        // Hides the flyout unconditionally — used by SessionManager before
        // starting an arrangement animation, since a flyout anchored to a
        // moving orb would look broken.
        public void HideFlyout() => HideFlyoutNow();

        // Immediate, not scheduled — dragging moves the orb every pointer
        // move, and a flyout animating toward a stale position underneath a
        // moving orb would read as broken rather than as a hover effect.
        private void HideFlyoutNow()
        {
            _hideFlyoutTimer?.Stop();
            _flyout?.Hide();
        }

        private void CancelFlyoutHide() => _hideFlyoutTimer?.Stop();

        // A no-op while recording: the flyout is the only way to stop, so it
        // must stay up regardless of where the pointer wanders.
        private void ScheduleFlyoutHide()
        {
            if (_recording) return;

            _hideFlyoutTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _hideFlyoutTimer.Stop();

            // Re-subscribing on every schedule would stack a duplicate Tick
            // handler per hover; there both is and only ever needs to be one.
            _hideFlyoutTimer.Tick -= OnFlyoutHideTick;
            _hideFlyoutTimer.Tick += OnFlyoutHideTick;
            _hideFlyoutTimer.Start();
        }

        private void OnFlyoutHideTick(object? sender, EventArgs e)
        {
            _hideFlyoutTimer!.Stop();

            // The grace period ends with the pointer having genuinely landed
            // on one of the two windows after all (a slow, deliberate move
            // across the gap) — nothing to hide in that case.
            if (Root.IsPointerOver || (_flyout?.IsPointerOverFlyout ?? false)) return;

            _flyout?.Hide();
        }

        private void StartRecording()
        {
            if (_recording) return;

            try
            {
                _recorder = new VoiceRecorder();

                // Fired from VoiceRecorder's own capture thread, so this has
                // to hop back to the UI thread before touching anything here
                // — StopRecording ends up updating Avalonia controls and
                // awaiting the transcription pipeline, none of which is safe
                // to do from off the dispatcher.
                _recorder.SilenceDetected += () => Dispatcher.UIThread.Post(StopRecording);

                _recorder.Start();
            }
            catch (Exception ex)
            {
                // No input device, permission denied, device busy — a
                // convenience feature failing to start is not worth a crash.
                _recorder = null;
                Console.Error.WriteLine($"Claude Buddy: couldn't start recording: {ex.Message}");
                return;
            }

            _recording = true;

            // Flat red, fast — visibly distinct from the waiting/generating
            // pulses, so "listening" reads as its own thing rather than as
            // the session itself having changed state.
            AnimateColor(RecordingColor, TimeSpan.FromMilliseconds(150), _lastState);
            StartPulse(1.18, TimeSpan.FromMilliseconds(350), new SineEaseInOut());

            // A hard cap, not just a courtesy: this runs whether or not the
            // user remembers to click again, so a missed second click can't
            // leave the mic — and VoiceRecorder's own capture thread — running
            // indefinitely.
            _recordingCap = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _recordingCap.Tick += (_, _) => StopRecording();
            _recordingCap.Start();
        }

        private async void StopRecording()
        {
            if (!_recording || _recorder is null) return;

            _recording = false;
            _recordingCap?.Stop();
            _recordingCap = null;

            // Back to whatever the session's own state actually is —
            // StartRecording never changed _lastState, only the pulse and
            // colour drawn over it.
            ApplyState(string.IsNullOrEmpty(_lastState) ? "idle" : _lastState);

            // The pointer is very likely still over the mic right after a
            // click, but the recording that was forcing the flyout to stay
            // up just ended — re-derive from where the pointer actually is
            // now rather than assuming either way.
            if (Root.IsPointerOver || (_flyout?.IsPointerOverFlyout ?? false))
            {
                CancelFlyoutHide();
            }
            else
            {
                HideFlyoutNow();
            }

            var recorder = _recorder;
            _recorder = null;

            float[] pcm;
            try
            {
                pcm = recorder.Stop();
            }
            finally
            {
                recorder.Dispose();
            }

            if (pcm.Length == 0) return;

            var text = await SpeechTranscriber.TranscribeAsync(pcm);
            if (string.IsNullOrWhiteSpace(text)) return;

            var status = _lastStatus;
            if (status is null) return;

            await TerminalFocuser.SendText(status, text);
        }

        // Ends an in-progress recording without transcribing or sending
        // anything — only reachable from Closed, where the orb (and the
        // session it belongs to) is going away regardless.
        private void CancelRecording()
        {
            _recordingCap?.Stop();
            _recordingCap = null;

            if (!_recording || _recorder is null) return;

            _recording = false;
            try { _recorder.Stop(); } catch { }
            _recorder.Dispose();
            _recorder = null;
        }

        // --- Click, dragging & context menu ---
        // Left-press starts as a potential click; it becomes a drag once the
        // pointer moves past a small threshold. A clean click jumps to the
        // session's terminal (macOS, best-effort — see TerminalFocuser).
        //
        // Dragging an orb pins it: it keeps that spot as sessions come and go
        // (SessionManager.ReflowPositions steps over pinned orbs) and the spot
        // is remembered across restarts, keyed by the session's directory. The
        // context menu's "Return this orb to the stack" undoes both.

        // Where the user dragged this orb is remembered against this key — the
        // session's cwd, set by SessionManager. Empty for a session with no cwd
        // reported, which pins for this run only since there's nothing stable
        // to remember it against.
        public string PositionKey { get; set; } = "";

        // True once the user has placed this orb by hand, whether in this run or
        // in an earlier one.
        public bool IsPinned { get; private set; }

        private SessionStatus? _lastStatus;
        private bool _pressed;
        private bool _dragging;
        private PixelPoint _windowStart;
        private PixelPoint _pointerStart;

        // A team lead drags its members along with it, so a team can be moved
        // out of the way as one thing — which is the whole point of drawing it
        // as one thing. Captured on press, because membership can change
        // mid-drag and an orb that joins the team while you're moving it should
        // not jump.
        private readonly List<(OrbWindow Orb, PixelPoint Start)> _followers = new();

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _pressed = true;
                _dragging = false;
                _windowStart = Position;
                _pointerStart = this.PointToScreen(e.GetPosition(this));

                _followers.Clear();
                foreach (var member in SessionManager.Instance?.MembersOf(SessionId)
                                       ?? Enumerable.Empty<OrbWindow>())
                {
                    _followers.Add((member, member.Position));
                }

                // When arranged, the whole cluster moves as one — every
                // orb in the pattern that isn't already a team follower
                // tags along so the shape stays intact.
                if (SessionManager.Instance?.IsArranged == true)
                {
                    var existing = new HashSet<string>(_followers.Select(f => f.Orb.SessionId));
                    foreach (var sibling in SessionManager.Instance.ArrangedSiblings(SessionId))
                    {
                        if (!existing.Contains(sibling.SessionId))
                            _followers.Add((sibling, sibling.Position));
                    }
                }

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

            // Only on the transition into dragging, not every move after —
            // a flyout animating toward a stale position underneath a moving
            // orb would read as broken, so it's simplest to just take it off
            // screen the instant a drag actually starts.
            if (!_dragging) HideFlyoutNow();

            _dragging = true;
            Position = new PixelPoint(_windowStart.X + dx, _windowStart.Y + dy);

            // The team travels with its lead, keeping the shape the user
            // arranged it in rather than being re-stacked around the new spot.
            foreach (var (member, start) in _followers)
            {
                member.Position = new PixelPoint(start.X + dx, start.Y + dy);
            }

            // Drag a member away and its arrow to the lead stretches with it —
            // which is the point of the arrow, since a dragged orb is exactly
            // the one that no longer sits next to the team it belongs to. Cheap
            // enough to do per pointer move: a few windows repositioned, no
            // scan of anything.
            TeamLinks.Refresh();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_pressed) return;

            _pressed = false;
            e.Pointer.Capture(null);

            if (_dragging)
            {
                SetPinned(true);
                SessionManager.Instance?.RememberOrbPosition(this);

                // Members carried along are pinned too, or the next reflow
                // would pull them back into the stack and leave the lead on its
                // own with three long arrows.
                //
                // Their positions are only *remembered* when they don't share
                // the lead's key. A team usually runs in one directory, and
                // positions are keyed by directory, so writing each member's
                // spot would overwrite the lead's with an offset copy of
                // itself — the group would come back scattered rather than not
                // come back at all. See RestoreOrbPosition.
                foreach (var (member, _) in _followers)
                {
                    member.SetPinned(true);
                    if (member.PositionKey != PositionKey)
                    {
                        SessionManager.Instance?.RememberOrbPosition(member);
                    }
                }
            }
            else
            {
                // A team member has no window of its own — its tmux server has
                // no client attached anywhere — so a click that finds nothing
                // falls through to the session leading it. See
                // TerminalFocuser.Focus.
                TerminalFocuser.Focus(
                    _lastStatus,
                    SessionManager.Instance?.StatusFor(_lastStatus?.Lead));
            }

            _followers.Clear();
        }

        // Put the orb at a position it was dragged to in an earlier run, without
        // treating it as a fresh drag (nothing to write back).
        public void PinAt(PixelPoint position)
        {
            Position = position;
            SetPinned(true);
        }

        public void Unpin() => SetPinned(false);

        private void SetPinned(bool pinned)
        {
            IsPinned = pinned;
            // Only worth offering once there's something to undo.
            ResetPositionItem.IsVisible = pinned;
        }

        private void ResetIdle_Click(object? sender, RoutedEventArgs e)
        {
            SessionManager.Instance?.ResetSessionToIdle(SessionId);
        }

        private void ResetPosition_Click(object? sender, RoutedEventArgs e)
        {
            SessionManager.Instance?.ReturnOrbToStack(SessionId);
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
