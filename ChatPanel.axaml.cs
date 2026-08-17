using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
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

        private readonly ObservableCollection<TurnView> _turns = new();

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
            var parts = session.DisplayName.Split(" — ", 2);
            TitleText.Text = parts[0];
            SubtitleText.Text = parts.Length > 1 ? parts[1] : "";
            SubtitleText.IsVisible = parts.Length > 1;

            ApplyAvatar(session.SessionId);
            OnStateChanged(session.State);

            _turns.Clear();
            foreach (var turn in session.History) _turns.Add(new TurnView(turn));

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

                Avatar.Fill = new SolidColorBrush(_owner.OrbColor);
                Avatar.IsVisible = true;

                // An initial wants less room than an emoji does.
                AvatarEmoji.Text = _owner.GlyphText;
                AvatarEmoji.FontSize = 26;
                AvatarEmoji.IsVisible = !string.IsNullOrEmpty(AvatarEmoji.Text);

                StateDot.HorizontalAlignment = HorizontalAlignment.Right;
                StateDot.VerticalAlignment = VerticalAlignment.Bottom;
                return;
            }

            _avatar = avatar;
            _avatarFrame = 0;

            // Reset from whatever the branch above may have left behind.
            AvatarEmoji.FontSize = 38;

            if (avatar is null)
            {
                Avatar.Fill = null;
                Avatar.IsVisible = false;

                // Emoji if there is one, and the state dot moves back to the
                // middle — a badge in the corner of an empty circle reads as a
                // picture that failed to load rather than as a status.
                AvatarEmoji.Text = identity?.Emoji ?? "";
                AvatarEmoji.IsVisible = !string.IsNullOrEmpty(identity?.Emoji);

                StateDot.HorizontalAlignment = AvatarEmoji.IsVisible
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Center;
                StateDot.VerticalAlignment = AvatarEmoji.IsVisible
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
            if (e.Key != Key.Enter && e.Key != Key.Return) return;

            // Shift+Enter is left entirely alone so the TextBox inserts the
            // newline itself, with its own caret handling and undo entry.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

            e.Handled = true;
            Send();
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
                _turns.Insert(i, new TurnView(_session.History[i]));
            }
        }

        private void OnHistoryReplaced()
        {
            if (_session is null) return;

            _turns.Clear();
            foreach (var turn in _session.History) _turns.Add(new TurnView(turn));

            // Straight to the bottom rather than the pinned-only rule: a
            // transcript that has just been replaced wholesale has no scroll
            // position worth preserving, and the newest turn is the one you
            // opened the panel to read.
            Dispatcher.UIThread.Post(() => Scroll.ScrollToEnd(), DispatcherPriority.Loaded);
        }

        private void OnTurnAdded(ChatTurn turn)
        {
            _turns.Add(new TurnView(turn));

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
                RemoteChatState.Connected => SpeakActiveFill,
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

            public TurnView(ChatTurn turn)
            {
                _turn = turn;
                turn.PropertyChanged += (_, e) => PropertyChanged?.Invoke(this, e);

                if (!string.IsNullOrEmpty(turn.ImageUrl)) LoadImage();
            }

            public ChatRole Role => _turn.Role;
            public string Text => _turn.Text;

            public bool HasText => !string.IsNullOrWhiteSpace(_turn.Text);
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

            private bool IsSystem => _turn.Role == ChatRole.System;
            private bool IsUser => _turn.Role == ChatRole.User;

            public HorizontalAlignment Side => IsSystem
                ? HorizontalAlignment.Center
                : IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

            // Blue for you, grey for the agent — the arrangement every messaging
            // app has trained everyone to read without being told. The blue is
            // the same #4A90D9 the speak button already uses for "live", so the
            // app keeps one accent rather than acquiring a second.
            public IBrush Bubble => IsSystem ? Transparent : IsUser ? UserBubble : AgentBubble;

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
