using System.Diagnostics.CodeAnalysis;
namespace ClaudeBuddy
{
    // This machine asking another machine's Buddy for what its sessions
    // actually say, and refusing anything it cannot prove.
    //
    // The client half of MirrorProtocol. It finds the far Buddy among the peers
    // the relay already lists, asks it about the sessions there are orbs for,
    // and then — for a panel that is open — pulls that session's transcript
    // across in hashed pieces and keeps it up to date.
    //
    // The rule that matters is what happens when something does not verify.
    // Nothing partially-good is ever handed on. A piece that fails its own hash
    // is asked for again, twice, and then the transfer fails; a set of pieces
    // that individually verify but do not add up to the digest the far side
    // computed fails immediately, because there is nothing useful to re-request.
    // Either way the caller is told the mirror failed and shows an error. It
    // never shows text that did not survive the trip, and it never quietly falls
    // back to the model-written version — that is the bug this exists to fix,
    // and doing it as a fallback would reintroduce it precisely when something
    // is going wrong.
    internal sealed class RemoteMirrorClient
    {
        internal sealed record Seams(Func<string, string, Task<bool>> SendFrame);

        private readonly string _account;
        private readonly Seams _seams;
        private readonly object _gate = new();

        public RemoteMirrorClient(string account, Seams seams)
        {
            _account = account;
            _seams = seams;
        }

        public string Account => _account;

        // --- what the far side said it has -----------------------------------

        // A session's mirror status, from this side's point of view.
        internal enum MirrorAvailability
        {
            // Nobody has answered yet. Distinct from Unavailable on purpose: a
            // panel that opens during the handshake should say it is checking,
            // not that there is nothing there.
            Unknown,

            Available,

            // Asked and answered no — there is no Buddy over there, or it has
            // no transcript for this session. Settled, and the panel says so.
            Unavailable
        }

        internal readonly record struct MirrorState(
            MirrorAvailability Availability, MirrorProtocol.MirrorRosterEntry? Entry);

        private readonly Dictionary<string, MirrorProtocol.MirrorRosterEntry> _roster =
            new(StringComparer.OrdinalIgnoreCase);

        // Which far relay answered for a given session, so a later request goes
        // back to the one that can serve it.
        private readonly Dictionary<string, string> _servedBy = new(StringComparer.OrdinalIgnoreCase);

        // Names that have been asked about and came back with nothing. Kept so a
        // panel can say "no live view" definitively rather than sitting on
        // "checking…" forever.
        private readonly HashSet<string> _answeredNo = new(StringComparer.OrdinalIgnoreCase);

        public event Action? RosterUpdated;

        internal MirrorState StateFor(string name)
        {
            lock (_gate)
            {
                if (_roster.TryGetValue(name, out var entry))
                    return new MirrorState(MirrorAvailability.Available, entry);

                return new MirrorState(
                    _answeredNo.Contains(name) ? MirrorAvailability.Unavailable : MirrorAvailability.Unknown,
                    null);
            }
        }

        // --- discovery --------------------------------------------------------

