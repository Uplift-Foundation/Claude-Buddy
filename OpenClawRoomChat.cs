using Avalonia.Threading;

namespace ClaudeBuddy
{
    // A channel as one conversation, instead of as one conversation per agent
    // standing in it.
    //
    // The gateway has no such object. It keeps a session per (agent, channel),
    // so a room with six agents in it is six transcripts, each of them that
    // agent's own view: their own replies, and everyone else's messages arriving
    // as input. Opening any one of them shows a sixth of the room and calls it
    // the conversation.
    //
    // Merging them is also what makes attribution free, which is the part worth
    // knowing. Inside `agent:zara:discord:channel:X`, every assistant message
    // *is Zara* — there is nothing to parse and nothing to guess. So a speaker
    // and their colour come from which transcript a message was found in, and
    // the machine header OpenClaw glues onto agent-to-agent messages, which an
    // earlier attempt leaned on, is not needed for this at all.
    //
    // Owns nothing: it subscribes to the member sessions that do, and rebuilds
    // its view when any of them changes.
    internal sealed class OpenClawRoomChatSession : IRemoteChatSession, IRemoteChatComposer, IRemoteChatBacklog
    {
        // Bounded like a member transcript, and for the same reason — except
        // this is several of them interleaved, so the same number of turns
        // covers proportionally less time.
        private const int Keep = 400;

        private readonly List<ChatTurn> _history = new();
        private readonly List<Member> _members = new();

        private sealed record Member(OpenClawChatSession Chat, string Agent, string Colour);

        public OpenClawRoomChatSession(string sessionId, string displayName)
        {
            SessionId = sessionId;
            DisplayName = displayName;
        }

        public string SessionId { get; }

        public string DisplayName { get; set; }

        public RemoteChatState State { get; private set; } = RemoteChatState.Connected;

        public IReadOnlyList<ChatTurn> History => _history;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;
        public event Action? HistoryReplaced;

        // IRemoteChatBacklog is implemented for one member of it: the panel
        // subscribes to HistoryReplaced through this interface and nowhere else,
        // so without it a merged transcript is assembled and never drawn.
        //
        // Paging back is genuinely not offered. Each member pages independently,
        // and pulling one page from six transcripts would interleave six
        // unrelated stretches of time into the middle of what you were reading.
        // Saying "no more" is the honest answer until that is worth doing
        // properly.
        public bool HasMore => false;

        public Task<bool> LoadOlderAsync(CancellationToken ct) => Task.FromResult(false);

        public event Action<int>? HistoryPrepended;

        public string ComposerHint => ClaudeBuddySettings.OpenClawReplyEnabled
            ? "Message the channel…"
            : "Replying is off";

        // Called on every scan, because who is in a room changes: an agent that
        // has not spoken lately drops out of the session list, and one that
        // joins has to start being listened to.
        public void SetMembers(IReadOnlyList<(OpenClawChatSession Chat, string Agent, string Colour)> members)
        {
            var wanted = members.Select(m => m.Chat.GatewayKey).ToHashSet(StringComparer.Ordinal);
            var have = _members.Select(m => m.Chat.GatewayKey).ToHashSet(StringComparer.Ordinal);

            if (wanted.SetEquals(have))
            {
                // The same people. Their names and colours can still have
                // improved — agents.list lands after the first connection — so
                // those are refreshed without tearing the subscriptions down.
                for (var i = 0; i < _members.Count; i++)
                {
                    var match = members.FirstOrDefault(m => m.Chat.GatewayKey == _members[i].Chat.GatewayKey);
                    if (match.Chat is not null) _members[i] = new Member(match.Chat, match.Agent, match.Colour);
                }

                return;
            }

            foreach (var member in _members) Unsubscribe(member.Chat);
            _members.Clear();

            foreach (var (chat, agent, colour) in members)
            {
                _members.Add(new Member(chat, agent, colour));
                Subscribe(chat);
            }

            Rebuild();
        }

