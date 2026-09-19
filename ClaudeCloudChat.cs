using System;
using System.Collections.Generic;
using System.Linq;
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

    // A cloud session, as the chat panel sees it.
    //
    // Read-only, polled, and honest about both. There is no live stream to
    // subscribe to here the way OpenClaw's gateway offers one, so a panel left
    // open re-reads the events endpoint and reconciles what came back against what
    // it is already showing — by `uuid`, which the transcript rows carry, so a
    // turn that grew a paragraph updates in place rather than appearing twice.
    internal sealed class ClaudeCloudChatSession : IRemoteChatSession, IRemoteChatComposer,
        IRemoteChatReadOnly, IRemoteChatMachine
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

        private readonly ICloudApi _api;
        private readonly ICloudCredentialSource _credentials;
        private readonly Action<Action> _post;
        private readonly List<ChatTurn> _history = new();
        private readonly Dictionary<string, ChatTurn> _byUuid = new(StringComparer.Ordinal);

        private RemoteChatState _state = RemoteChatState.Connecting;

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

        // **Always Failed, and nothing is raised.**
        //
        // Requirement 3 on the interface is that SendAsync raises TurnAdded for the
        // user's own turn so the panel never inserts optimistically; the contract
        // for Failed is that nothing was queued anywhere and the only copy of what
        // was typed is the one still in the box. Both are satisfied by doing
        // nothing at all, which is the honest implementation when the endpoint has
        // no input route — `/input`, `/messages`, `/turns` and `/conversation` are
        // all 404, measured.
        //
        // The panel does not show a box for one of these anyway (see
        // IRemoteChatReadOnly), so this is the belt to that braces: an
        // implementation whose only guard against sending was a UI decision would
        // be one keystroke of refactoring away from silently dropping a message.
        public Task<ChatSendOutcome> SendAsync(string text) =>
            Task.FromResult(ChatSendOutcome.Failed);

        // Nothing is in flight that this app started. The reply is happening in
        // Anthropic's cloud whether this panel is open or not, and a Cancel that
        // did nothing while looking like it should is worse than one that plainly
        // does nothing.
        public void Cancel()
        {
        }

        public bool IsReadOnly => true;

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

        // Shown in place of the box, for a panel that renders the hint even when
        // it has hidden the composer. Says that replying happens elsewhere; the
        // link beside it is what says *where*, so the address is deliberately
        // not spelled out here as well.
        public string ComposerHint => "This conversation is read-only here.";

        // The session's own address, which is what the panel's link opens.
        //
        // Carried from the roster row rather than rebuilt, for the reason
        // ClaudeCloudSessions.Session.Url gives: the payload's own session_url is
        // empty on every row measured, and the id is what the address is made of.
        // Null for a row that somehow arrived without one, and the panel then
        // draws no link rather than a link to nowhere.
        public string? ReplyUrl { get; }

        // Read the transcript and reconcile it against what is already shown.
        //
        // Returns false when nothing could be read at all — no credential, or the
        // endpoint refused us — which is the caller's cue to leave the panel in
        // Error rather than showing an empty conversation as though the session
        // had nothing in it. An empty transcript and an unreadable one look
        // identical on screen and only one of them is worth saying something about.
        internal async Task<bool> LoadAsync(CancellationToken ct)
        {
            var read = _credentials.Read();
            if (read.Outcome != CredentialOutcome.Found || read.AccessToken is not { } token)
            {
                Publish(RemoteChatState.Error);
                return false;
            }

            var rows = new List<ChatTranscript.Row>();
            string? after = null;

            for (var i = 0; i < MaxEventPages; i++)
            {
                var result = await _api.GetAsync(
                    new CloudRequestContext(token,
                        CloudRequest.EventsPath(SessionId, CloudRequest.MaxPageSize, after)),
                    ct).ConfigureAwait(false);

                if (result.Outcome.Kind != CloudOutcomeKind.Ok)
                {
                    Publish(RemoteChatState.Error);
                    return false;
                }

                var page = ClaudeCloudEvents.ParsePage(result.Body);
                if (!page.Parsed)
                {
                    Publish(RemoteChatState.Error);
                    return false;
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
            return true;
        }

        // Fold a freshly-read transcript into the one on screen.
        //
        // By `uuid`, not by position. A transcript being re-read while its last
        // turn is still being written means the same turn comes back longer than
        // it was, and matching by position would work right up until a row was
        // skipped — ChatTranscript drops sidechain rows and noise, so the indices
        // on two reads of a growing transcript are not the same indices.
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

        private void Publish(RemoteChatState state) => _post(() => SetState(state));

        private void SetState(RemoteChatState state)
        {
            if (_state == state) return;
            _state = state;
            StateChanged?.Invoke(state);
        }
    }
}