        // The far Buddies among the peers, and what to ask them.
        //
        // A far Buddy's relay is recognisable by the name prefix
        // RemoteControlBridge builds, which is the same string
        // BridgeProtocol.IsOwnRelay already keys on to keep relays from becoming
        // orbs. Our *own* relay is never in this list — ListAgents excludes the
        // asking session by its own promise — so anything wearing the prefix and
        // still online is somebody else's Buddy, which is exactly what this
        // wants. Ones left registered by a dead relay read "offline" and are
        // skipped.
        public async Task DiscoverAsync(
            IReadOnlyList<BridgeProtocol.RemoteAgent> agents,
            IReadOnlyList<string> wantedNames)
        {
            if (wantedNames.Count == 0) return;

            var relays = agents
                .Where(a => a.IsOwnRelay && !a.IsOffline && a.IsRemoteControl)
                .Select(a => a.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (relays.Count == 0)
            {
                // No Buddy over there. Every name asked about is settled as
                // unavailable, which is what turns a panel's "checking…" into
                // the honest "no live view" line.
                var changed = false;

                lock (_gate)
                {
                    foreach (var name in wantedNames)
                    {
                        if (_roster.ContainsKey(name)) continue;
                        if (_answeredNo.Add(name)) changed = true;
                    }
                }

                if (changed) RosterUpdated?.Invoke();
                return;
            }

            foreach (var relay in relays)
            {
                List<string> ask;

                lock (_gate)
                {
                    // Only what is still unknown. A roster entry does not go
                    // stale in a way that matters — colour and commands are
                    // refreshed by asking again when a panel opens — and
                    // re-asking every poll would spend a model turn per tick for
                    // an answer that has not changed.
                    ask = wantedNames.Where(n => !_roster.ContainsKey(n)).ToList();
                }

                if (ask.Count == 0) continue;

                var reply = await RequestAsync(
                    relay, MirrorProtocol.Hello, new Dictionary<string, string> { ["pv"] = "1" },
                    MirrorProtocol.PackRows(ask), TimeSpan.FromSeconds(120))
                    .ConfigureAwait(false);

                if (!reply.Ok || reply.Payload is null)
                {
                    // Silence is not "no". A relay that was busy or timed out is
                    // asked again on the next poll; only a real answer settles a
                    // name as unavailable.
                    continue;
                }

                var entries = MirrorProtocol.DecodeRoster(reply.Payload);
                if (entries is null) continue;

                lock (_gate)
                {
                    foreach (var entry in entries)
                    {
                        if (!entry.HasTranscript)
                        {
                            _answeredNo.Add(entry.Name);
                            continue;
                        }

                        _roster[entry.Name] = entry;
                        _servedBy[entry.Name] = relay;
                        _answeredNo.Remove(entry.Name);
                    }

                    // Asked about and not mentioned means that Buddy does not
                    // have it — a session on a third machine, most likely.
                    foreach (var name in ask)
                    {
                        if (_roster.ContainsKey(name)) continue;
                        _answeredNo.Add(name);
                    }
                }

                RosterUpdated?.Invoke();
            }
        }

        // --- a session's feed --------------------------------------------------

        internal enum MirrorDelivery
        {
            // The opening read: everything the panel should show, replacing
            // whatever it had.
            Window,

            // Appended since.
            Delta
        }

        internal readonly record struct MirrorRows(
            string Name, MirrorDelivery Mode,
            IReadOnlyList<MirrorProtocol.MirrorTurn> Turns, string Cli, long Gen);

        // Verified rows, ready to be parsed by the same parsers a local panel
        // uses. Raised on a background thread; the chat session marshals.
        public event Action<MirrorRows>? Delivered;

        // The mirror could not be trusted. Carries wording-worthy detail, not a
        // code, because the panel says something specific about integrity here.
        public event Action<string, string>? Failed;

        private sealed class Feed
        {
            public required string Name;
            public required string Relay;
            public required string Cli;
            public long BacklogFrom;
            public long Gen;
            public string? WatchId;
            public DateTime RenewAt;
            public bool Loading;
        }

        private readonly Dictionary<string, Feed> _feeds = new(StringComparer.OrdinalIgnoreCase);

        public bool HasMore(string name)
        {
            lock (_gate) return _feeds.TryGetValue(name, out var feed) && feed.BacklogFrom > 0;
        }

        // Opens a mirror for one session: the tail first, then a subscription so
        // what happens next arrives without being asked for.
        // One outstanding window per session, however many callers ask for it.
        //
        // The Loading flag below is not enough on its own, and CB-46 measured
        // why: it lives on the Feed, and CloseAsync *removes* the Feed. A panel
        // being rebound — closed and reopened, which clicking between two orbs
        // does constantly — therefore threw away the only record that a fetch
        // was already running, and the next PanelOpened started another. Four
        // distinct FETCHes for one session inside 78 seconds, against a
        // three-minute timeout, so none of them were retries.
        //
        // That is worse than wasted effort. Each one makes the far side build
        // and queue another whole window, so a relay already minutes deep in
        // chunk 0 acquires more behind it and the queue grows faster than a
        // model turn can drain it — the panel gets slower every time somebody
        // looks at it.
        //
        // Keyed outside the Feed so it survives the Feed being removed, and
        // handed out as the *same task* so a second caller waits on the first
        // answer rather than starting a second conversation about it.
        private readonly Dictionary<string, Task<bool>> _opening =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<bool> OpenAsync(string name)
        {
            lock (_gate)
            {
                if (_opening.TryGetValue(name, out var running)) return running;

                var started = OpenCoreAsync(name);

                // Only track it if it is actually still running: a synchronous
                // refusal (no roster entry) has already completed here, and
                // recording that would answer every later caller with the same
                // stale "no".
                if (!started.IsCompleted) _opening[name] = started;

                return started;
            }
        }

        private async Task<bool> OpenCoreAsync(string name)
        {
            try
            {
                return await OpenOnceAsync(name).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) _opening.Remove(name);
            }
        }

