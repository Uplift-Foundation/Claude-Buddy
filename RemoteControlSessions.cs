using Avalonia.Threading;

namespace ClaudeBuddy
{
    // The Remote Control half of what SessionManager displays: Claude Code
    // sessions running on the user's *other* machines, published as an immutable
    // snapshot the scan reads for free.
    //
    // Shaped like OpenClawSessions next door, and for the same reasons — a
    // static class with a private cache and a gate, off meaning Snapshot()
    // returns an empty array with nothing constructed at all. What differs is
    // when it runs. OpenClaw holds a socket open for as long as it is switched
    // on, because a socket is free; each relay here is a **live Claude Code
    // session on the user's own account**, which is not. So they start on
    // demand, stop themselves when nobody is looking, and poll only while up.
    //
    // That is the whole reason EnsureStarted exists rather than a Restart() that
    // brings everything up: turning the feature on in Settings must not by
    // itself begin spending someone's quota. Something has to ask.
    //
    // **One relay per account**, because Remote Control is account-scoped: a
    // session signed into one account cannot see another's, so two accounts
    // genuinely need two relays and no amount of cleverness makes one do. That
    // is a real multiplier on what this costs — two accounts is two live
    // sessions — which is why the accounts are a list the user ticks rather than
    // something discovered and started automatically.
    internal static class RemoteControlSessions
    {
        private static readonly object Gate = new();

        // Published whole and replaced whole, so the scan — which runs on the UI
        // thread and locks nothing else it reads — is handed a finished list.
        private static volatile IReadOnlyList<Remote> _snapshot = Array.Empty<Remote>();

        // One entry per account with a relay up or coming up. Keyed by profile
        // dir, which is also what makes "already starting" answerable without a
        // second flag per account.
        private static readonly Dictionary<string, Relay> Relays = new(StringComparer.Ordinal);

        private static DispatcherTimer? _poll;
        private static DateTime _lastUse = DateTime.MinValue;

        private sealed class Relay
        {
            public RemoteControlBridge? Bridge;

            // Kept so the subscription can be undone on stop. The handler closes
            // over the account name, so it is not the same delegate for two
            // relays and cannot be reconstructed to unsubscribe.
            public Action<BridgeProtocol.InboundMessage>? Handler;

            public bool Starting;
            public string State = "starting";
            public string? Warning;
            public bool Polled;
            public IReadOnlyList<Remote> Sessions = Array.Empty<Remote>();

            // The two halves of the verbatim mirror, one pair per relay because
            // the relay is the wire they both talk over. The server answers the
            // other machine's Buddy about sessions here; the client asks the
            // other machine's Buddy about sessions there. Both exist on both
            // machines — which is the point, since either end can be the one
            // being looked at.
            public RemoteMirrorServer? Server;
            public RemoteMirrorClient? Client;
        }

        // This machine's own sessions, for the mirror server to answer about.
        //
        // A delegate set by SessionManager rather than a reach into it: this
        // class is static and starts long before any window exists, and a
        // reference the other way would make the orb list a dependency of the
        // relay rather than the other way round.
        private static Func<IReadOnlyList<(string SessionId, SessionStatus Status)>>? _localSessions;

        public static void ProvideLocalSessions(
            Func<IReadOnlyList<(string SessionId, SessionStatus Status)>> provider) =>
            _localSessions = provider;

        // A session on another machine, as the orb scan wants it. Kept separate
        // from BridgeProtocol.RemoteAgent so the parser stays a parser: this one
        // carries which account it was seen through and when, neither of which
        // the peer list has an opinion about.
        internal sealed record Remote(
            string Name, string Ref, string Status, DateTime Seen, string Account, string? Color = null)
        {
            // The account is in the key, not just the record.
            //
            // Two accounts can hold identically-named sessions — the same person
            // naming things the same way twice is the normal case, not a corner
            // one — and without the account they would collapse onto one orb and
            // one chat panel, with messages going to whichever the dictionary
            // happened to hold. The prefix keeps them apart from local sessions
            // for the same reason OpenClaw's keys do.
            public string Key => "rc:" + Account + ":" + Name;

            // "running" is the one that matters, and it is the one the first
            // version of this missed.
            //
            // The peer list's vocabulary is **not** the same as `claude agents
            // --json`'s. That prints "busy" for a working local session, so this
            // was written against "busy" — and a remote session actually reports
            // `running`, which meant the orb sat still for the entire time a
            // machine elsewhere was working. Caught only by watching a real
            // relay transcript: idle → running → idle across four polls while
            // nothing on screen moved.
            //
            // Exactly the mistake this repo's fixture rule exists to prevent,
            // made by taking a vocabulary from the wrong source rather than from
            // the output being parsed. The other two are kept as tolerance, not
            // because either has been seen here.
            public bool Working =>
                Status.Contains("running", StringComparison.OrdinalIgnoreCase)
                || Status.Contains("busy", StringComparison.OrdinalIgnoreCase)
                || Status.Contains("working", StringComparison.OrdinalIgnoreCase);
        }

