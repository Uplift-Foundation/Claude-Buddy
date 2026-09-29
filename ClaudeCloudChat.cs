using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // Reading a cloud session's transcript.
    //
    // **`/v2/ccr-sessions/<id>/events` returns Claude Code's own transcript
    // format** — rows with a `type`, a `message` carrying a `role` and content
    // blocks of `text`, `thinking`, `tool_use` and `tool_result`. That is the
    // format ChatTranscript already reads, tested by
    // `dotnet run --project tests/TranscriptTests` and covered case by case. So
    // this file parses an envelope and hands the rows to that parser rather than
    // writing a second one: two parsers over one format is how a panel comes to
    // show something a terminal does not.
    internal static class ClaudeCloudEvents
    {
        // What one page of events yielded.
        internal sealed record Page(
            IReadOnlyList<ChatTranscript.Row> Rows,
            bool HasMore,
            string? LastId,
            bool Parsed);

        internal static Page ParsePage(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Page(Array.Empty<ChatTranscript.Row>(), false, null, false);
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return new Page(Array.Empty<ChatTranscript.Row>(), false, null, false);
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                {
                    return new Page(Array.Empty<ChatTranscript.Row>(), false, null, false);
                }

                // **Re-serialised rather than handed over as raw text.**
                // ChatTranscript.IsInteresting is a substring test over
                // `"type":"assistant"` — deliberately, so a megabyte of file
                // history is never turned into a JsonDocument — and that test is
                // sensitive to whitespace the way any substring test is. An API
                // that pretty-printed its response would make every row
                // uninteresting and the panel would show an empty conversation
                // with nothing anywhere saying why. JsonSerializer writes the
                // element back compactly and in document order, which is the shape
                // the parser was written against.
                var lines = data.EnumerateArray()
                    .Select(element => JsonSerializer.Serialize(element))
                    .ToList();

                var rows = ChatTranscript.Map(lines);

                var hasMore = root.TryGetProperty("has_more", out var more)
                              && more.ValueKind == JsonValueKind.True;

                var lastId = root.TryGetProperty("last_id", out var last)
                             && last.ValueKind == JsonValueKind.String
                    ? last.GetString()
                    : null;

                return new Page(rows, hasMore,
                    string.IsNullOrWhiteSpace(lastId) ? null : lastId, true);
            }
        }
    }


    // Whether a cloud session will take a message from this panel.
    //
    // **Busy is not here, and that is a measurement rather than an omission.**
    // CB-199's gate sent into a session mid-turn and got a 200, and the message
    // landed as the next turn once the running one finished — the server queues
    // it. So a busy session is Sendable, and the only thing busy changes is what
    // the empty box says.
    internal enum CloudSendability
    {
        Sendable,

        // It no longer takes input: a 409 on a write, which the Claude Code CLI
        // reads as `session_inactive` (not measured here), or a roster row whose
        // bucket says the session is over.
        Ended,

        // It is not there any more: a 404 on a write, or a cached session whose
        // roster row has disappeared. The roster drops archived rows, so a
        // vanished row is archived *or* deleted and the hint says both.
        Gone,

        // The API refused this login a write to this session — a 403 that came
        // from the API rather than from something in front of it. Learned from
        // the first refusal, because there is no pre-flight: the gate measured
        // an empty POST answering 400 to a real token and a bogus one alike, so
        // nothing short of a real message tells a permitted login from a
        // refused one. Per session, not process-wide: one refusal is evidence
        // about one session.
        NotPermitted,
    }

    // The decisions behind CloudSendability, with no session, no network and
    // no dispatcher behind them, so each is testable a case at a time.
    internal static class CloudChatSendability
    {
        // The roster buckets that mean a session is over. **Not measured**: the
        // gate saw "idle", "working" and "blocked" and never an ended session,
        // and the roster already drops `archived` rows before they get here. These
        // two are the plausible names and nothing more, which is why a bucket
        // this list does not know reads as sendable — a refused send then says
        // what actually happened, where a wrong guess here would hide the box
        // over a session that would have taken the message.
        internal static readonly string[] UnmeasuredEndedBuckets = { "archived", "failed" };

        // What a write's refusal says about the session, or null when it says
        // nothing lasting — a timeout, a rate limit, a token the CLI has not
        // refreshed yet, or an edge block, none of which is a fact about *this*
        // session and all of which the next attempt may not repeat.
        internal static CloudSendability? RefusalFor(CloudOutcome outcome) => outcome.Kind switch
        {
            CloudOutcomeKind.SessionInactive => CloudSendability.Ended,
            CloudOutcomeKind.SessionGone => CloudSendability.Gone,

            // Only the API's own refusal. An edge block says nothing about the
            // account, which is the CB-164 lesson CloudOutcomes.EdgeBlockedDetail
            // is written around, and it must not take the box away.
            CloudOutcomeKind.Blocked when outcome.Detail == CloudOutcomes.AccountBlockedDetail =>
                CloudSendability.NotPermitted,

            _ => null,
        };

        internal static bool RosterSaysEnded(ClaudeCloudSessions.Session row) =>
            UnmeasuredEndedBuckets.Any(b =>
                string.Equals(row.StatusBucket, b, StringComparison.OrdinalIgnoreCase));

        // A refusal the server gave outranks anything the roster says, because it
        // is an answer to the question actually being asked. Past that, a row
        // that has gone is Gone and one whose bucket says it is over is Ended.
        internal static CloudSendability For(CloudSendability? refusal,
            ClaudeCloudSessions.Session? row)
        {
            if (refusal is { } refused) return refused;
            if (row is null) return CloudSendability.Gone;
            return RosterSaysEnded(row) ? CloudSendability.Ended : CloudSendability.Sendable;
        }

        internal const string SendableHint = "Message…";

        // Short, because it is a watermark. Says the one thing a person would
        // otherwise get wrong: the message is not lost, it is waiting.
        internal const string BusyHint = "Message… (queued until this turn finishes)";

        internal const string EndedHint = "This session has ended and no longer takes messages.";

        internal const string GoneHint = "This session was archived or deleted, so it can't take messages.";

        internal const string NotPermittedHint = "The API refused messages to this session from this login.";

        internal static string HintFor(CloudSendability sendability, bool busy) => sendability switch
        {
            CloudSendability.Ended => EndedHint,
            CloudSendability.Gone => GoneHint,
            CloudSendability.NotPermitted => NotPermittedHint,
            _ => busy ? BusyHint : SendableHint,
        };
    }

    // A cloud session, as the chat panel sees it.
    //
    // Polled rather than streamed, and able to send. There is no live stream to
    // subscribe to here the way OpenClaw's gateway offers one, so the panel
    // re-reads the events endpoint and reconciles what came back against what it
    // is already showing — by `uuid`, which the transcript rows carry, so a turn
    // that grew a paragraph updates in place rather than appearing twice.
    //
    // **Two hosts, one each way, both measured.** History is read from
    // `/v2/ccr-sessions/{id}/events`, as before; a message goes out through
    // `POST /v1/code/sessions/{id}/events`, which is where the Claude Code CLI
    // writes. CB-199's gate sent through the second and read the result back
    // through the first: one User row, carrying the uuid this class minted,
    // about ten seconds after the POST. That round trip is what lets the bubble
    // this class raises on a 2xx and the row the history later returns be the
    // same bubble.
    internal sealed class ClaudeCloudChatSession : IRemoteChatSession, IRemoteChatComposer,
        IRemoteChatReadOnly, IRemoteChatMachine, IRemoteChatInterrupt
    {
        // How much of a long transcript the panel is given. The interface's own
        // contract is that history arrives already bounded and ordered oldest to
        // newest, and the panel never trims — so the trimming is here.
        //
        // **Not measured.** Nobody has counted how long a cloud session's
        // transcript runs, and this is a round number chosen to be more than a
        // person scrolls back through in a panel. It is the kind of constant that
        // becomes a bug when a real transcript turns out to be shorter than it.
        internal const int HistoryTurns = 200;

        // How many pages of events one load will ask for. Same posture as the
        // roster's page cap, same absence of a measurement behind the number, and
        // the same rule that hitting it must not be silent — see LoadAsync.
        internal const int MaxEventPages = 10;

        // When to look for the reply after a send.
        //
        // **The first delay is the one measured number here.** The gate's echo
        // arrived on /v2 about ten seconds after the POST and was not there at
        // three, so a read sooner than this mostly re-reads what is on screen.
        // The interval and the cap are **not measured**: they are chosen to be
        // slow against an endpoint this app is a guest on, and to give up well
        // before a panel left open overnight could spend anything noticeable.
        // Reaching the cap is not a failure — the busy→idle read in UpdateStatus
        // still fetches the finished reply whenever the turn ends.
        internal static readonly TimeSpan EchoDelay = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan UnmeasuredBusyRefreshInterval = TimeSpan.FromSeconds(15);
        internal static readonly TimeSpan UnmeasuredRefreshCap = TimeSpan.FromMinutes(2);

        private readonly ICloudApi _api;
        private readonly ICloudCredentialSource _credentials;
        private readonly Action<Action> _post;
        private readonly List<ChatTurn> _history = new();
        private readonly Dictionary<string, ChatTurn> _byUuid = new(StringComparer.Ordinal);
        private readonly object _loadGate = new();

        private RemoteChatState _state = RemoteChatState.Connecting;

        // What the roster last said, and what the server has refused. Kept
        // apart because they fail differently: a row can come back (the roster is
        // replaced whole, and a Restart empties it for a moment), a 404 cannot.
        private ClaudeCloudSessions.Session? _row;
        private CloudSendability? _refusal;
        private volatile bool _busy;
        private bool _interruptSent;
        private bool _panelOpen;

        private Task<LoadResult>? _inFlight;
        private CancellationTokenSource? _followUp;

        // How long a credential read is given before this panel gives up on it.
        // An init-only property rather than a constructor parameter so the shape
        // the UI layer constructs stays four arguments; tests set it to
        // milliseconds to drive the give-up path. See
        // ClaudeCliCredentials.ReadWithinAsync for why a budget exists at all.
        internal TimeSpan ReadBudget { get; init; } = ClaudeCliCredentials.UnmeasuredReadBudget;

        // The clock the follow-up reads and the one retry wait on. Task.Delay in
        // the app; a test hands in one that returns at once and records what it
        // was asked for, so the cadence is asserted rather than slept through.
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

        // The settings gate, read at the moment of sending. Off means no
        // credential read and no socket, which is the promise the settings copy
        // makes for the whole cloud arm; a panel opened before the switch was
        // flipped must not be a way round it. A seam so a test does not depend on
        // the settings file of the machine running it.
        internal Func<bool> Enabled { get; init; } = () => ClaudeBuddySettings.ClaudeCloudEnabled;

        // The seam that keeps this class testable without an Avalonia app.
        //
        // Requirement 1 on any IRemoteChatSession is that every event is raised on
        // the UI thread, and the implementation owns that rather than every
        // consumer hopping threads by hand. A parameter rather than a direct
        // Dispatcher call so a test can pass `action => action()` and assert on
        // what was raised, which is the same argument OrbGlyph makes for taking
        // the two-letter setting instead of reading it.
        internal ClaudeCloudChatSession(
            ClaudeCloudSessions.Session session,
            ICloudApi api,
            ICloudCredentialSource credentials,
            Action<Action>? post = null)
        {
            SessionId = session.Id;
            DisplayName = string.IsNullOrWhiteSpace(session.Title) ? session.Id : session.Title;
            ReplyUrl = string.IsNullOrWhiteSpace(session.Url) ? null : session.Url;
            _row = session;
            _busy = ClaudeCloudRoster.IsBusy(session);
            _api = api;
            _credentials = credentials;
            _post = post ?? (action => Dispatcher.UIThread.Post(action));
        }

        public string SessionId { get; }

        public string DisplayName { get; }

        public RemoteChatState State => _state;

        public IReadOnlyList<ChatTurn> History => _history;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;
        public event Action? ReadOnlyChanged;
        public event Action? InterruptChanged;

        internal CloudSendability Sendability => CloudChatSendability.For(_refusal, _row);

        public bool IsReadOnly => Sendability != CloudSendability.Sendable;

        // Only while a turn is running, only while the session still takes
        // input, and not again once Stop has been pressed for this turn — the
        // press is optimistic, and a button that stayed up after it would invite
        // a second request for something the first has already asked for.
        public bool CanInterrupt => _busy && !IsReadOnly && !_interruptSent;

        // The watermark while the box is up, and the reason in its place when it
        // is not. Never the address — ReplyUrl is the link beside it.
        public string ComposerHint => CloudChatSendability.HintFor(Sendability, _busy);

        // The session's own address, which is what the panel's link opens.
        //
        // Carried from the roster row rather than rebuilt, for the reason
        // ClaudeCloudSessions.Session.Url gives: the payload's own session_url is
        // empty on every row measured, and the id is what the address is made of.
        // Null for a row that somehow arrived without one, and the panel then
        // draws no link rather than a link to nowhere.
        public string? ReplyUrl { get; }

        // What the follow-up and the interrupt are doing, for a test to await.
        // Never awaited by the app: both are fire and forget by design.
        internal Task? FollowUpTask { get; private set; }

        // The transcript read in flight, if any — so a test can see that the end
        // of a turn started one, or that it did not.
        internal Task? LoadTask
        {
            get { lock (_loadGate) return _inFlight; }
        }
        internal Task? InterruptTask { get; private set; }

        // **Answered, and the answer is not a machine.**
        //
        // ChatHeaderMeta.MachineFor treats a session that names no machine as
        // being on this one, and its comment says why that is a rule rather than
        // a guess: the only implementer was the mirror, so silence meant a local
        // CLI session or a gateway conversation, both of which are read where
        // they run. A cloud session is the first thing that is silent and *not*
        // here, and left silent it would have put the user's own laptop's name
        // in the header of a session running in Anthropic's data centre —
        // quietly, and in the one line of the panel that exists to answer
        // "where is this".
        //
        // So it names the cloud instead. Not a hostname, because there is no
        // hostname the user could act on and inventing a plausible one would be
        // worse than the wrong-laptop bug it replaces; the header's job here is
        // to say "not one of yours", and that is what this says.
        //
        // Never null and never changes, so MachineChanged is declared to satisfy
        // the interface and deliberately never raised — the panel subscribes and
        // reads the property at bind, which is already the whole answer.
        public string? MachineName => "Anthropic's cloud";

        public event Action? MachineChanged
        {
            add { }
            remove { }
        }

        // --- sending ---------------------------------------------------------

        // Send a message into the session.
        //
        // **The user's bubble appears after the 2xx, never before.** Requirement 3
        // on the interface is that SendAsync raises TurnAdded for the user's own
        // turn; the contract for Failed is that nothing was queued anywhere and
        // the only copy of the text is the one still in the box. A bubble raised
        // before the POST would have to be taken back on a refusal, and a panel
        // that shows a message and then withdraws it has told the user twice
        // something only one of which was true. So a refusal writes a note and
        // leaves the box alone, and a 2xx raises the bubble with the uuid it
        // went out with — the uuid the history's echo carries, so Reconcile
        // folds the two into one.
        //
        // **One retry, on Unavailable only, with the same uuid.** Measured: a
        // repeated uuid comes back `duplicate: true` and is not written twice,
        // so a send whose answer was lost can safely go again. Nothing else is
        // retried. A 401 is not, because the credential was re-read a moment
        // earlier and a second read would return the same token; a 429 is not,
        // because retrying into a rate limit is how a guest loses the invitation.
        public async Task<ChatSendOutcome> SendAsync(string text)
        {
            if (IsReadOnly)
            {
                Note("Not sent: " + ComposerHint);
                return ChatSendOutcome.Failed;
            }

            if (!Enabled())
            {
                Note("Not sent: cloud sessions are switched off in Settings.");
                return ChatSendOutcome.Failed;
            }

            if (CloudRequest.CodeEventsPath(SessionId) is not { } path)
            {
                Note("Not sent: this session's id is not in a shape Buddy will write to.");
                return ChatSendOutcome.Failed;
            }

            // Re-read on every send and never kept, as on every load. Through the
            // source this session was handed — the one that owns the session's
            // account — and never one built here: a second source would be a
            // second Keychain consent surface, and would not know whose login
            // this session needs.
            var read = await ClaudeCliCredentials
                .ReadWithinAsync(_credentials, ReadBudget, CancellationToken.None).ConfigureAwait(false);

            if (read.Outcome != CredentialOutcome.Found || read.AccessToken is not { } token)
            {
                Note("Not sent: " + ClaudeCliCredentials.Describe(read.Outcome) + ".");
                return ChatSendOutcome.Failed;
            }

            var uuid = Guid.NewGuid().ToString();
            var context = new CloudRequestContext(token, path, HttpMethod.Post,
                ClaudeCloudSend.UserMessageBody(SessionId, text, uuid));

            var result = await _api.SendAsync(context, CancellationToken.None).ConfigureAwait(false);

            if (result.Outcome.Kind == CloudOutcomeKind.Unavailable)
            {
                await Delay(Backoff.UnavailableFloor, CancellationToken.None).ConfigureAwait(false);
                result = await _api.SendAsync(context, CancellationToken.None).ConfigureAwait(false);

                if (result.Outcome.Kind == CloudOutcomeKind.Unavailable)
                {
                    // Not "not sent": a request that timed out may well have
                    // been written, and saying otherwise invites a resend that —
                    // with a fresh uuid from the box — would land twice.
                    Note("Your message may not have arrived (" + result.Outcome.Detail
                         + ", twice). Check the session before sending it again.");
                    return ChatSendOutcome.Failed;
                }
            }

            if (result.Outcome.Kind != CloudOutcomeKind.Ok)
            {
                Refused(result.Outcome);
                return ChatSendOutcome.Failed;
            }

            // A 2xx whose receipt this version cannot read is still a 2xx: the
            // event almost certainly landed, and ClaudeCloudSend.ParseSendResult
            // says why "not sent" would be the wrong thing to tell anyone. The
            // receipt itself carries nothing this panel shows, so it is not read.
            var turn = new ChatTurn { Role = ChatRole.User, Text = text, IsComplete = true };
            _post(() =>
            {
                _history.Add(turn);
                _byUuid[uuid] = turn;
                TurnAdded?.Invoke(turn);

                // A new message is a new turn to stop, even if the roster has
                // not caught up with the last one ending yet.
                ChangeInterrupt(() => _interruptSent = false);
            });

            StartFollowUp();
            return ChatSendOutcome.Sent;
        }

        // Say why a send did not go, and take the box away if the reason is
        // about the session rather than about this attempt.
        private void Refused(CloudOutcome outcome)
        {
            var advice = outcome.Kind switch
            {
                CloudOutcomeKind.TokenRefused or CloudOutcomeKind.AuthFailed =>
                    " Run `claude` in a terminal and sign in, then try again.",
                CloudOutcomeKind.RateLimited => " Wait a minute before trying again.",
                CloudOutcomeKind.TooLarge => " Try a shorter message.",
                _ => "",
            };

            Note("Not sent: " + outcome.Detail + "." + advice);

            if (CloudChatSendability.RefusalFor(outcome) is { } refusal)
            {
                _post(() => ChangeReadOnly(() => ChangeInterrupt(() => _refusal ??= refusal)));
            }
        }

        // --- stopping --------------------------------------------------------

        // Stop the turn in flight.
        //
        // Measured: an interrupt takes a running session to idle within about a
        // second, and is harmless against an idle one. Harmless is not the same
        // as free, so a press with nothing running spends no request — the panel
        // hides the button then anyway (CanInterrupt), and this is the belt to
        // that braces.
        //
        // Fire and forget, like the click it answers. CanInterrupt goes false at
        // once so the button goes; if the request is refused the button comes
        // back and a note says why, since the turn is then still running.
        public void Cancel()
        {
            if (!CanInterrupt) return;

            ChangeInterrupt(() => _interruptSent = true);
            InterruptTask = InterruptAsync();
        }

        private async Task InterruptAsync()
        {
            var read = await ClaudeCliCredentials
                .ReadWithinAsync(_credentials, ReadBudget, CancellationToken.None).ConfigureAwait(false);

            string? failure;
            if (read.Outcome != CredentialOutcome.Found || read.AccessToken is not { } token)
            {
                failure = ClaudeCliCredentials.Describe(read.Outcome);
            }
            else
            {
                // CodeEventsPath cannot be null here: Cancel is only reachable
                // through CanInterrupt, and a session with a malformed id never
                // got a turn running from this panel — but the roster can report
                // one busy regardless, so the refusal is handled rather than
                // assumed away.
                var path = CloudRequest.CodeEventsPath(SessionId);
                if (path is null)
                {
                    failure = "this session's id is not in a shape Buddy will write to";
                }
                else
                {
                    var result = await _api.SendAsync(
                        new CloudRequestContext(token, path, HttpMethod.Post,
                            ClaudeCloudSend.InterruptBody(Guid.NewGuid().ToString(),
                                Guid.NewGuid().ToString())),
                        CancellationToken.None).ConfigureAwait(false);

                    failure = result.Outcome.Kind == CloudOutcomeKind.Ok ? null : result.Outcome.Detail;
                }
            }

            if (failure is null) return;

            Note("Stop did not reach the session: " + failure + ".");
            _post(() => ChangeInterrupt(() => _interruptSent = false));
        }

        // --- live state ------------------------------------------------------

        // What the roster says about this session now. Null means its row is not
        // in the roster at all.
        //
        // Pushed by SessionManager's scan, which already walks the roster every
        // pass to draw the orb — so this costs nothing on the network, and is
        // what moves Stop and the composer while the panel is open. Called on
        // the UI thread.
        internal void UpdateStatus(ClaudeCloudSessions.Session? row)
        {
            var wasBusy = _busy;

            ChangeReadOnly(() => ChangeInterrupt(() =>
            {
                _row = row;
                _busy = row is not null && ClaudeCloudRoster.IsBusy(row);

                // The turn that Stop was pressed for is over; the next one is
                // stoppable again.
                if (!_busy) _interruptSent = false;
            }));

            // The turn just finished, so the reply is complete now and worth one
            // read — for a panel someone is looking at, and only then.
            if (wasBusy && !_busy && _panelOpen && !IsReadOnly) _ = RefreshAsync();
        }

        // The panel's lifetime, called by ChatPanel from Bind and Unbind.
        //
        // Closing stops the follow-up reads: nobody is looking, and the next open
        // reads the transcript anyway. It does not stop a turn — closing a window
        // should never cancel work someone asked for, which is what Cancel's
        // own comment on the interface says.
        internal void PanelOpened() => _panelOpen = true;

        internal void PanelClosed()
        {
            _panelOpen = false;
            _followUp?.Cancel();
        }

        private void ChangeReadOnly(Action change)
        {
            var before = IsReadOnly;
            change();
            if (before != IsReadOnly) ReadOnlyChanged?.Invoke();
        }

        private void ChangeInterrupt(Action change)
        {
            var before = CanInterrupt;
            change();
            if (before != CanInterrupt) InterruptChanged?.Invoke();
        }

        // --- reading ---------------------------------------------------------

        // Look for the reply: once at the echo delay, then on the interval while
        // the session is busy, up to the cap. A second send replaces the first
        // send's follow-up rather than running beside it.
        private void StartFollowUp()
        {
            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _followUp, cts)?.Cancel();
            FollowUpTask = FollowUpAsync(cts.Token);
        }

        private async Task FollowUpAsync(CancellationToken ct)
        {
            var elapsed = TimeSpan.Zero;
            var wait = EchoDelay;

            while (true)
            {
                try
                {
                    await Delay(wait, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (ct.IsCancellationRequested) return;
                elapsed += wait;

                var loaded = await RefreshAsync().ConfigureAwait(false);

                // Any 429 ends it, with no retry of its own: the budget being
                // spent is the account's, and the busy→idle read still fetches
                // the reply when the turn ends.
                if (loaded.Kind == CloudOutcomeKind.RateLimited) return;
                if (!_busy || IsReadOnly) return;

                wait = UnmeasuredBusyRefreshInterval;
                if (elapsed + wait > UnmeasuredRefreshCap) return;
            }
        }

        // Read the transcript and reconcile it against what is already shown.
        //
        // Returns false when nothing could be read at all — no credential, or the
        // endpoint refused us — which is the caller's cue to leave the panel in
        // Error rather than showing an empty conversation as though the session
        // had nothing in it. An empty transcript and an unreadable one look
        // identical on screen and only one of them is worth saying something about.
        //
        // **Single flight.** Every open starts one of these, and so do the
        // follow-up and the end of a turn; two overlapping reads of the same
        // transcript would both reconcile, and at best spend a request each for
        // one answer. A call that arrives while one is running gets that one's
        // answer. The cancellation token is accepted for the callers that have
        // one and not passed down, because a read shared between callers cannot
        // be cancelled by one of them.
        internal async Task<bool> LoadAsync(CancellationToken ct) =>
            (await RefreshAsync().ConfigureAwait(false)).Kind == CloudOutcomeKind.Ok;

        private readonly record struct LoadResult(CloudOutcomeKind Kind);

        private Task<LoadResult> RefreshAsync()
        {
            lock (_loadGate)
            {
                if (_inFlight is { IsCompleted: false } running) return running;
                return _inFlight = ReadTranscriptAsync();
            }
        }

        private async Task<LoadResult> ReadTranscriptAsync()
        {
            // Budgeted for the same reason the arm's read is: the secret read can
            // block indefinitely with no window server session, and a panel whose
            // load never returns is a spinner that never stops.
            var read = await ClaudeCliCredentials
                .ReadWithinAsync(_credentials, ReadBudget, CancellationToken.None).ConfigureAwait(false);

            if (read.Outcome != CredentialOutcome.Found || read.AccessToken is not { } token)
            {
                Publish(RemoteChatState.Error);
                return new LoadResult(CloudOutcomeKind.AuthFailed);
            }

            var rows = new List<ChatTranscript.Row>();
            string? after = null;

            for (var i = 0; i < MaxEventPages; i++)
            {
                var result = await _api.SendAsync(
                    new CloudRequestContext(token,
                        CloudRequest.EventsPath(SessionId, CloudRequest.MaxPageSize, after)),
                    CancellationToken.None).ConfigureAwait(false);

                if (result.Outcome.Kind != CloudOutcomeKind.Ok)
                {
                    Publish(RemoteChatState.Error);
                    return new LoadResult(result.Outcome.Kind);
                }

                var page = ClaudeCloudEvents.ParsePage(result.Body);
                if (!page.Parsed)
                {
                    Publish(RemoteChatState.Error);
                    return new LoadResult(CloudOutcomeKind.ShapeChanged);
                }

                rows.AddRange(page.Rows);

                if (!page.HasMore || page.LastId is null) break;
                after = page.LastId;
            }

            // Oldest to newest is the order the events endpoint returns and the
            // order the interface promises, so the trim takes the *tail*. Trimming
            // the head would hand the panel the beginning of a conversation and
            // call it the end of one.
            var kept = rows.Count > HistoryTurns
                ? rows.Skip(rows.Count - HistoryTurns).ToList()
                : rows;

            _post(() => Reconcile(kept));
            return new LoadResult(CloudOutcomeKind.Ok);
        }

        // Fold a freshly-read transcript into the one on screen.
        //
        // By `uuid`, not by position. A transcript being re-read while its last
        // turn is still being written means the same turn comes back longer than
        // it was, and matching by position would work right up until a row was
        // skipped — ChatTranscript drops sidechain rows and noise, so the indices
        // on two reads of a growing transcript are not the same indices. The same
        // key is what makes a sent message's echo land on its own bubble: SendAsync
        // files the bubble under the uuid it sent, and the echo carries it back.
        //
        // Requirement 2 on the interface is that TurnUpdated carries the whole
        // turn already mutated rather than a delta, which is what mutating Text in
        // place and then raising does.
        private void Reconcile(IReadOnlyList<ChatTranscript.Row> rows)
        {
            foreach (var row in rows)
            {
                // A row with no uuid cannot be reconciled with anything, so it is
                // appended once and never matched again. Rare enough not to be
                // worth a second key, and appending a duplicate is a better
                // failure than silently replacing an unrelated turn.
                if (row.Uuid is { } uuid && _byUuid.TryGetValue(uuid, out var existing))
                {
                    if (existing.Text == row.Turn.Text
                        && existing.IsComplete == row.Turn.IsComplete)
                    {
                        continue;
                    }

                    existing.Text = row.Turn.Text;
                    existing.IsComplete = row.Turn.IsComplete;
                    TurnUpdated?.Invoke(existing);
                    continue;
                }

                _history.Add(row.Turn);
                if (row.Uuid is { } key) _byUuid[key] = row.Turn;
                TurnAdded?.Invoke(row.Turn);
            }

            SetState(RemoteChatState.Connected);
        }

        // A line in the transcript from this app rather than from the session.
        // Every caller builds it from fixed prose and the endpoint's Detail
        // strings, none of which is derived from the credential.
        private void Note(string text) => _post(() =>
        {
            var turn = new ChatTurn { Role = ChatRole.System, IsComplete = true, Text = text };
            _history.Add(turn);
            TurnAdded?.Invoke(turn);
        });

        private void Publish(RemoteChatState state) => _post(() => SetState(state));

        private void SetState(RemoteChatState state)
        {
            if (_state == state) return;
            _state = state;
            StateChanged?.Invoke(state);
        }
    }
}
