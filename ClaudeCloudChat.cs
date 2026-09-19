using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // A session that can be read and not written to.
    //
    // **The panel hides the composer entirely for one of these**, rather than
    // showing a disabled box or a box with a discouraging watermark. That is a
    // departure from IRemoteChatComposer's own reasoning, which argues for leaving
    // the box enabled and letting SendAsync explain itself — and the argument
    // holds where typing is *pointless*, which is not the same as there being
    // nowhere for the text to go. A cloud session has no input endpoint at all:
    // `/input`, `/messages` and `/turns` are all 404, measured. A box that accepts
    // a paragraph and then says the transport never had a way to deliver it is
    // worse than no box, because the paragraph is gone by the time that is said.
    //
    // Declared here rather than in RemoteChat.cs beside its siblings because
    // CB-164's two halves were built in parallel worktrees and RemoteChat.cs
    // belongs to the other one; moving it up to join the rest is a one-line
    // follow-up once both have landed, and is worth doing.
    public interface IRemoteChatReadOnly
    {
        // A property rather than a bare marker, for the same reason
        // IRemoteChatRoom.IsRoom is one: a fake has to be able to flip it per
        // instance to drive both sides of the panel's decision from one class.
        bool IsReadOnly { get; }
    }

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
        IRemoteChatReadOnly
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

        // Shown in place of the box, for a panel that renders the hint even when
        // it has hidden the composer. Says where the session *can* be replied to
        // rather than only that it cannot be replied to here.
        public string ComposerHint =>
            "this cloud session can be read here and replied to at claude.ai/code";

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