        // How often to re-ask while a relay is up. Slower than the 2s orb scan
        // on purpose: every poll is a real prompt into a real session, so this is
        // the one poll in the app with a per-tick cost — and now that cost is per
        // account, which is another reason not to make it eager.
        private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(20);

        // Faster while something is actually being waited on.
        //
        // 20 seconds is right for "is anything out there", and far too slow for
        // "did the thing I just asked for start yet" — a command can finish
        // inside one tick, so the orb never pulses and the panel never says
        // anything, which reads as nothing having happened. While at least one
        // remote session is working, or a send has just gone out, the poll drops
        // to this. It is still a real prompt per tick, which is why it is
        // temporary rather than the default.
        private static readonly TimeSpan PollEveryBusy = TimeSpan.FromSeconds(5);

        // When to go back to the slow cadence: a short grace period after the
        // last send, so a reply that takes a moment to start is still caught
        // promptly rather than falling into a 20-second gap.
        private static readonly TimeSpan BusyGrace = TimeSpan.FromSeconds(90);

        private static DateTime _lastSend = DateTime.MinValue;

        // Empty whenever the feature is off, which is what makes the scan's job
        // trivial — it never has to know why.
        public static IReadOnlyList<Remote> Snapshot() =>
            ClaudeBuddySettings.RemoteControlEnabled && RemoteControlBridge.IsSupported
                ? _snapshot
                : Array.Empty<Remote>();

        // One line for the settings window, covering however many relays there
        // are. Named per account once there is more than one, because "connected"
        // is not much use when the question is *which* of them.
        public static string StatusText
        {
            get
            {
                lock (Gate)
                {
                    if (Relays.Count == 0) return "off";

                    if (Relays.Count == 1)
                    {
                        var only = Relays.Values.First();
                        return Compose(only.State, only.Warning);
                    }

                    return string.Join("  ·  ",
                        Relays.Select(pair => $"{pair.Key}: {Compose(pair.Value.State, pair.Value.Warning)}"));
                }
            }
        }

        // Composed from two independent facts rather than one string, because
        // the first version wrote `warning ?? count` and so hid the count from
        // anyone who had a warning — which is everybody eventually, since the
        // login-expiry notice starts three days out. "Your login expires in 3
        // days" is useful; being unable to tell whether it also found anything
        // is not.
        private static string Compose(string state, string? warning)
        {
            if (warning is null) return state;
            return state is "off" or "starting" ? warning : $"{state} · {warning}";
        }

        // True once every relay that is up has completed a poll. Lets a caller
        // tell "up, and has looked" from "up, about to look" — a distinction the
        // status line cannot make, because it reads as connected the moment a
        // process starts, and the reason the first live test of this passed
        // while measuring nothing.
        public static bool HasPolled
        {
            get
            {
                lock (Gate)
                {
                    return Relays.Count > 0 && Relays.Values.All(r => r.Polled);
                }
            }
        }

        public static event Action<BridgeProtocol.InboundMessage>? MessageReceived;

        // Raised when a remote session starts or stops working, so an open chat
        // panel can say so. Carries the orb key rather than the bare name, since
        // the name alone no longer identifies a session across accounts.
        public static event Action<string, bool>? WorkingChanged;

        // Last known working state per orb key, so only transitions are raised.
        // Re-announcing "still working" every 20 seconds would fill a panel with
        // the same line.
        private static readonly Dictionary<string, bool> WorkingNow =
            new(StringComparer.OrdinalIgnoreCase);

        // What each remote session said its colour was, and which ones have
        // already been asked.
        //
        // In memory rather than in settings, deliberately. Persisting it would
        // save a message per launch but go stale the moment someone runs
        // /color on the other machine, and a wrong colour that never corrects
        // itself is worse than one extra message when Buddy starts. Asked
        // separately from answered so a session that never replies is not asked
        // again every poll.
        private static readonly Dictionary<string, string> KnownColors =
            new(StringComparer.OrdinalIgnoreCase);