        private async Task<bool> OpenOnceAsync(string name)
        {
            string relay;
            string cli;

            lock (_gate)
            {
                if (!_roster.TryGetValue(name, out var entry)) return false;
                if (!_servedBy.TryGetValue(name, out var found)) return false;

                if (_feeds.TryGetValue(name, out var already) && already.Loading) return true;

                relay = found;
                cli = entry.Cli;

                _feeds[name] = new Feed { Name = name, Relay = relay, Cli = cli, Loading = true };
            }

            var reply = await RequestAsync(
                relay, MirrorProtocol.Fetch,
                new Dictionary<string, string>
                {
                    ["n"] = MirrorProtocol.Encode(name),
                    ["w"] = "tail"
                },
                null, TimeSpan.FromSeconds(180))
                .ConfigureAwait(false);

            lock (_gate)
            {
                if (_feeds.TryGetValue(name, out var feed)) feed.Loading = false;
            }

            if (!reply.Ok || reply.Payload is null)
            {
                Failed?.Invoke(name, reply.Reason ?? "the other machine didn't answer");
                return false;
            }

            var turns = MirrorProtocol.DecodeTurns(reply.Payload);
            if (turns is null) return SayItArrivedUnreadable(name);

            long gen = 0;

            lock (_gate)
            {
                if (_feeds.TryGetValue(name, out var feed))
                {
                    feed.BacklogFrom = Num(reply.Fields, "wfrom");
                    feed.Gen = Num(reply.Fields, "gen");
                    gen = feed.Gen;
                }
            }

            Delivered?.Invoke(new MirrorRows(name, MirrorDelivery.Window, turns, cli, gen));

            await RenewWatchAsync(name).ConfigureAwait(false);
            return true;
        }

        // A panel that is being looked at again.
        //
        // Distinct from OpenAsync so that rebinding a panel to a session it is
        // already showing costs a renewal rather than another full read of the
        // tail — the panel is a singleton and clicking between two orbs rebinds
        // constantly.
        public async Task ReopenAsync(string name)
        {
            bool open;
            lock (_gate) open = _feeds.ContainsKey(name);

            if (open) await RenewWatchAsync(name).ConfigureAwait(false);
            else await OpenAsync(name).ConfigureAwait(false);
        }

