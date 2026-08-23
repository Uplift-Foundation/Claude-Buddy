using Avalonia.Threading;

namespace ClaudeBuddy
{
    // One Claude Code session on another machine, as something the chat panel
    // can talk to.
    //
    // Deliberately a **command channel, not a transcript mirror**, and the
    // difference is worth being plain about because the panel looks identical
    // either way. A local session's panel shows the whole conversation, because
    // Buddy reads that session's own transcript file off the disk it is sitting
    // on. There is no such file here — it is on the other machine — and the only
    // channel to it is peer messaging, which carries what you send and what
    // comes back and nothing else. So this shows your side and its replies, the
    // way a text thread does, and does not pretend to show what that session is
    // doing the rest of the time.
    //
    // Which is why IRemoteChatBacklog is not implemented: there is nothing to
    // page back into. Claiming it and returning empty would put a "loading
    // older messages" spinner on a conversation that has no history to load.
    //
    // The other half of the honesty is IRemoteChatComposer: the input box says
    // where the message is going, because typing into a panel that looks exactly
    // like a local one and having it arrive on a different computer is a
    // surprise worth spending a line of text on.
    internal sealed class RemoteControlChatSession :
        IRemoteChatSession, IRemoteChatComposer, IRemoteChatSlashCommands
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

        public RemoteControlChatSession(string sessionId, string account, string remoteName)
        {
            SessionId = sessionId;
            _account = account;
            _remoteName = remoteName;
            DisplayName = remoteName;

            // Opens with a line saying what this panel is, because otherwise it
            // opens empty and an empty panel reads as broken.
            //
            // Every other panel in this app fills itself from a transcript on
            // this disk, so a person reasonably expects to see the conversation
            // that session is already having. There is no such file here — it is
            // on the other machine — and this channel only carries what passes
            // through it. Saying so once, up front, is the difference between
            // "nothing loaded" and "nothing has been said yet".
            //
            // It also survives the one case that surprised me in testing: the
            // history is in memory, so restarting Claude Buddy empties it. With
            // this line the panel still explains itself after a restart instead
            // of being a blank box.
            Note($"Messages you send {remoteName} appear here, with its replies. "
                + "Its own conversation stays on the machine it is running on — this is a way to "
                + "talk to it, not a view of it.");
        }

        public string SessionId { get; }

        public string DisplayName { get; set; }

        public RemoteChatState State { get; private set; } = RemoteChatState.Connected;

        public IReadOnlyList<ChatTurn> History => _history;

        public event Action<ChatTurn>? TurnAdded;

        // Declared and never raised, which the compiler notes as CS0067 —
        // deliberately, and the same way OpenClawRoomChatSession carries the
        // identical warning. A turn here is never edited after the fact: a
        // message either arrives whole or does not arrive, since there is no
        // streaming to revise (see this class's header). The interface requires
        // the event, so it exists and stays quiet rather than being raised with
        // nothing to say.
        public event Action<ChatTurn>? TurnUpdated;

        public event Action<RemoteChatState>? StateChanged;

        // Said in the input box itself. "Message…" would be a lie by omission
        // here: this one leaves the machine.
        public string ComposerHint => $"Message {_remoteName} on the other machine…";

        // The built-in floor only — see SlashCommandCatalog.ForRemoteClaudeCode
        // for why the disk-discovered half is left out. Worth having even so:
        // sending "/" commands is most of what this channel is for, since a
        // command is exactly the kind of thing worth asking a machine elsewhere
        // to run, and typing one blind into a panel that offers no completion is
        // the worst version of that.
        public IReadOnlyList<SlashCommand> SlashCommands { get; } =
            SlashCommandCatalog.ForRemoteClaudeCode();

        public async Task SendAsync(string text)
        {
            // The user's own turn is added here rather than by the panel, so one
            // thing owns the transcript and a send that fails leaves the message
            // on screen with an explanation under it rather than a ghost. Same
            // reasoning as OpenClawChatSession's.
            Add(new ChatTurn { Role = ChatRole.User, Text = text, IsComplete = true });

            if (!ClaudeBuddySettings.RemoteControlEnabled)
            {
                Note("Remote sessions are switched off. Turn on \"Show sessions from other machines\" in Settings.");
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
        // failed, which is the wrong thing to leave someone guessing about. The
        // orb pulses for the same reason, but the orb is behind the panel you
        // are looking at.
        private ChatTurn? _workingNote;

        public void SetWorking(bool working)
        {
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
            // Nothing to cancel. Stopping work on another machine is not
            // something this channel can do — SendMessage delivers a message,
            // it does not interrupt a run — and a Cancel that silently did
            // nothing while looking like it worked would be worse than none.
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

            // Bounded for the same reason the local sessions' history is: a
            // panel left open on a chatty conversation should not grow without
            // limit. Generous, because this channel is low-volume by nature —
            // every turn in it is something a person typed or a machine
            // answered.
            const int keep = 200;
            if (_history.Count > keep) _history.RemoveRange(0, _history.Count - keep);

            if (Dispatcher.UIThread.CheckAccess()) TurnAdded?.Invoke(turn);
            else Dispatcher.UIThread.Post(() => TurnAdded?.Invoke(turn));
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
