using System.Diagnostics.CodeAnalysis;
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
        }

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
        //
        // A test seam, matching OpenClawSessions.SetSnapshotForTests and there for
        // the same reason: the only thing that publishes a snapshot in production
        // is the poll loop, which drives a live relay and is excluded. Without
        // this, the scan entries built from a remote session are unreachable for
        // a reason that has nothing to do with the code being hard to test.
        internal static void SetSnapshotForTests(IReadOnlyList<Remote> remotes)
        {
            _snapshot = remotes;
        }

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

        public static IReadOnlyList<SlashCommand> CommandsFor(string account, string name)
        {
            lock (Gate)
            {
                return KnownCommands.TryGetValue(account + ":" + name, out var c)
                    ? c
                    : Array.Empty<SlashCommand>();
            }
        }

        private static readonly HashSet<string> InfoAsked =
            new(StringComparer.OrdinalIgnoreCase);

        // Brings up a relay for every configured account that hasn't got one, and
        // marks them all as wanted either way. Every entry point that means "a
        // person is looking at remote sessions" calls this — the tray item,
        // opening a remote chat, sending to one.
        // Excluded from coverage: starts a relay, which launches a real Claude
        // Code session in tmux.
        [ExcludeFromCodeCoverage]
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
        // Excluded from coverage: deletes real scratch directories from disk.
        [ExcludeFromCodeCoverage]
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

        // Excluded from coverage: starts a relay bridge, which spends quota on a
        // real session.
        [ExcludeFromCodeCoverage]
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

            lock (Gate)
            {
                var relay = Relays.TryGetValue(account, out var found) ? found : new Relay();
                relay.Bridge = bridge;
                relay.Handler = OnMessageFrom;
                relay.Starting = false;
                relay.Polled = false;
                relay.State = "connected";
                relay.Warning = bridge.Warning;
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

        // Excluded from coverage: creates the Avalonia poll timer the relay runs
        // on.
        [ExcludeFromCodeCoverage]
        private static void EnsureTimer()
        {
            if (_poll is not null) return;

            _poll = new DispatcherTimer { Interval = PollEvery };
            _poll.Tick += (_, _) => _ = TickAsync();
            _poll.Start();
        }

        // One round: retire the relays nobody wants, then re-ask the rest.
        // Excluded from coverage: the poll loop, driving live relays.
        [ExcludeFromCodeCoverage]
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
        // Excluded from coverage: asks a live relay for its agent list.
        [ExcludeFromCodeCoverage]
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
        // Excluded from coverage: sends prompts into a live relay session.
        [ExcludeFromCodeCoverage]
        private static async Task AskForMissingInfoAsync(
            string account, IReadOnlyList<Remote> remotes, RemoteControlBridge bridge)
        {
            foreach (var remote in remotes)
            {
                var key = account + ":" + remote.Name;

                lock (Gate)
                {
                    if (KnownColors.ContainsKey(key)) continue;
                    if (!InfoAsked.Add(key)) continue;
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
        // Excluded from coverage: changes the interval of the Avalonia poll timer.
        [ExcludeFromCodeCoverage]
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
        // Excluded from coverage: sends a message through a live relay.
        [ExcludeFromCodeCoverage]
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

        // Excluded from coverage: kills a relay tmux session.
        [ExcludeFromCodeCoverage]
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

        // Excluded from coverage: kills every relay tmux session.
        [ExcludeFromCodeCoverage]
        public static void StopAll(string why = "off")
        {
            List<string> accounts;
            lock (Gate) accounts = Relays.Keys.ToList();

            foreach (var account in accounts) Stop(account, why);

            lock (Gate) WorkingNow.Clear();
        }

        // Excluded from coverage: stops the Avalonia poll timer.
        [ExcludeFromCodeCoverage]
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

        // Excluded from coverage: stops the Avalonia poll timer.
        [ExcludeFromCodeCoverage]
        private static void StopTimer()
        {
            _poll?.Stop();
            _poll = null;
        }

        // Settings changed under us. Unlike OpenClawSessions.Restart this only
        // ever tears down: bringing a relay back has to be asked for, because
        // starting one costs the user something.
        // Excluded from coverage: stops every relay and starts them again.
        [ExcludeFromCodeCoverage]
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
