using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers reading a cloud session's transcript, and the read-only chat session
// built on it.
//
// **The events endpoint returns Claude Code's own transcript format**, which is
// why nothing here parses a turn: ChatTranscript already does that, is covered
// case by case in tests/TranscriptTests, and a second parser over one format is
// how a panel comes to show something a terminal does not. What this file covers
// is the envelope around it, the reconciliation, and the refusal to send.
//
// **Every value in the fixture is invented.** The row shape is real — captured
// field by field from a /events response — and the uuids, text and instants are
// not.
public class ClaudeCloudEventsTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

    // --- the fixture ---------------------------------------------------------

    // Built by substitution rather than interpolation. These rows are dense with
    // braces, and a raw interpolated string whose content ends in `}}` is a
    // counting exercise that fails as a compile error — which is a fine way to
    // fail, and a worse way to read.
    private const string UserTemplate =
        """{"type":"user","uuid":"UUID","timestamp":"2026-09-19T10:00:00Z","message":{"role":"user","content":[{"type":"text","text":"TEXT"}]}}""";

    private const string AssistantTemplate =
        """{"type":"assistant","uuid":"UUID","timestamp":"2026-09-19T10:00:05Z","message":{"role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"TEXT"}],"usage":{"input_tokens":10,"output_tokens":20},"stop_reason":"end_turn"}}""";

    private static string UserRow(string uuid, string text) =>
        UserTemplate.Replace("UUID", uuid, StringComparison.Ordinal)
            .Replace("TEXT", text, StringComparison.Ordinal);

    private static string AssistantRow(string uuid, string text) =>
        AssistantTemplate.Replace("UUID", uuid, StringComparison.Ordinal)
            .Replace("TEXT", text, StringComparison.Ordinal);

    private static string Envelope(IEnumerable<string> rows, bool hasMore = false, string? lastId = null)
    {
        var last = lastId is null ? "null" : "\"" + lastId + "\"";
        return "{\"data\":[" + string.Join(",", rows) + "],\"has_more\":"
               + (hasMore ? "true" : "false") + ",\"last_id\":" + last + "}";
    }

    // --- the envelope --------------------------------------------------------

    [Fact]
    public void APageOfEventsBecomesTurnsThroughTheExistingParser()
    {
        var page = ClaudeCloudEvents.ParsePage(Envelope(new[]
        {
            UserRow("u1", "run the tests"),
            AssistantRow("a1", "running them now"),
        }, hasMore: true, lastId: "evt_9"));

        Assert.True(page.Parsed);
        Assert.True(page.HasMore);
        Assert.Equal("evt_9", page.LastId);
        Assert.Equal(2, page.Rows.Count);

        Assert.Equal(ChatRole.User, page.Rows[0].Turn.Role);
        Assert.Equal("run the tests", page.Rows[0].Turn.Text);
        Assert.Equal("u1", page.Rows[0].Uuid);

        Assert.Equal(ChatRole.Assistant, page.Rows[1].Turn.Role);
        Assert.Equal("running them now", page.Rows[1].Turn.Text);
    }

    // **The reason the rows are re-serialised rather than handed over raw.**
    // ChatTranscript.IsInteresting is a substring test over `"type":"assistant"` —
    // deliberately, so a megabyte of file history is never turned into a
    // JsonDocument — and that test is sensitive to whitespace. A pretty-printed
    // response would make every row uninteresting and the panel would show an
    // empty conversation with nothing anywhere saying why.
    [Fact]
    public void APrettyPrintedResponseStillParses()
    {
        const string pretty = """
        {
          "data": [
            {
              "type": "assistant",
              "uuid": "a1",
              "timestamp": "2026-09-19T10:00:05Z",
              "message": {
                "role": "assistant",
                "content": [ { "type": "text", "text": "spaced out" } ]
              }
            }
          ],
          "has_more": false,
          "last_id": null
        }
        """;

        var page = ClaudeCloudEvents.ParsePage(pretty);

        Assert.True(page.Parsed);
        Assert.Equal("spaced out", Assert.Single(page.Rows).Turn.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html>")]
    [InlineData("""{"no_data":true}""")]
    [InlineData("""{"data":"nope"}""")]
    [InlineData("""[]""")]
    public void AnUnusablePageSaysItDidNotParse(string? body)
    {
        var page = ClaudeCloudEvents.ParsePage(body);

        Assert.False(page.Parsed);
        Assert.Empty(page.Rows);
    }

    // Row types the parser does not handle are skipped, not fatal. A Claude Code
    // upgrade that adds a row type degrades rather than breaks, which is the
    // property ChatTranscript.Map was written for and is worth pinning here too.
    [Fact]
    public void RowTypesTheParserDoesNotHandleAreSkippedRatherThanFatal()
    {
        var page = ClaudeCloudEvents.ParsePage(Envelope(new[]
        {
            """{"type":"system","uuid":"s1","subtype":"init"}""",
            """{"type":"control_request","uuid":"c1"}""",
            """{"type":"result","uuid":"r1","duration_ms":1200}""",
            AssistantRow("a1", "still here"),
        }));

        Assert.True(page.Parsed);
        Assert.Equal("still here", Assert.Single(page.Rows).Turn.Text);
    }

    [Fact]
    public void ABlankLastIdIsNoCursor()
    {
        Assert.Null(ClaudeCloudEvents.ParsePage(
            """{"data":[],"has_more":true,"last_id":"  "}""").LastId);
    }

    // --- the chat session ----------------------------------------------------

    private sealed class FakeApi : ICloudApi
    {
        private readonly Func<string, CloudApiResult> _answer;

        internal FakeApi(Func<string, CloudApiResult> answer) => _answer = answer;

        internal List<string> Paths { get; } = new();

        public Task<CloudApiResult> GetAsync(CloudRequestContext context, CancellationToken token)
        {
            Paths.Add(context.Path);
            return Task.FromResult(_answer(context.Path));
        }
    }

    private sealed class FakeCredentials : ICloudCredentialSource
    {
        internal CredentialRead Reading { get; set; } =
            new(CredentialOutcome.Found, Token, null, "a credential is present");

        public string? Stamp() => "stamp-1";
        public CredentialRead Read() => Reading;
    }

    private static ClaudeCloudSessions.Session Session(string id = "session_a",
        string title = "a cloud session") =>
        new(id, title, "idle", new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc),
            "https://claude.ai/code/" + id, "idle", false, null, null, null, null);

    // Events are raised synchronously in tests by handing the session a post that
    // simply runs the action. In the app it is Dispatcher.UIThread.Post, which is
    // requirement 1 on the interface.
    private static ClaudeCloudChatSession Build(ICloudApi api, ICloudCredentialSource? creds = null,
        ClaudeCloudSessions.Session? session = null) =>
        new(session ?? Session(), api, creds ?? new FakeCredentials(), action => action());

    [Fact]
    public async Task LoadingReadsTheEventsPathAndFillsTheHistory()
    {
        var api = new FakeApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
            Envelope(new[] { UserRow("u1", "hello"), AssistantRow("a1", "hi") })));

        var chat = Build(api);
        var added = new List<ChatTurn>();
        chat.TurnAdded += added.Add;

        Assert.True(await chat.LoadAsync(CancellationToken.None));

        Assert.Equal("/v2/ccr-sessions/session_a/events?limit=100", Assert.Single(api.Paths));
        Assert.Equal(new[] { "hello", "hi" }, chat.History.Select(t => t.Text));
        Assert.Equal(2, added.Count);
        Assert.Equal(RemoteChatState.Connected, chat.State);
    }

    [Fact]
    public async Task TheEventsWalkFollowsItsCursor()
    {
        var pages = new Queue<string>(new[]
        {
            Envelope(new[] { UserRow("u1", "first") }, hasMore: true, lastId: "evt_1"),
            Envelope(new[] { AssistantRow("a1", "second") }),
        });

        var api = new FakeApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), pages.Dequeue()));
        var chat = Build(api);

        Assert.True(await chat.LoadAsync(CancellationToken.None));

        Assert.Equal(new[]
        {
            "/v2/ccr-sessions/session_a/events?limit=100",
            "/v2/ccr-sessions/session_a/events?limit=100&after_id=evt_1",
        }, api.Paths);

        Assert.Equal(new[] { "first", "second" }, chat.History.Select(t => t.Text));
    }

    [Fact]
    public async Task TheEventsWalkStopsAtItsPageCap()
    {
        var page = 0;
        var api = new FakeApi(_ =>
        {
            page++;
            return new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
                Envelope(new[] { UserRow($"u{page}", $"turn {page}") }, hasMore: true, lastId: $"evt_{page}"));
        });

        var chat = Build(api);
        Assert.True(await chat.LoadAsync(CancellationToken.None));

        Assert.Equal(ClaudeCloudChatSession.MaxEventPages, api.Paths.Count);
    }

    // Oldest to newest is what the interface promises, so a transcript longer than
    // the cap is trimmed at the **head**. Trimming the tail would hand the panel
    // the beginning of a conversation and call it the end of one.
    [Fact]
    public async Task ALongTranscriptIsTrimmedToItsMostRecentTurns()
    {
        var rows = Enumerable.Range(0, ClaudeCloudChatSession.HistoryTurns + 50)
            .Select(i => UserRow($"u{i}", $"turn {i}"));

        var api = new FakeApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
            Envelope(rows)));

        var chat = Build(api);
        Assert.True(await chat.LoadAsync(CancellationToken.None));

        Assert.Equal(ClaudeCloudChatSession.HistoryTurns, chat.History.Count);
        Assert.Equal("turn 50", chat.History[0].Text);
        Assert.Equal($"turn {ClaudeCloudChatSession.HistoryTurns + 49}", chat.History[^1].Text);
    }

    // **By uuid, not by position.** A transcript re-read while its last turn is
    // still being written comes back longer than it was, and matching by position
    // would work right up until a row was skipped — which ChatTranscript does, for
    // sidechains and noise.
    [Fact]
    public async Task ASecondReadUpdatesAGrowingTurnInPlaceRatherThanAppendingIt()
    {
        var reads = 0;
        var api = new FakeApi(_ =>
        {
            reads++;
            return new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), Envelope(new[]
            {
                UserRow("u1", "run the tests"),
                AssistantRow("a1", reads == 1 ? "running" : "running them now, all green"),
            }));
        });

        var chat = Build(api);
        await chat.LoadAsync(CancellationToken.None);

        var added = new List<ChatTurn>();
        var updated = new List<ChatTurn>();
        chat.TurnAdded += added.Add;
        chat.TurnUpdated += updated.Add;

        await chat.LoadAsync(CancellationToken.None);

        Assert.Equal(2, chat.History.Count);
        Assert.Empty(added);
        Assert.Equal("running them now, all green", Assert.Single(updated).Text);
        Assert.Equal("running them now, all green", chat.History[1].Text);

        // Requirement 2: the event carries the whole turn, already mutated, and the
        // list item is the same object rather than a replacement.
        Assert.Same(chat.History[1], updated[0]);
    }

    [Fact]
    public async Task ASecondReadRaisesNothingWhenNothingChanged()
    {
        var api = new FakeApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
            Envelope(new[] { UserRow("u1", "unchanged") })));

        var chat = Build(api);
        await chat.LoadAsync(CancellationToken.None);

        var raised = 0;
        chat.TurnAdded += _ => raised++;
        chat.TurnUpdated += _ => raised++;

        await chat.LoadAsync(CancellationToken.None);

        Assert.Equal(0, raised);
        Assert.Single(chat.History);
    }

    [Fact]
    public async Task ANewTurnOnASecondReadIsAppendedAndAnnounced()
    {
        var reads = 0;
        var api = new FakeApi(_ =>
        {
            reads++;
            var rows = reads == 1
                ? new[] { UserRow("u1", "first") }
                : new[] { UserRow("u1", "first"), AssistantRow("a1", "second") };
            return new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), Envelope(rows));
        });

        var chat = Build(api);
        await chat.LoadAsync(CancellationToken.None);

        var added = new List<ChatTurn>();
        chat.TurnAdded += added.Add;

        await chat.LoadAsync(CancellationToken.None);

        Assert.Equal("second", Assert.Single(added).Text);
        Assert.Equal(2, chat.History.Count);
    }

    // An empty transcript and an unreadable one look identical on screen, and only
    // one of them is worth saying something about. So a refusal leaves the session
    // in Error rather than showing nothing as though there were nothing.
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task ARefusedFetchLeavesTheSessionInErrorAndNotEmpty(int status)
    {
        var api = new FakeApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(status, null), null));

        var chat = Build(api);
        var states = new List<RemoteChatState>();
        chat.StateChanged += states.Add;

        Assert.False(await chat.LoadAsync(CancellationToken.None));

        Assert.Equal(RemoteChatState.Error, chat.State);
        Assert.Equal(RemoteChatState.Error, Assert.Single(states));
        Assert.Empty(chat.History);
    }

    [Fact]
    public async Task ABodyThatWillNotParseIsAnErrorRatherThanAnEmptyConversation()
    {
        var api = new FakeApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), "<html>"));

        var chat = Build(api);

        Assert.False(await chat.LoadAsync(CancellationToken.None));
        Assert.Equal(RemoteChatState.Error, chat.State);
    }

    // The panel's read is budgeted for the same reason the arm's is: the secret
    // read can block indefinitely with no window server session, and a panel whose
    // load never returns is a spinner that never stops.
    [Fact]
    public async Task AStoreThatNeverAnswersLeavesThePanelInErrorRatherThanLoadingForever()
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));
        using var gate = new ManualResetEventSlim(false);

        var chat = new ClaudeCloudChatSession(Session(), api,
            new HangingCredentials(gate), action => action())
        {
            ReadBudget = TimeSpan.FromMilliseconds(50),
        };

        // Task.Run for the reason the arm's equivalent test spells out: an async
        // method runs synchronously up to its first real await, so an un-budgeted
        // read would block the calling thread before there was a Task to race.
        var load = Task.Run(() => chat.LoadAsync(CancellationToken.None));
        var finished = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(ReferenceEquals(finished, load),
            "LoadAsync did not return: the credential read parked the panel.");

        Assert.False(await load);
        Assert.Equal(RemoteChatState.Error, chat.State);
        Assert.Empty(api.Paths);
        Assert.Empty(chat.History);
    }

    // Blocks in Read and answers instantly in Stamp, which is the measured
    // asymmetry — the data query hangs with no window server session and the
    // attributes-only query does not.
    private sealed class HangingCredentials : ICloudCredentialSource
    {
        private readonly ManualResetEventSlim _gate;

        internal HangingCredentials(ManualResetEventSlim gate) => _gate = gate;

        public string? Stamp() => "stamp-1";

        public CredentialRead Read()
        {
            _gate.Wait();
            return new CredentialRead(CredentialOutcome.Found, Token, null, "too late");
        }
    }

    [Fact]
    public async Task NoCredentialMeansNoRequestAtAll()
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));
        var chat = Build(api, new FakeCredentials
        {
            Reading = new CredentialRead(CredentialOutcome.NotLoggedIn, null, null, "…"),
        });

        Assert.False(await chat.LoadAsync(CancellationToken.None));

        Assert.Empty(api.Paths);
        Assert.Equal(RemoteChatState.Error, chat.State);
    }

    // --- the refusal to send -------------------------------------------------

    // **`/input`, `/messages`, `/turns` and `/conversation` are all 404, measured.**
    // So the session reports no send capability and SendAsync does nothing at all:
    // Failed means nothing was queued anywhere and the only copy of what was typed
    // is the one still in the box, which is exactly true here.
    [Fact]
    public async Task SendAlwaysFailsAndRaisesNothing()
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));
        var chat = Build(api);

        var raised = 0;
        chat.TurnAdded += _ => raised++;
        chat.TurnUpdated += _ => raised++;

        Assert.Equal(ChatSendOutcome.Failed, await chat.SendAsync("please do the thing"));

        Assert.Equal(0, raised);
        Assert.Empty(chat.History);
        Assert.Empty(api.Paths);
    }

    [Fact]
    public void ItDeclaresItselfReadOnlyAndSaysWhereItCanBeRepliedTo()
    {
        var chat = Build(new FakeApi(_ => throw new InvalidOperationException()));

        Assert.True(Assert.IsAssignableFrom<IRemoteChatReadOnly>(chat).IsReadOnly);
        Assert.Contains("claude.ai/code",
            Assert.IsAssignableFrom<IRemoteChatComposer>(chat).ComposerHint, StringComparison.Ordinal);
    }

    // Nothing this app started is in flight — the reply is happening in the cloud
    // whether the panel is open or not.
    [Fact]
    public void CancelDoesNothingAndSaysSoByDoingNothing()
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));
        var chat = Build(api);

        chat.Cancel();

        Assert.Empty(api.Paths);
    }

    // --- identity ------------------------------------------------------------

    [Fact]
    public void ThePanelTitleIsTheSessionTitle()
    {
        var chat = Build(new FakeApi(_ => throw new InvalidOperationException()),
            session: Session("session_a", "refactor the parser"));

        Assert.Equal("session_a", chat.SessionId);
        Assert.Equal("refactor the parser", chat.DisplayName);
    }

    // A cloud session can have no title — an id is a worse name than a title and a
    // better one than an empty header.
    [Fact]
    public void AnUntitledSessionFallsBackToItsId()
    {
        var chat = Build(new FakeApi(_ => throw new InvalidOperationException()),
            session: Session("session_a", "   "));

        Assert.Equal("session_a", chat.DisplayName);
    }

    [Fact]
    public void ANewSessionStartsConnectingRatherThanConnected()
    {
        Assert.Equal(RemoteChatState.Connecting,
            Build(new FakeApi(_ => throw new InvalidOperationException())).State);
    }

    // The token reaches nothing the panel shows.
    [Fact]
    public async Task TheTokenNeverReachesTheTranscriptOrTheHint()
    {
        var api = new FakeApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
            Envelope(new[] { UserRow("u1", "hello") })));

        var chat = Build(api);
        await chat.LoadAsync(CancellationToken.None);

        Assert.DoesNotContain(Token, chat.ComposerHint, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, chat.DisplayName, StringComparison.Ordinal);
        Assert.All(chat.History,
            turn => Assert.DoesNotContain(Token, turn.Text, StringComparison.Ordinal));
    }
}