        // The slash commands each far session says it can actually run. Empty
        // until it answers, and empty is the right starting point: offering a
        // command that cannot work over this channel is worse than offering none.
        private static readonly Dictionary<string, IReadOnlyList<SlashCommand>> KnownCommands =
            new(StringComparer.OrdinalIgnoreCase);

        // Raised when a far Buddy has answered about what it can mirror, so an
        // open panel can upgrade itself from a messaging channel to a live view
        // without being reopened.
        public static event Action<string>? MirrorChanged;

        public static IReadOnlyList<SlashCommand> CommandsFor(string account, string name)
        {
            // A roster answer wins over a CB-INFO one, and it is a better answer
            // in every way: it was read off the far machine's own disk by its
            // Buddy rather than recited by a model, it carries built-ins as well
            // as custom commands — which now genuinely run, because a mirrored
            // send is typed into that session's input line — and it cannot come
            // back mangled, because it arrived hashed.
            var mirror = MirrorFor(account, name);
            if (mirror?.Commands is { Count: > 0 } commands)
                return commands.Select(c => new SlashCommand(c, "")).ToList();

            lock (Gate)
            {
                return KnownCommands.TryGetValue(account + ":" + name, out var c)
                    ? c
                    : Array.Empty<SlashCommand>();
            }
        }

        // What the far Buddy said about one session, or null if none has.
        internal static MirrorProtocol.MirrorRosterEntry? MirrorFor(string account, string name) =>
            MirrorStateFor(account, name).Entry;

        internal static RemoteMirrorClient.MirrorState MirrorStateFor(string account, string name)
        {
            RemoteMirrorClient? client;
            lock (Gate) client = Relays.TryGetValue(account, out var relay) ? relay.Client : null;

            return client?.StateFor(name)
                   ?? new RemoteMirrorClient.MirrorState(
                       RemoteMirrorClient.MirrorAvailability.Unknown, null);
        }

        internal static RemoteMirrorClient? MirrorClientFor(string account)
        {
            lock (Gate) return Relays.TryGetValue(account, out var relay) ? relay.Client : null;
        }

        // Installs a mirror client without a relay behind it.
        //
        // A test seam, and the same kind as `Now` above: the alternative is a
        // test that starts a real Claude Code session to prove that a chat panel
        // renders a transcript, which would cost the person running it money and
        // still not be deterministic. The client handed in here is the real one,
        // wired to a fake wire — see MirrorRoundTripTests, which does the same
        // thing with a real server on the other end of it.
        //
        // Never called by the app: a relay always builds its own pair in
        // StartAsync.
        internal static void UseMirrorClientForTests(string account, RemoteMirrorClient? client)
        {
            lock (Gate)
            {
                if (!Relays.TryGetValue(account, out var relay))
                {
                    relay = new Relay { State = "test" };
                    Relays[account] = relay;
                }

                relay.Client = client;
            }

            if (client is not null) client.RosterUpdated += () => MirrorChanged?.Invoke(account);
        }

        // Puts the statics back, so one test cannot leave a mirror installed for
        // every test after it — the mistake bugfix/rc-tests-leak-remote-setting
        // already had to fix once for the Remote Control setting itself.
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                Relays.Clear();
                KnownColors.Clear();
                KnownCommands.Clear();
                InfoAsked.Clear();
                WorkingNow.Clear();
                _snapshot = Array.Empty<Remote>();
            }

            // Chat sessions subscribe to this in their constructor and are
            // deliberately never disposed, so without clearing it every session
            // any earlier test built stays subscribed for the rest of the run.
            MirrorChanged = null;