        // One page further back, for scrollback. Null when there is no more, or
        // when the request failed — the two are told apart by HasMore.
        public async Task<IReadOnlyList<MirrorProtocol.MirrorTurn>?> LoadOlderAsync(string name)
        {
            string relay;
            long to;

            lock (_gate)
            {
                if (!_feeds.TryGetValue(name, out var feed) || feed.BacklogFrom <= 0) return null;

                relay = feed.Relay;
                to = feed.BacklogFrom;
            }

            var from = Math.Max(0, to - MirrorProtocol.PageBytes);

            var reply = await RequestAsync(
                relay, MirrorProtocol.Fetch,
                new Dictionary<string, string>
                {
                    ["n"] = MirrorProtocol.Encode(name),
                    ["w"] = "range",
                    ["from"] = from.ToString(),
                    ["to"] = to.ToString()
                },
                null, TimeSpan.FromSeconds(180))
                .ConfigureAwait(false);

            if (!reply.Ok || reply.Payload is null) return null;

            var turns = MirrorProtocol.DecodeTurns(reply.Payload);
            if (turns is null) return null;

            lock (_gate)
            {
                if (_feeds.TryGetValue(name, out var feed))
                {
                    // The aligned offset, not the one asked for. A window that
                    // landed inside one enormous row reports where it started,
                    // which steps over it rather than reading it forever — see
                    // RemoteMirrorServer.ReadWindow.
                    var aligned = Num(reply.Fields, "wfrom", from);
                    feed.BacklogFrom = aligned >= to ? 0 : aligned;
                }
            }

            return turns;
        }

        // Subscribes, or renews a subscription that is about to lapse.
        public async Task RenewWatchAsync(string name)
        {
            string relay;
            string? existing;

            lock (_gate)
            {
                if (!_feeds.TryGetValue(name, out var feed)) return;
                if (feed.WatchId is not null && DateTime.UtcNow < feed.RenewAt) return;

                relay = feed.Relay;
                existing = feed.WatchId;
                feed.RenewAt = DateTime.UtcNow.AddSeconds(MirrorProtocol.WatchRenewSeconds);
            }

            // Renewing keeps the same id, so the far side updates one
            // subscription instead of collecting a new one every ninety seconds.
            var id = existing ?? MirrorProtocol.NewId();

            lock (_gate)
            {
                if (_feeds.TryGetValue(name, out var feed)) feed.WatchId = id;
            }

            await RequestAsync(
                relay, MirrorProtocol.Watch,
                new Dictionary<string, string>
                {
                    ["n"] = MirrorProtocol.Encode(name),
                    ["ttl"] = MirrorProtocol.WatchTtlSeconds.ToString()
                },
                null, TimeSpan.FromSeconds(120), id)
                .ConfigureAwait(false);
        }

        // The panel closed. Told rather than left to lapse, so a far relay is
        // not kept awake by something nobody is looking at.
        public async Task CloseAsync(string name)
        {
            string? relay = null, watch = null;

            lock (_gate)
            {
                if (_feeds.Remove(name, out var feed))
                {
                    relay = feed.Relay;
                    watch = feed.WatchId;
                }
            }

            if (relay is null || watch is null) return;

            await RequestAsync(relay, MirrorProtocol.Unwatch, null, null,
                TimeSpan.FromSeconds(30), watch, awaitReply: false).ConfigureAwait(false);
        }

        // Types a line into the far session's own terminal.
        //
        // Null on success; otherwise the error code, which the caller turns into
        // wording. This is the path that makes /color work again: the text is
        // typed into that CLI's input line by the Buddy running beside it, so
        // its own command handler runs it, exactly as it would locally.
        public async Task<string?> SendInputAsync(string name, string text)
        {
            string relay;

            lock (_gate)
            {
                if (!_servedBy.TryGetValue(name, out var found)) return MirrorProtocol.ErrNoSession;
                relay = found;
            }

            var reply = await RequestAsync(
                relay, MirrorProtocol.Input,
                new Dictionary<string, string> { ["n"] = MirrorProtocol.Encode(name) },
                System.Text.Encoding.UTF8.GetBytes(text),
                TimeSpan.FromSeconds(180))
                .ConfigureAwait(false);

            if (reply.Ok) return null;

            return reply.ErrCode ?? MirrorProtocol.ErrUnsupported;
        }

