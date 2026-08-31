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

        // Orb rows that came off the direct link rather than a relay poll.
        //
        // A separate bucket rather than a fake Relay entry, because the two are
        // filled by different things at different times and a Relay carries a
        // bridge, a state string and a warning that a socket has no answer for.
        // Both are unioned into _snapshot by Republish, which is the only place
        // that has to know there are two.
        private static IReadOnlyList<Remote> _peerRows = Array.Empty<Remote>();

        // One entry per account with a relay up or coming up. Keyed by profile
        // dir, which is also what makes "already starting" answerable without a
        // second flag per account.
        private static readonly Dictionary<string, Relay> Relays = new(StringComparer.Ordinal);

        private static DispatcherTimer? _poll;
        private static DateTime _lastUse = DateTime.MinValue;

        // What drives a relay while there is no dispatcher to drive it from.
        // See ServePump for the whole of why, and ServeTickAsync below for what
        // one round does. Null once the UI is up: EnsureTimer disposes it,
        // because from that point the two DispatcherTimers are doing strictly
        // more than this can.
        private static ServePump? _servePump;

        // When a DispatcherTimer last actually fired.
        //
        // **The difference between "a dispatcher exists" and "a dispatcher is
        // running", which is the whole of CB-61.** EnsureTimer is delivered by
        // Dispatcher.UIThread.Post, so reaching it proves the loop ran once —
        // and StopServePump used to take that as proof it would keep running,
        // and destroyed the stand-in on the strength of it. A DispatcherTimer
        // only fires while that loop is pumping. On a headless machine it can
        // deliver the Post at startup and then go quiet, at which point both
        // timers stop firing and the thing that covered for exactly this has
        // already been disposed.
        //
        // Measured on job-hunter-mac-mini: Buddy alive at 0% CPU, its relay
        // receiving well-formed HELLOs from a correctly-named peer, and two
        // ListAgents in seven minutes — the first of which was StartAsync's own
        // direct call, not a tick. Nothing drained the transcript, so nothing
        // was ever routed to the mirror server, so it answered none of them.
        // From the far end: a panel that says the other machine did not answer.
        private static DateTime _dispatcherTickedAt;

        // How long the dispatcher may be silent before the stand-in takes over
        // again. Comfortably longer than MirrorPumpEvery (1.5s), so a healthy
        // machine never double-pumps, and short enough that a machine which has
        // gone quiet is covered within seconds rather than left dark.
        internal static readonly TimeSpan DispatcherSilenceBeforeStandIn =
            TimeSpan.FromSeconds(15);

        // Whether the dispatcher has ticked recently enough to be trusted with
        // the work. Pure so the handover rule is testable without a dispatcher —
        // which is the one thing a test of this cannot conjure.
        internal static bool DispatcherLooksAlive(DateTime now, DateTime lastTick) =>
            lastTick != default && now - lastTick < DispatcherSilenceBeforeStandIn;

        // Called from both DispatcherTimers. The proof is the firing, not the
        // existence.
        private static void DispatcherTicked()
        {
            lock (Gate) _dispatcherTickedAt = DateTime.UtcNow;
        }

        // Fast, because it is free — the same reasoning as MirrorPumpEvery next
        // door, and a shade slower because this one runs when the mirror halves
        // are idle as well as when they are not.
        private static readonly TimeSpan ServePumpEvery = TimeSpan.FromSeconds(2);

        // What is left of a relay, which is its published sessions and the
        // mirror halves that once talked over it.
        //
        // **The bridge itself is gone and this is the shell it lived in.** The
        // table is still filled by the test seams — SetRelayForTests and
        // UseMirrorClientForTests — because the rules that read it are the same
        // rules whichever transport supplied the rows, and asserting them
        // without inventing a second table is worth the vestigial type. Nothing
        // in the app writes it any more.
        private sealed class Relay
        {
            public bool Starting;
            public string State = "starting";
            public string? Warning;

            // Why this relay is not answering, when the pane says so. Separate
            // from Warning because they are different kinds of fact: a warning
            // is something that will bite later, a stall is something that has
            // already stopped the machine serving and names the keypress that
            // clears it.
            public string? Stall;
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




        // Named rather than written as a lambda at the one place a relay is
        // built, because the fallback is a real answer with a real consequence:
        // it is what a far machine's roster request is answered with before
        // SessionManager has started. That used to be "no sessions", which was
        // wrong in exactly the case the serve-on-launch setting exists for — a
        // headless machine whose screen never unlocks never starts
        // SessionManager at all (CB-24), so its relay answered every HELLO with
        // an empty roster and the far panel silently stayed a messaging
        // channel. Now the answer comes from the same scan rules, composed
        // without the UI — see SessionManager.HeadlessSnapshot.
        //
        // A relay is only built with a live bridge behind it, so this is also
        // the only way to ask what the provider currently says.
        internal static IReadOnlyList<(string SessionId, SessionStatus Status)> LocalSessions()
        {
            lock (Gate)
            {
                if (_localSessionsAt is { } at && Now() - at < LocalSessionsFor)
                    return _localSessionsWere;
            }

            var fresh = HeadlessFallback();

            lock (Gate)
            {
                _localSessionsWere = fresh;
                _localSessionsAt = Now();
            }

            return fresh;
        }

        // **This used to prefer the orb list and fall back to a scan, and the
        // preference was the bug.** The orb list is what is on screen, and what
        // is on screen has had the user's orb-lifetime preference applied to it
        // — so an idle session that had stopped being drawn was reported to
        // every other machine as not existing. On a headless Mac it was worse
        // still: the orb list is filled by a scan on a dispatcher that never
        // pumps, so the list was not merely filtered but empty.
        //
        // Serving is a question of fact and the disk is what knows the answer,
        // so the disk is asked. See SessionManager.HeadlessSnapshot, which is
        // told to ignore orb lifetime for exactly this call.
        //
        // Memoised for a moment because a peer asks every ten seconds and the
        // scan reads a directory and a job listing. Two seconds is short enough
        // that a session starting is noticed at once and long enough that
        // several peers asking together cost one scan.
        private static readonly TimeSpan LocalSessionsFor = TimeSpan.FromSeconds(2);

        private static IReadOnlyList<(string SessionId, SessionStatus Status)> _localSessionsWere =
            Array.Empty<(string, SessionStatus)>();

        private static DateTime? _localSessionsAt;

        // Swappable because the real scan reads this machine's actual status
        // directory, which a unit test has no business depending on.
        internal static Func<IReadOnlyList<(string SessionId, SessionStatus Status)>>
            HeadlessFallback = () => SessionManager.HeadlessSnapshot(honourOrbLifetime: false);

        // Also clears the memo, or one test's answer is still being served two
        // seconds into the next one.
        internal static void ForgetLocalSessionsForTests()
        {
            lock (Gate)
            {
                _localSessionsWere = Array.Empty<(string, SessionStatus)>();
                _localSessionsAt = null;
            }
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
            Visible(_snapshot, ClaudeBuddySettings.PeerLinkEnabled);

        // Whether remote orbs are shown at all.
        //
        // **One switch again, and a different one.** This asked about the relay
        // for as long as a relay was the only way a remote row could exist; then
        // briefly about both; and now about the link alone, because the relay is
        // gone and the link is the only thing that fills the list.
        //
        // Worth keeping as a function rather than collapsing into the property:
        // it is the reason the scan never has to know *why* the list is empty,
        // and the arms have been wrong once already — the two-transport version
        // shipped drawing nothing on exactly the machine most likely to have
        // rows, because it insisted on a switch the user had been told to turn
        // off.
        internal static IReadOnlyList<Remote> Visible(IReadOnlyList<Remote> rows, bool linkOn) =>
            linkOn ? rows : Array.Empty<Remote>();

        // Lets the link raise the event a panel listens on. The event itself
        // cannot be raised from outside the class it is declared in, and the
        // link deliberately lives outside — see PeerMirrorHost's note on why it
        // is not account-scoped.
        [ExcludeFromCodeCoverage]
        internal static void RaiseMirrorChanged(string account) => MirrorChanged?.Invoke(account);

        // ---- test seams ----------------------------------------------------

        // StatusText and HasPolled are pure reads of the relay table, but the
        // only way to fill that table for real is to start a bridge subprocess
        // and talk to a live account — which is what RemoteControlBridgeLiveTests
        // does, deliberately, and what a unit test must not.
        //
        // Same shape as OpenClawSessions.SetSnapshotForTests: seed the state, ask
        // the question, put it back. Nothing here is reachable outside the four
        // test assemblies InternalsVisibleTo names.
        internal static void SetRelayForTests(
            string account, string state, string? warning = null, bool polled = false,
            IReadOnlyList<Remote>? sessions = null, string? stall = null)
        {
            lock (Gate)
            {
                if (!Relays.TryGetValue(account, out var relay))
                {
                    relay = new Relay();
                    Relays[account] = relay;
                }

                relay.State = state;
                relay.Warning = warning;
                relay.Stall = stall;
                relay.Polled = polled;
                relay.Sessions = sessions ?? Array.Empty<Remote>();
            }
        }

        // The peer list, as the orb scan wants it: only the sessions worth an orb,
        // each stamped with the account it was seen through and whatever colour
        // that session has told us about.
        //
        // Pulled out of the poll, which needs a live relay. The filter is the part
        // that matters — a peer list carries entries that are not sessions anyone
        // would want an orb for, and showing them would fill the screen with orbs
        // nobody can click usefully.
        internal static List<Remote> RemotesFrom(
            IEnumerable<BridgeProtocol.RemoteAgent> agents, string account, DateTime now)
        {
            var remotes = new List<Remote>();

            foreach (var agent in agents)
            {
                if (!agent.IsWorthAnOrb) continue;

                var key = account + ":" + agent.Name;
                string? colour;
                lock (Gate) KnownColors.TryGetValue(key, out colour);

                remotes.Add(new Remote(agent.Name, agent.Ref, agent.Status, now, account, colour));
            }

            return remotes;
        }

        internal static void ClearRelaysForTests()
        {
            lock (Gate) Relays.Clear();
        }

        internal static void SetLastUseForTests(DateTime when)
        {
            lock (Gate) _lastUse = when;
        }

        // The working-transition memory. Cleared rather than set, because what a
        // test needs is a known-empty starting point: the first observation of a
        // session is a transition from false by definition, and leaving another
        // test's entries behind turns that into a coin flip.
        internal static void ClearWorkingMemoryForTests()
        {
            lock (Gate) WorkingNow.Clear();
        }

        internal static IReadOnlyList<Remote> SnapshotForTests
        {
            get { lock (Gate) return _snapshot; }
        }

        internal static void SetLastSendForTests(DateTime when)
        {
            lock (Gate) _lastSend = when;
        }

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
                        return Compose(only.State, only.Warning, only.Stall);
                    }

                    return string.Join("  ·  ",
                        Relays.Select(pair =>
                            $"{pair.Key}: {Compose(pair.Value.State, pair.Value.Warning, pair.Value.Stall)}"));
                }
            }
        }

        // Composed from two independent facts rather than one string, because
        // the first version wrote `warning ?? count` and so hid the count from
        // anyone who had a warning — which is everybody eventually, since the
        // login-expiry notice starts three days out. "Your login expires in 3
        // days" is useful; being unable to tell whether it also found anything
        // is not.
        // The stall joins on the same terms and for the same reason. It is the
        // most actionable of the three — it names a keypress — but it does not
        // replace the count either: "3 remote sessions" and "none of them can be
        // reached right now" are both true and a reader needs both to know what
        // they have lost.
        internal static string Compose(string state, string? warning, string? stall = null)
        {
            var text = Join(state, stall);
            return Join(text, warning);
        }

        private static string Join(string state, string? extra)
        {
            if (extra is null) return state;
            return state is "off" or "starting" ? extra : $"{state} · {extra}";
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

        // The colour a remote session answered with, if it has. Reads the same
        // table CommandsFor does and exists for the same reason: the answer
        // arrives as an ordinary inbound message and is swallowed, so without a
        // reader there is no way to tell it landed.
        internal static string? ColourFor(string account, string name)
        {
            lock (Gate) return KnownColors.GetValueOrDefault(account + ":" + name);
        }

        internal static void ForgetAnswersForTests()
        {
            lock (Gate)
            {
                KnownColors.Clear();
                KnownCommands.Clear();
            }
        }
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

        // Why an account's relay is not answering, for a panel that is waiting on
        // it. Null when nothing says it is stuck, which includes "we have not
        // looked yet" — an absent answer must not read as a clean bill of
        // health.
        internal static string? StallFor(string account)
        {
            lock (Gate) return Relays.TryGetValue(account, out var relay) ? relay.Stall : null;
        }

        internal static RemoteMirrorClient? MirrorClientFor(string account)
        {
            // The direct link first, when there is one.
            //
            // **This is the line that moves the panel off the relay.** Both
            // clients are the same class doing the same work; what differs is
            // what carries their frames — a socket, or a model retyping base64
            // at 222 to 247 seconds a chunk.
            //
            // The peer client is not account-scoped, because a socket is not:
            // this machine talks to that machine whatever either is signed into.
            // Asking for one by account and getting the machine-wide one is
            // therefore correct rather than sloppy, and is why the parameter is
            // ignored on this path.
            //
            // Falls through while the link is off or has no client yet, so a
            // user who has not turned it on keeps exactly the behaviour they
            // had. That fallback goes when the relay does.
            RemoteMirrorClient? relayed;
            lock (Gate) relayed = Relays.TryGetValue(account, out var relay) ? relay.Client : null;

            return Prefer(PeerSessions.Host?.Client, relayed);
        }

        // Which of the two clients a panel gets, given both.
        //
        // Pure so the precedence is a rule with a test rather than an `??` in
        // the middle of a lock — this one line decides whether a transcript
        // arrives in milliseconds or in four minutes, and it should be as hard
        // to change by accident as that implies.
        internal static RemoteMirrorClient? Prefer(
            RemoteMirrorClient? direct, RemoteMirrorClient? relayed) => direct ?? relayed;

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
                _peerRows = Array.Empty<Remote>();

                // The memoised local-session scan, or one test's answer is
                // still being served two seconds into the next one.
                _localSessionsWere = Array.Empty<(string, SessionStatus)>();
                _localSessionsAt = null;
            }

            // Chat sessions subscribe to this in their constructor and are
            // deliberately never disposed, so without clearing it every session
            // any earlier test built stays subscribed for the rest of the run.
            MirrorChanged = null;
            SendOverrideForTests = null;

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

        // Caller holds Gate. Internal so the retry rule can be tested against an
        // injected clock rather than by waiting ten minutes.
        internal static bool ShouldAsk(string key)
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
        // Whether starting a real relay is forbidden in this process.
        //
        // A test suite must never start one: it is a live Claude Code session in
        // a tmux pane, on the developer's own account, spending quota and
        // holding a machine-wide relay name that the installed app also wants.
        // Three suites already say so in comments and route around the calls
        // that would do it — and CB-42 showed a comment is not a mechanism.
        // `RemoteScanTests` opens a remote chat panel, which counts as asking
        // for the bridge (SessionManager.RemoteChatFor), and had been calling
        // EnsureStarted all along. It was harmless only because the relay could
        // not start: the default account was being launched into a config
        // context nobody had onboarded, so it died in a first-run wizard every
        // time. Fixing that turned a dormant call into a real relay on every
        // developer machine — and left CI green, because a GitHub runner has no
        // `claude` installed to start.
        //
        // An environment variable rather than a property a test sets, so it is
        // in place before any code in the assembly runs — the same shape and
        // the same reason as CLAUDE_BUDDY_SETTINGS_DIR, which each suite's
        // [ModuleInitializer] sets for exactly this class of accident. Read on
        // every call rather than cached, so the opt-in live-bridge tests can
        // clear it for their own duration.
        internal static bool StartsBlocked =>
            Environment.GetEnvironmentVariable("CLAUDE_BUDDY_NO_RELAY") == "1";

        // Whether the stand-in is still needed, as a question rather than a
        // field, so the handover can be asserted from either side.
        internal static bool ServePumpRunning
        {
            get { lock (Gate) return _servePump?.Running == true; }
        }

        // Keeps relays that are being used from being idled out from under
        // whoever is using them. Cheap enough to call on every send.
        public static void Touch()
        {
            lock (Gate) _lastUse = DateTime.UtcNow;
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

        // The one guard both pumps take, so that "only one round at a time" is a
        // single rule rather than two that agree by luck. Internal so a test can
        // hold it and watch a round decline — the only way to prove from outside
        // that a caller actually asks.
        //
        // It replaced a plain bool here. The bool was sound while MirrorTickAsync
        // was the only caller and the UI thread the only thread; it stopped being
        // sound when ServeOneAsync started calling it from the pool. See TickGate.
        internal static readonly TickGate PumpGate = new();

        // Excluded from coverage: one round of the pump above, reading files
        // belonging to live relays and ticking a mirror server and client that
        // only exist when one is running.
        [ExcludeFromCodeCoverage]
        // One turn of both mirror halves, under the shared pump gate.
        //
        // **This used to take a bridge and pump it first; it no longer does, and
        // that is the only thing that changed.** What it always actually was is
        // the gate and the two ticks — the bridge was the transport that
        // happened to need waking, and the transport now wakes itself on its own
        // read loop.
        //
        // Kept rather than folded into MirrorTickAsync because the thing worth
        // asserting is the *decline*: two pumps must not tick the same halves at
        // once, and a test can only watch that happen if it can call one of them
        // directly. False means the gate was held, which is never an error —
        // whichever timer called this comes back around.
        internal static async Task<bool> ServeOneAsync(
            RemoteMirrorServer? server, RemoteMirrorClient? client)
        {
            if (!PumpGate.TryEnter()) return false;

            try
            {
                if (server is not null)
                {
                    try { await server.TickAsync().ConfigureAwait(true); } catch { }
                }

                if (client is not null)
                {
                    try { await client.TickAsync().ConfigureAwait(true); } catch { }
                }
            }
            finally
            {
                PumpGate.Exit();
            }

            return true;
        }

        // Internal rather than private only so a UI test can watch it decline
        // while the serve pump holds the gate — the assertion that the two
        // pumps really do share one, which is otherwise a claim about code
        // nobody can call.
        internal static async Task MirrorTickAsync()
        {
            // Ticks can overlap when a file read is slow, and two pumps racing
            // on one relay would read the same bytes twice. Shared with
            // ServeOneAsync, which is the other pump.
            if (!PumpGate.TryEnter()) return;

            try
            {
                // **Repointed at the link rather than deleted with the relay,
                // and that distinction matters.** What this used to do was pump
                // a tmux pane and then tick the two mirror halves that read it.
                // The pane is gone; the halves are not, and neither is their
                // reason for wanting a tick — deadlines lapse and watches renew
                // on this clock, not on the arrival of bytes. Deleting it
                // wholesale would have left a fetch that never times out and a
                // watch that quietly expires, both of which look like the far
                // machine having gone quiet.
                var host = PeerSessions.Host;
                if (host is null) return;

                var client = host.Client;
                var server = host.Server;

                if (server is not null)
                {
                    try { await server.TickAsync().ConfigureAwait(true); } catch { }
                }

                if (client is not null)
                {
                    try { await client.TickAsync().ConfigureAwait(true); } catch { }
                }
            }
            finally
            {
                PumpGate.Exit();
            }
        }

        // When a stopped relay was last given another go, per account. A
        // dictionary rather than a field because the accounts fail
        // independently — one relay wedged is no reason to hold the other's
        // retry back.
        private static readonly Dictionary<string, DateTime> RevivedAt =
            new(StringComparer.Ordinal);

        // Slow enough that a relay which cannot start is not restarted in a
        // loop, spending quota on a session that dies each time; fast enough
        // that a machine nobody is looking at is dark for a minute rather than
        // until somebody notices.
        internal static readonly TimeSpan ReviveEvery = TimeSpan.FromMinutes(1);

        // Whether it is worth trying this account again.
        //
        // Pure so the rule is testable without a relay, a bridge or a clock —
        // the same reason IdleExpired was split. The three arms are the whole of
        // it: a live bridge needs nothing, a never-tried account goes now, and
        // one tried recently waits.
        internal static bool ShouldRevive(bool hasBridge, DateTime now, DateTime? lastAttempt) =>
            !hasBridge && (lastAttempt is not { } last || now - last >= ReviveEvery);

        internal static bool ShouldPollFast()
        {
            if (_snapshot.Any(r => r.Working)) return true;

            DateTime lastSend;
            lock (Gate) lastSend = _lastSend;

            return DateTime.UtcNow - lastSend < BusyGrace;
        }

        // A mirror roster, as orb rows.
        //
        // Pure, and the only place the two vocabularies meet: a roster entry
        // says what a session *is* (name, CLI, transcript, colour), and a Remote
        // says what to draw. The peer name doubles as the Ref, because over a
        // direct link the machine that served a session is the machine it is on
        // — which is the fact the relay path had to parse back out of a session
        // name (see RemoteControlChatSession.MachineName).
        //
        // An entry nobody has claimed is dropped rather than drawn with a blank
        // machine: it would be an orb the panel could not then ask anyone about.
        internal static IReadOnlyList<Remote> RemotesFromRoster(
            string account,
            IReadOnlyList<(string Peer, MirrorProtocol.MirrorRosterEntry Entry)> known,
            DateTime now) =>
            known
                .Where(k => !string.IsNullOrWhiteSpace(k.Peer))
                .Select(k => new Remote(
                    k.Entry.Name,
                    k.Peer,
                    k.Entry.Status ?? "idle",
                    now,
                    account,
                    string.IsNullOrWhiteSpace(k.Entry.Color) ? null : k.Entry.Color))
                .ToList();

        // Excluded from coverage: reads the live link. What it decides is
        // RemotesFromRoster, which is tested; this is the two lines that fetch
        // and store.
        [ExcludeFromCodeCoverage]
        internal static void RepublishFromLink()
        {
            var client = PeerSessions.Host?.Client;

            var rows = client is null
                ? Array.Empty<Remote>()
                : RemotesFromRoster(
                    ClaudeBuddySettings.DefaultRemoteControlProfileDir,
                    client.Known(),
                    Now());

            lock (Gate) _peerRows = rows;

            Republish();
        }

        // Every relay's sessions and every peer's, flattened into the one list
        // the scan reads.
        internal static void Republish()
        {
            lock (Gate)
            {
                _snapshot = OnePerSession(
                    Relays.Values.SelectMany(r => r.Sessions).ToList(), _peerRows);
            }
        }

        // One row per session, however many transports can see it.
        //
        // **Both can see the same session, and until this they both drew it.**
        // A session running `claude --remote-control` is listed by any relay on
        // that account *and* served over the link by the Buddy beside it — which
        // is not an odd configuration, it is the normal one for a machine that
        // was reachable before the link existed and still is. Two rows share a
        // Key, so the scan draws two orbs for one terminal.
        //
        // That is the "there's two Claude Buddy's running" complaint in a
        // smaller form, and it would have been the first thing anybody saw on
        // turning the link on with the relay still enabled.
        //
        // **The direct row wins.** Not arbitrarily: the two carry the same
        // session but not the same capability. A relay row can only ever offer a
        // messaging channel unless the mirror also answers, while the link's row
        // comes with a live transcript by construction — the roster it was built
        // from is the answer to "can you show me this". Preferring the other way
        // round would put a "no live view" panel on a session that has one.
        //
        // Pure, because "which orb does the user see" is a rule and the answer
        // is not obvious from either list alone.
        internal static IReadOnlyList<Remote> OnePerSession(
            IReadOnlyList<Remote> relayed, IReadOnlyList<Remote> direct)
        {
            var byKey = new Dictionary<string, Remote>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            foreach (var row in direct)
            {
                if (byKey.TryAdd(row.Key, row)) order.Add(row.Key);
            }

            foreach (var row in relayed)
            {
                if (byKey.ContainsKey(row.Key)) continue;

                byKey[row.Key] = row;
                order.Add(row.Key);
            }

            return order.Select(k => byKey[k]).ToList();
        }

        internal static void RaiseWorkingTransitions(IEnumerable<Remote> remotes)
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

        internal static bool IdleExpired()
        {
            var minutes = ClaudeBuddySettings.RemoteControlIdleMinutes;
            if (minutes <= ClaudeBuddySettings.RemoteControlIdleNever) return false;

            // Somebody watching is somebody using it.
            //
            // Touch() is what holds a relay open, and its own comment says it is
            // "cheap enough to call on every send" — which is the whole of where
            // it was called from. Watching a mirrored panel sends nothing, so a
            // panel that was open and streaming counted as idle and had its
            // relays retired underneath it. Measured overnight: 27 deltas
            // delivered, then nothing from 01:36, and the panel still showing
            // 1 a.m. at 8 a.m.
            //
            // Asked of the clients rather than fixed by touching on delivery,
            // because a delta only arrives when the far session says something.
            // Touching on delivery would keep a busy far session alive and still
            // idle out a quiet one, which is the same bug with a smaller window
            // and a much harder repro — a panel watching a thinking agent is
            // exactly when this must not happen.
            DateTime last;
            lock (Gate) last = _lastUse;

            return IdleExpired(WatchingAnywhere(), last, minutes, DateTime.UtcNow);
        }

        // The rule itself, with the state read out of it.
        //
        // Split so the decision can be tested without relays, clients or a
        // window behind it — the same reason OrbArrangement and OrbGlyph are
        // pure. Reaching the watching arm otherwise means standing up a real
        // mirror client with an open feed inside the UI suite, which is a lot of
        // machinery to assert one boolean.
        internal static bool IdleExpired(
            bool watching, DateTime lastUse, int minutes, DateTime now)
        {
            if (minutes <= ClaudeBuddySettings.RemoteControlIdleNever) return false;

            if (watching) return false;

            return now - lastUse > TimeSpan.FromMinutes(minutes);
        }

        // Whether any account's mirror client has a panel open on a far session.
        internal static bool WatchingAnywhere()
        {
            List<Relay> all;
            lock (Gate) all = Relays.Values.ToList();

            return all.Any(r => r.Client?.Watching == true);
        }

        // Hands text to a session on another machine, through the relay for the
        // account that session belongs to. Null when there is no relay to send
        // it through, which the caller shows as a system line rather than
        // swallowing.
        // Stands in for the send in tests.
        //
        // Added for CB-43, whose whole subject is what happens *after* a send is
        // attempted: without it the fallback below could only be reached by
        // starting a live relay on somebody's account, which is the same reason
        // this method is excluded from coverage in the first place. Cleared by
        // ResetForTests, the way UseMirrorClientForTests is.
        internal static Func<string, string, string, Task<string?>>? SendOverrideForTests;

        // Forgets every remote session and everything remembered about them.
        //
        // There is no longer a subprocess to kill: this used to end each
        // account's relay tmux session, and now it only clears the table those
        // relays filled. Kept because the tray and the settings switch both need
        // a way to say "stop showing me these", which is a different thing from
        // the transport being off.
        public static void StopAll(string why = "off")
        {
            lock (Gate)
            {
                Relays.Clear();
                WorkingNow.Clear();
                _peerRows = Array.Empty<Remote>();
                _snapshot = Array.Empty<Remote>();
            }
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

            _mirrorPump?.Stop();
            _mirrorPump = null;
        }

        // Internal so the two things this decides — that a frame never reaches a
        // chat bubble, and that a CB-INFO answer is swallowed — can be tested
        // without a relay to deliver one.
        // A discarded task that faults is a fault nobody hears about.
        //
        // Both mirror halves were started with `_ =`, which is correct about not
        // waiting and wrong about not looking: an exception inside HandleAsync
        // went nowhere at all. That is how a machine could accept a frame, run
        // its handler, and answer nothing, with every visible signal saying it
        // was fine.
        private static void Watched(Task work, string half, MirrorProtocol.MirrorFrame frame)
        {
            _ = work.ContinueWith(
                t => MirrorLog.Say("threw",
                    $"{half} t={frame.Type} id={frame.Id} "
                    + $"{t.Exception?.GetBaseException().GetType().Name}: "
                    + $"{t.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        internal static void OnMessage(string account, BridgeProtocol.InboundMessage message)
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

                if (frame is null)
                {
                    // A frame that looked like one and would not parse. Silent
                    // until now, and indistinguishable from never arriving.
                    MirrorLog.Say("frame-unparseable",
                        $"from={message.FromName} len={message.Body.Length}");
                    return;
                }

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
                MirrorLog.Say("frame-in",
                    $"t={frame.Type} id={frame.Id} from={message.FromName} "
                    + $"server={(server is null ? "null" : "yes")} "
                    + $"client={(client is null ? "null" : "yes")}");

                switch (frame.Type)
                {
                    case MirrorProtocol.Chunk:
                    case MirrorProtocol.Ok:
                    case MirrorProtocol.Err:
                        if (client is not null) Watched(client.OnFrameAsync(message.FromName, frame), "client", frame);
                        else MirrorLog.Say("dropped", $"t={frame.Type} no client for {account}");
                        break;

                    default:
                        if (server is not null) Watched(server.HandleAsync(message.FromName, frame), "server", frame);
                        else MirrorLog.Say("dropped", $"t={frame.Type} no server for {account}");
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
        internal static void RepublishWithColors()
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

                _snapshot = OnePerSession(
                    Relays.Values.SelectMany(r => r.Sessions).ToList(), _peerRows);
            }
        }
    }
}