        private void Subscribe(OpenClawChatSession chat)
        {
            chat.TurnAdded += OnMemberChanged;
            chat.TurnUpdated += OnMemberChanged;
            chat.HistoryReplaced += Rebuild;
        }

        private void Unsubscribe(OpenClawChatSession chat)
        {
            chat.TurnAdded -= OnMemberChanged;
            chat.TurnUpdated -= OnMemberChanged;
            chat.HistoryReplaced -= Rebuild;
        }

        private void OnMemberChanged(ChatTurn _) => Rebuild();

        // The whole view is rebuilt, rather than the one turn that changed being
        // inserted in the right place.
        //
        // A merge has no append: a message from an agent whose backlog arrives
        // late belongs in the middle, and working out where costs more than
        // redoing a few hundred rows of objects that are already in memory. The
        // panel is told the transcript was replaced, which it already handles
        // for a backlog landing after it opened.
        private void Rebuild()
        {
            var merged = new List<ChatTurn>();

            // Everything a person says reaches every agent in the room, so it is
            // in every transcript. Without this the room repeats your own
            // messages once per agent listening.
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var member in _members)
            {
                foreach (var turn in member.Chat.History)
                {
                    if (turn.Role == ChatRole.Assistant)
                    {
                        merged.Add(new ChatTurn
                        {
                            Role = ChatRole.Assistant,
                            Text = turn.Text,
                            ImageUrl = turn.ImageUrl,
                            ImageAlt = turn.ImageAlt,
                            At = turn.At,
                            IsComplete = turn.IsComplete,

                            // Free, and the whole reason to merge: a message
                            // found in Zara's transcript, in the assistant role,
                            // is Zara.
                            Speaker = member.Agent,
                            SpeakerColor = member.Colour
                        });

                        continue;
                    }

                    // Anything else is input to this agent — which for another
                    // agent's message means it appears as an assistant turn in
                    // *its* transcript, where it is attributable. Taking it
                    // there instead is what keeps one message to one bubble.
                    if (turn.Role != ChatRole.User) continue;

                    // A person's own message appears nowhere else, so it is
                    // taken here and de-duplicated. Keyed on the minute rather
                    // than the millisecond: the same message is timestamped per
                    // delivery, so two agents record it slightly apart.
                    var key = turn.At.ToUnixTimeSeconds() / 60 + " " + turn.Text.Trim();
                    if (!seen.Add(key)) continue;

                    merged.Add(turn);
                }
            }

            merged.Sort((a, b) => a.At.CompareTo(b.At));
            if (merged.Count > Keep) merged.RemoveRange(0, merged.Count - Keep);

            _history.Clear();
            _history.AddRange(merged);

            Dispatcher.UIThread.Post(() => HistoryReplaced?.Invoke());
        }

        // Sent through one member, because the gateway has no room to send to —
        // but with delivery on, which posts it to the channel itself, so every
        // agent there receives it the way they receive anything else said in the
        // room. Which member is therefore not a routing decision, only a
        // question of whose transcript carries the send; first by key keeps it
        // stable rather than depending on who happened to speak last.
        public async Task SendAsync(string text)
        {
            if (!ClaudeBuddySettings.OpenClawReplyEnabled)
            {
                Note("Replying is off. Turn on \"Allow replying to agents\" in Settings.");
                return;
            }

            var via = _members.OrderBy(m => m.Chat.GatewayKey, StringComparer.Ordinal).FirstOrDefault();
            if (via is null)
            {
                Note("Nobody is in this channel right now.");
                return;
            }

            await via.Chat.SendAsync(text);
        }

        private void Note(string text)
        {
            var note = new ChatTurn { Role = ChatRole.System, IsComplete = true, Text = text };
            _history.Add(note);
            TurnAdded?.Invoke(note);
        }

        public void Cancel()
        {
            // Nothing to stop here: a room is a view, and an agent's own run is
            // stopped from its own orb.
        }

        public void SetState(RemoteChatState state)
        {
            if (State == state) return;

            State = state;
            StateChanged?.Invoke(state);
        }
    }
}
