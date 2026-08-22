using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // A small conversation anchored to an orb: the last few turns, a line to
    // type in, and a mic. Opened by clicking an orb that represents a session
    // with no terminal to jump to — for those, this is where the click goes.
    //
    // One instance, reused. Two panels would be two windows competing to be the
    // key window, each one's dismiss-on-deactivate closing the other; a
    // singleton makes "opening B closes A" correct by construction rather than
    // emergent. What is worth keeping per session is the draft and the scroll
    // position, and those live in a dictionary — a window is not a storage
    // mechanism. The draft store isn't a nicety either: the panel hides whenever
    // you switch apps, so without it every alt-tab would eat a half-typed
    // sentence.
    public partial class ChatPanel : Window
    {
        private static ChatPanel? _instance;

        private static readonly Dictionary<string, string> Drafts = new(StringComparer.Ordinal);

        private IRemoteChatSession? _session;
        private OrbWindow? _owner;

        // Whether the header is currently wearing the orb's identity rather
        // than an agent's own. Only that case can be refreshed from the orb.
        private bool _borrowedIdentity;

        // Who is talking in this session when the transcript does not say.
        //
        // A box shared by reference with every TurnView, not a value copied
        // into each. The name is not always known when the panel binds — a
        // terminal session's title arrives with a later hook write, and the
        // panel opens on whatever the orb had at the time, which is often
        // nothing. Copied, that nothing was baked into every row already built
        // and the chips never appeared; boxed, filling it in later fills in the
        // rows that were waiting for it. Same one-shot-read mistake the header
        // made two commits ago, in a second place.
        private sealed class Speaker { public string? Name; }

        private readonly Speaker _soleSpeaker = new();

        // The colour a reply is drawn in when the turn itself doesn't name one.
        //
        // A room's turns carry their own, because several agents are talking. In
        // every other panel exactly one agent is, and repeating its name on
        // every bubble would be noise — but its colour is not, so it comes from
        // the orb rather than from each message.
        private Color? _defaultBubble;

        private readonly ObservableCollection<TurnView> _turns = new();

        // What the bound session's CLI understands, so "/" in the box can
        // offer the same autocomplete the terminal itself would. Empty for a
        // session with no answer for IRemoteChatSlashCommands, which quietly
        // turns the whole feature off for it rather than needing a check at
        // every call site.
        private IReadOnlyList<SlashCommand> _slashCommands = Array.Empty<SlashCommand>();

        // The suggestions currently shown, and which one Up/Down has landed
        // on. Kept as a plain list rather than something observable: the
        // popup is small and rebuilt wholesale on every keystroke or arrow
        // press anyway, so there is nothing an incremental update would save.
        private List<SlashCommand> _slashMatches = new();
        private int _slashSelected;

        // Distance from the orb's centre to the panel's near edge. Clears the
        // 56pt orb with a small gap, the same way OrbFlyout's ArcRadius does.
        private const int Gap = 34;

        public ChatPanel()
        {
            InitializeComponent();

            Turns.ItemsSource = _turns;

            CloseButton.PointerPressed += (_, e) => { e.Handled = true; HideNow(); };

            // The portrait opens at four times the size, centred on itself.
            // Handled so the click doesn't also travel on to anything behind it.
            AvatarBox.PointerPressed += (_, e) =>
            {
                if (_avatar is null) return;

                e.Handled = true;

                var centre = AvatarBox.Bounds.Center;
                AvatarPopup.Show(_avatar, this.PointToScreen(new Point(
                    AvatarBox.Bounds.X + centre.X,
                    AvatarBox.Bounds.Y + centre.Y)));
            };
            SendButton.PointerPressed += (_, e) => { e.Handled = true; Send(); };
            MicButton.PointerPressed += (_, e) => { e.Handled = true; _owner?.ToggleRecording(); };
            SpeakButton.PointerPressed += (_, e) => { e.Handled = true; SpeakLatest(); };

            // Tunnel, not bubble. TextBox handles Return in a class handler that
            // runs before any instance handler on the same element, so a plain
            // KeyDown += would fire after the newline had already been inserted.
            // Getting there first is the whole point.
            Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
            Input.TextChanged += (_, _) => UpdateSlashSuggestions();

            KeyDown += OnPanelKeyDown;

            Opened += (_, _) =>
            {
                // Orbs follow you across Spaces; a panel that didn't would be
                // stranded behind you mid-sentence.
                this.ShowOnAllSpaces();

                // A no-op in practice — the shared class patch is installed by
                // whichever orb you clicked to get here — but kept for symmetry
                // with OrbFlyout, and correct if that ever stops being true.
                this.AcceptFirstClick();
            };

            // Deferred and re-checked: clicking our own mic or close button can
            // deactivate the window for an instant, and a recording in progress
            // must not be orphaned by a click elsewhere — the same rule
            // ScheduleFlyoutHide already follows for the arc.
            Deactivated += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (IsActive) return;
                if (_owner?.IsRecording == true) return;

                // The enlarged portrait is this panel's own window: opening it
                // deactivates the panel, and closing the panel out from under it
                // would be a strange answer to "show me that picture".
                if (AvatarPopup.IsOpen) return;

                HideNow();
            }, DispatcherPriority.Background);

            // SizeToContent means the height isn't known until after layout, so
            // the first open of a tall transcript would otherwise be positioned
            // as though it were a short one.
            SizeChanged += (_, _) => Reposition();

            // Reaching the top asks for the page before. A threshold rather than
            // exactly zero, because a trackpad flick lands a few pixels short of
            // the top as often as it lands on it.
            Scroll.ScrollChanged += (_, _) =>
            {
                // Only when there is something to scroll. ScrollChanged fires on
                // extent and viewport changes too, and a transcript shorter than
                // the panel sits at offset zero forever — so this asked for the
                // page before, which grew the extent, which fired it again, and
                // walked the entire backlog the instant the orb was clicked.
                if (Scroll.Extent.Height <= Scroll.Viewport.Height + 8) return;
                if (Scroll.Offset.Y > 24) return;

                _ = LoadOlderAsync();
            };
        }

        public static bool IsOpenFor(string sessionId) =>
            _instance is { IsVisible: true } panel
            && panel._session?.SessionId == sessionId;

        public static void OpenFor(OrbWindow orb, IRemoteChatSession session)
        {
            _instance ??= new ChatPanel();
            _instance.Bind(orb, session);
        }

        // Used when the orb goes away, or is about to move under the panel —
        // an arrangement animation, or the orb's own close.
        public static void HideFor(string sessionId)
        {
            if (_instance is null) return;
            if (_instance._session?.SessionId != sessionId) return;

            _instance.HideNow();
        }

        public static void RepositionFor(OrbWindow orb)
        {
            if (_instance is not { IsVisible: true } panel) return;
            if (!ReferenceEquals(panel._owner, orb)) return;

            panel.Reposition();
        }

        // Speech is global rather than per-orb, so the panel is told about it
        // the same way the flyout is, from one place.
        public static void SetSpeakState(TextToSpeech.SpeakState state) =>
            _instance?.ApplySpeakState(state);

        public static void SetRecording(OrbWindow orb, bool recording)
        {
            if (_instance is not { IsVisible: true } panel) return;
            if (!ReferenceEquals(panel._owner, orb)) return;

            panel.MicFill.Fill = recording ? RecordingFill : IdleFill;
        }

        // Dictation lands here rather than being sent. Same rule
        // TerminalFocuser.SendText has always followed and explains at its own
        // definition: transcription is a typing aid, and it does not get to
        // decide that you meant it.
        public static void AppendToInput(string text)
        {
            if (_instance is not { IsVisible: true } panel) return;

            var existing = panel.Input.Text ?? "";
            panel.Input.Text = existing.Length == 0 ? text : existing.TrimEnd() + " " + text;
            panel.Input.CaretIndex = panel.Input.Text.Length;
            panel.Input.Focus();
        }

        private static readonly IBrush IdleFill = new SolidColorBrush(Color.Parse("#E0202024"));
        private static readonly IBrush RecordingFill = new SolidColorBrush(Color.Parse("#E0D93B3B"));
        private static readonly IBrush SpeakActiveFill = new SolidColorBrush(Color.Parse("#E04A90D9"));
        private static readonly IBrush SpeakPreparingFill = new SolidColorBrush(Color.Parse("#E0B8860B"));

        // Connected is green rather than the speak button's blue. The two dots
        // sit inches apart and meant different things in the same colour: blue
        // on the button is "this is playing right now", blue on the portrait was
        // "this conversation is reachable". Green is what a presence dot is
        // everywhere else, so it needs no explanation, and it leaves blue to
        // mean one thing again.
        //
        // #00AF5F rather than a green picked by eye — it is the app's green
        // already, the value Claude Code's own /color green resolves to (see
        // OrbWindow's palette and ClaudeDesktopColors), so a connected dot and
        // a green orb are the same green.
        private static readonly IBrush ConnectedFill = new SolidColorBrush(Color.Parse("#E000AF5F"));

        private void Unbind()
        {
            if (_session is null) return;

            Drafts[_session.SessionId] = Input.Text ?? "";

            _session.TurnAdded -= OnTurnAdded;
            _session.TurnUpdated -= OnTurnUpdated;
            _session.StateChanged -= OnStateChanged;

            if (_session is IRemoteChatBacklog previous)
            {
                previous.HistoryReplaced -= OnHistoryReplaced;
                previous.HistoryPrepended -= OnHistoryPrepended;
            }

            if (_session is IRemoteChatPrompts prompts) prompts.PromptChanged -= OnPromptChanged;

            // The last good name is per session, not per panel. The panel is a
            // singleton and the box outlives a session, so leaving it set meant
            // the *next* conversation inherited it — and because "we already
            // knew a name" beats "we do not know one yet", a session whose
            // title had not arrived would wear the previous session's initials
            // on every bubble rather than none. Wrong is worse than absent
            // here: the chip is there to say who is talking.
            _soleSpeaker.Name = null;
        }

        private void Bind(OrbWindow orb, IRemoteChatSession session)
        {
            Unbind();

            // Tell the orb that is losing the panel. Only HideNow used to clear
            // this, and rebinding skips it — so clicking a second orb left the
            // first believing its panel was still open, and its hover flyout
            // never appeared again for the life of the process.
            if (_owner is not null && !ReferenceEquals(_owner, orb)) _owner.SetChatOpen(false);

            _owner = orb;
            _session = session;

            session.TurnAdded += OnTurnAdded;
            session.TurnUpdated += OnTurnUpdated;
            session.StateChanged += OnStateChanged;

            // The backlog usually lands a moment after the panel opens, so the
            // transcript has to be able to be replaced under it rather than only
            // appended to. Optional on purpose — a session that only ever
            // appends never raises it.
            if (session is IRemoteChatBacklog loader)
            {
                loader.HistoryReplaced += OnHistoryReplaced;
                loader.HistoryPrepended += OnHistoryPrepended;
            }

            if (session is IRemoteChatPrompts prompts) prompts.PromptChanged += OnPromptChanged;

            // "Zara — wtvamp" is built as name plus place, so it splits back
            // into the two lines the header now has. A name with no place (an
            // agent's own main session) simply leaves the second line empty.
            ApplyTitle();
            RefreshSoleSpeaker();

            // Read off the orb rather than from the session, so the panel and
            // the badge on the thing that was clicked cannot disagree — the
            // same reason the header takes its colour and letter from there.
            var kind = orb.KindLabel;
            KindChip.IsVisible = kind is not null;
            KindChipText.Text = kind is null ? "" : $"{orb.KindGlyphText}  {kind}";

            _defaultBubble = orb.AccentColor;

            ApplyAvatar(session.SessionId);
            OnStateChanged(session.State);

            _turns.Clear();
            foreach (var turn in session.History) _turns.Add(new TurnView(turn, _defaultBubble, _soleSpeaker));

            _slashCommands = (session as IRemoteChatSlashCommands)?.SlashCommands ?? Array.Empty<SlashCommand>();
            HideSlashSuggestions();

            Input.Text = Drafts.GetValueOrDefault(session.SessionId, "");
            MicButton.IsVisible = ClaudeBuddySettings.VoiceInputEnabled;
            ApplySpeakState(TextToSpeech.State);

            // The box stays enabled even when sending won't work, and says why
            // on itself instead. A disabled box can't be pasted into or drafted
            // in, and SendAsync explains itself in the transcript anyway.
            Input.Watermark = (session as IRemoteChatComposer)?.ComposerHint ?? "Message…";
            ApplyPrompt();

            Reposition();

            if (!IsVisible) Show();

            // Show() then Activate(): an accessory app can be activated
            // programmatically or by a click on one of its windows, and this one
            // is opened by exactly such a click. Focus is taken on Activated
            // rather than by WaitForOwnActivation, which sleeps the UI thread up
            // to 600ms — fine at the tail of a TerminalFocuser call, not between
            // a click and a window appearing.
            Activate();
            Dispatcher.UIThread.Post(() => Input.Focus(), DispatcherPriority.Input);

            Dispatcher.UIThread.Post(ScrollToEndIfPinned, DispatcherPriority.Loaded);
        }

        // The same decoded frames the orb draws, at a size worth looking at.
        // Animated here too: this is the one place you are actually looking at
        // the picture rather than glancing at it, so a still frame of an
        // animated avatar would be the wrong half of the trade.
        private OpenClawAvatars.Avatar? _avatar;
        private ImageBrush? _avatarBrush;
        private int _avatarFrame;
        private DispatcherTimer? _avatarTimer;

        // The ring is the one part of the portrait that is always visible,
        // whether the circle holds a photo, a colour or nothing, so it is where
        // an identity colour belongs. Default is the flat white the XAML ships
        // with, which is what a session with no colour of its own keeps.
        private static readonly IBrush DefaultRing = new SolidColorBrush(Color.Parse("#40FFFFFF"));

        private void RingFor(Color? color)
        {
            if (color is not { } c)
            {
                Avatar.Stroke = DefaultRing;
                Avatar.StrokeThickness = 1;
                return;
            }

            Avatar.Stroke = new SolidColorBrush(c);
            // Thicker than the default hairline: a coloured ring is carrying
            // information now, and at 1px against a dark panel it reads as an
            // antialiasing artefact rather than a deliberate mark.
            Avatar.StrokeThickness = 2.5;
        }

        // An agent's colour, keyed on its id so the same agent is the same
        // colour everywhere — the orb, the team view and now this header.
        //
        // Asked of OpenClawSessions rather than of AgentPalette directly, which
        // is the difference between that sentence being true and being nearly
        // true. HexFor gives an agent the colour its id hashes to; two agents
        // can hash close enough to be indistinguishable, so the assignment is
        // made across the whole set and moves whichever of them collided.
        // Calling HexFor here would hand this header the pre-collision answer
        // and quietly disagree with the ring on the orb it opened from.
        private static Color? AgentColorFor(string sessionId)
        {
            var agent = OpenClawSessions.AgentIdOf(sessionId);
            if (string.IsNullOrEmpty(agent)) return null;

            var hex = OpenClawSessions.ColourForAgent(agent);
            return Color.TryParse(hex, out var colour) ? colour : null;
        }

        // The header borrows the orb's letters and colours, and borrowed them
        // exactly once — at Bind. Anything that changed the orb afterwards left
        // the panel showing what the orb used to say: a /rename, a /color, a
        // title arriving after the first hook write, or the two-letter setting
        // being toggled while a panel was open. Worse than stale, at open time
        // it could be empty — an orb clicked before its first status write has
        // no glyph yet, and the header copied the nothing and kept it.
        //
        // Same shape as RepositionFor and SetRecording above: the orb tells the
        // panel, the panel checks the message is from the orb it is showing.
        public static void RefreshIdentityFor(OrbWindow orb)
        {
            if (_instance is not { IsVisible: true } panel) return;
            if (!ReferenceEquals(panel._owner, orb)) return;

            panel.ApplyBorrowedIdentity();
            panel.RefreshSoleSpeaker();
        }

        // Only the case that borrows from the orb. An agent with a portrait or
        // an OpenClaw identity has its own, and re-running the whole of
        // ApplyAvatar here would restart an animated avatar on every hook
        // write — which is several a second while a session is working.
        // The agent whose messages these are.
        //
        // For a gateway session that is the agent in the session key, not the
        // panel's title: "#openclaw-management" is where the conversation is
        // and Lilibeth is who is talking in it, and a chip reading "Op" would
        // be naming the room as its own speaker. Only a terminal session, whose
        // title *is* its agent, falls back to the title.
        // "Zara — wtvamp" is built as name plus place, so it splits back into
        // the two lines the header has. A name with no place — an agent's own
        // main session — leaves the second line empty.
        //
        // Read from the session every time rather than once at Bind. A terminal
        // session is usually nameless when its panel opens and gets its title
        // from a later hook write; the header used to keep the empty string it
        // was born with.
        private void ApplyTitle()
        {
            var parts = (_session?.DisplayName ?? "").Split(" — ", 2);

            TitleText.Text = parts[0];
            SubtitleText.Text = parts.Length > 1 ? parts[1] : "";
            SubtitleText.IsVisible = parts.Length > 1;
        }

        private void RefreshSoleSpeaker()
        {
            ApplyTitle();

            var was = _soleSpeaker.Name;

            var identity = _session is null
                ? null
                : OpenClawSessions.IdentityForSession(_session.SessionId);

            // The rule itself is in ChatSpeaker, pure and tested — including
            // the part that matters here, that a name we already knew is never
            // replaced by not knowing it. That is what made the chips vanish
            // after a while rather than simply never appear.
            var name = ChatSpeaker.Resolve(identity?.Name, TitleText.Text, was);

            if (name == was) return;

            _soleSpeaker.Name = name;

            foreach (var view in _turns) view.SpeakerChanged();
        }

        private void ApplyBorrowedIdentity()
        {
            if (!_borrowedIdentity || _owner is null) return;

            Avatar.Fill = new SolidColorBrush(_owner.OrbColor);
            AvatarEmoji.Foreground = InkOn(_owner.OrbColor);
            RingFor(_owner.AccentColor);

            var letters = BorrowedLetters();

            // Never blank what is already there. Same rule as ChatSpeaker and
            // the last place in the panel that still lacked it: both of this
            // one's sources can be momentarily empty for reasons that are about
            // us rather than about the session — the orb clears its glyph while
            // an avatar loads, and a title is empty until a hook write brings
            // one — and either used to wipe a circle that was reading fine.
            //
            // There is no case where going from letters to nothing is the truth
            // about a conversation. Nothing to say yet is the empty circle at
            // the start; nothing to say any more does not happen.
            if (string.IsNullOrEmpty(letters)) return;
            if (AvatarEmoji.Text == letters) return;

            AvatarEmoji.Text = letters;
            AvatarEmoji.IsVisible = true;
        }

        // What the orb is drawing, or what it would draw if it had got round to
        // it. The fallback matters because the panel can be bound before the
        // orb's first status write, and an empty circle beside a perfectly good
        // title is the one outcome that is never right. It derives them the way
        // the orb would rather than with Initials(), so the two agree on case
        // as well as on letters — "Cb" here and on the orb, not "CB" here.
        private string BorrowedLetters()
        {
            var letters = _owner?.GlyphText ?? "";
            if (!string.IsNullOrEmpty(letters)) return letters;

            var name = TitleText.Text;
            return string.IsNullOrWhiteSpace(name)
                ? ""
                : OrbGlyph.For(name, ClaudeBuddySettings.TwoLetterGlyphs);
        }

        // Ink that can be read on a given circle.
        //
        // AvatarEmoji had no Foreground at all and inherited the panel's, which
        // is near-black — fine on nothing, because the circle it sits in was
        // invisible until an identity was drawn behind it. Once the fill became
        // the orb's *state* colour it was black on black, and idle is near-black
        // by default. That is the whole of the "initials keep disappearing"
        // report: they were there the entire time, and the letters went from
        // legible to invisible when a session stopped working, because
        // generating and waiting are bright and idle is not.
        //
        // Chosen by luminance rather than fixed at white, which is what the orb
        // does. The orb only ever draws on a state colour and white suits all
        // of them; this circle is also filled with an agent's own colour, and
        // several of those are light enough that white letters vanish the same
        // way black ones just did.
        private static readonly IBrush LightInk = new SolidColorBrush(Color.Parse("#EEFFFFFF"));
        private static readonly IBrush DarkInk = new SolidColorBrush(Color.Parse("#E6000000"));

        private static IBrush InkOn(Color fill)
        {
            // Rec. 709 luminance: the eye is far more sensitive to green than
            // to blue, so a plain average calls mid-blue light and gets it
            // backwards.
            var luminance = (0.2126 * fill.R + 0.7152 * fill.G + 0.0722 * fill.B) / 255.0;

            return luminance > 0.55 ? DarkInk : LightInk;
        }

        private void ApplyAvatar(string sessionId)
        {
            StopAvatarAnimation();

            var avatar = OpenClawSessions.AvatarForSession(sessionId);
            var identity = OpenClawSessions.IdentityForSession(sessionId);

            // Neither a portrait nor an emoji, which is every local session and
            // a gateway one whose agent list hasn't landed yet. Its orb already
            // carries both halves of an identity — a letter and a colour, the
            // ones just clicked — so the header borrows them. Better than an
            // empty circle, and better than a second scheme invented for this
            // window: the panel ends up looking like the orb it came out of.
            //
            // Keyed on there being no OpenClaw identity rather than on the
            // session's type, because the panel deliberately doesn't know what
            // kinds of session exist.
            if (avatar is null && identity is null && _owner is not null)
            {
                _avatar = null;
                _avatarFrame = 0;

                // Fill from the state, ring from the identity — which is
                // exactly how the orb itself is drawn, so the header reads as
                // the same object rather than as a second scheme.
                //
                // Both from OrbColor is what this said first, and that made the
                // ring the state colour twice over: idle is a user setting and
                // is commonly near black, so the "identity ring" was an
                // invisible ring around a circle of its own colour.
                Avatar.Fill = new SolidColorBrush(_owner.OrbColor);
                Avatar.IsVisible = true;
                AvatarEmoji.Foreground = InkOn(_owner.OrbColor);
                RingFor(_owner.AccentColor);

                // An initial wants less room than an emoji does.
                // Set outright rather than through ApplyBorrowedIdentity,
                // which refuses to blank: this is a new conversation and the
                // letters on screen are the last one's. The never-blank rule is
                // about refreshing what is already right, not about carrying
                // one session's identity onto another — the same distinction
                // Unbind draws for the speaker.
                _borrowedIdentity = true;
                AvatarEmoji.Text = BorrowedLetters();
                AvatarEmoji.FontSize = 26;
                AvatarEmoji.IsVisible = !string.IsNullOrEmpty(AvatarEmoji.Text);

                StateDot.HorizontalAlignment = HorizontalAlignment.Right;
                StateDot.VerticalAlignment = VerticalAlignment.Bottom;
                return;
            }

            _avatar = avatar;
            _avatarFrame = 0;
            _borrowedIdentity = false;

            // Reset from whatever the branch above may have left behind.
            AvatarEmoji.FontSize = 38;

            if (avatar is null)
            {
                // No portrait. This used to leave a hollow circle — no fill, no
                // ring, and nothing inside unless the agent happened to have an
                // emoji, which reads as a picture that failed to load rather
                // than as a person.
                //
                // An agent already has both halves of an identity elsewhere in
                // the app: a colour from AgentPalette, keyed on its id so it is
                // stable, and a name. So the header shows what the orb shows —
                // that colour as the fill and ring, and the initials of the
                // name when there is no emoji to use instead.
                var agentColor = AgentColorFor(sessionId);

                AvatarEmoji.Text = !string.IsNullOrEmpty(identity?.Emoji)
                    ? identity!.Emoji!
                    : OrbGlyph.Initials(identity?.Name);
                AvatarEmoji.IsVisible = !string.IsNullOrEmpty(AvatarEmoji.Text);

                // Initials are letterforms, not a pictograph, so they want the
                // smaller size an emoji would overflow at.
                if (string.IsNullOrEmpty(identity?.Emoji)) AvatarEmoji.FontSize = 26;

                if (agentColor is { } c)
                {
                    Avatar.Fill = new SolidColorBrush(c);
                    Avatar.IsVisible = true;
                    AvatarEmoji.Foreground = InkOn(c);
                    RingFor(c);
                }
                else
                {
                    Avatar.Fill = null;
                    Avatar.IsVisible = false;
                }

                // With a filled circle the badge has somewhere to sit, so it
                // keeps its corner. Only a genuinely empty circle centres it.
                var filled = Avatar.IsVisible || AvatarEmoji.IsVisible;
                StateDot.HorizontalAlignment = filled
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Center;
                StateDot.VerticalAlignment = filled
                    ? VerticalAlignment.Bottom
                    : VerticalAlignment.Center;

                return;
            }

            AvatarEmoji.IsVisible = false;
            StateDot.HorizontalAlignment = HorizontalAlignment.Right;
            StateDot.VerticalAlignment = VerticalAlignment.Bottom;

            _avatarBrush ??= new ImageBrush { Stretch = Stretch.UniformToFill };
            _avatarBrush.Source = avatar.Frames[0];
            Avatar.Fill = _avatarBrush;
            Avatar.IsVisible = true;
            // A portrait gets the ring too. Without it the one avatar with a
            // picture is the only one in the app not wearing its own colour.
            RingFor(AgentColorFor(sessionId));

            if (!avatar.IsAnimated) return;

            _avatarTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(avatar.DelaysMs[0])
            };

            // The tick closes over its own timer and its own avatar rather than
            // reading the fields. A tick already queued on the dispatcher when
            // the panel rebinds to another agent would otherwise fire against
            // whatever is there now: a null timer if that agent's picture is
            // static, or the previous picture's frame delay if it isn't.
            var timer = _avatarTimer;
            var frames = avatar;
            var frame = 0;

            timer.Tick += (_, _) =>
            {
                if (!ReferenceEquals(_avatar, frames) || _avatarBrush is null) return;

                frame = (frame + 1) % frames.Frames.Count;
                _avatarBrush.Source = frames.Frames[frame];
                timer.Interval = TimeSpan.FromMilliseconds(frames.DelaysMs[frame]);
            };

            _avatarTimer.Start();
        }

        // Stopped when the panel goes away: a hidden window animating a GIF is
        // work nobody asked for, and the panel is hidden far more than it is up.
        private void StopAvatarAnimation()
        {
            _avatarTimer?.Stop();
            _avatarTimer = null;
        }

        // Clicking a picture opens it full size in whatever this machine views
        // pictures with. Handled so the click doesn't travel on to the panel
        // behind it, and so it can't be mistaken for a click-away dismiss.
        private void Image_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if ((sender as Control)?.DataContext is TurnView turn) turn.OpenFullSize();
        }

        private void Reposition()
        {
            if (_owner is null) return;

            // Anchor on the orb's centre, the same constant EnsureFlyoutShown
            // uses. PointToScreen because Position is physical pixels and these
            // are DIPs, and the two only agree at 100% scaling.
            var anchor = _owner.PointToScreen(new Point(28, 28));
            var screen = Screens.ScreenFromPoint(anchor) ?? Screens.Primary;
            if (screen is null) return;

            var scale = screen.Scaling;
            var work = screen.WorkingArea;

            var width = (int)(340 * scale);
            var height = (int)(Math.Max(Root.Bounds.Height, MinHeight) * scale);
            var gap = (int)(Gap * scale);

            // Below by default, flipped above when it would run off the bottom.
            // Flipped rather than clamped upward: a clamped panel ends up
            // covering the orb you just clicked.
            var y = anchor.Y + gap;
            if (y + height > work.Bottom) y = anchor.Y - gap - height;

            var x = Math.Clamp(anchor.X - width / 2, work.X, Math.Max(work.X, work.Right - width));
            y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - height));

            Position = new PixelPoint(x, y);
        }

        private void OnInputKeyDown(object? sender, KeyEventArgs e)
        {
            // While suggestions are up, the keys that would otherwise send or
            // insert a newline instead drive the popup — the same keys a
            // terminal's own "/" autocomplete would claim.
            if (_slashMatches.Count > 0)
            {
                if (e.Key == Key.Down)
                {
                    _slashSelected = (_slashSelected + 1) % _slashMatches.Count;
                    RenderSlashSuggestions();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Up)
                {
                    _slashSelected = (_slashSelected - 1 + _slashMatches.Count) % _slashMatches.Count;
                    RenderSlashSuggestions();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Escape)
                {
                    // Dismisses the popup only. A second Escape then reaches
                    // OnPanelKeyDown and closes the panel — the same
                    // two-step precedent recording already sets below.
                    HideSlashSuggestions();
                    e.Handled = true;
                    return;
                }

                var accepting = e.Key == Key.Tab
                    || (e.Key is Key.Enter or Key.Return && !e.KeyModifiers.HasFlag(KeyModifiers.Shift));

                if (accepting)
                {
                    AcceptSlashSuggestion(_slashMatches[_slashSelected]);
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key != Key.Enter && e.Key != Key.Return) return;

            // Shift+Enter is left entirely alone so the TextBox inserts the
            // newline itself, with its own caret handling and undo entry.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

            e.Handled = true;
            Send();
        }

        // Only while the input's first word is still being typed and starts
        // with "/" — a slash command is the whole message, not something
        // that can appear after other text, so anything past the first space
        // isn't a command being completed any more.
        private void UpdateSlashSuggestions()
        {
            if (_slashCommands.Count == 0) { HideSlashSuggestions(); return; }

            var text = Input.Text ?? "";
            var caret = Math.Clamp(Input.CaretIndex, 0, text.Length);
            var token = text[..caret];

            if (token.Length == 0 || token[0] != '/' || token.Contains(' ') || token.Contains('\n'))
            {
                HideSlashSuggestions();
                return;
            }

            _slashMatches = _slashCommands
                .Where(c => c.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Also closes once the only match is exactly what's already
            // typed — otherwise finishing a command by hand and pressing
            // Enter would "accept" it into itself instead of sending.
            if (_slashMatches.Count == 0
                || (_slashMatches.Count == 1 && string.Equals(_slashMatches[0].Name, token, StringComparison.OrdinalIgnoreCase)))
            {
                HideSlashSuggestions();
                return;
            }

            _slashSelected = 0;
            RenderSlashSuggestions();
            SlashBox.IsVisible = true;
        }

        private static readonly IBrush SlashRowFill = new SolidColorBrush(Colors.Transparent);
        private static readonly IBrush SlashRowSelected = new SolidColorBrush(Color.Parse("#33FFFFFF"));

        private void RenderSlashSuggestions()
        {
            SlashList.ItemsSource = _slashMatches
                .Select((c, i) => new SlashSuggestionView(c, i == _slashSelected ? SlashRowSelected : SlashRowFill))
                .ToList();
        }

        private void HideSlashSuggestions()
        {
            if (_slashMatches.Count == 0 && !SlashBox.IsVisible) return;

            _slashMatches = new List<SlashCommand>();
            _slashSelected = 0;
            SlashBox.IsVisible = false;
            SlashList.ItemsSource = null;
        }

        // Replaces the token being completed with the chosen command, the
        // way every other editor's autocomplete does — not sent outright.
        // Deciding a bare "/rename" is done and should go is the same
        // judgement call Send() already leaves to whoever is typing.
        private void AcceptSlashSuggestion(SlashCommand command)
        {
            var text = Input.Text ?? "";
            var caret = Math.Clamp(Input.CaretIndex, 0, text.Length);
            var rest = text[caret..];
            var replacement = command.Name + " ";

            Input.Text = replacement + rest;
            Input.CaretIndex = replacement.Length;

            // Last, not first: setting Text above already re-ran this via
            // TextChanged, and calling it again here is what makes the
            // outcome "closed" regardless of what that intermediate pass
            // computed.
            HideSlashSuggestions();
            Input.Focus();
        }

        private void SlashSuggestion_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if ((sender as Control)?.DataContext is SlashSuggestionView view) AcceptSlashSuggestion(view.Command);
        }

        private sealed record SlashSuggestionView(SlashCommand Command, IBrush RowFill)
        {
            public string Name => Command.Name;
            public string Description => Command.Description;
        }

        private void OnPanelKeyDown(object? sender, KeyEventArgs e)
        {
            var isClose = e.Key == Key.Escape
                || (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Meta));

            if (!isClose) return;

            e.Handled = true;

            // Escape while recording stops the recording and leaves the panel
            // up. Not a new rule: ScheduleFlyoutHide already refuses to hide the
            // arc while recording, because the control that stops it is on it. A
            // second Escape then dismisses.
            if (_owner?.IsRecording == true)
            {
                _owner.ToggleRecording();
                return;
            }

            HideNow();
        }

        private void Send()
        {
            var text = (Input.Text ?? "").Trim();
            if (text.Length == 0 || _session is null) return;

            Input.Text = "";

            // Sending is the one time the view should jump to the bottom
            // regardless. The autoscroll rule elsewhere deliberately leaves you
            // where you are reading, but a message you just sent landing
            // somewhere off screen reads as it not having sent at all.
            Dispatcher.UIThread.Post(() => Scroll.ScrollToEnd(), DispatcherPriority.Loaded);

            // Deliberately not inserting the user's turn here: the session
            // raises TurnAdded for it, so one thing owns the transcript and a
            // failed send leaves nothing behind to clean up.
            _ = _session.SendAsync(text);
        }

        private void SpeakLatest()
        {
            if (TextToSpeech.IsSpeaking)
            {
                TextToSpeech.Cancel();
                return;
            }

            var last = _turns.LastOrDefault(t => t.Role == ChatRole.Assistant);
            if (last is null || string.IsNullOrWhiteSpace(last.Text)) return;

            TextToSpeech.Speak(last.Text, ClaudeBuddySettings.SpeakVoice);
        }

        private void ApplySpeakState(TextToSpeech.SpeakState state)
        {
            SpeakFill.Fill = state switch
            {
                TextToSpeech.SpeakState.Speaking => SpeakActiveFill,
                TextToSpeech.SpeakState.Preparing => SpeakPreparingFill,
                _ => IdleFill
            };

            SpeakGlyph.Text = state switch
            {
                TextToSpeech.SpeakState.Speaking => "⏹",
                TextToSpeech.SpeakState.Preparing => "⏳",
                _ => "\U0001F508"
            };
        }

        private bool _loadingOlder;

        // Older messages, fetched when the transcript is scrolled to its top.
        //
        // The awkward part is not the fetch, it is that content appearing above
        // where you are reading pushes what you were reading down the screen.
        // So the extent is measured before and after, and the offset is moved by
        // the difference — which leaves the same words under the pointer and the
        // new ones above, the way every message app that does this behaves.
        private async Task LoadOlderAsync()
        {
            if (_loadingOlder) return;
            if (_session is not IRemoteChatBacklog chat || !chat.HasMore) return;

            _loadingOlder = true;

            try
            {
                var before = Scroll.Extent.Height;

                if (!await chat.LoadOlderAsync(CancellationToken.None)) return;

                // The prepend itself happens on the event below; this only has
                // to restore the position once layout has caught up with it.
                //
                // Twice, at two priorities: one yield gets the items into the
                // tree, and the measure that gives them height happens after
                // that. Measuring too early reads the old extent, and the
                // correction is then silently zero — the failure looks like the
                // scroll jumping rather than like a missing yield.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var grew = Scroll.Extent.Height - before;
                if (grew > 0) Scroll.Offset = Scroll.Offset.WithY(Scroll.Offset.Y + grew);
            }
            finally
            {
                _loadingOlder = false;
            }
        }

        private void OnHistoryPrepended(int count)
        {
            if (_session is null) return;

            // Inserted at the front in order, rather than rebuilding the whole
            // list: rebuilding would discard every already-fetched picture and
            // start them downloading again.
            for (var i = 0; i < count && i < _session.History.Count; i++)
            {
                _turns.Insert(i, new TurnView(_session.History[i], _defaultBubble, _soleSpeaker));
            }
        }

        private void OnHistoryReplaced()
        {
            if (_session is null) return;

            _turns.Clear();
            foreach (var turn in _session.History) _turns.Add(new TurnView(turn, _defaultBubble, _soleSpeaker));

            // Straight to the bottom rather than the pinned-only rule: a
            // transcript that has just been replaced wholesale has no scroll
            // position worth preserving, and the newest turn is the one you
            // opened the panel to read.
            Dispatcher.UIThread.Post(() => Scroll.ScrollToEnd(), DispatcherPriority.Loaded);
        }

        private void OnTurnAdded(ChatTurn turn)
        {
            _turns.Add(new TurnView(turn, _defaultBubble, _soleSpeaker));

            // Your own turn always brings the view with it; everything else
            // respects where you were reading.
            if (turn.Role == ChatRole.User)
            {
                Dispatcher.UIThread.Post(() => Scroll.ScrollToEnd(), DispatcherPriority.Loaded);
                return;
            }

            Dispatcher.UIThread.Post(ScrollToEndIfPinned, DispatcherPriority.Loaded);
        }

        private void OnTurnUpdated(ChatTurn turn)
        {
            // Nothing to do to the collection: the view wraps the same object
            // and forwards its own change notification, so no row is recreated
            // and nothing can steal focus by being re-templated.
            Dispatcher.UIThread.Post(ScrollToEndIfPinned, DispatcherPriority.Loaded);
        }

        private void OnStateChanged(RemoteChatState state)
        {
            StateDot.Fill = state switch
            {
                RemoteChatState.Connected => ConnectedFill,
                RemoteChatState.Connecting => SpeakPreparingFill,
                RemoteChatState.Error => RecordingFill,
                _ => IdleFill
            };
        }

        private void OnPromptChanged() => ApplyPrompt();

        // A dialog the session has stopped on, or nothing.
        //
        // The options are shown whether or not replying is switched on, and
        // clicking one while it is off produces the same explanation in the
        // transcript that sending a message would. Same reasoning as the
        // composer: the panel doesn't hide what is happening because you can't
        // act on it yet, and the session — which owns the rule — is the thing
        // that states it.
        private void ApplyPrompt()
        {
            var prompt = (_session as IRemoteChatPrompts)?.Prompt;

            if (prompt is null)
            {
                PromptBox.IsVisible = false;
                PromptOptions.ItemsSource = null;
                return;
            }

            PromptTitle.Text = prompt.Title;

            // No options means the screen couldn't be read. The box still
            // appears — something is waiting and the transcript won't say so —
            // but the only thing offered is the terminal.
            PromptOptions.ItemsSource = prompt.Options.Count > 0 ? prompt.Options : null;
            PromptOptions.IsVisible = prompt.Options.Count > 0;

            PromptBox.IsVisible = true;
        }

        private void PromptOption_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if ((sender as Control)?.DataContext is not ChatPromptOption option) return;
            if (_session is not IRemoteChatPrompts prompts) return;

            _ = prompts.AnswerAsync(option);
        }

        private void PromptElsewhere_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if (_session is not IRemoteChatPrompts prompts) return;

            prompts.AnswerElsewhere();

            // Dismissed, because this asked to be somewhere else. Leaving the
            // panel up over the terminal it just brought forward would be
            // covering the dialog it sent you to answer.
            HideNow();
        }

        // Only when already at the bottom, so reading back through a long reply
        // isn't yanked forward as it grows. DispatcherPriority.Loaded because
        // the extent isn't updated until the text has re-measured.
        private void ScrollToEndIfPinned()
        {
            var pinned = Scroll.Offset.Y >= Scroll.Extent.Height - Scroll.Viewport.Height - 8;
            if (pinned) Scroll.ScrollToEnd();
        }

        private void HideNow()
        {
            if (_session is not null) Drafts[_session.SessionId] = Input.Text ?? "";

            StopAvatarAnimation();
            AvatarPopup.Close();

            // Cleared with the panel, not left standing: the next session bound
            // here is very unlikely to be waiting on the same dialog, and a
            // stale one would offer buttons that answer nothing.
            PromptBox.IsVisible = false;
            PromptOptions.ItemsSource = null;

            // Detached while hidden. The panel is a singleton that stays alive
            // between openings, and a hidden panel left subscribed goes on
            // appending a row per event for a conversation nobody is watching —
            // the session's own history is bounded, this collection was not.
            // Bind rebuilds from History anyway, so there is nothing to keep.
            Unbind();

            _owner?.SetChatOpen(false);
            Hide();
        }

        // The row's own view. Exists so the template can bind colour, shape and
        // alignment per role without the transport's ChatTurn knowing what a
        // brush is — and so the three roles share one template instead of three.
        private sealed class TurnView : System.ComponentModel.INotifyPropertyChanged
        {
            private readonly ChatTurn _turn;
            private readonly Speaker? _soleSpeaker;
            private readonly Color? _defaultBubble;

            // soleSpeaker is who is talking when the transcript does not say.
            // A room stamps every turn with its speaker because there are
            // several; a one-to-one session — a Claude Code or Codex terminal,
            // or a single agent — stamps none, because there is only one and it
            // was obvious to whoever wrote the transport. It is not obvious in
            // the bubbles, which is the whole point of the chip.
            public TurnView(ChatTurn turn, Color? defaultBubble, Speaker? soleSpeaker)
            {
                _turn = turn;
                _defaultBubble = defaultBubble;
                _soleSpeaker = soleSpeaker;

                turn.PropertyChanged += (_, e) =>
                {
                    // A streaming turn replaces its whole text, so the rendered
                    // Markdown has to be thrown away with it. Without this the
                    // first snapshot of a reply is the only one ever drawn.
                    if (e.PropertyName == nameof(ChatTurn.Text))
                    {
                        _body = null;
                        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Body)));
                    }

                    PropertyChanged?.Invoke(this, e);
                };

                if (!string.IsNullOrEmpty(turn.ImageUrl)) LoadImage();
            }

            public ChatRole Role => _turn.Role;
            public string Text => _turn.Text;

            public bool HasText => !string.IsNullOrWhiteSpace(_turn.Text);

            // The rendered Markdown, rebuilt when the text changes.
            //
            // A control rather than a bound string because a reply has
            // structure — code blocks want a monospace box, list items want a
            // bullet and a hanging indent, and neither is expressible as one
            // TextBlock. Cached because OpenClaw streams: a snapshot arrives per
            // delta, and reparsing on read would reparse per layout pass too.
            private Control? _body;

            public Control Body => _body ??= BuildBody();

            private Control BuildBody()
            {
                var stack = new StackPanel { Spacing = 4 };

                foreach (var block in ChatMarkdown.Parse(_turn.Text))
                    stack.Children.Add(BuildBlock(block));

                // A turn whose text is only whitespace still needs something to
                // hand back; HasText hides it either way.
                if (stack.Children.Count == 0)
                    stack.Children.Add(Line(_turn.Text));

                return stack;
            }

            private Control BuildBlock(ChatMarkdown.MdBlock block)
            {
                switch (block.Kind)
                {
                    case ChatMarkdown.MdKind.Code:
                        // Wrapped rather than scrolled: the bubble is 244pt and
                        // a horizontal scrollbar per code block in a column of
                        // them is worse than a wrapped line.
                        return new Border
                        {
                            Background = CodeBackground,
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(6, 4),
                            Margin = new Thickness(0, 1),
                            Child = new TextBlock
                            {
                                Text = block.Text,
                                TextWrapping = TextWrapping.Wrap,
                                FontFamily = Mono,
                                FontSize = Size - 1,
                                Foreground = CodeInk
                            }
                        };

                    case ChatMarkdown.MdKind.Heading:
                    {
                        var heading = Line(block.Text);
                        heading.FontWeight = FontWeight.SemiBold;

                        // Only two sizes. Six levels of heading inside a bubble
                        // this size would be a distinction nobody could see.
                        heading.FontSize = block.Depth <= 2 ? Size + 1 : Size;
                        heading.Margin = new Thickness(0, 2, 0, 0);
                        return heading;
                    }

                    case ChatMarkdown.MdKind.Quote:
                    {
                        var quote = Line(block.Text);
                        quote.Opacity = 0.75;
                        return new Border
                        {
                            BorderBrush = QuoteEdge,
                            BorderThickness = new Thickness(2, 0, 0, 0),
                            Padding = new Thickness(6, 0, 0, 0),
                            Child = quote
                        };
                    }

                    case ChatMarkdown.MdKind.Bullet:
                    case ChatMarkdown.MdKind.Ordered:
                    {
                        // Two columns so the text hangs under itself rather than
                        // wrapping back beneath the bullet.
                        var row = new Grid
                        {
                            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                            Margin = new Thickness(block.Depth * 10, 0, 0, 0)
                        };

                        var marker = Line(block.Marker);
                        marker.Margin = new Thickness(0, 0, 5, 0);
                        marker.Opacity = 0.7;

                        var text = Line(block.Text);
                        Grid.SetColumn(text, 1);

                        row.Children.Add(marker);
                        row.Children.Add(text);
                        return row;
                    }

                    default:
                        return Line(block.Text);
                }
            }

            // One line of inline Markdown as a TextBlock of styled runs.
            private TextBlock Line(string text)
            {
                var block = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = Size,
                    FontStyle = Style,
                    Foreground = Ink
                };

                var spans = ChatMarkdown.Inline(text);

                // No markup at all is the common case, and setting Text avoids
                // building an Inlines collection for every plain line.
                if (spans.Count == 1 && spans[0].Style == ChatMarkdown.MdStyle.Normal)
                {
                    block.Text = spans[0].Text;
                    return block;
                }

                foreach (var span in spans)
                {
                    var run = new Run(span.Text);

                    switch (span.Style)
                    {
                        case ChatMarkdown.MdStyle.Bold:
                            run.FontWeight = FontWeight.SemiBold;
                            break;

                        case ChatMarkdown.MdStyle.Italic:
                            run.FontStyle = FontStyle.Italic;
                            break;

                        case ChatMarkdown.MdStyle.BoldItalic:
                            run.FontWeight = FontWeight.SemiBold;
                            run.FontStyle = FontStyle.Italic;
                            break;

                        // Avalonia's Run has no background, so inline code is
                        // told apart by face and colour rather than by a chip.
                        case ChatMarkdown.MdStyle.Code:
                            run.FontFamily = Mono;
                            run.FontSize = Size - 0.5;
                            run.Foreground = CodeInk;
                            break;

                        case ChatMarkdown.MdStyle.Link:
                            run.Foreground = LinkInk;
                            run.TextDecorations = TextDecorations.Underline;
                            break;
                    }

                    block.Inlines?.Add(run);
                }

                return block;
            }

            private static readonly FontFamily Mono = new("Menlo,SF Mono,Consolas,monospace");
            private static readonly IBrush CodeBackground = new SolidColorBrush(Color.Parse("#33000000"));
            private static readonly IBrush CodeInk = new SolidColorBrush(Color.Parse("#FFD9A0"));
            private static readonly IBrush LinkInk = new SolidColorBrush(Color.Parse("#9FD0FF"));
            private static readonly IBrush QuoteEdge = new SolidColorBrush(Color.Parse("#4DFFFFFF"));
            public bool HasImage => _image is not null;

            private Bitmap? _image;
            private byte[]? _bytes;

            // Full size, in the OS's own viewer — see OpenClawMedia for why this
            // isn't a window of ours.
            public void OpenFullSize()
            {
                if (_bytes is null) return;

                OpenClawMedia.Open(_bytes, _turn.ImageAlt);
            }

            public Bitmap? Image
            {
                get => _image;
                private set
                {
                    _image = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Image)));
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasImage)));
                }
            }

            // Fetched when the row is created rather than when it scrolls into
            // view. A transcript holds thirty turns at most and only a few carry
            // pictures, so the simpler thing is also cheap enough — and the
            // bytes are cached by url, so reopening the panel costs nothing.
            public async void LoadImage()
            {
                if (string.IsNullOrEmpty(_turn.ImageUrl)) return;

                var bytes = await OpenClawSessions.FetchMediaAsync(_turn.ImageUrl!, CancellationToken.None);
                if (bytes is null || bytes.Length == 0) return;

                // Kept as they arrived, not as they were decoded: opening the
                // picture full size should hand over the original rather than
                // the 456px copy the bubble draws.
                _bytes = bytes;

                try
                {
                    // Decoded on a worker: this awaits a network fetch that
                    // usually starts on the UI thread, so the continuation lands
                    // back there, and decoding an 840x1024 PNG on the thread
                    // that draws is a visible hitch per picture.
                    //
                    // Decoded to the width it is drawn at, twice over for
                    // Retina: keeping them at full size to show them at 228
                    // would be most of a megabyte of pixels each, held for as
                    // long as the panel is open.
                    var bitmap = await Task.Run(() =>
                    {
                        using var stream = new MemoryStream(bytes);
                        return Bitmap.DecodeToWidth(stream, 456);
                    });

                    Dispatcher.UIThread.Post(() => Image = bitmap);
                }
                catch
                {
                    // Not an image, or not one we can decode. The message keeps
                    // whatever text it had.
                }
            }

            // The name was filled in after this row was built. Everything
            // drawn from it has to be asked again — the chip is bound to five
            // separate properties and a stale one leaves half a chip.
            public void SpeakerChanged()
            {
                foreach (var name in new[]
                {
                    nameof(HasSpeaker), nameof(SpeakerName), nameof(ShowSpeakerName),
                    nameof(SpeakerInitials), nameof(SpeakerAvatar),
                    nameof(HasSpeakerAvatar), nameof(HasSpeakerInitials),
                    nameof(SpeakerChip), nameof(SpeakerChipInk)
                })
                {
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
                }
            }

            private bool IsSystem => _turn.Role == ChatRole.System;

            // A named speaker is never *you*, whatever role the transport gave
            // it. Another agent's message in a channel arrives with role "user"
            // — it is user-role input as far as this agent is concerned — and
            // taking that at face value drew it right-aligned in your own blue,
            // so a room full of agents looked like you talking to yourself.
            private bool IsUser => _turn.Role == ChatRole.User && !HasSpeaker;

            public bool HasSpeaker => !string.IsNullOrEmpty(SpeakerName);

            // Falls back to the session's one agent, but only on the agent's
            // own turns. Your messages are yours whoever else is in the room,
            // and a system note is about the conversation rather than in it —
            // stamping either with the agent's name would say it spoke them.
            public string SpeakerName =>
                !string.IsNullOrEmpty(_turn.Speaker) ? _turn.Speaker!
                : _turn.Role == ChatRole.Assistant ? _soleSpeaker?.Name ?? ""
                : "";

            // The name in words, beside the chip, only when the transcript
            // named the speaker itself. In a room that is worth the line: eight
            // agents talk and the names are how you follow who. In a one-to-one
            // it is the panel's own title repeated down the whole transcript,
            // which says nothing the header has not already said — so the chip
            // goes on alone, the way a messaging app shows a face and not a
            // name against every message from one person.
            public bool ShowSpeakerName => !string.IsNullOrEmpty(_turn.Speaker);

            // The speaker's own picture, when the gateway has one for them.
            //
            // The first frame only, even for an animated avatar. The header
            // animates its portrait with a timer; a room is a scrolling list of
            // dozens of turns, and one timer per row to animate a 16-pixel
            // circle is a lot of machinery for something too small to read a
            // motion in. The portrait is the place you look, and it still moves.
            public Bitmap? SpeakerAvatar =>
                OpenClawSessions.AvatarForAgentName(SpeakerName)?.Frames.FirstOrDefault();

            public bool HasSpeakerAvatar => SpeakerAvatar is not null;

            // Initials are the fallback, not the design: an agent with a face
            // shows the face, and the letters are for the ones without one and
            // for a name this cannot resolve to a single agent.
            public bool HasSpeakerInitials => !HasSpeakerAvatar;

            // The speaker's initials, for the chip beside their name.
            //
            // A name alone in a colour was enough while a room had two or three
            // agents in it. With eight it is a column of similar words in
            // similar hues, and the eye has to read each one — a shape it can
            // recognise without reading is what a room view is for. Same
            // letters the agent's own orb shows, so the chip and the orb are
            // recognisably the same agent.
            public string SpeakerInitials => OrbGlyph.Initials(SpeakerName);

            // Filled in the speaker's own colour, with the panel's own
            // background punched through it for the letters. Ink on a tinted
            // chip was the alternative and reads as a third bubble; a solid
            // dot reads as a person.
            public IBrush SpeakerChip =>
                SpeakerColor is { } c ? new SolidColorBrush(c) : SystemInk;

            public IBrush SpeakerChipInk =>
                SpeakerColor is not null ? ChipInk : SystemInk;

            // Near-black rather than the window's background brush: the chip is
            // a solid colour whatever is behind it, so the letters only have to
            // read against the chip.
            private static readonly IBrush ChipInk =
                new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));

            // The agent's own colour, the one their orb's ring is drawn in.
            private Color? SpeakerColor
            {
                get
                {
                    if (!string.IsNullOrEmpty(_turn.SpeakerColor)
                        && Color.TryParse(_turn.SpeakerColor, out var named))
                    {
                        return named;
                    }

                    // Only what the session said. Your own bubbles keep their
                    // blue, and a system note keeps none — the fallback is the
                    // agent's colour, and neither of those is the agent.
                    return IsSystem || IsUser ? null : _defaultBubble;
                }
            }

            public IBrush SpeakerInk =>
                SpeakerColor is { } c ? new SolidColorBrush(c) : SystemInk;

            public HorizontalAlignment Side => IsSystem
                ? HorizontalAlignment.Center
                : IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

            // Blue for you, grey for the agent — the arrangement every messaging
            // app has trained everyone to read without being told. The blue is
            // the same #4A90D9 the speak button already uses for "live", so the
            // app keeps one accent rather than acquiring a second.
            // A speaker's own colour, at low alpha. Full strength would be a
            // wall of saturated colour in a busy room — the name above it is
            // drawn in the same hue at full strength, which is enough to tie the
            // two together and to the orb.
            public IBrush Bubble => IsSystem
                ? Transparent
                : SpeakerColor is { } c
                    ? new SolidColorBrush(Color.FromArgb(0x3D, c.R, c.G, c.B))
                    : IsUser ? UserBubble : AgentBubble;

            public IBrush Ink => IsSystem ? SystemInk : BubbleInk;

            // The corner nearest the speaker is squared off. It is the one
            // detail that makes a column of bubbles read as two people talking
            // rather than as a list.
            public CornerRadius Corners => IsSystem
                ? new CornerRadius(0)
                : IsUser ? new CornerRadius(11, 11, 3, 11) : new CornerRadius(11, 11, 11, 3);

            public Thickness Pad => IsSystem
                ? new Thickness(0, 1)
                : HasImage && !HasText ? new Thickness(5) : new Thickness(9, 6);

            // Bubbles sit closer to their own side's previous bubble than to the
            // other side's, which is what gives a conversation its rhythm.
            public Thickness Gap => IsSystem
                ? new Thickness(0, 2, 0, 2)
                : IsUser ? new Thickness(40, 2, 0, 3) : new Thickness(0, 2, 40, 3);

            public double MaxBubbleWidth => IsSystem ? 300 : 244;

            public double Size => IsSystem ? 10 : 11.5;

            public FontStyle Style => IsSystem ? FontStyle.Italic : FontStyle.Normal;

            public bool ShowTime => !IsSystem;

            // Time alone for today, date and time for anything older — a
            // conversation that has been running since yesterday should say so,
            // and one from this afternoon shouldn't waste the width.
            public string TimeText
            {
                get
                {
                    var at = _turn.At.ToLocalTime();
                    return at.Date == DateTimeOffset.Now.Date
                        ? at.ToString("HH:mm")
                        : at.ToString("d MMM HH:mm");
                }
            }

            private static readonly IBrush UserBubble = new SolidColorBrush(Color.Parse("#E04A90D9"));
            private static readonly IBrush AgentBubble = new SolidColorBrush(Color.Parse("#26FFFFFF"));
            private static readonly IBrush Transparent = new SolidColorBrush(Colors.Transparent);
            private static readonly IBrush BubbleInk = new SolidColorBrush(Color.Parse("#F2FFFFFF"));
            private static readonly IBrush SystemInk = new SolidColorBrush(Color.Parse("#8CFFFFFF"));

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
