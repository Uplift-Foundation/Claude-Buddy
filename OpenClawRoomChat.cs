using System.Diagnostics.CodeAnalysis;
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
        // There is more as long as any member is still holding some back — that
        // member is what stops the window opening further, so it is exactly what
        // reaching the top should fetch.
        //
        // This was declined at first, on the grounds that pulling a page from
        // six transcripts would interleave six unrelated stretches of time into
        // the middle of what you were reading. That was true while the room
        // showed everything it had, and stopped being true once the view is cut
        // to where every member reaches: paging then has one meaning, which is
        // to move that line back, and the interleaving happens above where you
        // are reading rather than through it.
        public bool HasMore => _members.Any(m => m.Chat.HasMore);

        public async Task<bool> LoadOlderAsync(CancellationToken ct)
        {
            var before = TrustworthyFrom();

            await PageUntilTheWindowMovesAsync(before, ct);

            return TrustworthyFrom() != before;
        }

        // A few rounds per scroll, not one. Paging the constraining member once
        // often just makes a different member the constraint, and a scroll that
        // fetched a page and moved the window by nothing would read as the top of
        // the conversation.
        //
        // Excluded from coverage: the loop only goes round a second time when a
        // page actually came back, which needs a gateway answering a chat.history
        // request. With none, the first round breaks — which is the behaviour
        // LoadOlderAsync's tests assert, through the answer it returns rather
        // than through this.
        [ExcludeFromCodeCoverage]
        private async Task PageUntilTheWindowMovesAsync(
            DateTimeOffset? before, CancellationToken ct)
        {
            for (var round = 0; round < 3; round++)
            {
                if (!await PageBindingMemberAsync(ct)) break;
                if (TrustworthyFrom() != before) break;
            }
        }

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
            chat.HistoryReplaced += ScheduleRebuild;
            chat.HistoryPrepended += OnMemberPrepended;
        }

        private void Unsubscribe(OpenClawChatSession chat)
        {
            chat.TurnAdded -= OnMemberChanged;
            chat.TurnUpdated -= OnMemberChanged;
            chat.HistoryReplaced -= ScheduleRebuild;
            chat.HistoryPrepended -= OnMemberPrepended;
        }

        private void OnMemberChanged(ChatTurn _) => ScheduleRebuild();

        private bool _rebuildQueued;

        // Coalesced to one rebuild per pass of the dispatcher.
        //
        // A streaming reply raises TurnUpdated per snapshot, several times a
        // second, and each one would otherwise re-merge and re-sort every
        // transcript in the room — for a single row whose text changed. The
        // panel cannot draw faster than this anyway.
        private void ScheduleRebuild()
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;

            Dispatcher.UIThread.Post(() =>
            {
                _rebuildQueued = false;
                Rebuild();
            }, DispatcherPriority.Background);
        }

        // Paging a member back is the one thing that widens the window this can
        // be trusted over, so it has to redraw — and it was missing, which meant
        // Deepen's work would not have shown until something else happened.
        private void OnMemberPrepended(int _) => ScheduleRebuild();

        // The whole view is rebuilt, rather than the one turn that changed being
        // inserted in the right place.
        //
        // A merge has no append: a message from an agent whose backlog arrives
        // late belongs in the middle, and working out where costs more than
        // redoing a few hundred rows of objects that are already in memory. The
        // panel is told the transcript was replaced, which it already handles
        // for a backlog landing after it opened.
        // internal: the merge is where three shipped bugs were fixed, and it
        // needs no dispatcher of its own — only ScheduleRebuild above does, and
        // that exists to coalesce, not to decide anything.
        internal void Rebuild()
        {
            // Every agent's own words, and the text of them.
            //
            // The text set is the whole trick for telling a person's message
            // apart from an agent's. Both arrive as user-role turns in the
            // transcripts of the agents that *received* them, and nothing in the
            // payload says which is which — an earlier version assumed user-role
            // meant human, and every agent's message was drawn twice: once
            // attributed in its own colour, and once as a blue bubble from you.
            //
            // What separates them is that an agent's message is *also* an
            // assistant turn somewhere in this room, and a person's is not. So
            // the assistant turns are collected first and everything matching
            // one of them is dropped on the second pass.
            var merged = new List<ChatTurn>();

            // A list rather than a set, because matching is no longer equality:
            // a relayed copy can be cut short. See SaidByAnAgent.
            var agentTexts = new List<string>();

            foreach (var member in _members)
            {
                foreach (var turn in member.Chat.History)
                {
                    if (turn.Role != ChatRole.Assistant) continue;

                    agentTexts.Add(Normalise(turn.Text));

                    merged.Add(new ChatTurn
                    {
                        Role = ChatRole.Assistant,
                        Text = turn.Text,
                        ImageUrl = turn.ImageUrl,
                        ImageAlt = turn.ImageAlt,
                        At = turn.At,
                        IsComplete = turn.IsComplete,

                        // Free, and the whole reason to merge: a message found
                        // in Zara's transcript, in the assistant role, is Zara.
                        Speaker = member.Agent,
                        SpeakerColor = member.Colour
                    });
                }
            }

            // Anything a person said. It appears in every transcript that
            // received it and as an assistant turn in none, which is what is
            // left once the agents' own words are accounted for.
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var member in _members)
            {
                foreach (var turn in member.Chat.History)
                {
                    if (turn.Role != ChatRole.User) continue;

                    var text = Normalise(turn.Text);
                    if (text.Length == 0) continue;

                    // Said by an agent, and already in the list attributed to
                    // whichever one. This is the same message, seen from the
                    // other side.
                    if (SaidByAnAgent(agentTexts, text)) continue;

                    // The same message reaches every agent in the room, so it is
                    // taken once. Keyed on the text alone rather than on the
                    // time: the same message is timestamped per delivery, and
                    // two agents can record it either side of a minute boundary.
                    if (!seen.Add(text)) continue;

                    // Whether this is yours is genuinely not known, and drawing
                    // it in your own blue was the app asserting that it is.
                    //
                    // Three things arrive here and look identical: something you
                    // said, something another person in the channel said, and
                    // something an agent said whose own transcript is not
                    // available to match against — an agent whose session the
                    // gateway no longer lists, which is most likely for exactly
                    // the old messages where this was going wrong. Only the
                    // first belongs in your blue, and nothing distinguishes it.
                    //
                    // So it is drawn as the room's own voice: left, neutral, no
                    // name. "Somebody said this" is true of all three, where
                    // "you said this" is true of one.
                    merged.Add(new ChatTurn
                    {
                        Role = ChatRole.Assistant,
                        Text = turn.Text,
                        ImageUrl = turn.ImageUrl,
                        ImageAlt = turn.ImageAlt,
                        At = turn.At,
                        IsComplete = true
                    });
                }
            }

            merged.Sort((a, b) => a.At.CompareTo(b.At));

            // Cut back to where every member's transcript actually reaches.
            //
            // Each member loads a page at a time, and they do not cover the same
            // stretch of time — one agent's page can reach back an hour further
            // than another's. In the part only some of them cover, the missing
            // members' messages exist solely as echoes in the others, with no
            // assistant turn to attribute them against, so they came out as blue
            // bubbles from you. Historical messages were wrong far more often
            // than recent ones for exactly this reason.
            //
            // Showing less is the honest answer: before this point the room does
            // not know who was talking, and saying so by omission beats saying
            // it was you. Deepen widens it.
            var from = TrustworthyFrom();
            if (from is { } start) merged.RemoveAll(t => t.At < start);

            // Deliberately uncapped. A cap here trimmed the *front*, which is
            // the end paging adds to — so scrolling up fetched older messages
            // and then dropped them again, and the window could never open past
            // the cap however far you scrolled. What bounds this is how far the
            // members have been paged back, which is the reader's own doing.

            // Whether this is the same conversation with older messages on the
            // front, or a different one.
            //
            // It matters because the panel treats them differently: a replaced
            // transcript scrolls to the bottom, which is right when a room first
            // opens and exactly wrong when you have just scrolled to the top and
            // asked for more. Prepending keeps you where you were reading.
            var prepended = PrependedCount(merged);

            _history.Clear();
            _history.AddRange(merged);

            if (prepended > 0) HistoryPrepended?.Invoke(prepended);
            else HistoryReplaced?.Invoke();
        }

        // How many turns were added to the front, if that is all that happened.
        // Zero when anything else changed, which the caller reads as "replaced".
        //
        // Compared by value rather than by reference: an assistant turn is
        // copied on every merge to carry its speaker, so the objects differ even
        // when the conversation has not.
        private int PrependedCount(List<ChatTurn> merged)
        {
            if (_history.Count == 0 || merged.Count <= _history.Count) return 0;

            var offset = merged.Count - _history.Count;

            for (var i = 0; i < _history.Count; i++)
            {
                if (!Same(_history[i], merged[i + offset])) return 0;
            }

            return offset;
        }

        private static bool Same(ChatTurn a, ChatTurn b) =>
            a.Role == b.Role
            && a.At == b.At
            && a.Speaker == b.Speaker
            && a.Text == b.Text;

        // The earliest moment every member's loaded history covers, or null if
        // no member is holding anything back.
        //
        // A member that has reached the beginning of its transcript constrains
        // nothing — it has everything there is, and its silence before some
        // point is real rather than unloaded. Only a member with more to fetch
        // draws the line, at the oldest message it has so far.
        private DateTimeOffset? TrustworthyFrom()
        {
            DateTimeOffset? start = null;

            foreach (var member in _members)
            {
                if (!member.Chat.HasMore) continue;

                var earliest = Earliest(member.Chat);
                if (earliest is null) continue;

                if (start is null || earliest > start) start = earliest;
            }

            return start;
        }

        private static DateTimeOffset? Earliest(OpenClawChatSession chat)
        {
            DateTimeOffset? earliest = null;

            foreach (var turn in chat.History)
                if (earliest is null || turn.At < earliest) earliest = turn.At;

            return earliest;
        }

        private bool _deepening;

        // Pages members back until they cover a common stretch of time.
        //
        // Without it the window is set by whichever member happens to have the
        // shallowest page, which in a busy room can be a few minutes — correct,
        // but not much of a conversation. Each round pushes back the member that
        // is currently the binding constraint, so the requests go where they buy
        // the most rather than being spread evenly.
        //
        // Bounded, because a room with a member that has thousands of messages
        // behind it would otherwise page the whole way there on open.
        public async Task DeepenAsync()
        {
            if (_deepening) return;
            _deepening = true;

            try
            {
                await PageBackHardAsync();
            }
            finally
            {
                _deepening = false;
            }
        }

        // Excluded from coverage: eight rounds of a chat.history request, and the
        // catch for a gateway that stops answering partway through — which is not
        // worth failing an open conversation over, so the window simply stays
        // where it is. With no gateway the first round returns and neither the
        // loop nor the catch has anything to do, which is what DeepenAsync's
        // tests exercise: that it runs once, releases its flag, and disturbs
        // nothing.
        [ExcludeFromCodeCoverage]
        private async Task PageBackHardAsync()
        {
            try
            {
                for (var round = 0; round < 8; round++)
                {
                    if (!await PageBindingMemberAsync(CancellationToken.None)) return;
                }
            }
            catch
            {
                // See above: a gateway that stops answering leaves the window
                // where it is rather than failing the conversation.
            }
        }

        // Fetches one page for whichever member is currently stopping the
        // window opening further — the one whose oldest loaded message is the
        // most recent. Sending every request to where it buys the most beats
        // spreading them evenly over members that are already deeper than the
        // view can show.
        private async Task<bool> PageBindingMemberAsync(CancellationToken ct)
        {
            var binding = _members
                .Where(m => m.Chat.HasMore && Earliest(m.Chat) is not null)
                .OrderByDescending(m => Earliest(m.Chat))
                .FirstOrDefault();

            if (binding is null) return false;

            return await OpenClawSessions.LoadOlderAsync(binding.Chat, ct);
        }

        // Compared on the words alone. A relayed copy can differ in surrounding
        // whitespace from the one the agent wrote, and matching has to survive
        // that or the duplicate comes back.
        private static string Normalise(string text) =>
            string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        // Shortest run of characters that is allowed to identify an echo on its
        // own. Below this a match means very little — "yes" is the opening of
        // plenty of sentences — and dropping a person's short reply because an
        // agent happened to start a paragraph the same way is a worse failure
        // than showing one duplicate.
        private const int EchoPrefix = 16;

        // Whether this is an agent's message arriving from the other side.
        //
        // Not equality, which is what the first attempt used and what left the
        // duplicates in place: the copy that reaches the other agents is
        // sometimes **cut short**, so Lilibeth's full sentence appeared once
        // attributed and once as a blue bubble ending at its first colon. So a
        // long enough prefix counts, in either direction — the relay truncates,
        // and nothing says the stored original is the longer of the two.
        private static bool SaidByAnAgent(List<string> agentTexts, string text)
        {
            foreach (var said in agentTexts)
            {
                if (said == text) return true;

                if (text.Length >= EchoPrefix && said.StartsWith(text, StringComparison.Ordinal))
                    return true;

                if (said.Length >= EchoPrefix && text.StartsWith(said, StringComparison.Ordinal))
                    return true;
            }

            return false;
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