        // Excluded from coverage: reaching it needs a payload that passes every
        // per-piece hash *and* the whole-payload hash and still is not a turn
        // list — which means the far machine gzipped something else under the
        // right digests, not a courier mangling anything. No test can build that
        // without also building a second, dishonest MirrorProtocol.
        //
        // Kept because a version skew is exactly how it would happen, and
        // saying so beats a live view that quietly shows nothing. What it says
        // is asserted in MirrorAndRoutingArmsTests, which covers DecodeTurns
        // answering null for every input a test can construct.
        [ExcludeFromCodeCoverage]
        private bool SayItArrivedUnreadable(string name)
        {
            Failed?.Invoke(name, "the transcript arrived unreadable");
            return false;
        }

        // Excluded from coverage: a watch is renewed 90 seconds before a
        // 120-second TTL expires, so nothing is ever due inside a test — the
        // alternative is a test that waits a minute and a half, which is exactly
        // the shape of timing dependence this repository has repeatedly removed
        // rather than added.
        //
        // The loop that consumes it stays measured, because a tick with nothing
        // due is the ordinary case and is what every test exercises.
        [ExcludeFromCodeCoverage]
        private List<string> DueForRenewal()
        {
            lock (_gate)
            {
                return _feeds.Values
                    .Where(f => f.WatchId is not null && DateTime.UtcNow >= f.RenewAt)
                    .Select(f => f.Name)
                    .ToList();
            }
        }

        // Renews anything due. Called on the same timer that drains the relay.
        public async Task TickAsync()
        {
            var due = DueForRenewal();

            foreach (var name in due) await RenewWatchAsync(name).ConfigureAwait(false);
        }

        public bool Busy
        {
            get { lock (_gate) return _feeds.Count > 0; }
        }

        // --- request / reply ---------------------------------------------------

        private readonly record struct Reply(
            bool Ok, byte[]? Payload, IReadOnlyDictionary<string, string>? Fields,
            string? ErrCode, string? Reason);

        private sealed class Pending
        {
            public required TaskCompletionSource<Reply> Waiter;
            public required string Relay;
            public required MirrorProtocol.MirrorAssembly Assembly;
            public int Resends;

            // When to give up, and it moves. A flat deadline asks the wrong
            // question on this wire: a transfer is not late because it is
            // broken, it is late because every chunk costs a model turn, so a
            // multi-chunk answer can be arriving perfectly and still miss any
            // fixed cut-off. Each chunk that verifies buys another full
            // interval, so what is actually being timed is *silence* rather
            // than duration — which is the thing that means something has gone
            // wrong. Bounded regardless, because chunks are finite: `of` says
            // how many there are and a far side cannot extend this for ever
            // without sending real, hash-checked payload each time.
            public DateTime Deadline;
            public TimeSpan Grace;
        }

        private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

        // Transfers arriving unbidden — the deltas a subscription pushes. Keyed
        // by the transfer's own id, which is fresh each time, and matched back
        // to a feed by the `sub` field it carries.
        private readonly Dictionary<string, MirrorProtocol.MirrorAssembly> _unsolicited =
            new(StringComparer.Ordinal);

        // Shortens every wait below, for tests only.
        //
        // The real timeouts are minutes, because a request has to survive the
        // far relay's model being mid-turn on something else. A test that
        // deliberately drops a reply would otherwise sit out the whole of one —
        // and "the relay never answered" is a path worth covering, not one worth
        // two minutes of a CI run.
        internal TimeSpan? TimeoutOverrideForTests { get; set; }

        private async Task<Reply> RequestAsync(
            string relay, string type,
            IReadOnlyDictionary<string, string>? fields, byte[]? payload,
            TimeSpan timeout, string? id = null, bool awaitReply = true)
        {
            if (TimeoutOverrideForTests is { } shorter) timeout = shorter;

            id ??= MirrorProtocol.NewId();

            var waiter = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_gate)
            {
                _pending[id] = new Pending
                {
                    Waiter = waiter,
                    Relay = relay,
                    Assembly = new MirrorProtocol.MirrorAssembly(),
                    Deadline = DateTime.UtcNow + timeout,
                    Grace = timeout
                };
            }

