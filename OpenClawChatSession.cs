using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // One turn as the history parser hands it over: what TurnsFromHistory reads
    // out of a page of chat.history, and what SetHistory and PrependHistory put
    // into a transcript.
    //
    // A record rather than the seven-field tuple this was. The tuple was
    // tolerable while it was three fields and stopped being so at seven — every
    // producer and consumer restated the whole shape in its own signature, so
    // the shape appeared eight times in two files plus once in each test's
    // helper, and adding a field meant editing all of them before anything
    // compiled again. Named members also read at the call site: `turn.Speaker`
    // says what `t.Item6` does not.
    //
    // A struct because it is a value and is treated as one — a page is a few
    // hundred of these, each copied into a ChatTurn immediately, and nothing
    // ever holds on to one.
    internal readonly record struct HistoryTurn(
        ChatRole Role,
        string Text,
        string? ImageUrl,
        string ImageAlt,
        DateTimeOffset At,
        string? Speaker,
        string? SpeakerColor);

    // One OpenClaw session, as something the chat panel can talk to.
    //
    // Reading works today. **Sending does not**, and says so rather than
    // failing quietly: the app pairs itself with `operator.read` and nothing
    // else, so `chat.send` would be refused by the gateway. Widening that is a
    // deliberate act — it means re-pairing this device with `operator.write`,
    // which a person has to approve on the gateway — and it is not something
    // opening a window should do on their behalf. Until then this is a reader
    // with an input box that explains itself.
    internal sealed class OpenClawChatSession : IRemoteChatSession, IRemoteChatBacklog, IRemoteChatComposer
    {
        private readonly List<ChatTurn> _history = new();

        // The turn currently being streamed, if any. Held so an `agent` event
        // can update it in place rather than appending a row per delta.
        private ChatTurn? _streaming;

        // Which stream the turn above belongs to. An agent emits "thinking"
        // and then "assistant", each as its own growing snapshot, so they have
        // to become two turns — appending one to the other would produce a
        // paragraph that says the same thing twice in different voices.
        private string? _streamingKind;

        public OpenClawChatSession(string sessionId, string gatewayKey, string displayName)
        {
            SessionId = sessionId;
            GatewayKey = gatewayKey;
            DisplayName = displayName;
        }

        public string SessionId { get; }

        // The gateway's own key, without the "openclaw:" prefix the app adds to
        // keep session ids in one namespace.
        public string GatewayKey { get; }

        // Settable, because the name can improve after the session was created:
        // agents.list arrives moments after the connection does, so a panel
        // opened in that window would otherwise keep the raw id ("main") in its
        // header for as long as the app runs.
        public string DisplayName { get; set; }

        public RemoteChatState State { get; private set; } = RemoteChatState.Connected;

        // Where this conversation lives, when it lives somewhere — a Discord DM,
        // a channel. Null for a session with no channel behind it, which is the
        // signal not to mirror anything anywhere.
        public OpenClawSessions.Delivery? Delivery { get; set; }

        public IReadOnlyList<ChatTurn> History => _history;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;

        public async Task SendAsync(string text)
        {
            if (!ClaudeBuddySettings.OpenClawReplyEnabled)
            {
                // A System turn rather than an exception: the person has just
                // typed a sentence, and losing it behind a dialog would be a
                // poor answer to "why didn't that send".
                Note("Replying is off. Turn on \"Allow replying to agents\" in Settings — "
                   + "it asks the gateway for permission to write, which you approve there.");
                return;
            }

            // The user's own turn is added here rather than by the panel, so one
            // thing owns the transcript and a send that fails leaves a message
            // on screen with an explanation under it rather than a ghost.
            var mine = new ChatTurn { Role = ChatRole.User, Text = text, IsComplete = true };
            Add(mine);

            var failure = await SendOrFailureAsync(text);
            if (failure is not null) Note("Couldn't send: " + failure);
        }

        // The request, and the catch around it, moved behind a method that
        // returns the failure instead of throwing it.
        //
        // Excluded from coverage because it is the gateway call, but the shape is
        // what matters: an await that always faults never resumes, so the line
        // that awaited it is reported unhit even though the catch beside it runs.
        // Returning the message rather than throwing it means the caller's await
        // completes, and the decision that reads it — say so in the transcript —
        // is measured where it belongs.
        [ExcludeFromCodeCoverage]
        private async Task<string?> SendOrFailureAsync(string text)
        {
            try
            {
                await OpenClawSessions.SendAsync(this, text, CancellationToken.None);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private void Note(string text) => Add(new ChatTurn
        {
            Role = ChatRole.System,
            IsComplete = true,
            Text = text
        });

        public void Cancel()
        {
            // Nothing to cancel while this is read-only. Stopping someone else's
            // run — one started from Discord or a cron schedule — is not
            // something a viewer should be able to do by accident anyway.
        }

        // Fed from OpenClawSessions' event stream. Everything here is already on
        // the UI thread, which is the contract IRemoteChatSession states and the
        // panel relies on.
        public void OnAgentEvent(string name, JsonElement payload)
        {
            switch (name)
            {
                case "agent":
                    OnAgentText(payload);
                    break;

                case "session.tool":
                    OnTool(payload);
                    break;

                case "cron" when Str(payload, "action") == "finished":
                case "task" when Str(payload, "action") == "upserted":
                    Complete();
                    break;
            }
        }

        private void OnAgentText(JsonElement payload)
        {
            if (!payload.TryGetProperty("data", out var data)) return;

            // data.text is a full snapshot of the turn so far, alongside a
            // data.delta. Using the snapshot means a dropped or coalesced event
            // costs nothing — which is exactly the property the panel is written
            // against, so it is worth taking even though the delta is right
            // there and looks cheaper.
            var text = Str(data, "text");
            if (string.IsNullOrEmpty(text)) return;

            // Thinking is shown — watching an agent think is most of the value
            // of an orb that pulses — but kept as its own turn rather than
            // mixed into the reply it will eventually give.
            var kind = Str(payload, "stream") ?? "assistant";

            if (_streaming is null || _streaming.IsComplete || _streamingKind != kind)
            {
                _streaming = new ChatTurn
                {
                    Role = kind == "thinking" ? ChatRole.System : ChatRole.Assistant,
                    Text = text
                };
                _streamingKind = kind;
                Add(_streaming);
                return;
            }

            _streaming.Text = text;
            TurnUpdated?.Invoke(_streaming);
        }

        private void OnTool(JsonElement payload)
        {
            if (!payload.TryGetProperty("data", out var data)) return;
            if (Str(data, "phase") != "start") return;

            var tool = Str(data, "name");
            if (string.IsNullOrEmpty(tool)) return;

            // One line per tool call, in the transcript rather than in a status
            // area: what an agent reached for is part of what it said.
            Add(new ChatTurn
            {
                Role = ChatRole.System,
                IsComplete = true,
                Text = "· " + tool
            });
        }

        private void Complete()
        {
            if (_streaming is null) return;

            _streaming.IsComplete = true;
            TurnUpdated?.Invoke(_streaming);
            _streaming = null;
            _streamingKind = null;
        }

        private void Add(ChatTurn turn)
        {
            _history.Add(turn);

            // Generous, because this is now the only thing that discards
            // anything: at 60 a busy conversation dropped its own beginning
            // while you were reading it, which is what "stuff disappears"
            // looked like. Scrolling back can load more than this, so the cap
            // is high enough that reaching it means a genuinely enormous
            // scrollback rather than an ordinary afternoon.
            const int Keep = 500;
            if (_history.Count > Keep) _history.RemoveRange(0, _history.Count - Keep);

            TurnAdded?.Invoke(turn);
        }

        // The backlog, once the gateway has told us what it is. Replaces
        // whatever is there rather than merging: this arrives moments after the
        // panel opens, and the alternative is reconciling two orderings of the
        // same conversation for the sake of a turn or two that might have
        // landed in between.
        //
        // Historical turns are marked complete so a live reply that arrives
        // next starts its own row instead of appending to the last thing
        // somebody said an hour ago.
        // How many of the gateway's own messages this transcript has consumed,
        // which is the offset the next page back starts at. Not the same as the
        // number of turns: one message can be text plus three pictures, and
        // some are dropped as scaffolding.
        public int LoadedMessages { get; set; }

        // False once the gateway answers a page with nothing left to give.
        public bool HasMore { get; set; } = true;

        // The fetch itself lives on OpenClawSessions, which owns the connection;
        // this is only the seam the panel reaches it through, so the panel does
        // not have to know that a page comes from a gateway rather than a file.
        public Task<bool> LoadOlderAsync(CancellationToken ct) =>
            OpenClawSessions.LoadOlderAsync(this, ct);

        public string ComposerHint => ClaudeBuddySettings.OpenClawReplyEnabled
            ? "Message…"
            : "Replying is off";

        // Older turns, from scrolling back. Prepended rather than replacing, and
        // raising its own event, because the panel has to put the scroll
        // position back afterwards — content appearing above where you are
        // reading would otherwise throw you down the page.
        public void PrependHistory(IReadOnlyList<HistoryTurn> turns)
        {
            if (turns.Count == 0) return;

            var older = turns.Select(t => new ChatTurn
            {
                Role = t.Role,
                Text = t.Text,
                ImageUrl = t.ImageUrl,
                ImageAlt = t.ImageAlt,
                At = t.At,
                Speaker = t.Speaker,
                SpeakerColor = t.SpeakerColor,
                IsComplete = true
            }).ToList();

            _history.InsertRange(0, older);
            HistoryPrepended?.Invoke(older.Count);
        }

        public event Action<int>? HistoryPrepended;

        public void SetHistory(IReadOnlyList<HistoryTurn> turns)
        {
            if (turns.Count == 0) return;

            _history.Clear();
            _streaming = null;
            _streamingKind = null;
            HasMore = true;

            foreach (var turn in turns)
            {
                _history.Add(new ChatTurn
                {
                    Role = turn.Role,
                    Text = turn.Text,
                    ImageUrl = turn.ImageUrl,
                    ImageAlt = turn.ImageAlt,
                    At = turn.At,
                    Speaker = turn.Speaker,
                    SpeakerColor = turn.SpeakerColor,
                    IsComplete = true
                });
            }

            HistoryReplaced?.Invoke();
        }

        // Raised when the whole transcript changes underneath the panel, which
        // TurnAdded can't express. Deliberately not on IRemoteChatSession: the
        // panel treats it as an optional extra, so an implementation that only
        // ever appends — the fake this was developed against — needs nothing.
        public event Action? HistoryReplaced;

        public void SetState(RemoteChatState state)
        {
            if (State == state) return;

            State = state;
            StateChanged?.Invoke(state);
        }

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
    }
}