            Now = () => DateTime.UtcNow;
        }

        // When each session was last asked, and how often it has been.
        //
        // This was a HashSet, asked-once-ever, and once-ever turned out to mean
        // never for anyone whose first ask went unanswered. A relay that was
        // busy, a model that didn't call the tool, a session that started while
        // the far machine was asleep — any of those, and that session's
        // autocomplete stayed empty for as long as Buddy ran, with nothing on
        // screen to suggest a question had been asked at all. The person's
        // recourse was to restart the app, which is not a recourse.
        //
        // So it retries, and is bounded in both directions: not before the
        // interval, and never more than a few times, because the cost is a real
        // message into a real session and a session that has ignored three is
        // telling you something.
        private static readonly Dictionary<string, (DateTime At, int Count)> InfoAsked =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeSpan InfoRetryAfter = TimeSpan.FromMinutes(10);
        private const int InfoMaxAsks = 3;

        // Caller holds Gate.
        private static bool ShouldAsk(string key)
        {
            var now = Now();

            if (!InfoAsked.TryGetValue(key, out var asked))
            {
                InfoAsked[key] = (now, 1);
                return true;
            }

            if (asked.Count >= InfoMaxAsks) return false;
            if (now - asked.At < InfoRetryAfter) return false;

            InfoAsked[key] = (now, asked.Count + 1);
            return true;
        }

        // Injectable only so the retry rule above can be tested without a
        // ten-minute test. Never replaced in the app.
        internal static Func<DateTime> Now = () => DateTime.UtcNow;

        // Brings up a relay for every configured account that hasn't got one, and
        // marks them all as wanted either way. Every entry point that means "a
        // person is looking at remote sessions" calls this — the tray item,
        // opening a remote chat, sending to one.
        public static void EnsureStarted()
        {
            if (!ClaudeBuddySettings.RemoteControlEnabled) return;
            if (!RemoteControlBridge.IsSupported) return;

            var wanted = ClaudeBuddySettings.RemoteControlProfileDirs;
            var toStart = new List<string>();

            lock (Gate)
            {
                _lastUse = DateTime.UtcNow;

                foreach (var account in wanted)
                {
                    if (Relays.TryGetValue(account, out var existing)
                        && (existing.Bridge is not null || existing.Starting))
                    {
                        continue;
                    }

                    Relays[account] = new Relay { Starting = true, State = "starting" };
                    toStart.Add(account);
                }
            }

            SweepStaleScratch(wanted);

            foreach (var account in toStart) _ = StartAsync(account);
        }

        // Deletes scratch directories no configured account owns.
        //
        // Each relay's private TMPDIR collects a Node compile cache — about
        // 2.5MB a time — and a relay only removes its *own* on a clean stop. So
        // the ones left by a crash, by an earlier version that used a different
        // layout, by an account since un-ticked, or by a tagged test run are
        // never reclaimed by anything. Measured at 7.2MB across four directories
        // after a day of development, three of them belonging to nothing.
        //
        // Keyed on the names a relay would build for the accounts currently
        // selected, so a live relay's directory is never a candidate — including
        // a sibling account's, which matters now that several can run at once.
        // The one case this can catch mid-flight is an account un-ticked while
        // its relay is still winding down, which the next poll was about to
        // retire anyway.
        private static void SweepStaleScratch(IReadOnlyList<string> wanted)
        {
            try
            {
                var root = RemoteControlBridge.ScratchRoot;
                if (!Directory.Exists(root)) return;

                var keep = new HashSet<string>(
                    wanted.Select(a => new RemoteControlBridge(a).ScratchName),
                    StringComparer.Ordinal);

                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    if (keep.Contains(Path.GetFileName(dir))) continue;

                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
            catch
            {
                // Housekeeping. Never a reason to fail a start.
            }
        }

        // Keeps relays that are being used from being idled out from under
        // whoever is using them. Cheap enough to call on every send.
        public static void Touch()
        {
            lock (Gate) _lastUse = DateTime.UtcNow;
        }

        private static async Task StartAsync(string account)
        {
            var bridge = new RemoteControlBridge(account);
            void OnMessageFrom(BridgeProtocol.InboundMessage m) => OnMessage(account, m);
            bridge.MessageReceived += OnMessageFrom;

            bool ok;
            try
            {
                ok = await bridge.StartAsync().ConfigureAwait(false);
            }
            catch
            {
                ok = false;
            }

            if (!ok)
            {
                bridge.MessageReceived -= OnMessageFrom;
                bridge.Dispose();

                lock (Gate)
                {
                    // Kept rather than removed, so the settings window can say
                    // *which* account failed instead of silently showing one
                    // fewer than the user ticked.
                    if (Relays.TryGetValue(account, out var failed))
                    {
                        failed.Starting = false;

                        // The specific reason when there is one — an unfinished
                        // first-run setup is the common case and is fixable in
                        // ten seconds, but only if it says so.
                        failed.State = bridge.StartFailure ?? "failed to start";
                        failed.Warning = null;
                    }
                }

                return;
            }

            // Both mirror halves are built with the relay and live as long as
            // it does. Neither costs anything until something asks: the server
            // answers frames that arrive, the client sends none until there is a
            // remote orb to ask about.
            var client = new RemoteMirrorClient(
                account,
                new RemoteMirrorClient.Seams(bridge.SendFrameToAsync));

            var server = new RemoteMirrorServer(
                account,
                RemoteMirrorServer.RealSeams(
                    account,
                    bridge.SendFrameToAsync,
                    () => _localSessions?.Invoke()
                          ?? Array.Empty<(string, SessionStatus)>()));

            client.RosterUpdated += () => Dispatcher.UIThread.Post(() => MirrorChanged?.Invoke(account));

            lock (Gate)
            {
                var relay = Relays.TryGetValue(account, out var found) ? found : new Relay();
                relay.Bridge = bridge;
                relay.Handler = OnMessageFrom;
                relay.Starting = false;
                relay.Polled = false;
                relay.State = "connected";
                relay.Warning = bridge.Warning;
                relay.Client = client;
                relay.Server = server;
                Relays[account] = relay;
            }

            // Posted, not awaited. The timer has to be created on the UI thread
            // because DispatcherTimer belongs to it, but waiting for that to
            // happen would put the first poll behind the dispatcher being free —
            // and awaiting InvokeAsync where no dispatcher is pumping blocks
            // forever, which is exactly what it did: the relay reported
            // "connected" and then never asked for a peer list, because startup
            // stopped one line short of doing so.
            //
            // One timer for all relays, not one each: they are polled together,
            // and N timers on the same interval would just be N wakeups.
            Dispatcher.UIThread.Post(EnsureTimer);

            await PollAsync(account).ConfigureAwait(false);
        }

        private static void EnsureTimer()
        {
            if (_poll is not null) return;

            _poll = new DispatcherTimer { Interval = PollEvery };
            _poll.Tick += (_, _) => _ = TickAsync();
            _poll.Start();

            EnsureMirrorTimer();
        }

        // The one poll in this class that is free.
        //
        // Everything else here costs a model turn, which is why the ordinary
        // cadence is twenty seconds and drops to five only under duress. This
        // one calls Pump() — reading bytes off the relay's own transcript file
        // on this disk — and ticks the two mirror halves, which read local files
        // and expire subscriptions. No prompt is pasted, so nothing is spent,
        // and a mirror frame that has landed is noticed in a second and a half
        // rather than waiting out a poll. Without it a transfer of thirty
        // pieces, each needing the reply to be seen before the next is asked
        // for, would take ten minutes of walking-pace polling.
        //
        // Only runs while something is actually mirroring: with no panel open
        // and nobody asking, both halves report not busy and this does nothing
        // but check a flag.
        private static DispatcherTimer? _mirrorPump;

        private static readonly TimeSpan MirrorPumpEvery = TimeSpan.FromMilliseconds(1500);

        private static void EnsureMirrorTimer()
        {
            if (_mirrorPump is not null) return;

            _mirrorPump = new DispatcherTimer { Interval = MirrorPumpEvery };
            _mirrorPump.Tick += (_, _) => _ = MirrorTickAsync();
            _mirrorPump.Start();
        }

        private static bool _mirrorTicking;

        private static async Task MirrorTickAsync()
        {
            // Ticks can overlap when a file read is slow, and two pumps racing
            // on one relay would read the same bytes twice.
            if (_mirrorTicking) return;
            _mirrorTicking = true;

            try
            {
                List<Relay> live;
                lock (Gate) live = Relays.Values.Where(r => r.Bridge is not null).ToList();

                foreach (var relay in live)
                {
                    var busy = relay.Server?.Busy == true || relay.Client?.Busy == true;
                    if (!busy) continue;

                    try { relay.Bridge!.Pump(); } catch { }

                    if (relay.Server is not null)
                    {
                        try { await relay.Server.TickAsync().ConfigureAwait(true); } catch { }
                    }

                    if (relay.Client is not null)
                    {
                        try { await relay.Client.TickAsync().ConfigureAwait(true); } catch { }
                    }
                }
            }
            finally
            {
                _mirrorTicking = false;
            }
        }

        // One round: retire the relays nobody wants, then re-ask the rest.
        private static async Task TickAsync()
        {
            // Idle check first, so relays nobody wants aren't kept alive by the
            // very poll that was about to retire them.
            if (IdleExpired())
            {
                StopAll("idle");
                return;
            }

            if (!ClaudeBuddySettings.RemoteControlEnabled)
            {
                StopAll("off");
                return;
            }

            // An account un-ticked in Settings while its relay was up. Retired
            // here rather than by the settings window, so there is one place
            // that decides which relays should exist.
            var wanted = ClaudeBuddySettings.RemoteControlProfileDirs;
            List<string> unwanted;
            lock (Gate)
            {
                unwanted = Relays.Keys.Where(a => !wanted.Contains(a, StringComparer.Ordinal)).ToList();
            }

            foreach (var account in unwanted) Stop(account, "no longer selected");

            List<string> live;
            lock (Gate) live = Relays.Where(p => p.Value.Bridge is not null).Select(p => p.Key).ToList();

            foreach (var account in live) await PollAsync(account).ConfigureAwait(false);
        }

        // Re-asks one account's relay for its peers and republishes.
        private static async Task PollAsync(string account)
        {
            RemoteControlBridge? bridge;
            lock (Gate)
            {
                bridge = Relays.TryGetValue(account, out var relay) ? relay.Bridge : null;
            }

            if (bridge is null) return;

            // Also drains the transcript, which is how an inbound reply that
            // arrived while nobody was asking anything gets noticed at all.
            bridge.Pump();

            IReadOnlyList<BridgeProtocol.RemoteAgent>? agents;
            try
            {
                agents = await bridge.ListAgentsAsync().ConfigureAwait(false);
            }
            catch
            {
                agents = null;
            }

            if (agents is null)
            {
                // A relay that has stopped answering is worse than none: it
                // would publish a peer list frozen at whatever was true when it
                // died. Retiring it means those orbs go away, which is at least
                // honest, and the next EnsureStarted brings a fresh one up.
                if (!bridge.IsRunning) Stop(account, "relay stopped");
                else lock (Gate) { if (Relays.TryGetValue(account, out var r)) r.State = "not answering"; }

                Republish();
                return;
            }

            var now = DateTime.UtcNow;
            var remotes = agents
                .Where(a => a.IsWorthAnOrb)
                .Select(a =>
                {
                    var key = account + ":" + a.Name;
                    string? colour;
                    lock (Gate) KnownColors.TryGetValue(key, out colour);
                    return new Remote(a.Name, a.Ref, a.Status, now, account, colour);
                })
                .ToList();

            lock (Gate)
            {
                if (Relays.TryGetValue(account, out var relay))
                {
                    relay.Sessions = remotes;
                    relay.Polled = true;
                    relay.Warning = bridge.Warning;
                    relay.State = remotes.Count switch
                    {
                        0 => "no remote sessions found",
                        1 => "1 remote session",
                        _ => $"{remotes.Count} remote sessions"
                    };
                }
            }

            Republish();
            RaiseWorkingTransitions(remotes);
            RetuneTimer();

            // Before the colour question below, because it can answer it for
            // free. A far Buddy's roster carries the colour and the command list
            // read off its own disk, so a session it covers never needs the
            // CB-INFO round trip at all.
            //
            // The *unfiltered* peer list goes in on purpose: a far Buddy's relay
            // is deliberately not worth an orb, so the filtered list is the one
            // place it has been removed from.
            RemoteMirrorClient? client;
            lock (Gate) client = Relays.TryGetValue(account, out var found) ? found.Client : null;

            if (client is not null)
            {
                try
                {
                    await client.DiscoverAsync(agents, remotes.Select(r => r.Name).ToList())
                        .ConfigureAwait(false);
                }
                catch
                {
                    // A handshake that failed leaves every session on the
                    // messaging channel, which is where they already were.
                }
            }

            await AskForMissingInfoAsync(account, remotes, bridge).ConfigureAwait(false);
        }

        // Asks each newly seen session what colour it is, once.
        //
        // This costs a message per remote session — there is no cheaper route,
        // since a peer row carries neither the transcript nor the cwd a colour
        // is derived from (see BridgeProtocol's own note). Bounded by asking
        // only once per session per run, and only for sessions already deemed
        // worth an orb, so nothing is spent on relays or dead registrations.
        //
        // Sequential rather than fanned out: the relay serializes requests
        // anyway, and firing five at once would just queue five deep behind one
        // input line.
        private static async Task AskForMissingInfoAsync(
            string account, IReadOnlyList<Remote> remotes, RemoteControlBridge bridge)
        {
            foreach (var remote in remotes)
            {
                var key = account + ":" + remote.Name;

                // A far Buddy has already said, off its own disk. Asking its
                // model the same question would cost a turn to get a worse
                // answer.
                if (MirrorFor(account, remote.Name) is not null) continue;

                lock (Gate)
                {
                    if (KnownColors.ContainsKey(key)) continue;
                    if (!ShouldAsk(key)) continue;
                }

                try
                {
                    await bridge.AskCapabilitiesAsync(remote.Name).ConfigureAwait(false);
                }
                catch
                {
                    // Cosmetic. A colour that never arrives leaves the derived
                    // one in place, which is what every orb had before this.
                }
            }
        }

        // Picks the cadence from what is actually happening. Called after every
        // poll rather than on a schedule of its own, because the thing it reacts
        // to — a session going busy — is exactly what a poll discovers.
        private static void RetuneTimer()
        {
            var wanted = ShouldPollFast() ? PollEveryBusy : PollEvery;

            Dispatcher.UIThread.Post(() =>
            {
                if (_poll is null || _poll.Interval == wanted) return;
                _poll.Interval = wanted;
            });
        }

        private static bool ShouldPollFast()
        {
            if (_snapshot.Any(r => r.Working)) return true;

            DateTime lastSend;
            lock (Gate) lastSend = _lastSend;

            return DateTime.UtcNow - lastSend < BusyGrace;
        }

        // Every relay's sessions, flattened into the one list the scan reads.
        private static void Republish()
        {
            lock (Gate)
            {
                _snapshot = Relays.Values.SelectMany(r => r.Sessions).ToList();
            }
        }

        private static void RaiseWorkingTransitions(IEnumerable<Remote> remotes)
        {
            foreach (var remote in remotes)
            {
                bool was;
                lock (Gate) WorkingNow.TryGetValue(remote.Key, out was);

                if (was == remote.Working) continue;

                lock (Gate) WorkingNow[remote.Key] = remote.Working;

                var key = remote.Key;
                var working = remote.Working;
                Dispatcher.UIThread.Post(() => WorkingChanged?.Invoke(key, working));
            }
        }

        private static bool IdleExpired()
        {
            var minutes = ClaudeBuddySettings.RemoteControlIdleMinutes;
            if (minutes <= ClaudeBuddySettings.RemoteControlIdleNever) return false;

            DateTime last;
            lock (Gate) last = _lastUse;

            return DateTime.UtcNow - last > TimeSpan.FromMinutes(minutes);
        }

        // Hands text to a session on another machine, through the relay for the
        // account that session belongs to. Null when there is no relay to send
        // it through, which the caller shows as a system line rather than
        // swallowing.
        public static async Task<string?> SendToAsync(string account, string remoteName, string text)
        {
            EnsureStarted();

            RemoteControlBridge? bridge;
            lock (Gate)
            {
                bridge = Relays.TryGetValue(account, out var relay) ? relay.Bridge : null;
            }

            if (bridge is null) return null;

            Touch();
            lock (Gate) _lastSend = DateTime.UtcNow;

            var id = await bridge.SendToAsync(remoteName, text).ConfigureAwait(false);

            // Straight back to the relay rather than waiting up to a full tick:
            // the moment after a send is exactly when whether it started is
            // worth knowing, and it is also when someone is watching.
            if (id is not null) _ = PollAsync(account);

            return id;
        }

        public static void Stop(string account, string why = "off")
        {
            RemoteControlBridge? bridge = null;
            Action<BridgeProtocol.InboundMessage>? handler = null;

            lock (Gate)
            {
                if (Relays.TryGetValue(account, out var relay))
                {
                    bridge = relay.Bridge;
                    handler = relay.Handler;

                    // Both mirror halves go with the wire they talked over.
                    // Nothing to unsubscribe from the far side: its watches
                    // carry a TTL precisely so a relay that vanished without
                    // saying goodbye stops being served.
                    relay.Client = null;
                    relay.Server = null;

                    Relays.Remove(account);
                }
            }

            if (bridge is not null)
            {
                if (handler is not null) bridge.MessageReceived -= handler;
                bridge.Dispose();
            }

            Republish();
            StopTimerIfIdle();
        }

        public static void StopAll(string why = "off")
        {
            List<string> accounts;
            lock (Gate) accounts = Relays.Keys.ToList();

            foreach (var account in accounts) Stop(account, why);

            lock (Gate) WorkingNow.Clear();
        }

        private static void StopTimerIfIdle()
        {
            bool any;
            lock (Gate) any = Relays.Count > 0;
            if (any) return;

            // Reachable from a poll tick, which is already on the UI thread, and
            // from a settings change, which may not be.
            if (Dispatcher.UIThread.CheckAccess()) StopTimer();
            else Dispatcher.UIThread.Post(StopTimer);
        }

        private static void StopTimer()
        {
            _poll?.Stop();
            _poll = null;

            _mirrorPump?.Stop();
            _mirrorPump = null;
        }

        // Settings changed under us. Unlike OpenClawSessions.Restart this only
        // ever tears down: bringing a relay back has to be asked for, because
        // starting one costs the user something.
        public static void Restart()
        {
            if (!ClaudeBuddySettings.RemoteControlEnabled || !RemoteControlBridge.IsSupported)
            {
                StopAll("off");
                return;
            }

            bool any;
            lock (Gate) any = Relays.Count > 0;
            if (any) StopAll("restarting");
        }

        private static void OnMessage(string account, BridgeProtocol.InboundMessage message)
        {
            // A mirror frame is plumbing between two Buddies and must never
            // reach a person.
            //
            // Intercepted here, at the same point and for the same reason
            // CB-INFO is below: this is the last place an inbound message is
            // still just data, and everything past the Post at the bottom of
            // this method is on its way to a chat bubble. A frame that got past
            // would be a screenful of base64 in someone's conversation.
            //
            // Swallowed whether or not it parses, again like CB-INFO — a
            // malformed frame is still not something anyone asked to read.
            if (MirrorProtocol.IsFrame(message.Body))
            {
                var frame = MirrorProtocol.TryParseFrame(message.Body);
                if (frame is null) return;

                RemoteMirrorServer? server;
                RemoteMirrorClient? client;

                lock (Gate)
                {
                    var relay = Relays.TryGetValue(account, out var found) ? found : null;
                    server = relay?.Server;
                    client = relay?.Client;
                }

                // Which half answers is decided by the frame, not by who sent
                // it: a request is for the server here, a reply is for the
                // client here, and one relay carries both directions at once
                // because both machines are asking each other.
                switch (frame.Type)
                {
                    case MirrorProtocol.Chunk:
                    case MirrorProtocol.Ok:
                    case MirrorProtocol.Err:
                        if (client is not null) _ = client.OnFrameAsync(message.FromName, frame);
                        break;

                    default:
                        if (server is not null) _ = server.HandleAsync(message.FromName, frame);
                        break;
                }

                // Keeps a relay serving a mirror from being idled out from under
                // it — the same reason a send calls this.
                Touch();
                return;
            }

            // A colour answer is not a message to the person reading the panel —
            // they never asked the question. Swallowed whether or not it parses,
            // because showing someone a fumbled answer to a question they did
            // not ask is worse than showing nothing.
            if (BridgeProtocol.IsInfoReply(message.Body))
            {
                var key = account + ":" + message.FromName;
                var colour = BridgeProtocol.ParseColorReply(message.Body);
                var commands = BridgeProtocol.ParseCommandsReply(message.Body);

                lock (Gate)
                {
                    if (colour is not null) KnownColors[key] = colour;
                    if (commands.Count > 0) KnownCommands[key] = commands;
                }

                // Republished so the next scan picks the colour up without
                // waiting for another poll to rebuild the list.
                if (colour is not null) RepublishWithColors();

                return;
            }

            Dispatcher.UIThread.Post(() => MessageReceived?.Invoke(
                message with { Account = account }));
        }

        // Re-stamps the published snapshot with whatever colours are now known.
        private static void RepublishWithColors()
        {
            lock (Gate)
            {
                foreach (var relay in Relays)
                {
                    relay.Value.Sessions = relay.Value.Sessions
                        .Select(r =>
                        {
                            KnownColors.TryGetValue(relay.Key + ":" + r.Name, out var colour);
                            return colour is null ? r : r with { Color = colour };
                        })
                        .ToList();
                }

                _snapshot = Relays.Values.SelectMany(r => r.Sessions).ToList();
            }
        }
    }
}