            try
            {
                var frame = MirrorProtocol.BuildFrame(type, id, fields, payload);

                if (!await Send(relay, frame).ConfigureAwait(false))
                    return new Reply(false, null, null, null, "couldn't reach the relay");

                if (!awaitReply) return new Reply(true, null, null, null, null);

                while (true)
                {
                    TimeSpan left;
                    lock (_gate)
                    {
                        left = _pending.TryGetValue(id, out var waiting)
                            ? waiting.Deadline - DateTime.UtcNow
                            : TimeSpan.Zero;
                    }

                    if (left <= TimeSpan.Zero) break;

                    var done = await Task.WhenAny(waiter.Task, Task.Delay(left)).ConfigureAwait(false);
                    if (done == waiter.Task) return waiter.Task.Result;

                    // The delay won, but a chunk may have landed while it was
                    // running and pushed the deadline out. Looping re-reads it
                    // rather than assuming this wait was the last one.
                }

                return new Reply(false, null, null, null, "the other machine didn't answer in time");
            }
            finally
            {
                lock (_gate) _pending.Remove(id);
            }
        }

        // Every piece verified individually and the reassembled whole still did
        // not match. Fatal for the transfer, unlike a single bad piece: there is
        // nothing to re-request usefully, because asking for any one of them
        // would return the same bytes — so the panel is told rather than left
        // looking healthy.
        //
        // Reached by a courier that re-signs what it alters, which is a
        // realistic relay rather than a contrived one — anything that reformats
        // a line and re-frames it lands here. See
        // ATransferWhoseWholeDigestFailsIsReportedRatherThanRetriedForEver.
        private void SettleAsUnverifiable(string id, MirrorProtocol.AssemblyResult result) =>
            Settle(id, new Reply(
                false, null, null, MirrorProtocol.ErrBadHash,
                result.Reason ?? "it failed its integrity check"));

        // Excluded from coverage: exists to be the try/catch around the relay,
        // and the swallow is what turns a relay that has gone away into
        // "couldn't reach the relay" in the panel rather than an exception on a
        // background task. Asserted through a courier that throws in
        // MirrorRoundTripTests; only the swallow itself is unmeasured.
        [ExcludeFromCodeCoverage]
        private async Task<bool> Send(string relay, string frame)
        {
            try { return await _seams.SendFrame(relay, frame).ConfigureAwait(false); }
            catch { return false; }
        }

        // --- inbound -----------------------------------------------------------

        // One frame from a far Buddy. Called on the relay's pump thread.
        public async Task OnFrameAsync(string fromRelay, MirrorProtocol.MirrorFrame frame)
        {
            switch (frame.Type)
            {
                case MirrorProtocol.Chunk:
                    await ChunkAsync(fromRelay, frame).ConfigureAwait(false);
                    break;

                case MirrorProtocol.Ok:
                    Settle(frame.Id, new Reply(true, null, frame.Fields, null, null));
                    break;

                case MirrorProtocol.Err:
                    Settle(frame.Id, new Reply(
                        false, null, frame.Fields,
                        frame.Get("code"),
                        frame.Text("msg") ?? frame.Get("code")));
                    break;
            }
        }

