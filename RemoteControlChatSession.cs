using Avalonia.Threading;

namespace ClaudeBuddy
{
    // One Claude Code session on another machine, as something the chat panel
    // can talk to — in one of two modes, and the difference between them is the
    // whole point of this class.
    //
    // **Live view** is what you get when the other machine is also running
    // Claude Buddy. That Buddy reads the session's transcript off its own disk
    // and sends it here in hashed pieces, so this panel shows the same
    // conversation the person sitting in front of that machine sees — verbatim,
    // byte for byte, parsed by the same ChatTranscript a local panel uses. What
    // you type is typed into that session's own input line, which means slash
    // commands work: /color, /rename, all of them, because the CLI's own command
    // handler is what runs them.
    //
    // **Messaging** is the fallback, and it is what this class used to be
    // always. Without a Buddy on the far side there is no way to read a file
    // there — the only channel is peer messaging, which reaches the far
    // session's *model*, not its terminal. So what comes back is a reply that
    // model wrote for a peer: its own words about its conversation rather than
    // its conversation. That is not a bug in the transport and cannot be fixed
    // by asking nicely; it was measured being asked nicely and it still
    // paraphrased. It is simply the most that channel can carry, and the panel
    // says so in as many words rather than letting a summary pass for a
    // transcript.
    //
    // A panel opens in messaging mode and **upgrades in place** when the far
    // Buddy answers, which is why this is one class with a mode rather than two
    // classes: the handshake costs a round trip through a model and can take
    // half a minute, and nobody should have to close a panel and reopen it to
    // find out it could have been a live view all along. HistoryReplaced is what
    // makes that free — the panel already redraws from History when it fires.
    internal sealed class RemoteControlChatSession :
        IRemoteChatSession, IRemoteChatComposer, IRemoteChatSlashCommands, IRemoteChatBacklog, IDisposable
    {
        private readonly List<ChatTurn> _history = new();

        // The peer's name on the other machine — the correlation key that
        // matches an inbound message back to this conversation. Not a display
        // nicety: it is the only link, because replies arrive on some later turn
        // of the bridge's conversation with nothing tying them to the send.
        private readonly string _remoteName;

        // Which account's relay this conversation goes through. Needed because
        // there is one relay per account now, and a name alone no longer says
        // which machine — or which login — a message should leave by.
        private readonly string _account;

        // Rows already turned into turns, by transcript uuid. Only meaningful in
        // live view, where the same row can legitimately arrive twice: the
        // opening window and the first delta can overlap if the file grew
        // between the two reads.
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        private bool _mirroring;
        private CliChatFormat _format = CliChatFormat.ClaudeCode;
        private bool _saidNoLiveView;
        private bool _disposed;

        public RemoteControlChatSession(string sessionId, string account, string remoteName)
        {
            SessionId = sessionId;
            _account = account;
            _remoteName = remoteName;
            DisplayName = remoteName;

            // Opens with a line saying what this panel is, because otherwise it
            // opens empty and an empty panel reads as broken.
            //
            // It also survives the one case that surprised me in testing: the
            // history is in memory, so restarting Claude Buddy empties it. With
            // this line the panel still explains itself after a restart instead
            // of being a blank box.
            //
            // Deliberately does not promise a live view yet. Saying "mirroring…"
            // and then falling back would be worse than saying "checking" and
            // then succeeding.
            Note($"Messages you send {remoteName} appear here, with its replies. "
                + "Checking whether a live view of its conversation is available…");

            RemoteControlSessions.MirrorChanged += OnMirrorChanged;

            // The answer may already be in — a second panel on a machine that
            // handshook minutes ago upgrades before it is ever drawn.
            TryUpgrade();
        }

        public string SessionId { get; }

        public string DisplayName { get; set; }

        public RemoteChatState State { get; private set; } = RemoteChatState.Connected;

        public IReadOnlyList<ChatTurn> History => _history;

        public bool IsMirroring => _mirroring;

        public event Action<ChatTurn>? TurnAdded;

        // Raised in live view, where the transcript settles a turn that was
        // already on screen: the message you typed comes back as a real row once
        // the far CLI has read it, and adopts the bubble rather than adding a
        // second. Never raised in messaging mode, which has no echo to
        // reconcile — see the note on Reconcile.
        public event Action<ChatTurn>? TurnUpdated;

        public event Action<RemoteChatState>? StateChanged;

        // Said in the input box itself. "Message…" would be a lie by omission
        // here: this one leaves the machine, and in live view it is typed into
        // somebody else's terminal, which is worth being even plainer about.
        public string ComposerHint => _mirroring
            ? $"Type into {_remoteName}'s terminal on the other machine…"
            : $"Message {_remoteName} on the other machine…";

        // In live view: every command that session can run, read off the far
        // machine's own disk by the Buddy sitting next to it — built-ins
        // included, because a mirrored send is typed into that CLI's input line
        // and its own handler runs it.
        //
        // In messaging mode: only what the far session said it can run, and
        // nothing until it has said so. A built-in genuinely cannot run over
        // that channel — measured, with /color coming back "I can't run /color
        // ... only the harness's own command handler can set" it — so offering
        // one would be offering something that quietly does nothing when
        // accepted. RemoteControlSessions.CommandsFor knows which of the two
        // answers it has.
        public IReadOnlyList<SlashCommand> SlashCommands =>
            RemoteControlSessions.CommandsFor(_account, _remoteName);

        // --- live view ---------------------------------------------------------

        private void OnMirrorChanged(string account)
        {
            if (!account.Equals(_account, StringComparison.Ordinal)) return;

            if (Dispatcher.UIThread.CheckAccess()) TryUpgrade();
            else Dispatcher.UIThread.Post(TryUpgrade);
        }

        private void TryUpgrade()
        {
            if (_disposed || _mirroring) return;

            var state = RemoteControlSessions.MirrorStateFor(_account, _remoteName);

            if (state.Availability == RemoteMirrorClient.MirrorAvailability.Unavailable)
            {
                SayNoLiveView();
                return;
            }

            if (state.Availability != RemoteMirrorClient.MirrorAvailability.Available) return;
            if (state.Entry is not { } entry) return;

            var client = RemoteControlSessions.MirrorClientFor(_account);
            if (client is null) return;

            _mirroring = true;
            _format = CliChatFormat.For(
                entry.Cli.Equals(MirrorProtocol.CliCodex, StringComparison.OrdinalIgnoreCase)
                    ? SessionSource.Codex
                    : SessionSource.ClaudeCode);

            client.Delivered += OnDelivered;
            client.Failed += OnMirrorFailed;

            // Only if somebody is actually looking. A handshake that lands while
            // every panel is closed switches the mode and stops there; the next
            // PanelOpened reads the tail, which by then is the current one
            // anyway.
            if (_panelOpen) _ = client.OpenAsync(_remoteName);
        }

        private void SayNoLiveView()
        {
            if (_saidNoLiveView) return;
            _saidNoLiveView = true;

            Note($"No live view: the other machine isn't running Claude Buddy's Remote Control for "
               + $"this session, so this stays a messaging channel — a way to talk to {_remoteName}, "
               + "not a view of it. Its replies here are written for you, and may summarise what it "
               + "actually did.");
        }

        private void OnDelivered(RemoteMirrorClient.MirrorRows rows)
        {
            if (!rows.Name.Equals(_remoteName, StringComparison.OrdinalIgnoreCase)) return;

            // Parsed off the UI thread would be nicer, but this is already on a
            // background thread — the relay's pump — and the parse is the same
            // one a local panel does inline on open.
            var mapped = _format.Map(rows.Rows);

            OnUi(() =>
            {
                if (_disposed) return;

                if (rows.Mode == RemoteMirrorClient.MirrorDelivery.Window)
                {
                    _history.Clear();
                    _seen.Clear();
                    _pending = null;

                    foreach (var row in mapped)
                    {
                        if (row.Uuid is not null && !_seen.Add(row.Uuid)) continue;
                        _history.Add(row.Turn);
                    }

                    Trim();

                    // Said once, at the top of the real conversation, so nobody
                    // mistakes a mirror for a chat thread and wonders why their
                    // half of it is missing.
                    _history.Insert(0, new ChatTurn
                    {
                        Role = ChatRole.System,
                        IsComplete = true,
                        Text = $"Live view: this panel mirrors {_remoteName}'s own conversation from "
                             + "the other machine, a few seconds behind. Messages you type are typed "
                             + "into its terminal."
                    });

                    HistoryReplaced?.Invoke();
                    return;
                }

                foreach (var row in mapped)
                {
                    if (row.Uuid is not null && !_seen.Add(row.Uuid)) continue;
                    if (Reconcile(row.Turn)) continue;

                    Add(row.Turn);
                }
            });
        }

        private void OnMirrorFailed(string name, string why)
        {
            if (!name.Equals(_remoteName, StringComparison.OrdinalIgnoreCase)) return;

            OnUi(() =>
            {
                if (_disposed) return;

                // Nothing of the failed transfer is shown — not a partial
                // window, not the messaging-channel version of it. The whole
                // reason this feature exists is that a plausible-looking second
                // draft is indistinguishable from the real thing once it is on
                // screen, and quietly substituting one at the exact moment
                // integrity failed would be the worst possible time to do it.
                Note($"Couldn't verify {_remoteName}'s transcript — {why}. Showing nothing rather "
                   + "than something altered; close and reopen the panel to try again.");
            });
        }

        // --- backlog -------------------------------------------------------------

        // Claimed in both modes, answered honestly in each. In messaging mode
        // there is nothing older to fetch and this is false forever, which is
        // what keeps a "loading older messages" spinner off a conversation that
        // has no history to load — the panel asks before every fetch.
        public bool HasMore =>
            _mirroring && RemoteControlSessions.MirrorClientFor(_account)?.HasMore(_remoteName) == true;

        public async Task<bool> LoadOlderAsync(CancellationToken ct)
        {
            if (!HasMore) return false;

            var client = RemoteControlSessions.MirrorClientFor(_account);
            if (client is null) return false;

            var rows = await client.LoadOlderAsync(_remoteName).ConfigureAwait(true);
            if (rows is null) return false;

            var mapped = _format.Map(rows);
            var older = new List<ChatTurn>();

            foreach (var row in mapped)
            {
                if (row.Uuid is not null && !_seen.Add(row.Uuid)) continue;
                older.Add(row.Turn);
            }

            if (older.Count == 0)
            {
                // A page that parsed to nothing but moved the offset is not the
                // end — the window can be entirely tool results. Same rule as
                // LocalCliChatSession: the answer is whether there is more to
                // ask for, not whether this page had anything in it.
                return HasMore;
            }

            // After the live-view banner, which is the one thing that stays at
            // the top: it describes the panel, not a moment in the conversation.
            var at = _history.Count > 0 && _history[0].Role == ChatRole.System ? 1 : 0;
            _history.InsertRange(at, older);

            HistoryPrepended?.Invoke(older.Count);
            return true;
        }

        public event Action? HistoryReplaced;

        public event Action<int>? HistoryPrepended;

        // --- sending -------------------------------------------------------------

        public async Task SendAsync(string text)
        {
            // The user's own turn is added here rather than by the panel, so one
            // thing owns the transcript and a send that fails leaves the message
            // on screen with an explanation under it rather than a ghost. Same
            // reasoning as OpenClawChatSession's.
            var mine = new ChatTurn { Role = ChatRole.User, Text = text, IsComplete = true };
            Add(mine);

            if (!ClaudeBuddySettings.RemoteControlEnabled)
            {
                Note("Remote sessions are switched off. Turn on \"Show sessions from other machines\" in Settings.");
                return;
            }

            if (_mirroring)
            {
                await SendTypedAsync(mine, text).ConfigureAwait(true);
                return;
            }

            string? id;
            try
            {
                // Starts the bridge if it isn't up, so a message typed after an
                // idle shutdown just works rather than needing the tray item
                // again. The wait is the price of that, and it is why the
                // composer stays enabled rather than being disabled while down.
                id = await RemoteControlSessions.SendToAsync(_account, _remoteName, text).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Note("Couldn't send: " + ex.Message);
                return;
            }

            if (id is null)
            {
                // Deliberately vague about which link failed, because from here
                // they are indistinguishable: the bridge may not have started,
                // its login may have expired, or the model may not have called
                // the tool. Naming one would be a guess presented as a fact.
                Note($"Couldn't reach {_remoteName}. The relay session may not be running — "
                   + "check Settings, or try again to restart it.");
                return;
            }

            // No "sent" confirmation on screen. The message is already there as
            // the user's own turn, and a receipt under every line would be noise
            // — the reply, when it comes, is the confirmation that matters.
        }

        // The live-view send: typed into the far session's terminal by the Buddy
        // beside it. The far transcript will produce this message back, because
        // it went in through the input line — which is exactly what makes slash
        // commands work, and why the echo has to be reconciled rather than shown
        // twice.
        private async Task SendTypedAsync(ChatTurn mine, string text)
        {
            var client = RemoteControlSessions.MirrorClientFor(_account);
            if (client is null)
            {
                Note("The relay session isn't running. Try again to start it back up.");
                return;
            }

            // Marked pending after Add, never before: Add runs every turn
            // through Reconcile, and setting this first would make the message
            // match itself and vanish on the spot.
            _pending = mine;
            _pendingText = text.Trim();
            _pendingAt = DateTimeOffset.Now;

            RemoteControlSessions.Touch();

            var error = await client.SendInputAsync(_remoteName, text).ConfigureAwait(true);
            if (error is null) return;

            _pending = null;

            Note(error switch
            {
                MirrorProtocol.ErrReplyOff =>
                    $"{_remoteName}'s machine has replying to sessions switched off, so nothing was typed. "
                    + "That is its own setting, and it has to be turned on over there.",

                MirrorProtocol.ErrNoPane =>
                    $"{_remoteName} isn't in a tmux pane on the other machine, so there is nowhere to "
                    + "type without bringing its terminal forward.",

                MirrorProtocol.ErrNoSession =>
                    $"The other machine's Claude Buddy no longer has a session called {_remoteName}.",

                MirrorProtocol.ErrBadHash =>
                    "That message didn't survive the trip intact and was refused rather than typed "
                    + "in a form you didn't write. Try sending it again.",

                _ => $"Couldn't type that into {_remoteName}."
            });
        }

        private ChatTurn? _pending;
        private string _pendingText = "";
        private DateTimeOffset _pendingAt;

        // The mirrored transcript will produce the message just sent, because it
        // went through the terminal. So the row that comes back adopts the turn
        // already on screen rather than adding a second.
        //
        // Matched on text and bounded by time, the same way LocalCliChatSession
        // does it and for the same reason: an identical message sent twice an
        // hour apart must not have the second swallowed by a stale pending turn
        // that never arrived.
        private bool Reconcile(ChatTurn incoming)
        {
            if (_pending is null) return false;

            if (DateTimeOffset.Now - _pendingAt > TimeSpan.FromMinutes(2))
            {
                _pending = null;
                return false;
            }

            if (ReferenceEquals(incoming, _pending)) return false;
            if (incoming.Role != ChatRole.User) return false;
            if (!string.Equals(incoming.Text.Trim(), _pendingText, StringComparison.Ordinal)) return false;

            // Keep the transcript's own text: it is what that session actually
            // received, which is the thing this panel exists to show.
            var settled = _pending;
            _pending = null;

            settled.Text = incoming.Text;
            TurnUpdated?.Invoke(settled);
            return true;
        }

        // --- inbound (messaging mode) ---------------------------------------------

        // A message from the other machine. Called on the UI thread by
        // RemoteControlSessions, which is the contract IRemoteChatSession states.
        public void OnInbound(BridgeProtocol.InboundMessage message)
        {
            // Both halves must match. The name says which session and the
            // account says whose — and with two accounts in play, a name on its
            // own can be true of two different machines at once.
            if (!message.FromName.Equals(_remoteName, StringComparison.OrdinalIgnoreCase)) return;
            if (message.Account.Length > 0
                && !message.Account.Equals(_account, StringComparison.Ordinal))
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(message.Body)) return;

            // In live view the transcript is the source of truth and a peer
            // message would be a second, differently-worded account of
            // something already shown. Dropped rather than appended: showing
            // both is precisely the confusion this feature was built to end.
            if (_mirroring) return;

            // The answer supersedes the "working" line, so that comes off first —
            // leaving it above the reply would read as though it were still going.
            ClearWorkingNote();

            Add(new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = message.Body,
                IsComplete = true
            });
        }

        // The waiting indicator, and the reason it is not decorative.
        //
        // A reply can be minutes away — the remote session may be running a
        // whole command — and until it lands the panel is a message you typed
        // and nothing else. That is indistinguishable from a send that silently
        // failed, which is the wrong thing to leave someone guessing about.
        //
        // Only in messaging mode. A live view shows the work itself: the far
        // session's own turns arrive as it makes them, so a line claiming it is
        // working would sit under the evidence that it is.
        private ChatTurn? _workingNote;

        public void SetWorking(bool working)
        {
            if (_mirroring) return;

            if (working)
            {
                if (_workingNote is not null) return;

                // IsComplete false rather than true: this is a turn still in
                // progress, which is what the flag means everywhere else, and it
                // keeps the row from reading as a finished statement.
                _workingNote = new ChatTurn
                {
                    Role = ChatRole.System,
                    Text = $"{_remoteName} is working…",
                    IsComplete = false
                };

                Add(_workingNote);
                return;
            }

            // Went idle without answering. The note still comes off — a stale
            // "working…" is worse than no indicator, because it is a claim rather
            // than an absence.
            ClearWorkingNote();
        }

        private void ClearWorkingNote()
        {
            if (_workingNote is null) return;

            var note = _workingNote;
            _workingNote = null;

            // Removed rather than rewritten. Turning it into "finished" would
            // leave a line nobody needs in a transcript that is only ever a
            // handful of turns long.
            if (_history.Remove(note)) Removed?.Invoke(note);
        }

        // The panel rebuilds its list from History when this fires. There is no
        // TurnRemoved on IRemoteChatSession — nothing else has ever needed to
        // take a turn back — so this is deliberately local to this class and the
        // panel subscribes only when it recognises the type.
        public event Action<ChatTurn>? Removed;

        // Said out loud rather than silently dropping the conversation, because
        // an idle shutdown is invisible from the panel: nothing on screen
        // changes, and the next message would otherwise be the first hint.
        public void OnBridgeStopped(string why)
        {
            if (State == RemoteChatState.Error) return;

            Note($"The relay session stopped ({why}). Sending again will start it back up.");
        }

        public void Cancel()
        {
            // Nothing to cancel, in either mode, and for two different reasons.
            //
            // Messaging: stopping work on another machine is not something that
            // channel can do — SendMessage delivers a message, it does not
            // interrupt a run.
            //
            // Live view: it could, in principle — Escape is what interrupts the
            // TUI and the far Buddy can send a key as easily as a line — but the
            // protocol carries no key frame yet, and a Cancel that silently did
            // nothing while looking like it worked would be worse than none.
        }

        // --- the panel coming and going --------------------------------------

        // Whether anyone is actually looking. A live view is the one part of
        // this that costs something while unwatched — it holds a subscription on
        // the other machine's Buddy, and that keeps a real session on the user's
        // account awake — so it follows the panel rather than this object, which
        // deliberately outlives every panel that shows it.
        private bool _panelOpen;

        public void PanelOpened()
        {
            _panelOpen = true;

            // The handshake may have finished while nothing was open.
            if (!_mirroring) TryUpgrade();
            else _ = RemoteControlSessions.MirrorClientFor(_account)?.ReopenAsync(_remoteName);
        }

        public void PanelClosed()
        {
            _panelOpen = false;

            if (!_mirroring) return;

            // The history stays exactly as it is. Only the subscription goes:
            // reopening re-reads the tail, which is cheap and is also the right
            // thing, since the conversation will have moved on.
            _ = RemoteControlSessions.MirrorClientFor(_account)?.CloseAsync(_remoteName);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            RemoteControlSessions.MirrorChanged -= OnMirrorChanged;

            var client = RemoteControlSessions.MirrorClientFor(_account);
            if (client is null) return;

            client.Delivered -= OnDelivered;
            client.Failed -= OnMirrorFailed;

            if (_mirroring) _ = client.CloseAsync(_remoteName);
        }

        // Everything that touches the history has to land on the UI thread,
        // which is the contract IRemoteChatSession states. Run inline when
        // already there rather than always posting: a mirror delivered from the
        // relay's pump is on a background thread, but one delivered inside a
        // test — or by a reopen from a click — is not, and posting there would
        // defer the update behind a dispatcher turn nobody pumps.
        private static void OnUi(Action work)
        {
            if (Dispatcher.UIThread.CheckAccess()) work();
            else Dispatcher.UIThread.Post(work);
        }

        private void Note(string text) => Add(new ChatTurn
        {
            Role = ChatRole.System,
            IsComplete = true,
            Text = text
        });

        private void Add(ChatTurn turn)
        {
            _history.Add(turn);
            Trim();

            if (Dispatcher.UIThread.CheckAccess()) TurnAdded?.Invoke(turn);
            else Dispatcher.UIThread.Post(() => TurnAdded?.Invoke(turn));
        }

        private void Trim()
        {
            // Bounded for the same reason the local sessions' history is: a
            // panel left open on a chatty conversation should not grow without
            // limit.
            //
            // Live view keeps as much as a local panel does, because it is one:
            // it is showing a real transcript and scrolling back through it.
            // Messaging keeps less because that channel is low-volume by nature
            // — every turn in it is something a person typed or a machine
            // answered.
            var keep = _mirroring ? 500 : 200;
            if (_history.Count > keep) _history.RemoveRange(0, _history.Count - keep);
        }

        internal void SetState(RemoteChatState state)
        {
            if (State == state) return;

            State = state;

            if (Dispatcher.UIThread.CheckAccess()) StateChanged?.Invoke(state);
            else Dispatcher.UIThread.Post(() => StateChanged?.Invoke(state));
        }
    }
}
