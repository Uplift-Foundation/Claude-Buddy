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
        private Color? _accentColor;
        private string _lastGlyphName = "";

        // Colour for the team arrow leaving this orb, when it has one. Follows
        // /color so several members pointing at one lead stay apart; sessions
        // without a colour share the neutral. See TeamLinks.
        public Color LinkColor { get; private set; } = PlainLink;

        // Seeded from the settings-backed colour at field-init time, the same way
        // SessionManager seeds OrbsVisible from ClaudeBuddySettings.ShowOrbs.
        private readonly SolidColorBrush _orbBrush = new(OrbColors.Idle);

        // The two halves of this orb's identity, for the chat panel's header.
        // A local session has no portrait and no emoji to draw there, and these
        // are what it has instead — read from the orb rather than re-derived, so
        // the header cannot disagree with the thing that was clicked.
        public string GlyphText => Glyph.Text ?? "";
        public Color OrbColor => _orbBrush.Color;

        // This session's own colour — /color for a Claude Code session, the
        // derived one for a gateway agent — or null where it has none. Distinct
        // from OrbColor, which is the *state* and changes as the session works.
        public Color? AccentColor => _accentColor;

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

        // True while this orb's chat panel is open. The arc and the panel want
        // the same piece of screen — ArcRadius is 56, directly below the orb —
        // and the arc's mic and speak duplicate what the panel already offers,
        // so one hides for the other rather than both being there.
        private bool _chatOpen;

        // The flyout used to open the instant the pointer touched an orb,
        // which made orbs hostile to each other: the arc one orb throws out
        // covers its neighbours, so a cursor travelling toward a second orb
        // summoned a menu that then sat in the way of the click it was on its
        // way to make. Requiring the pointer to *rest* on the orb separates
        // "I want this orb's menu" from "I am passing over this orb", and
        // costs a deliberate hover nothing it would notice.
        private DispatcherTimer? _showFlyoutTimer;
        private static readonly TimeSpan FlyoutHoverDelay = TimeSpan.FromMilliseconds(450);

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
                ScheduleFlyoutShow();
            };
            Root.PointerExited += (_, _) =>
            {
                CancelFlyoutShow();
                ScheduleFlyoutHide();
            };

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
            Closed += (_, _) =>
            {
                Pulsing.Remove(this);
                StopAvatarAnimation();
                ChatPanel.HideFor(SessionId);
            };

            // A session going away mid-dictation (the window closing) must
            // not leave a capture thread or a native mic handle running.
            Closed += (_, _) => CancelRecording();

            // Same reason for the other half of the voice feature: speech
            // outlives the window that started it unless it's cancelled.
            Closed += (_, _) => TextToSpeech.Cancel();

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

            var tipTitle = string.IsNullOrEmpty(described) ? SessionId : described;
            var tipPath = string.IsNullOrEmpty(status.Cwd) ? null : status.Cwd;
            ToolTip.SetTip(Root, ThoughtBubble(tipTitle, tipPath));

            ToolTip.SetPlacement(Root, PlacementMode.Top);

            _lastGlyphName = name;
            ApplyAvatar(status);
            if (!_hasAvatar) Glyph.Text = _agentEmoji ?? GlyphFor(name);
            ApplyAccent(status.Color);
            ApplyKind(status.IsRoom ? SessionKind.Unknown : status.Kind);

            // A room is a place, not somebody. "#" says that at a glance, and it
            // replaces the badge rather than joining it — a room orb wearing a
            // channel badge is the same fact drawn twice, once as the thing
            // itself and once as a note about it.
            if (status.IsRoom)
            {
                Glyph.Text = "#";
                Glyph.IsVisible = true;
            }
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
        //
        // A "#RRGGBB" is accepted as well as a name, which is how a gateway
        // agent gets an accent: it has no /color to give, so one is derived
        // from its id (see AgentPalette). Taking it through the same field
        // rather than adding a second one means the ring, the glyph and the
        // team arrow all pick it up with no further wiring.
        private void ApplyAccent(string colorName, bool force = false)
        {
            // force is for a redraw the colour itself did not ask for — a
            // picture arriving or going changes how the ring is drawn while
            // leaving the colour alone, and the early return would swallow it.
            if (!force && colorName == _lastColor) return;
            _lastColor = colorName;

            Color accent = default;
            var known = !string.IsNullOrEmpty(colorName)
                        && (AgentColors.TryGetValue(colorName, out accent)
                            || (colorName[0] == '#' && Color.TryParse(colorName, out accent)));

            _accentColor = known ? accent : null;

            // The ring says *who*, including over a picture.
            //
            // It used to carry the state there instead, on the reasoning that a
            // picture takes the fill and leaves the ring as the only solid
            // colour. That was wrong in practice for a reason the reasoning
            // couldn't see: the idle colour is a user setting, and set near
            // black — which most installs are, since idle is meant to be quiet —
            // the "state ring" is a **black band** around the picture for the
            // 95% of the time an agent is idle. It reads as a rendering fault,
            // not as a status.
            //
            // Nothing is lost by giving it up. State on these orbs is carried by
            // the glow, which appears only for the states worth noticing
            // (GlowsFor) and pulses while they last — so "working" still
            // announces itself, and "idle" correctly says nothing at all.
            Orb.Stroke = new SolidColorBrush(known ? accent : _hasAvatar ? _orbBrush.Color : PlainStroke);

            // Thicker over a picture: it is a ring around a photograph rather
            // than an outline on a flat circle, and at 2px it reads as an edge.
            Orb.StrokeThickness = _hasAvatar ? 3 : known ? 2 : 1;

            Glyph.Foreground = new SolidColorBrush(known ? accent : PlainGlyph);
            LinkColor = known ? accent : PlainLink;

            if (Glow.IsVisible)
                _glowBrush.GradientStops = GlowStops(_accentColor ?? _orbBrush.Color);
        }

        // Re-runs ApplyAccent when something other than the colour has changed —
        // a picture arriving or going, which changes how thick the ring is and
        // what it falls back to. ApplyAccent returns early when the colour is
        // the same, and here it is: where it is *drawn* is what moved.
        private void RefreshAccent() => ApplyAccent(_lastColor, force: true);

        private const double BadgeSize = 16;

        // A scheduled job, a private message, or a room with other people in
        // it. Nothing at all for a local session or for an agent's own main
        // session: every agent has a main, so badging it would put a mark on
        // almost every orb and distinguish nothing.
        //
        // @ and # are the symbols the surfaces themselves use for these two
        // things, so they need no learning. The clock is the odd one out and
        // has to be: a cron session is the one kind with nobody on the other
        // end, which is the distinction most worth seeing from across a screen.
        private static (string Glyph, string Label)? BadgeFor(SessionKind kind) => kind switch
        {
            SessionKind.Cron => ("\u23F1", "cron"),
            SessionKind.Direct => ("@", "direct message"),
            SessionKind.Channel => ("#", "channel"),
            _ => null
        };

        // What the chat panel puts in its header. Null where there is no badge,
        // so the panel shows nothing rather than the word "unknown".
        public string? KindLabel => BadgeFor(_lastStatus?.Kind ?? SessionKind.Unknown)?.Label;

        public string? KindGlyphText => BadgeFor(_lastStatus?.Kind ?? SessionKind.Unknown)?.Glyph;

        private void ApplyKind(SessionKind kind)
        {
            var badge = BadgeFor(kind);

            if (badge is null)
            {
                KindBadge.IsVisible = false;
                return;
            }

            KindGlyph.Text = badge.Value.Glyph;
            KindBadge.IsVisible = true;
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

            // Kept on the orb's edge rather than in the window's corner. The
            // orb is a circle of radius 18*scale centred at (28,28), so its
            // lower-right edge is at 28 + 18*scale*sin45. Solving for the
            // margin that puts the badge's centre there is what keeps it
            // touching the rim at both sizes instead of drifting off a team
            // member's smaller circle.
            KindBadge.Width = KindBadge.Height = BadgeSize * scale;
            KindBadge.CornerRadius = new CornerRadius(BadgeSize * scale / 2);
            KindGlyph.FontSize = 9 * scale;

            var inset = 28 - (18 * scale * 0.7071) - (BadgeSize * scale / 2);
            KindBadge.Margin = new Thickness(0, 0, Math.Max(0, inset), Math.Max(0, inset));
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

        // An agent's own picture, drawn as the orb itself.
        //
        // This is the one place the app's usual rule bends: normally the fill is
        // the state and the letter is which session. A face says which session
        // far better than a letter can, so the state moves outward to the ring —
        // which still carries the colour, and the pulse and halo were always
        // doing most of that work anyway. Sessions with no picture keep the
        // ordinary filled orb and get their agent's emoji instead of a letter,
        // which is why both paths stay.
        private bool _hasAvatar;
        private string? _agentEmoji;
        private ImageBrush? _avatarBrush;

        // The state ring on an avatar orb. One brush with its own transition,
        // rather than a fresh SolidColorBrush per state change: the fill it
        // replaced faded over 300ms and a ring that snaps instead reads as a
        // different, cruder thing. Also stops allocating a brush every time a
        // session changes state, which for a busy gateway is often.
        private SolidColorBrush? _ringBrush;
        private OpenClawAvatars.Avatar? _avatar;
        private int _avatarFrame;
        private DispatcherTimer? _avatarTimer;

        private void ApplyAvatar(SessionStatus status)
        {
            if (status.Source != SessionSource.OpenClaw)
            {
                ClearAvatar();
                return;
            }

            var identity = OpenClawSessions.IdentityForSession(SessionId);
            _agentEmoji = identity?.Emoji;

            var avatar = identity is null ? null : OpenClawAvatars.For(IdOf(SessionId), identity.Avatar);
            if (avatar is null)
            {
                ClearAvatar();
                return;
            }

            if (ReferenceEquals(avatar, _avatar)) return;

            _avatar = avatar;
            _avatarFrame = 0;
            _hasAvatar = true;

            Glyph.IsVisible = false;

            _avatarBrush ??= new ImageBrush { Stretch = Stretch.UniformToFill };
            _avatarBrush.Source = avatar.Frames[0];
            Orb.Fill = _avatarBrush;

            _ringBrush ??= new SolidColorBrush(_orbBrush.Color)
            {
                Transitions = new Transitions
                {
                    new ColorTransition
                    {
                        Property = SolidColorBrush.ColorProperty,
                        Duration = TimeSpan.FromMilliseconds(300),
                        Easing = new QuadraticEaseOut()
                    }
                }
            };

            _ringBrush.Color = _orbBrush.Color;

            // The picture lands long after the accent did, and it is what
            // decides how the ring is drawn — so the accent is applied again
            // rather than assumed to have got there first. An agent with no
            // colour at all falls back to the state ring inside ApplyAccent,
            // which is what _ringBrush is still here for.
            Orb.Stroke = _ringBrush;
            RefreshAccent();

            StartAvatarAnimation();
        }

        private static string IdOf(string sessionId)
        {
            const string Prefix = "openclaw:";
            var key = sessionId.StartsWith(Prefix, StringComparison.Ordinal)
                ? sessionId[Prefix.Length..]
                : sessionId;

            var parts = key.Split(':');
            return parts.Length >= 2 ? parts[1] : key;
        }

        private void ClearAvatar()
        {
            if (!_hasAvatar)
            {
                Glyph.IsVisible = true;
                return;
            }

            _hasAvatar = false;
            _avatar = null;
            StopAvatarAnimation();

            Orb.Fill = _orbBrush;
            Orb.Stroke = new SolidColorBrush(Color.Parse("#22FFFFFF"));
            Orb.StrokeThickness = 1;
            Glyph.IsVisible = true;

            // Thinner ring, and the accent back on a flat circle.
            RefreshAccent();
        }

        // Its own timer rather than the shared pulse ticker: frame delays are
        // whatever each GIF's author chose, and are neither 60fps nor the same
        // between two agents.
        private void StartAvatarAnimation()
        {
            StopAvatarAnimation();

            if (_avatar is null || !_avatar.IsAnimated) return;

            _avatarTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(_avatar.DelaysMs[0])
            };

            _avatarTimer.Tick += (_, _) =>
            {
                if (_avatar is null || _avatarBrush is null) return;

                _avatarFrame = (_avatarFrame + 1) % _avatar.Frames.Count;
                _avatarBrush.Source = _avatar.Frames[_avatarFrame];
                _avatarTimer!.Interval = TimeSpan.FromMilliseconds(_avatar.DelaysMs[_avatarFrame]);
            };

            _avatarTimer.Start();
        }

        private void StopAvatarAnimation()
        {
            _avatarTimer?.Stop();
            _avatarTimer = null;
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
            // Only words with something readable in them count, and the initial
            // is the first such character rather than the first character.
            // "Lilibeth — wtvamp" splits into three tokens, the middle one a
            // lone em dash, and taking the first two of those produced "L—" on
            // every orb — which is how this was found. Skipping *within* a word
            // as well is what makes "#kubernetes" contribute "k" rather than
            // being thrown away for starting with a hash.
            var words = label
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(Initial)
                .Where(initial => initial.Length > 0)
                .ToArray();

            if (words.Length >= 2)
            {
                return words[0].ToUpperInvariant() + words[1].ToLowerInvariant();
            }

            // One word, or none worth reading: take two letters from the label
            // itself, which is the old behaviour and still right for "Menu".
            var first = FirstGrapheme(label);
            var rest = label[first.Length..];
            var second = rest.Length > 0 ? FirstGrapheme(rest) : "";
            return first.ToUpperInvariant() + second.ToLowerInvariant();
        }

        // One printable character, or a full surrogate pair if the string
        // starts with one (e.g. an emoji) — never split in half, which is
        // what renders as a broken box instead of the emoji.
        // The first character of a word that a person would say out loud —
        // skipping any leading punctuation, so "#general" gives "g" and a lone
        // "—" gives nothing at all.
        private static string Initial(string word)
        {
            for (var i = 0; i < word.Length; i++)
            {
                if (char.IsHighSurrogate(word[i])) return word.Substring(i, Math.Min(2, word.Length - i));
                if (char.IsLetterOrDigit(word[i])) return word.Substring(i, 1);
            }

            return "";
        }

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
            if (Glow.IsVisible) _glowBrush.GradientStops = GlowStops(_accentColor ?? to);

            // With a picture in the fill, the ring is the only thing left
            // carrying the colour, so it has to follow the same changes — and
            // fade rather than snap, the way the fill did.
            if (_hasAvatar && _ringBrush is not null) _ringBrush.Color = to;
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

        private static Control ThoughtBubble(string title, string? path)
        {
            var bg = Color.Parse("#E6EAECF0");
            var fg = Color.Parse("#FF2A2A35");
            var font = new FontFamily(
                "SF Pro Rounded, .AppleSystemUIFontRounded, Segoe UI, sans-serif");

            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                FontFamily = font,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(fg),
                TextWrapping = TextWrapping.NoWrap,
                LineHeight = 17
            });

            if (path is not null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = path,
                    FontSize = 11.5,
                    FontFamily = font,
                    Foreground = new SolidColorBrush(Color.FromArgb(180, fg.R, fg.G, fg.B)),
                    TextWrapping = TextWrapping.NoWrap,
                    LineHeight = 15
                });
            }

            var bubble = new Border
            {
                Background = new SolidColorBrush(bg),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 9),
                BoxShadow = BoxShadows.Parse("0 2 8 0 #30000000"),
                Child = content
            };

            var canvas = new Canvas
            {
                Width = 16, Height = 16,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(0, -2, 0, 0)
            };

            var dot1 = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(Color.FromArgb(180, bg.R, bg.G, bg.B))
            };
            Canvas.SetLeft(dot1, 4);
            Canvas.SetTop(dot1, 0);

            var dot2 = new Ellipse
            {
                Width = 5, Height = 5,
                Fill = new SolidColorBrush(Color.FromArgb(120, bg.R, bg.G, bg.B))
            };
            Canvas.SetLeft(dot2, 6);
            Canvas.SetTop(dot2, 10);

            canvas.Children.Add(dot1);
            canvas.Children.Add(dot2);

            var stack = new StackPanel();
            stack.Children.Add(bubble);
            stack.Children.Add(canvas);
            return stack;
        }

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
                _flyout.SpeakClicked += OnSpeakClicked;
                _flyout.ChatClicked += OpenChat;

                // The other half of the hover bridge described on
                // _hideFlyoutTimer: entering the flyout must cancel a hide
                // that Root.PointerExited already scheduled, and leaving it
                // must schedule one of its own in case the pointer doesn't
                // land back on the orb either.
                _flyout.PointerEntered += (_, _) => CancelFlyoutHide();
                _flyout.PointerExited += (_, _) => ScheduleFlyoutHide();
            }

            // Shown for both kinds now. It used to be hidden on gateway orbs
            // because dictation had nowhere to go for them; the chat panel is
            // that somewhere, and StopRecording opens it with the words in its
            // input box rather than sending them.
            bool micOn = ClaudeBuddySettings.VoiceInputEnabled;
            _flyout.SetMicVisible(micOn);

            // Only on local sessions. A gateway orb opens its panel when you
            // click it, so a button that did the same thing one ring further out
            // would be a second way to do the thing the orb already does.
            _flyout.SetChatVisible(
                _lastStatus?.Source == SessionSource.ClaudeCode
                && ClaudeBuddySettings.ClaudeCodeChatEnabled);

            _flyout.SetArranged(SessionManager.Instance?.IsArranged ?? false);

            // Speech is global rather than per-orb, so a flyout opening
            // while something is already being read has to show the stop
            // glyph rather than offer to start a second one. Reads the real
            // state now rather than a flag some click left behind.
            _flyout.SetSpeakState(TextToSpeech.State);

            // The arc's virtual centre (ArcOrigin) aligns with the orb's
            // centre so the semicircle sits concentric with the orb. The
            // animation starts with the flyout centred on the orb so the
            // buttons are hidden behind it and emerge downward.
            //
            // PointToScreen, not raw arithmetic: Position is physical screen
            // pixels, these are DIP measurements, and the two only line up
            // at 100% display scaling.
            var target = new Point(
                OrbCentre - _flyout.ArcOriginX,
                OrbCentre - _flyout.ArcOriginY);
            var from = new Point(
                OrbCentre - _flyout.Width / 2,
                OrbCentre - _flyout.Height / 2);

            _flyout.ShowNear(
                from: this.PointToScreen(from),
                to: this.PointToScreen(target),
                owner: this);
        }

        // Centre of the orb in its own window's DIPs — half of Root's pinned
        // 56x56. Unchanged by MemberScale: a team member is drawn smaller
        // around this same point, never moved off it.
        private const double OrbCentre = 28;

        // --- Speak latest turn --------------------------------------------------

        // Deliberately tells the flyout nothing. It used to push the glyph itself
        // either side of the call, which was a guess that happened to be right on
        // the way in and always wrong on the way out — speech that ended by itself
        // left the stop glyph up until the flyout was reopened, and the neural
        // engine's several seconds of preparation looked identical to playing.
        // TextToSpeech.StateChanged is the single source now; see SessionManager,
        // which broadcasts it to every orb because speech is global rather than
        // per-orb.
        private void OnSpeakClicked()
        {
            if (TextToSpeech.IsSpeaking)
            {
                TextToSpeech.Cancel();
                return;
            }

            // A gateway session has no transcript on this machine, so the text
            // comes from the conversation itself — fetched if this session has
            // never been opened, which is why this branch is async where the
            // local one isn't.
            if (_lastStatus?.Source == SessionSource.OpenClaw)
            {
                _ = SpeakRemoteAsync();
                return;
            }

            var text = FindSpeakableText();
            if (text is null) return;

            TextToSpeech.Speak(text, ClaudeBuddySettings.SpeakVoice);
        }

        private async Task SpeakRemoteAsync()
        {
            var title = _lastStatus?.Title ?? "";
            var text = await OpenClawSessions.LastAssistantTextAsync(SessionId, title);

            if (string.IsNullOrWhiteSpace(text)) return;

            Dispatcher.UIThread.Post(() => TextToSpeech.Speak(text, ClaudeBuddySettings.SpeakVoice));
        }

        // Called by SessionManager when speech starts, changes phase or stops.
        public void SetFlyoutSpeakState(TextToSpeech.SpeakState state) =>
            _flyout?.SetSpeakState(state);

        // This session's own transcript first, then a search by directory.
        //
        // The fallback is for a session that dispatches work rather than
        // doing it: a controller has no transcript of its own, but the
        // background jobs it launched write theirs into project dirs named
        // for the same cwd, and the most recent of those is what "read the
        // last turn" means when you click its orb.
        private string? FindSpeakableText()
        {
            // Not for a gateway session. It has no transcript on this machine,
            // and the cwd fallback below would match a *local* project directory
            // with the same path and speak an unrelated local session's last
            // turn as though it were the remote agent's. The lookup also walks
            // every project directory recursively, on the UI thread, before
            // getting there.
            if (_lastStatus?.Source != SessionSource.ClaudeCode) return null;

            var path = _lastStatus?.TranscriptPath;
            var text = TranscriptReader.LatestAssistantText(path, SessionId);
            if (text is not null) return text;

            var cwd = _lastStatus?.Cwd;
            if (string.IsNullOrEmpty(cwd)) return null;

            var fallback = TranscriptReader.LatestTranscriptForCwd(cwd);
            if (fallback is not null)
            {
                text = TranscriptReader.LatestAssistantText(fallback);
                if (text is not null) return text;
            }

            return null;
        }

        // Called by SessionManager when the arrangement state changes, so
        // every orb's flyout (if it exists) reflects whether clicking the
        // arrange button would arrange or restore.
        public void SetFlyoutArranged(bool arranged) => _flyout?.SetArranged(arranged);

        // Called by ChatPanel when it closes itself, so the arc becomes
        // available again without the orb having to watch the window.
        public void SetChatOpen(bool open) => _chatOpen = open;

        // The flyout's keyboard button. Same destination a gateway orb's click
        // reaches, arrived at differently because for a local session the click
        // is already spoken for.
        private void OpenChat()
        {
            var chat = SessionManager.Instance?.RemoteChatFor(SessionId);
            if (chat is null) return;

            _chatOpen = true;
            HideFlyoutNow();
            ChatPanel.OpenFor(this, chat);
        }

        // Whether a dictation capture is in progress. The panel mirrors it on
        // its own mic button and refuses to be dismissed while it is true.
        public bool IsRecording => _recording;

        // The panel's mic drives this orb's recorder rather than constructing a
        // second one — that keeps one recorder per session, along with the red
        // pulse and the 30-second cap, all working exactly as they already do.
        public void ToggleRecording()
        {
            if (_recording) StopRecording();
            else StartRecording();
        }

        // Hides the flyout unconditionally — used by SessionManager before
        // starting an arrangement animation, since a flyout anchored to a
        // moving orb would look broken.
        public void HideFlyout() => HideFlyoutNow();

        // Immediate, not scheduled — dragging moves the orb every pointer
        // move, and a flyout animating toward a stale position underneath a
        // moving orb would read as broken rather than as a hover effect.
        private void HideFlyoutNow()
        {
            CancelFlyoutShow();
            _hideFlyoutTimer?.Stop();
            _flyout?.Hide();
        }

        private void CancelFlyoutHide() => _hideFlyoutTimer?.Stop();

        private void CancelFlyoutShow() => _showFlyoutTimer?.Stop();

        // The delay is only for *opening* the flyout from nothing. Coming back
        // onto the orb from its own open flyout is the other half of the hover
        // bridge, not a new request, and pausing there would be a stutter in
        // the middle of an interaction the user is already having.
        private void ScheduleFlyoutShow()
        {
            // On the method rather than on PointerEntered, the same way
            // ScheduleFlyoutHide carries its own _recording guard: the rule is
            // "the arc does not open while the chat panel has that space", and a
            // rule about the arc belongs where the arc is scheduled. Left on the
            // handler it is one caller's business, and the next caller has to
            // remember it.
            if (_chatOpen) return;

            if (_flyout?.IsVisible == true)
            {
                EnsureFlyoutShown();
                return;
            }

            _showFlyoutTimer ??= new DispatcherTimer { Interval = FlyoutHoverDelay };
            _showFlyoutTimer.Stop();

            // One handler, however many hovers — same reason as the hide timer.
            _showFlyoutTimer.Tick -= OnFlyoutShowTick;
            _showFlyoutTimer.Tick += OnFlyoutShowTick;
            _showFlyoutTimer.Start();
        }

        private void OnFlyoutShowTick(object? sender, EventArgs e)
        {
            _showFlyoutTimer!.Stop();

            // PointerExited cancels this timer, but a drag that carries the orb
            // out from under a stationary cursor, or an orb closing mid-wait,
            // doesn't necessarily raise one — so confirm rather than assume.
            if (!Root.IsPointerOver) return;

            EnsureFlyoutShown();
        }

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
            ChatPanel.SetRecording(this, true);

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
            ChatPanel.SetRecording(this, false);
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

            // With a chat panel open for this session, the words belong in its
            // input box — unsent, for the reason TerminalFocuser.SendText gives
            // at its own definition: transcription is a typing aid and doesn't
            // get to decide you meant it. Reviewing before Enter is the whole
            // contract, and it is the same one either way.
            if (ChatPanel.IsOpenFor(SessionId))
            {
                ChatPanel.AppendToInput(text);
                return;
            }

            // Dictated at a gateway orb with no panel up: open one and put the
            // words in it. Still unsent — the panel is what makes "review before
            // Enter" possible for a session that has no terminal to review in.
            if (_lastStatus?.Source == SessionSource.OpenClaw)
            {
                var chat = SessionManager.Instance?.RemoteChatFor(SessionId);
                if (chat is not null)
                {
                    _chatOpen = true;
                    HideFlyoutNow();
                    ChatPanel.OpenFor(this, chat);
                    ChatPanel.AppendToInput(text);
                }

                return;
            }

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
            ChatPanel.SetRecording(this, false);
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
            ChatPanel.RepositionFor(this);
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
                // A click has always meant "take me to this session". For a
                // Claude Code session that is its terminal; for a gateway
                // session there is no terminal anywhere, and the honest answer
                // is a place to read and reply — so the panel *is* the
                // destination rather than an extra affordance.
                //
                // Guarded on the source rather than on RemoteChatFor answering,
                // which it now does for local sessions as well: that is what the
                // flyout's keyboard button opens, and it must not quietly become
                // what a click does instead. Going to the terminal is the oldest
                // behaviour this app has and people reach for it without looking.
                if (_lastStatus?.Source != SessionSource.ClaudeCode)
                {
                    var chat = SessionManager.Instance?.RemoteChatFor(SessionId);
                    if (chat is not null)
                    {
                        _chatOpen = true;
                        HideFlyoutNow();
                        ChatPanel.OpenFor(this, chat);
                        return;
                    }
                }

                TerminalFocuser.Focus(
                    _lastStatus,
                    SessionManager.Instance?.StatusFor(_lastStatus?.Lead),
                    SessionId);
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