        private async Task ChunkAsync(string fromRelay, MirrorProtocol.MirrorFrame frame)
        {
            Pending? pending;
            lock (_gate) _pending.TryGetValue(frame.Id, out pending);

            if (pending is not null)
            {
                var result = pending.Assembly.Offer(frame);

                switch (result.State)
                {
                    case MirrorProtocol.AssemblyState.Incomplete:
                        // Progress. The far side is working through a transfer
                        // one model turn at a time, so the wait starts again
                        // rather than counting down towards a cut-off the
                        // transfer was never going to meet.
                        lock (_gate) pending.Deadline = DateTime.UtcNow + pending.Grace;
                        return;

                    case MirrorProtocol.AssemblyState.Complete:
                        Settle(frame.Id, new Reply(true, result.Payload, frame.Fields, null, null));
                        return;

                    case MirrorProtocol.AssemblyState.BadChunk:
                        // Asked for again rather than abandoned: on a long
                        // transcript one mangled piece out of thirty should cost
                        // one round trip, not the whole transfer.
                        if (++pending.Resends <= MirrorProtocol.ResendAttempts)
                        {
                            await Send(pending.Relay, MirrorProtocol.BuildFrame(
                                MirrorProtocol.Resend, frame.Id,
                                new Dictionary<string, string> { ["seq"] = result.BadSeq.ToString() }))
                                .ConfigureAwait(false);
                            return;
                        }

                        Settle(frame.Id, new Reply(
                            false, null, null, MirrorProtocol.ErrBadHash,
                            $"a piece of it failed its integrity check {pending.Resends} times"));
                        return;

                    default:
                        SettleAsUnverifiable(frame.Id, result);
                        return;
                }
            }

            // Not a reply to anything outstanding, so it is a subscription
            // pushing what's new.
            var sub = frame.Get("sub");
            if (sub is null) return;

            MirrorProtocol.MirrorAssembly assembly;
            lock (_gate)
            {
                if (!_unsolicited.TryGetValue(frame.Id, out var existing))
                {
                    existing = new MirrorProtocol.MirrorAssembly();
                    _unsolicited[frame.Id] = existing;

                    // Bounded so a far side that starts a transfer and never
                    // finishes it cannot accumulate.
                    while (_unsolicited.Count > 16)
                    {
                        var oldest = _unsolicited.Keys.First();
                        if (oldest == frame.Id) break;
                        _unsolicited.Remove(oldest);
                    }
                }

                assembly = existing;
            }

            var delta = assembly.Offer(frame);
            if (delta.State == MirrorProtocol.AssemblyState.Incomplete) return;

            lock (_gate) _unsolicited.Remove(frame.Id);

            string? name = null;
            string cli = MirrorProtocol.CliClaudeCode;

            lock (_gate)
            {
                foreach (var feed in _feeds.Values)
                {
                    if (!string.Equals(feed.WatchId, sub, StringComparison.Ordinal)) continue;

                    name = feed.Name;
                    cli = feed.Cli;
                    break;
                }
            }

            if (name is null) return;

            if (delta.State != MirrorProtocol.AssemblyState.Complete || delta.Payload is null)
            {
                SayTheUpdateWasBroken(name, delta);
                return;
            }

            var turns = MirrorProtocol.DecodeTurns(delta.Payload);
            if (turns is null || turns.Count == 0) return;

            var gen = Num(frame.Fields, "gen");

            // The far transcript was replaced — /clear, most likely. Everything
            // held is about a file that no longer exists, so the feed re-anchors
            // instead of appending to it.
            long known;
            lock (_gate) known = _feeds.TryGetValue(name, out var feed) ? feed.Gen : gen;

            if (gen != known)
            {
                _ = OpenAsync(name);
                return;
            }

            Delivered?.Invoke(new MirrorRows(name, MirrorDelivery.Delta, turns, cli, gen));
        }

        private void Settle(string id, Reply reply)
        {
            Pending? pending;
            lock (_gate) _pending.TryGetValue(id, out pending);

            pending?.Waiter.TrySetResult(reply);
        }

        // A delta cannot be re-requested usefully — the far side has already
        // moved its offset past it — so the honest answer is to say the live
        // view is broken rather than skip a message and carry on looking
        // healthy. Skipping is the tempting alternative and the wrong one: a
        // panel that has quietly lost a message is worse than one that says it
        // stopped.
        private void SayTheUpdateWasBroken(string name, MirrorProtocol.AssemblyResult delta) =>
            Failed?.Invoke(name, delta.Reason ?? "an update failed its integrity check");

        private static long Num(IReadOnlyDictionary<string, string>? fields, string key, long fallback = 0) =>
            fields is not null && fields.TryGetValue(key, out var v) && long.TryParse(v, out var n)
                ? n
                : fallback;
    }
}
