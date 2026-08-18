using System.Text.Json;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // The OpenClaw half of what SessionManager displays: one connection to the
    // gateway, kept alive in the background, publishing an immutable snapshot
    // the scan reads for free.
    //
    // Shaped like ClaudeDesktopManager rather than as an interface with two
    // implementations: every other second data source in this app is a static
    // class with a private cache and a gate (BackgroundJobs, AgentTeam,
    // AgentTeamViewer), and an interface whose second implementation is
    // permanently off would be ceremony. Off means Snapshot() returns an empty
    // array — no socket, no task, no key, nothing constructed at all.
    internal static class OpenClawSessions
    {
        private static readonly object Gate = new();

        // Published whole and replaced whole. The scan runs on the UI thread and
        // never locks anything else it reads; handing it a finished list keeps
        // that true.
        private static volatile IReadOnlyList<Session> _snapshot = Array.Empty<Session>();

        private static Task? _supervisor;
        private static CancellationTokenSource? _cts;

        // The live connection, so an opened chat can ask for its own backlog.
        // Null while disconnected, which is the only reason a history load
        // silently doesn't happen — the panel then fills from live events.
        private static OpenClawGateway? _gateway;

        private static string _state = "off";

        // Which sessions are mid-run, by session key. Maintained from the event
        // stream rather than from sessions.list, because the list is wrong about
        // it: `hasActiveRun` never once flipped across a complete observed run,
        // and a run's own key never appears in the list at all. Events are also
        // immediate, where a poll is up to a scan behind.
        private static readonly Dictionary<string, DateTime> Running =
            new(StringComparer.Ordinal);

        // When we last saw *any* activity on a session, from the event stream.
        //
        // Not the same thing as Running, and it exists because sessions.list
        // lies about this. Its lastActivityAt/updatedAt are hours stale for a
        // Discord conversation that is happening right now — measured: a chat
        // in progress reported 6640s since last activity while its agent was
        // mid-reply, where a cron session on the same gateway updated every five
        // minutes. Trusting the list alone made a Discord orb appear for the
        // twenty seconds of a reply and then vanish, which reads as the feature
        // not working rather than as a stale timestamp.
        private static readonly Dictionary<string, DateTime> LastSeen =
            new(StringComparer.Ordinal);

        // One chat session per gateway key, created the first time an orb is
        // clicked and kept afterwards so its transcript survives the panel being
        // dismissed and reopened. Only sessions someone has actually opened are
        // in here — a gateway with 59 sessions does not get 59 transcripts.
        private static readonly Dictionary<string, OpenClawChatSession> Chats =
            new(StringComparer.Ordinal);

        // Agent id -> the name its owner gave it: main is Lilibeth, comfyui is
        // Zara. The ids are what the session keys are built from, but they are
        // an implementation detail of somebody's config — the names are what
        // the agents are called in Discord, in conversation, and in the user's
        // head. An orb showing "M" for four different agents called main is a
        // worse answer than one showing "L", "Z", "A".
        private static readonly Dictionary<string, string> AgentNames =
            new(StringComparer.OrdinalIgnoreCase);

        // The rest of what an agent's owner gave it: an emoji, and a picture.
        // Both come down inside agents.list — the avatar as a base64 data URI,
        // which is generous of it and also about 8 MB across seven agents, so
        // it is asked for once per connection and the decoded result is what
        // gets kept.
        private static readonly Dictionary<string, AgentIdentity> Identities =
            new(StringComparer.OrdinalIgnoreCase);

        internal sealed record AgentIdentity(string Name, string? Emoji, byte[]? Avatar);

        public static AgentIdentity? IdentityOf(string agentId)
        {
            lock (Gate) return Identities.GetValueOrDefault(agentId);
        }

        // The agent's picture for a session, already decoded and scaled. Shared
        // with the orb rather than decoded twice — the frames are immutable and
        // the cache is keyed by agent, so both surfaces draw the same objects.
        public static OpenClawAvatars.Avatar? AvatarForSession(string sessionId)
        {
            var identity = IdentityForSession(sessionId);
            if (identity is null) return null;

            var agent = AgentIdOf(sessionId);
            return agent is null ? null : OpenClawAvatars.For(agent, identity.Avatar);
        }

        public static string? AgentIdOf(string sessionId)
        {
            const string Prefix = "openclaw:";
            var key = sessionId.StartsWith(Prefix, StringComparison.Ordinal)
                ? sessionId[Prefix.Length..]
                : sessionId;

            var parts = key.Split(':');
            return parts.Length >= 2 && parts[0] == "agent" ? parts[1] : null;
        }

        // The agent behind a session, from its key: "agent:<id>:<surface>…".
        public static AgentIdentity? IdentityForSession(string sessionId)
        {
            const string Prefix = "openclaw:";
            var key = sessionId.StartsWith(Prefix, StringComparison.Ordinal)
                ? sessionId[Prefix.Length..]
                : sessionId;

            var parts = key.Split(':');
            return parts.Length >= 2 && parts[0] == "agent" ? IdentityOf(parts[1]) : null;
        }

        // How long a session stays "working" after its last event. A turn emits
        // events continuously while it runs — thinking deltas, tool phases — so
        // silence for this long means it stopped, whether or not a terminal
        // event arrived. Long enough to bridge a slow tool call, short enough
        // that a finished orb doesn't keep pulsing at you.
        private static readonly TimeSpan RunIdle = TimeSpan.FromSeconds(20);

        // How far back a session counts as current at all.
        //
        // This is deliberately *not* the user's "Keep orbs for" setting, which
        // was the first design and is wrong. That setting answers "how long does
        // a session that has gone quiet stay on screen", and it is commonly set
        // to Forever — perfectly sensible for Claude Code, where the list only
        // ever holds sessions that are actually running. A gateway's list is not
        // that: it holds every conversation it has ever had. On the machine this
        // was built against that is 59, of which two had been touched in the last
        // five minutes, so "Forever" meant 59 permanent orbs.
        //
        // So the two questions are separated: this bounds which sessions exist
        // as far as Claude Buddy is concerned, and the lifetime setting still
        // decides how long one of those lingers after it goes quiet.
        // Read per scan rather than cached, so changing it in Settings takes
        // effect on the next poll rather than at the next launch.
        private static TimeSpan? ActiveWithin
        {
            get
            {
                var minutes = ClaudeBuddySettings.OpenClawActiveWithinMinutes;
                return minutes == ClaudeBuddySettings.OpenClawActiveWithinAll
                    ? null
                    : TimeSpan.FromMinutes(minutes);
            }
        }

        internal sealed record Session(
            string Key,
            string Title,
            string Channel,
            string State,
            DateTime LastActivity,
            Delivery? Delivery,
            SessionKind Kind);

        // Where a reply in this session is supposed to end up. The gateway
        // resolves this itself when asked to deliver an agent's answer, but a
        // message *you* typed has to be posted to the channel explicitly, so
        // the client needs to know the address too.
        internal sealed record Delivery(string Channel, string To, string? AccountId);

        // An agent's colour, assigned across the whole set so no two are
        // confusable (see AgentPalette.Assign).
        //
        // Kept here rather than recomputed by each caller because the orb's ring
        // and a chat bubble from the same agent have to be the same colour or
        // the attribution means nothing — and Assign's answer depends on which
        // other agents exist, so two callers computing it from different sets
        // would quietly disagree.
        private static Dictionary<string, string> _agentColours = new(StringComparer.Ordinal);

        public static string ColourForAgent(string? agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return "";
            lock (Gate) return _agentColours.GetValueOrDefault(agentId, "");
        }

        private static void AssignColours(IEnumerable<string> agentIds)
        {
            var colours = AgentPalette.Assign(agentIds);
            lock (Gate) _agentColours = colours;
        }

        // What the settings window shows on its status row.
        public static string StatusText
        {
            get { lock (Gate) return _state; }
        }

        // True when the last attempt failed *only* because the certificate no
        // longer matches the pinned one. The settings window offers to accept
        // the new one when this is set, and offers nothing of the sort
        // otherwise — see the button's own comment for why it is not automatic.
        public static bool CertificateRejected
        {
            get { lock (Gate) return _certificateRejected; }
        }

        private static bool _certificateRejected;

        // The conversation in a channel, as one thing. memberKeys are the
        // gateway keys of the sessions standing in it — see
        // OpenClawSessionKind.RoomOf for what decides that.
        //
        // The member chats are created here as a side effect, which is what
        // starts their backlogs loading. That is the same thing opening any one
        // of their orbs would do, so a room costs the same requests as reading
        // it agent by agent, made at once instead of one at a time.
        private static readonly Dictionary<string, OpenClawRoomChatSession> Rooms =
            new(StringComparer.Ordinal);

        // Everyone in a channel, whether or not their orb is on screen. Keyed
        // by OpenClawSessionKind.RoomOf.
        private static Dictionary<string, List<string>> _roomMembers = new(StringComparer.Ordinal);

        public static IReadOnlyList<string> MembersOfRoom(string roomKey)
        {
            lock (Gate)
                return _roomMembers.TryGetValue(roomKey, out var members)
                    ? members.ToList()
                    : Array.Empty<string>();
        }

        public static IRemoteChatSession? RoomChatFor(
            string sessionId, string displayName, IReadOnlyList<string> memberKeys)
        {
            if (!ClaudeBuddySettings.OpenClawEnabled) return null;
            if (memberKeys.Count == 0) return null;

            OpenClawRoomChatSession room;
            lock (Gate)
            {
                if (!Rooms.TryGetValue(sessionId, out var existing))
                {
                    existing = new OpenClawRoomChatSession(sessionId, displayName);
                    Rooms[sessionId] = existing;
                }

                existing.DisplayName = displayName;
                room = existing;
            }

            var members = new List<(OpenClawChatSession Chat, string Agent, string Colour)>();
            foreach (var key in memberKeys)
            {
                if (ChatFor("openclaw:" + key, displayName) is not OpenClawChatSession chat) continue;

                var agentId = AgentIdOf(key) ?? key;
                members.Add((chat, AgentNameOf(agentId), ColourForAgent(agentId)));
            }

            room.SetMembers(members);

            // Widening the window the merge can be trusted over, in the
            // background: the members' first pages rarely cover the same
            // stretch, and the room can only show where they overlap.
            _ = room.DeepenAsync();

            return room;
        }

        // The panel's view of one session. sessionId is the app's namespaced
        // id; the gateway knows it without the prefix.
        public static IRemoteChatSession? ChatFor(string sessionId, string displayName)
        {
            var delivery = _snapshot.FirstOrDefault(s => "openclaw:" + s.Key == sessionId)?.Delivery;

            if (!ClaudeBuddySettings.OpenClawEnabled) return null;

            const string Prefix = "openclaw:";
            if (!sessionId.StartsWith(Prefix, StringComparison.Ordinal)) return null;

            var key = sessionId[Prefix.Length..];

            lock (Gate)
            {
                if (!Chats.TryGetValue(key, out var chat))
                {
                    chat = new OpenClawChatSession(sessionId, key, displayName);
                    Chats[key] = chat;
                }

                chat.Delivery = delivery;

                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    // Names arrive from agents.list a moment after the
                    // connection, so a session first opened in that window was
                    // created holding the raw id.
                    chat.DisplayName = displayName;
                }

                // Refreshed on every open, not just on the first: the gateway
                // records the same turns we see live, plus everything that
                // happened through Discord or the TUI while this panel was
                // closed. Re-reading is both simpler than merging and more
                // truthful than whatever we happened to catch.
                //
                // Fire and forget, so the panel opens now and fills in a moment
                // later rather than making the click wait on a round trip.
                _ = LoadHistoryAsync(chat, _cts?.Token ?? CancellationToken.None);

                return chat;
            }
        }

        public static IReadOnlyList<Session> Snapshot() =>
            ClaudeBuddySettings.OpenClawEnabled ? _snapshot : Array.Empty<Session>();

        // Accept whatever certificate the gateway is now serving.
        //
        // Clearing the pin *and* the rejection together, rather than letting the
        // next successful connection clear the flag, because the flag means "the
        // pin is refusing this gateway" and after this call there is no pin to
        // refuse with. Waiting for the connection left the settings window still
        // offering to trust a certificate that had already been trusted — the
        // reconnect is asynchronous and the window redraws long before it
        // finishes, so the button sat there until something else redrew it.
        //
        // The status line moves too, for the same reason: leaving the old
        // sentence up under a button that has just gone would read as the click
        // having done nothing.
        public static void TrustNewCertificate()
        {
            ClaudeBuddySettings.OpenClawFingerprint = "";

            lock (Gate)
            {
                _certificateRejected = false;
                _state = "connecting…";
            }

            Restart();
        }

        // Called on launch and whenever the settings change. Idempotent: a
        // second call while running is a restart, which is what changing the
        // host or the token means.
        public static void Restart()
        {
            lock (Gate)
            {
                _cts?.Cancel();
                _cts = null;
                _supervisor = null;
                _snapshot = Array.Empty<Session>();
                Running.Clear();

                // LastSeen deliberately survives a reconnect: a session that was
                // busy ten seconds before the socket dropped is still a session
                // worth showing when it comes back.

                // Transcripts are deliberately kept across a reconnect: the
                // conversation didn't stop happening because the socket did.
                foreach (var chat in Chats.Values) chat.SetState(RemoteChatState.Connecting);

                if (!ClaudeBuddySettings.OpenClawEnabled)
                {
                    _state = "off";
                    return;
                }

                var host = ClaudeBuddySettings.OpenClawHost;
                if (string.IsNullOrWhiteSpace(host))
                {
                    _state = "no gateway address set";
                    return;
                }

                _state = "connecting…";
                _cts = new CancellationTokenSource();
                _supervisor = Task.Run(() => RunAsync(host, ClaudeBuddySettings.OpenClawPort, _cts.Token));
            }
        }

        private static async Task RunAsync(string host, int port, CancellationToken ct)
        {
            var backoff = TimeSpan.FromSeconds(2);

            while (!ct.IsCancellationRequested)
            {
                OpenClawGateway? gateway = null;

                try
                {
                    var token = OpenClawIdentity.GatewayTokenFor(host) ?? "";
                    gateway = new OpenClawGateway(host, port, token);
                    gateway.EventReceived += OnEvent;

                    var pinned = ClaudeBuddySettings.OpenClawFingerprint;
                    var result = await gateway.ConnectAsync(
                        string.IsNullOrEmpty(pinned) ? null : pinned, ct);

                    if (result.Outcome != OpenClawGateway.Outcome.Connected)
                    {
                        Report(Describe(result));

                        // Recorded as a flag as well as a sentence, because the
                        // settings window has to *offer something* for this one
                        // rather than only describe it — a changed certificate
                        // is otherwise a permanent dead end with no way through
                        // but editing settings.json.
                        lock (Gate)
                        {
                            _certificateRejected =
                                result.Outcome == OpenClawGateway.Outcome.CertificateMismatch;
                        }

                        // Terminal states get no retry. A gateway that refuses
                        // our credentials will refuse them again in two seconds,
                        // and again after that — the only thing a retry loop
                        // achieves is a connection attempt per second against a
                        // machine the user owns, forever.
                        if (result.Outcome is OpenClawGateway.Outcome.AuthRejected
                            or OpenClawGateway.Outcome.CertificateMismatch)
                        {
                            return;
                        }

                        // Falling through, not continuing.
                        //
                        // `continue` jumps to the loop condition, which is past
                        // the finally *and* past the backoff delay at the bottom
                        // — so a gateway waiting to be approved was re-attempted
                        // as fast as a TLS handshake can complete, forever. The
                        // comment here used to claim this reached "the retry
                        // below". It did not.
                        //
                        // Not throwing either: that put raw exception text
                        // through Report and wiped out the instructions just
                        // written, which is what left it saying "connecting…"
                        // instead of what to do.
                    }
                    else
                    {

                    // Remember what we agreed to trust, the first time only. A
                    // later mismatch is then a refusal rather than a silent
                    // re-pinning, which is the entire value of pinning.
                    if (string.IsNullOrEmpty(pinned)
                        && !string.IsNullOrEmpty(gateway.ObservedFingerprint))
                    {
                        var seen = gateway.ObservedFingerprint;
                        Dispatcher.UIThread.Post(() => ClaudeBuddySettings.OpenClawFingerprint = seen);
                    }

                    backoff = TimeSpan.FromSeconds(2);   // reset on a real connect, never before

                    // Whatever the certificate was, it is agreed now — so the
                    // offer to accept a new one goes away with the problem
                    // rather than lingering as a button that would clear a pin
                    // nothing is complaining about.
                    lock (Gate)
                    {
                        _gateway = gateway;
                        _certificateRejected = false;
                    }

                    // A panel opened while disconnected has an empty transcript
                    // and no way to know it should try again, so reconnecting
                    // refills whatever is already on screen.
                    foreach (var chat in OpenChats()) _ = LoadHistoryAsync(chat, ct);

                    await LoadAgentNamesAsync(gateway, ct);
                    await SubscribeAsync(gateway, ct);
                    await PollAsync(gateway, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Ours: the feature was switched off or the app is closing.
                    break;
                }
                catch (OperationCanceledException)
                {
                    // Not ours — a request timed out. RequestAsync cancels its
                    // own task after twenty seconds, and TaskCanceledException
                    // derives from this, so an unguarded catch here treated a
                    // dead socket as "we were asked to stop" and left the
                    // supervisor broken until the app was restarted. That is
                    // exactly the case this loop exists for: a sleeping gateway
                    // is the most likely reason a request never comes back.
                    Report("The gateway stopped responding. Reconnecting…");
                }
                catch (Exception ex)
                {
                    Report(ex.Message);
                }
                finally
                {
                    // Only if it is still ours. Restart() cancels this loop and
                    // starts another without waiting for it, so a slow unwind
                    // here could otherwise null out the *new* connection's
                    // gateway — leaving a live socket whose orbs keep updating
                    // while every send and every history load fails with "not
                    // connected", until the next reconnect happened to fix it.
                    lock (Gate)
                    {
                        if (ReferenceEquals(_gateway, gateway)) _gateway = null;
                    }

                    gateway?.Dispose();
                }

                if (ct.IsCancellationRequested) break;

                // Orbs go the moment the connection does: an orb for a session
                // we can no longer see the state of is a lie that pulses.
                _snapshot = Array.Empty<Session>();

                try { await Task.Delay(backoff, ct); } catch { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
            }
        }

        private static string Describe(OpenClawGateway.ConnectResult result) => result.Outcome switch
        {
            OpenClawGateway.Outcome.PairingPending =>
                "waiting to be approved on the gateway — run `openclaw devices approve --latest`",
            OpenClawGateway.Outcome.AuthRejected =>
                "the gateway refused these credentials: " + result.Detail,
            OpenClawGateway.Outcome.CertificateMismatch =>
                "the gateway is presenting a different certificate than the one this install trusts",
            OpenClawGateway.Outcome.Unreachable =>
                "can't reach the gateway: " + result.Detail,
            _ => result.Detail ?? "not connected"
        };

        private static async Task LoadAgentNamesAsync(OpenClawGateway gateway, CancellationToken ct)
        {
            try
            {
                var res = await gateway.RequestAsync("agents.list", new Dictionary<string, object>(), ct);
                if (!res.TryGetProperty("agents", out var agents)
                    || agents.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                // Built outside the lock. Base64-decoding seven avatars is
                // about 8 MB of work, and the UI thread takes this same lock on
                // every scan to read agent names — holding it through that would
                // stall the orbs for as long as the decode took.
                var parsed = new List<(string Id, AgentIdentity Identity)>();

                foreach (var agent in agents.EnumerateArray())
                {
                    var id = Str(agent, "id");
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    var identity = agent.TryGetProperty("identity", out var block)
                        && block.ValueKind == JsonValueKind.Object
                            ? block
                            : default;

                    var name = Str(agent, "displayName");
                    if (string.IsNullOrWhiteSpace(name)) name = Str(agent, "name");
                    if (string.IsNullOrWhiteSpace(name) && identity.ValueKind == JsonValueKind.Object)
                    {
                        name = Str(identity, "name");
                    }

                    parsed.Add((id!, new AgentIdentity(
                        name?.Trim() ?? id!,
                        identity.ValueKind == JsonValueKind.Object ? Str(identity, "emoji") : null,
                        identity.ValueKind == JsonValueKind.Object
                            ? DecodeDataUri(Str(identity, "avatarUrl"))
                            : null)));
                }

                lock (Gate)
                {
                    AgentNames.Clear();
                    Identities.Clear();

                    foreach (var (id, identity) in parsed)
                    {
                        AgentNames[id] = identity.Name;
                        Identities[id] = identity;
                    }
                }

                // Decoded here, on this background task, rather than the first
                // time an orb asks for one. OpenClawAvatars.For runs SkiaSharp
                // over every frame — 24 of them for the animated avatar here —
                // and the orb asks for it from inside the scan, which is the UI
                // thread. Warming it costs nothing extra and moves that work off
                // the thread that draws.
                foreach (var (id, identity) in parsed)
                {
                    if (identity.Avatar is not null) OpenClawAvatars.For(id, identity.Avatar);
                }
            }
            catch
            {
                // Names are a courtesy; without them the ids still identify a
                // session perfectly well.
            }
        }

        // "data:image/png;base64,iVBOR…" -> the bytes. Anything else, including a
        // real URL, is declined rather than fetched: this app has one connection
        // to one machine the user pointed it at, and quietly reaching out to
        // some other host because a field said so is not a thing it should do.
        private static byte[]? DecodeDataUri(string? uri)
        {
            if (string.IsNullOrEmpty(uri)) return null;
            if (!uri.StartsWith("data:", StringComparison.Ordinal)) return null;

            var comma = uri.IndexOf(',');
            if (comma < 0) return null;

            var header = uri[..comma];
            if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;

            try { return Convert.FromBase64String(uri[(comma + 1)..]); }
            catch { return null; }
        }

        private static async Task SubscribeAsync(OpenClawGateway gateway, CancellationToken ct)
        {
            try
            {
                await gateway.RequestAsync("sessions.subscribe", new Dictionary<string, object>(), ct);
            }
            catch (Exception ex)
            {
                // Not fatal: the poll below still produces orbs, they just take
                // a scan longer to notice a new session.
                Report("connected, but couldn't subscribe: " + ex.Message);
            }
        }

        private static async Task PollAsync(OpenClawGateway gateway, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var res = await gateway.RequestAsync("sessions.list", new Dictionary<string, object>(), ct);
                var (sessions, total) = Parse(res);

                _snapshot = sessions;
                Report(total == sessions.Count
                    ? $"Connected — {sessions.Count} session{(sessions.Count == 1 ? "" : "s")}."
                    : $"Connected — showing {sessions.Count} of {total}. The rest have been quiet "
                      + "for longer than the window above.");

                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        private static (IReadOnlyList<Session> Sessions, int Total) Parse(JsonElement payload)
        {
            var list = payload;
            if (payload.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "sessions", "items", "rows", "list" })
                {
                    if (payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
                    {
                        list = v;
                        break;
                    }
                }
            }

            if (list.ValueKind != JsonValueKind.Array) return (Array.Empty<Session>(), 0);

            var now = DateTime.UtcNow;
            var result = new List<Session>();

            var roomMembers = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            // Every agent the gateway knows of, filtered or not, so that a
            // colour is reserved for one whose orb isn't drawn — their messages
            // still appear in a room, and an uncoloured bubble in a coloured
            // conversation reads as a failure rather than as an absence.
            var everyAgent = new List<string>();

            foreach (var s in list.EnumerateArray())
            {
                var key = Str(s, "key") ?? Str(s, "sessionKey");
                if (string.IsNullOrEmpty(key)) continue;

                var origin = s.TryGetProperty("origin", out var o) && o.ValueKind == JsonValueKind.Object
                    ? o
                    : default;

                var channel = origin.ValueKind == JsonValueKind.Object
                    ? Str(origin, "provider") ?? Str(s, "lastChannel") ?? ""
                    : Str(s, "lastChannel") ?? "";

                var state = StateFor(key);
                var activity = Activity(s, key);

                // Room membership is recorded *before* the recency filter, and
                // deliberately ignores it.
                //
                // Those are two different questions. "Which orbs are worth
                // showing" is about what you are working with now; "who is in
                // this channel" is about the conversation, and an agent that
                // spoke an hour ago is still one of the people in the room. With
                // this after the filter, Amber's session was dropped, her
                // transcript never loaded, and the "Nodes loaded" she posted
                // survived only as input to the others — anonymous, unmatchable,
                // and drawn as though you had said it.
                var roomKey = OpenClawSessionKind.RoomOf(key);
                if (roomKey is not null)
                {
                    if (!roomMembers.TryGetValue(roomKey, out var members))
                        roomMembers[roomKey] = members = new List<string>();

                    members.Add(key);
                }

                everyAgent.Add(AgentIdOf(key) ?? key);

                // A session mid-run is current whatever its timestamps say —
                // it is the one thing an orb is most worth showing.
                var within = ActiveWithin;
                if (state != "generating" && within is not null && now - activity > within) continue;

                result.Add(new Session(
                    key,
                    TitleFor(s, origin, key),
                    channel,
                    state,
                    activity,
                    DeliveryFor(s),
                    KindFor(s, origin, key)));
            }

            AssignColours(everyAgent);

            lock (Gate) _roomMembers = roomMembers;

            return (result, list.GetArrayLength());
        }

        // What kind of thing this session is: a scheduled job, a private
        // conversation, or one in a room with other people in it.
        //
        // Worth telling apart because they are not the same kind of object at
        // all — "Zara — general" and "Zara — wtvamp" read identically today,
        // and one of them is a channel anyone can see while the other is a DM.
        // A cron session is further still: nobody is on the other end of it.
        //
        // Two sources, deliberately in this order. The key is structural and
        // always present — `agent:<name>:cron:<uuid>` cannot be anything but a
        // cron job — while origin.chatType is the gateway's own word for a
        // conversation and is the only thing that separates a DM from a
        // channel. Where the key is uninformative (`agent:main:discord:…`),
        // chatType decides; where chatType is missing, the key's fourth segment
        // carries the same word.
        private static SessionKind KindFor(JsonElement session, JsonElement origin, string key) =>
            OpenClawSessionKind.From(key, Str(origin, "chatType"));

        // What to call a session. Two halves: who is talking, and where.
        //
        // The session key is "agent:<id>:<surface>[:<type>:<id>]", and the id is
        // what somebody's config happens to call that agent — "main",
        // "comfyui". The names their owner actually uses for them live in
        // agents.list: Lilibeth, Zara. Four orbs showing "M" because four agents
        // have ids starting with main is worse than L, Z, A, so the name wins
        // whenever there is one.
        //
        // The second half is needed because one agent commonly has a DM with
        // you, a DM with somebody else and two channels at once, and repeating
        // "Lilibeth — discord" four times identifies nothing.
        private static string TitleFor(JsonElement session, JsonElement origin, string key)
        {
            var label = Str(session, "label");
            var parts = key.Split(':');

            if (parts.Length >= 3 && parts[0] == "agent")
            {
                var agent = parts[1];
                var surface = parts[2];

                string name;
                lock (Gate) name = AgentNames.GetValueOrDefault(agent, agent);

                // A cron session is best identified by its job; everything else
                // by where the conversation is happening. "Cron: " is dropped
                // because the name after it already says that.
                var detail = !string.IsNullOrWhiteSpace(label)
                    ? label!.StartsWith("Cron: ", StringComparison.OrdinalIgnoreCase)
                        ? label![6..]
                        : label!
                    : Where(origin) ?? surface;

                return string.Equals(name, detail, StringComparison.OrdinalIgnoreCase)
                    ? name
                    : $"{name} — {detail}";
            }

            if (!string.IsNullOrWhiteSpace(label)) return label!;

            if (origin.ValueKind == JsonValueKind.Object)
            {
                var originLabel = Str(origin, "label");
                if (!string.IsNullOrWhiteSpace(originLabel)) return originLabel!;
            }

            return key;
        }

        // origin.label is written for a log, not for a person: "#general channel
        // id:1474991965354463274", "wtvamp user id:246722755112861696",
        // "discord:amber". The useful part is always at the front, so cut at the
        // id and drop the noun that introduces it.
        private static string? Where(JsonElement origin)
        {
            if (origin.ValueKind != JsonValueKind.Object) return null;

            var label = Str(origin, "label");
            if (string.IsNullOrWhiteSpace(label)) return null;

            var text = label!;

            var id = text.IndexOf(" id:", StringComparison.Ordinal);
            if (id > 0) text = text[..id];

            foreach (var noun in new[] { " channel", " user", " group" })
            {
                if (text.EndsWith(noun, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[..^noun.Length];
                }
            }

            // "discord:amber" — the surface is already in the title if it is
            // going to be, so only the name after it is worth keeping.
            var colon = text.LastIndexOf(':');
            if (colon >= 0 && colon < text.Length - 1) text = text[(colon + 1)..];

            text = text.Trim();
            return text.Length == 0 ? null : text;
        }

        // deliveryContext is the authoritative answer; lastChannel/lastTo are
        // what the gateway itself falls back to, so this falls back the same
        // way rather than inventing its own rule.
        private static Delivery? DeliveryFor(JsonElement session)
        {
            string? channel = null, to = null, account = null;

            if (session.TryGetProperty("deliveryContext", out var context)
                && context.ValueKind == JsonValueKind.Object)
            {
                channel = Str(context, "channel");
                to = Str(context, "to");
                account = Str(context, "accountId");
            }

            channel ??= Str(session, "lastChannel");
            to ??= Str(session, "lastTo");
            account ??= Str(session, "lastAccountId");

            return string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(to)
                ? null
                : new Delivery(channel!, to!, account);
        }

        private static string StateFor(string key)
        {
            lock (Gate)
            {
                if (!Running.TryGetValue(key, out var last)) return "idle";
                if (DateTime.UtcNow - last > RunIdle)
                {
                    Running.Remove(key);
                    return "idle";
                }

                return "generating";
            }
        }

        // The later of what the gateway claims and what we have watched happen.
        // Ours wins whenever the two disagree, because ours came from an event
        // the session actually emitted.
        private static DateTime Activity(JsonElement session, string key)
        {
            var ms = Math.Max(Num(session, "lastActivityAt"), Num(session, "updatedAt"));
            var reported = ms <= 0
                ? DateTime.UtcNow
                : DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

            lock (Gate)
            {
                if (LastSeen.TryGetValue(key, out var seen) && seen > reported) return seen;
            }

            return reported;
        }

        // Every event that names a session is evidence that session is working.
        // The key on an event is run-scoped — "…:run:<runId>" appended to the
        // session's own key — so it has to be trimmed back before it means
        // anything to the list.
        private static void OnEvent(string name, JsonElement payload)
        {
            if (name is "tick" or "health" or "presence" or "connect.challenge") return;
            if (payload.ValueKind != JsonValueKind.Object) return;

            var key = Str(payload, "sessionKey");
            if (string.IsNullOrEmpty(key)) return;

            var run = key.IndexOf(":run:", StringComparison.Ordinal);
            if (run > 0) key = key[..run];

            OpenClawChatSession? chat;

            lock (Gate)
            {
                Chats.TryGetValue(key, out chat);

                // A finished run stops counting immediately rather than waiting
                // out RunIdle — the gateway said so, which beats inferring it.
                // Seen is recorded for every event including the one that ends a
                // run: a conversation that just finished replying is exactly the
                // one worth keeping on screen.
                LastSeen[key] = DateTime.UtcNow;

                if (name is "cron" && Str(payload, "action") == "finished") Running.Remove(key);
                else Running[key] = DateTime.UtcNow;
            }

            // Only for a session someone has opened: building a transcript for
            // 59 sessions nobody is looking at would be work and memory spent on
            // nothing. Marshalled here so every implementation of
            // IRemoteChatSession can promise its events arrive on the UI thread.
            if (chat is not null)
            {
                Dispatcher.UIThread.Post(() => chat.OnAgentEvent(name, payload));
            }
        }

        // Sends a reply into a session. Fails loudly rather than silently: the
        // panel puts whatever comes back in front of the person who typed it,
        // because a message that didn't arrive and didn't say so is the worst
        // outcome a chat window can produce.
        public static async Task SendAsync(OpenClawChatSession chat, string text, CancellationToken ct)
        {
            OpenClawGateway? gateway;
            lock (Gate) gateway = _gateway;

            if (gateway is null) throw new IOException("not connected to the gateway");

            // Post what you typed into the conversation first, then ask the
            // agent to answer it.
            //
            // The gateway only ever delivers the *agent's* side to a channel —
            // it assumes your side arrived from that channel in the first
            // place, which is true right up until you type it somewhere else.
            // Left alone, Discord shows an answer with no question above it.
            //
            // Ordering matters and is why this is awaited: the reply can come
            // back fast, and a mirror that lands after it puts the question
            // below its own answer.
            if (chat.Delivery is { } delivery)
            {
                try
                {
                    var mirror = new Dictionary<string, object>
                    {
                        ["to"] = delivery.To,
                        ["message"] = "**(via Claude Buddy)** " + text,
                        ["channel"] = delivery.Channel,
                        ["idempotencyKey"] = Guid.NewGuid().ToString()
                    };

                    if (!string.IsNullOrWhiteSpace(delivery.AccountId))
                    {
                        mirror["accountId"] = delivery.AccountId!;
                    }

                    await gateway.RequestAsync("send", mirror, ct);
                }
                catch
                {
                    // A mirror that fails must not eat the message. The reply
                    // still goes through and still gets delivered; the Discord
                    // log is just missing the prompt, which is where this
                    // started.
                }
            }

            await gateway.RequestAsync("chat.send", new Dictionary<string, object>
            {
                ["sessionKey"] = chat.GatewayKey,
                ["message"] = text,

                // Without this the gateway routes the reply to its internal
                // channel — the agent answers, the transcript records it, and
                // nothing ever reaches Discord. Its own routing reads:
                //
                //   if (!(params.deliver === true)) return { originatingChannel: INTERNAL_MESSAGE_CHANNEL, … }
                //   const sessionDeliveryContext = deliveryContextFromSession(entry)
                //
                // so `true` is what makes it look up where this conversation
                // actually lives and deliver there. We don't have to carry the
                // channel or recipient ourselves; the session already knows, and
                // the gateway is connected to Discord whether or not anything is
                // open on this machine.
                //
                // Always on rather than a choice: a reply typed into a Discord
                // conversation is a reply *to* that conversation. Anything else
                // would be a message that looked sent and wasn't.
                ["deliver"] = true,

                // Side-effecting methods want one of these; a fresh id per send
                // means a retry after a timeout can't post the message twice.
                ["idempotencyKey"] = Guid.NewGuid().ToString()
            }, ct);
        }

        // One agent messaging another arrives as a user turn with a machine
        // header glued to the front:
        //
        //   [Inter-session message] sourceSession=agent:comfyui:discord:direct:2467…
        //   sourceChannel=discord sourceTool=sessions_send isUser=false <the actual message>
        //
        // Left as-is, a transcript in a multi-agent setup is mostly routing
        // metadata. It isn't noise to be dropped though — it is one of your
        // agents talking — so the header is replaced by the thing it was
        // actually saying, attributed to whoever said it.
        private static string Readable(string text) => Readable(text, out _);

        private static string Readable(string text, out string? speakerId)
        {
            speakerId = null;

            // Not something a person said: OpenClaw writes this into the user
            // role when it restarts a CLI session under the covers. Dropped
            // rather than shortened, because there is nothing in it for the
            // person reading — an empty result is skipped by the caller.
            if (text.StartsWith("OpenClaw resumed this CLI session", StringComparison.Ordinal)) return "";

            text = WithoutTrailingInstruction(text);
            text = WithShortAttachments(text);

            const string Marker = "[Inter-session message]";
            if (!text.StartsWith(Marker, StringComparison.Ordinal)) return text;

            var rest = text[Marker.Length..].TrimStart();
            string? from = null;

            // The header is a run of key=value tokens; the message is whatever
            // follows the last of them. Parsed by shape rather than by a fixed
            // list of keys, so a new one appearing doesn't leak into the body.
            while (true)
            {
                var space = rest.IndexOf(' ');
                if (space <= 0) break;

                var token = rest[..space];
                var equals = token.IndexOf('=');
                if (equals <= 0) break;

                if (token.StartsWith("sourceSession=", StringComparison.Ordinal))
                {
                    // "agent:comfyui:discord:direct:…" — the agent's name is the
                    // one part of that a person recognises.
                    var value = token["sourceSession=".Length..].Split(':');
                    if (value.Length >= 2) from = value[1];
                }

                rest = rest[(space + 1)..].TrimStart();
            }

            if (string.IsNullOrWhiteSpace(rest)) return text;

            if (from is null) return rest;

            // Reported rather than glued to the front of the text. A name in the
            // string is a name the panel can only draw as part of the sentence;
            // as a field it can be a label above the bubble and can colour it.
            speakerId = from;
            return rest;
        }

        // The agent's name if we have it. The key carries the id, and the id is
        // what its owner's config calls it rather than what they do.
        public static string AgentNameOf(string agentId)
        {
            lock (Gate) return AgentNames.GetValueOrDefault(agentId, agentId);
        }

        // The last thing the agent said, for the speak button on the orb's own
        // flyout — which has no panel open and so no transcript to read from.
        // Loads the history if this session has never been opened.
        public static async Task<string?> LastAssistantTextAsync(string sessionId, string displayName)
        {
            if (ChatFor(sessionId, displayName) is not OpenClawChatSession chat) return null;

            var existing = LastAssistantText(chat);
            if (existing is not null) return existing;

            // ChatFor kicks off a load; give it a moment rather than duplicating
            // the request. Speaking is a deliberate act, so a short wait is
            // better than saying nothing at all.
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(100);

                var text = LastAssistantText(chat);
                if (text is not null) return text;
            }

            return null;
        }

        private static string? LastAssistantText(OpenClawChatSession chat)
        {
            for (var i = chat.History.Count - 1; i >= 0; i--)
            {
                var turn = chat.History[i];
                if (turn.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(turn.Text))
                {
                    return turn.Text;
                }
            }

            return null;
        }

        // Voice-mode messages arrive with an instruction stapled to the end:
        //
        //   What is the status of our OpenClaw install?
        //
        //   [Reply out loud in a natural speaking voice: 1-3 short sentences, …]
        //
        // That is OpenClaw's scaffolding rather than anything the person said,
        // and it is longer than most of their actual messages.
        //
        // The rule is deliberately narrow: a *final* paragraph that is entirely
        // wrapped in brackets, in a message that has something else in it. A
        // broader "strip bracketed text" would eat legitimate content, and a
        // check for this exact wording would rot the first time the prompt is
        // reworded.
        // An attachment arrives as its staging path:
        //
        //   [media attached: /Users/…/media/inbound/openclaw-staged-f71b696d-….png]
        //
        // The path is a detail of where the gateway put the file, and it is
        // longer than most messages. The filename is the only part worth
        // showing, and even that mostly to say something was attached at all.
        private static string WithShortAttachments(string text)
        {
            const string Marker = "[media attached: ";

            var start = text.IndexOf(Marker, StringComparison.Ordinal);
            while (start >= 0)
            {
                var end = text.IndexOf(']', start);
                if (end < 0) break;

                var path = text[(start + Marker.Length)..end];
                var name = path[(path.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];

                text = text[..start] + "📎 " + (name.Length == 0 ? "attachment" : name) + text[(end + 1)..];
                start = text.IndexOf(Marker, StringComparison.Ordinal);
            }

            return text;
        }

        private static string WithoutTrailingInstruction(string text)
        {
            var trimmed = text.TrimEnd();
            if (!trimmed.EndsWith(']')) return text;

            var open = trimmed.LastIndexOf("\n\n[", StringComparison.Ordinal);
            if (open < 0) return text;

            var body = trimmed[..open].TrimEnd();
            if (body.Length == 0) return text;

            // Only when the bracket really does open that last paragraph — a
            // message whose final paragraph merely contains a bracket keeps it.
            var tail = trimmed[(open + 2)..];
            return tail.IndexOf(']') == tail.Length - 1 ? body : text;
        }

        // Pictures sent in a conversation. The gateway serves them from its own
        // HTTP endpoint, authorised with the same gateway token the socket
        // uses, and they arrive as ordinary bytes.
        //
        // Cached by url: a transcript is re-read every time its panel opens, and
        // refetching a megabyte per image per open would be wasteful and slow
        // in exactly the moment the user is waiting to see something.
        private static readonly Dictionary<string, byte[]?> Media = new(StringComparer.Ordinal);

        public static async Task<byte[]?> FetchMediaAsync(string url, CancellationToken ct)
        {
            lock (Gate)
            {
                if (Media.TryGetValue(url, out var cached)) return cached;
            }

            var host = ClaudeBuddySettings.OpenClawHost;
            var token = OpenClawIdentity.GatewayTokenFor(host);
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrEmpty(token)) return null;

            byte[]? bytes = null;

            try
            {
                var pinned = ClaudeBuddySettings.OpenClawFingerprint;

                bytes = await OpenClawSocket.GetAsync(
                    host, ClaudeBuddySettings.OpenClawPort, url, token!,
                    string.IsNullOrEmpty(pinned) ? null : pinned, ct);
            }
            catch
            {
                // A picture that won't load is a picture that won't load. The
                // message it belongs to still reads.
            }

            lock (Gate)
            {
                // Only successes are cached. Storing the failure meant one
                // hiccup — a gateway mid-restart — hid that picture for the life
                // of the process, however many times the panel was reopened.
                if (bytes is null || bytes.Length == 0) return null;

                Media[url] = bytes;

                // Bounded, because these are megabyte-sized and a long
                // conversation full of renders would otherwise grow the cache
                // for as long as the app runs. Oldest out first; a picture that
                // gets evicted and scrolled back to is simply fetched again.
                const int Keep = 24;
                while (Media.Count > Keep)
                {
                    Media.Remove(Media.Keys.First());
                }

                return bytes;
            }
        }

        private static List<OpenClawChatSession> OpenChats()
        {
            lock (Gate) return Chats.Values.ToList();
        }

        // The conversation as it already stands. Without this a panel opens
        // blank and you are answering a question you cannot see — which is
        // exactly how it felt the first time one was opened for real.
        // How many of the gateway's messages a page asks for. Small enough that
        // opening a panel is quick, large enough that scrolling back doesn't
        // feel like it is fetching one line at a time.
        private const int PageSize = 40;

        // An older page, fetched when the panel is scrolled to the top.
        // chat.history counts its offset back from the newest message, so
        // walking backwards is simply an increasing offset — verified against
        // the gateway: consecutive pages do not overlap.
        public static async Task<bool> LoadOlderAsync(OpenClawChatSession chat, CancellationToken ct)
        {
            if (!chat.HasMore) return false;

            var page = await FetchPageAsync(chat, chat.LoadedMessages, ct);
            if (page is null) return false;

            var (turns, messages) = page.Value;

            // Nothing came back, so there is nothing behind this. Asked once and
            // remembered, rather than re-asking every time the user reaches the
            // top of a conversation that has no more to give.
            if (messages == 0)
            {
                chat.HasMore = false;
                return false;
            }

            // A short page is the last page. Without this the next scroll to the
            // top spends a round trip discovering the same thing again.
            if (messages < PageSize) chat.HasMore = false;

            chat.LoadedMessages += messages;
            chat.PrependHistory(turns);
            return turns.Count > 0;
        }

        private static async Task LoadHistoryAsync(OpenClawChatSession chat, CancellationToken ct)
        {
            var first = await FetchPageAsync(chat, 0, ct);
            if (first is null) return;

            var (initial, count) = first.Value;

            chat.LoadedMessages = count;
            chat.HasMore = count >= PageSize;

            Dispatcher.UIThread.Post(() => chat.SetHistory(initial));
        }

        private static async Task<(List<(ChatRole Role, string Text, string? ImageUrl, string ImageAlt, DateTimeOffset At, string? Speaker, string? SpeakerColor)> Turns, int Messages)?>
            FetchPageAsync(OpenClawChatSession chat, int offset, CancellationToken ct)
        {
            OpenClawGateway? gateway;
            lock (Gate) gateway = _gateway;

            if (gateway is null) return null;

            try
            {
                var res = await gateway.RequestAsync("chat.history", new Dictionary<string, object>
                {
                    ["sessionKey"] = chat.GatewayKey,
                    ["limit"] = PageSize,
                    ["offset"] = offset
                }, ct);

                if (!res.TryGetProperty("messages", out var messages)
                    || messages.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var turns = new List<(ChatRole Role, string Text, string? ImageUrl, string ImageAlt, DateTimeOffset At, string? Speaker, string? SpeakerColor)>();

                foreach (var message in messages.EnumerateArray())
                {
                    var role = Str(message, "role") == "user" ? ChatRole.User : ChatRole.Assistant;

                    // content is a list of blocks; only the text ones are worth
                    // showing. Tool calls arrive live as their own turns, and a
                    // replayed tool_use block would be a wall of JSON.
                    if (!message.TryGetProperty("content", out var content)) continue;

                    // The two roles are shaped differently, which is easy to miss
                    // and silently drops half the conversation: an assistant turn
                    // carries `content` as a list of blocks, and a user turn
                    // carries it as a plain string. Reading only the block form
                    // showed an agent talking to nobody.
                    // Pictures are their own turns rather than being folded into
                    // the text of one. A message is commonly several images and
                    // nothing else, and a bubble containing four of them stacked
                    // reads worse than four bubbles.
                    if (content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            if (Str(block, "type") != "image") continue;

                            var url = Str(block, "url");
                            if (string.IsNullOrWhiteSpace(url)) continue;

                            var ms2 = Num(message, "timestamp");
                            turns.Add((role, "", url!, Str(block, "alt") ?? "", ms2 <= 0
                                ? DateTimeOffset.Now
                                : DateTimeOffset.FromUnixTimeMilliseconds(ms2).ToLocalTime(),
                                null, null));
                        }
                    }

                    var text = content.ValueKind switch
                    {
                        JsonValueKind.String => content.GetString() ?? "",

                        JsonValueKind.Array => string.Join("\n", content.EnumerateArray()
                            .Where(b => Str(b, "type") == "text")
                            .Select(b => Str(b, "text"))
                            .Where(t => !string.IsNullOrWhiteSpace(t))),

                        JsonValueKind.Object => Str(content, "text") ?? "",

                        _ => ""
                    };

                    if (string.IsNullOrWhiteSpace(text)) continue;

                    text = Readable(text, out var speakerId);
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var ms = Num(message, "timestamp");
                    var at = ms > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime()
                        : DateTimeOffset.Now;

                    turns.Add((role, text.Trim(), null, "", at,
                        speakerId is null ? null : AgentNameOf(speakerId),
                        speakerId is null ? null : ColourForAgent(speakerId)));
                }

                // The message count, not the turn count: it is what the next
                // page's offset is measured in, and one message can produce
                // several turns or none.
                return (turns, messages.GetArrayLength());
            }
            catch
            {
                // A gateway that won't tell us the backlog is not a reason to
                // refuse the conversation — the panel still works forward from
                // whatever happens next.
                return null;
            }
        }

        private static void Report(string state)
        {
            lock (Gate) _state = state;
        }

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        private static long Num(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number
                ? v.GetInt64()
                : 0;
    }
}
