using System.Text;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests;

// The relay's tmux names, and what it does with a transcript row.
//
// None of this starts a relay. RemoteControlBridge's own live tests are opt-in
// behind an environment variable because they start a real Claude Code session
// and spend quota, so they do not run in CI at all — which is why this file
// exists: the constructor is pure string work and the routing takes text, so both
// are reachable for free, and between them they are where the bridge's mistakes
// would actually be made.
//
// In IntegrationTests rather than UnitTests because Adopt reads a real status
// file, which is the point of that case.
public class RemoteControlBridgeRoutingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-bridge-" + Guid.NewGuid().ToString("N"));

    public RemoteControlBridgeRoutingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // --- tmux names: three rules, each one a measured failure ---

    // A profile dir starts with a dot, and tmux parses dots and colons as
    // window/pane separators — so a name carrying one addresses something that
    // does not exist.
    [Fact]
    public void DotsAndColonsAreReplacedInTheSessionName()
    {
        var names = RemoteControlBridge.TmuxNames(".claude", null);

        Assert.DoesNotContain('.', names.Session);
        Assert.DoesNotContain(':', names.Session);
    }

    // The measured one: `kill-session -t claude-buddy-rc--claude` killed
    // `claude-buddy-rc--claude-board`, because tmux resolves a target by prefix.
    // Starting the second relay silently destroyed the first and the survivor
    // then answered nothing. "=" forces an exact match, and one account's name
    // being a prefix of another's is the common case the moment somebody has
    // ".claude" and ".claude-board".
    [Fact]
    public void TargetsForceAnExactMatchSoOneAccountCannotKillAnother()
    {
        var claude = RemoteControlBridge.TmuxNames(".claude", null);
        var board = RemoteControlBridge.TmuxNames(".claude-board", null);

        Assert.StartsWith("=", claude.Target);
        Assert.StartsWith("=", board.Target);

        // The bug this prevents, stated as the condition that caused it: the
        // two names share a prefix, so a target that is not exact resolves to
        // whichever tmux finds first.
        //
        // Asserted on the shared prefix rather than on one full name being a
        // prefix of the other, which is what it used to say. Appending the
        // machine tag ended that — "…-claude-warrens-mbp" is not a prefix of
        // "…-claude-board-warrens-mbp" — but it did not end the hazard, because
        // what tmux resolves against is any prefix, and "…-claude" is still one
        // of both.
        Assert.StartsWith("claude-buddy-rc--claude", claude.Session);
        Assert.StartsWith("claude-buddy-rc--claude", board.Session);
        Assert.NotEqual(claude.Session, board.Session);
        Assert.NotEqual(claude.Target, board.Target);
    }

    // Also measured: `send-keys -t =name` answers "can't find pane", because for
    // a pane target tmux wants session:window.pane and "=name" alone is not one.
    // "=name:" resolves to that exact session's active pane, which is what a
    // freshly created session has exactly one of.
    [Fact]
    public void ThePaneTargetCarriesTheTrailingColon()
    {
        var names = RemoteControlBridge.TmuxNames(".claude", null);

        Assert.Equal(names.Target + ":", names.PaneTarget);
        Assert.EndsWith(":", names.PaneTarget);
    }

    // The test tag exists because the relay name is a machine-wide mutex per
    // account: a test that started a relay would kill the running app's, the app
    // would take its own back, and the two would trade it until one lost a race —
    // measured as the same live test passing and failing on consecutive runs.
    [Fact]
    public void ATagKeepsATestsRelayOutOfTheAppsWay()
    {
        var app = RemoteControlBridge.TmuxNames(".claude", null);
        var test = RemoteControlBridge.TmuxNames(".claude", "test");

        Assert.NotEqual(app.Session, test.Session);

        // The tag lands in the middle now, between the account and the machine
        // name, so the app's whole name is no longer a prefix of the tagged
        // one. What has to be true is that the tag is in there and that the two
        // cannot collide — a tagged relay must not be the app's relay.
        Assert.Contains("-test-", test.Session);
        Assert.StartsWith("claude-buddy-rc--claude", app.Session);
        Assert.StartsWith("claude-buddy-rc--claude", test.Session);
    }

    // A tag is sanitised the same way the account is, for the same reason.
    [Fact]
    public void ATagsDotsAndColonsAreReplacedToo()
    {
        var names = RemoteControlBridge.TmuxNames(".claude", "ci.2:1");

        Assert.DoesNotContain('.', names.Session);
        Assert.DoesNotContain(':', names.Session);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankTagChangesNothing(string? tag)
    {
        Assert.Equal(
            RemoteControlBridge.TmuxNames(".claude", null).Session,
            RemoteControlBridge.TmuxNames(".claude", tag).Session);
    }

    // A blank account falls back to the default rather than producing a nameless
    // relay two accounts would then share.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankAccountUsesTheDefault(string profileDir)
    {
        using var bridge = new RemoteControlBridge(profileDir);

        Assert.Equal(ClaudeBuddySettings.DefaultRemoteControlProfileDir, bridge.ProfileDir);
    }

    // --- Adopt: picking the session up out of the hook's status file ---

    private string StatusFile(string sessionId, string json)
    {
        var path = Path.Combine(_root, sessionId + ".txt");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void AStatusFileWithATranscriptIsAdopted()
    {
        using var bridge = new RemoteControlBridge(".claude");
        var file = StatusFile("95eddb0e-aaaa", """
            {"state":"idle","transcript_path":"/tmp/t.jsonl","tmux_pane":"%30",
             "tmux_socket":"/tmp/tmux-501/default"}
            """);

        Assert.True(bridge.Adopt(file));
    }

    // No transcript path means there is nothing to read, so the relay is not
    // ready — adopting anyway would leave it pumping a file it never had.
    [Theory]
    [InlineData("""{"state":"idle"}""")]
    [InlineData("""{"state":"idle","transcript_path":""}""")]
    [InlineData("""{"state":"idle","transcript_path":"   "}""")]
    public void AStatusFileWithNoTranscriptIsNotAdopted(string json)
    {
        using var bridge = new RemoteControlBridge(".claude");

        Assert.False(bridge.Adopt(StatusFile("s1", json)));
    }

    // Malformed or missing is false rather than a throw: the caller is polling
    // for the file to appear and a half-written one is an ordinary intermediate
    // state, not an error.
    [Fact]
    public void AMalformedOrMissingStatusFileIsRefusedQuietly()
    {
        using var bridge = new RemoteControlBridge(".claude");

        Assert.False(bridge.Adopt(StatusFile("s1", "{ not json")));
        Assert.False(bridge.Adopt(Path.Combine(_root, "does-not-exist.txt")));
    }

    // --- Route: one transcript row in, at most one message out ---

    // Composed rather than interpolated into a raw string: the JSON is itself
    // full of braces, and the brace-counting rules make the nested form more
    // fragile than it is readable.
    // A **user** row, and that is the whole point rather than an arbitrary
    // choice. A message from another machine is handed to the relay the way a
    // person's typing is handed to a session, so it lands in a user row; an
    // assistant row carrying the same tag is the relay's own model quoting the
    // message back while it narrates, which is its own second draft and is
    // deliberately not delivered (BridgeProtocol.ParseInboundMessagesFrom, and
    // MirrorRoutingTests, which pins that rule directly).
    //
    // These rows used to say "assistant", which made every negative case below
    // pass for the wrong reason — empty because the row type disqualified it,
    // not because the content held no message.
    private static string Row(string uuid, string content) =>
        "{\"uuid\":\"" + uuid + "\",\"type\":\"user\","
        + "\"message\":{\"role\":\"user\",\"content\":" + content + "}}";

    private static string ToolResult(string text) =>
        "[{\"type\":\"tool_result\",\"content\":" + JsonSerializer.Serialize(text) + "}]";

    private static string TextBlock(string text) =>
        "[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + "}]";

    // The wire format the relay looks for, as BridgeProtocol documents it:
    // <cross-session-message from="…" from-name="…" from-mode="…">body</…>.
    // Built here rather than by a formatter because the app only ever parses it —
    // the tag is written by the remote machine's own prompt, not by this code.
    private static string Tagged(string fromName, string body) =>
        "<cross-session-message from=\"bridge:session_01SX9H\" from-name=\"" + fromName
        + "\" from-mode=\"prompting\">" + body + "</cross-session-message>";

    private static List<BridgeProtocol.InboundMessage> Collect(
        RemoteControlBridge bridge, params string[] lines)
    {
        var seen = new List<BridgeProtocol.InboundMessage>();
        void Watch(BridgeProtocol.InboundMessage m) => seen.Add(m);

        bridge.MessageReceived += Watch;
        try
        {
            foreach (var line in lines) bridge.Route(line);
        }
        finally
        {
            bridge.MessageReceived -= Watch;
        }

        return seen;
    }

    // A tagged message inside a tool_result is the ordinary case — the relay
    // reads what the remote machine sent by watching the transcript.
    [Fact]
    public void ATaggedMessageInAToolResultIsDelivered()
    {
        using var bridge = new RemoteControlBridge(".claude");
        var tagged = Tagged("mac-mini", "hello there");
        var row = Row("u1", ToolResult(tagged));

        var seen = Collect(bridge, row);

        Assert.Single(seen);
        Assert.Contains("hello there", seen[0].Body);
    }

    // Rows are re-read after a restart or a rewrite, so a row delivered twice
    // would be a duplicate chat bubble. The uuid is what stops it.
    [Fact]
    public void ARowSeenTwiceIsDeliveredOnce()
    {
        using var bridge = new RemoteControlBridge(".claude");
        var tagged = Tagged("mac-mini", "only once");
        var row = Row("u1", ToolResult(tagged));

        var seen = Collect(bridge, row, row, row);

        Assert.Single(seen);
    }

    // Two different rows carrying the same text are two messages: the dedup is
    // on the row's identity, not on what it says, because somebody really can
    // send the same thing twice.
    [Fact]
    public void TwoRowsWithTheSameTextAreTwoMessages()
    {
        using var bridge = new RemoteControlBridge(".claude");
        var tagged = Tagged("mac-mini", "again");
        var content = ToolResult(tagged);

        var seen = Collect(bridge, Row("u1", content), Row("u2", content));

        Assert.Equal(2, seen.Count);
    }

    // A text block is read as well as a tool_result: it is the bridge narrating
    // a reply rather than the reply row itself, and a paraphrase still carries
    // the tag when the model quotes it back.
    [Fact]
    public void ATaggedMessageInATextBlockIsDelivered()
    {
        using var bridge = new RemoteControlBridge(".claude");
        var tagged = Tagged("mac-mini", "narrated");
        var row = Row("u1", TextBlock(tagged));

        Assert.Single(Collect(bridge, row));
    }

    // Content is sometimes a bare string rather than an array of blocks. Both
    // shapes appear in one transcript.
    [Fact]
    public void ContentThatIsABareStringIsRead()
    {
        using var bridge = new RemoteControlBridge(".claude");
        var tagged = Tagged("mac-mini", "bare");

        var row = Row("u1", JsonSerializer.Serialize(tagged));

        Assert.Single(Collect(bridge, row));
    }

    // Untagged text is somebody's ordinary conversation, not a relayed message,
    // and must not appear as one — this is what keeps the user's own transcript
    // out of their chat panel.
    [Fact]
    public void UntaggedTextIsNotAMessage()
    {
        using var bridge = new RemoteControlBridge(".claude");
        var row = Row("u1", """[{"type":"text","text":"just thinking out loud"}]""");

        Assert.Empty(Collect(bridge, row));
    }

    // Rows that are not messages at all are stepped over rather than ending the
    // pump — a transcript is full of them.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("""{"type":"user"}""")]
    [InlineData("""{"uuid":"u1","message":{}}""")]
    [InlineData("""{"uuid":"u1","message":{"content":7}}""")]
    [InlineData("""{"uuid":"u1","message":{"content":[{"type":"thinking"}]}}""")]
    public void ARowThatIsNotAMessageIsIgnored(string line)
    {
        using var bridge = new RemoteControlBridge(".claude");

        Assert.Empty(Collect(bridge, line));
    }

    // A row with no uuid cannot be deduplicated, and is delivered rather than
    // dropped: losing a real message is worse than showing one twice.
    [Fact]
    public void ARowWithNoUuidIsStillDelivered()
    {
        using var bridge = new RemoteControlBridge(".claude");
        var tagged = Tagged("mac-mini", "no uuid");
        var row = "{\"type\":\"user\",\"message\":{\"content\":"
                  + ToolResult(tagged) + "}}";

        Assert.Single(Collect(bridge, row));
    }

    // --- Flatten: a tool_result's content, in both shapes ---

    private static JsonElement Block(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void AStringContentFlattensToItself()
    {
        Assert.Equal("hello", RemoteControlBridge.Flatten(Block("""{"content":"hello"}""")));
    }

    [Fact]
    public void AnArrayContentFlattensToItsTextPartsOnePerLine()
    {
        var flat = RemoteControlBridge.Flatten(
            Block("""{"content":[{"type":"text","text":"one"},{"type":"text","text":"two"}]}"""));

        Assert.Contains("one", flat);
        Assert.Contains("two", flat);
        Assert.True(flat.IndexOf("one", StringComparison.Ordinal) < flat.IndexOf("two", StringComparison.Ordinal));
    }

    // Parts with no text are skipped rather than contributing a blank line, and
    // anything that is not a recognisable content shape flattens to nothing
    // rather than throwing.
    [Theory]
    [InlineData("""{"content":[{"type":"image"},{"type":"text","text":"kept"}]}""", "kept")]
    [InlineData("""{"content":[]}""", "")]
    [InlineData("""{"content":7}""", "")]
    [InlineData("{}", "")]
    public void UnusableContentFlattensToNothing(string json, string expected)
    {
        Assert.Equal(expected, RemoteControlBridge.Flatten(Block(json)).Trim());
    }

    // --- TakeWholeLines: the same carry rule as the chat session's ---

    [Fact]
    public void OnlyCompleteRowsAreTaken()
    {
        using var bridge = new RemoteControlBridge(".claude");

        Assert.Equal(new[] { "a" }, bridge.TakeWholeLines(Encoding.UTF8.GetBytes("a\nb")));
        Assert.Equal(new[] { "b" }, bridge.TakeWholeLines(Encoding.UTF8.GetBytes("\n")));
    }

    [Fact]
    public void AWriteSplittingACharacterDoesNotCorruptIt()
    {
        using var bridge = new RemoteControlBridge(".claude");
        var bytes = Encoding.UTF8.GetBytes("{\"t\":\"café\"}\n");
        var cut = Array.IndexOf(bytes, (byte)0xC3) + 1;

        Assert.Empty(bridge.TakeWholeLines(bytes[..cut]));
        var lines = bridge.TakeWholeLines(bytes[cut..]);

        Assert.Equal("{\"t\":\"café\"}", lines[0]);
        Assert.DoesNotContain('�', lines[0]);
    }

    // --- Pump: the tail of a real transcript file on disk ---

    // Pump is the half of this class that owns a file offset, and the offset is
    // where the interesting mistakes live: read the same bytes twice and every
    // remote message arrives twice, carry a stale offset and the relay reads
    // from the middle of an unrelated row forever. All of it is reachable with a
    // real file, since Adopt takes the transcript path from a status file.

    // A bridge that has adopted nothing has no file to read, and must return
    // rather than reach for a null path.
    [Fact]
    public void PumpingBeforeAnythingIsAdoptedDoesNothing()
    {
        using var bridge = new RemoteControlBridge(".claude");

        bridge.Pump();   // no throw is the assertion
    }

    // A transcript that has been deleted under us — or is mid-write — is an
    // ordinary state, not an error: the next tick tries again.
    [Fact]
    public void PumpingAMissingTranscriptIsQuiet()
    {
        using var bridge = new RemoteControlBridge(".claude");
        var missing = Path.Combine(_root, "gone-" + Guid.NewGuid() + ".jsonl");
        Assert.True(bridge.Adopt(StatusFile("s-missing", TranscriptStatus(missing))));

        bridge.Pump();
    }

    [Fact]
    public void PumpReadsAWholeLineAndRoutesIt()
    {
        var transcript = NewTranscript();
        using var bridge = new RemoteControlBridge(".claude");
        Assert.True(bridge.Adopt(StatusFile("s-read", TranscriptStatus(transcript))));

        var seen = Watched(bridge, () =>
        {
            File.AppendAllText(transcript,
                Row("u1", ToolResult(Tagged("Zara", "hello from away"))) + "\n");
            bridge.Pump();
        });

        var message = Assert.Single(seen);
        Assert.Equal("hello from away", message.Body);
    }

    // The offset is the point: pumping again with nothing new added must produce
    // nothing. Without it every poll would re-deliver the whole transcript.
    [Fact]
    public void PumpingTwiceDoesNotDeliverTheSameMessageTwice()
    {
        var transcript = NewTranscript();
        using var bridge = new RemoteControlBridge(".claude");
        bridge.Adopt(StatusFile("s-twice", TranscriptStatus(transcript)));

        File.AppendAllText(transcript,
            Row("u1", ToolResult(Tagged("Zara", "only once"))) + "\n");

        var first = Watched(bridge, bridge.Pump);
        var second = Watched(bridge, bridge.Pump);

        Assert.Single(first);
        Assert.Empty(second);
    }

    // A /clear on the far side starts a new transcript, which is shorter than
    // where we had got to. Carrying the old offset would read from the middle of
    // an unrelated row forever, so the offset resets and the new file is read
    // from the top.
    [Fact]
    public void AReplacedTranscriptIsReadFromTheStartAgain()
    {
        var transcript = NewTranscript();
        using var bridge = new RemoteControlBridge(".claude");
        bridge.Adopt(StatusFile("s-clear", TranscriptStatus(transcript)));

        // Something long, consumed.
        File.AppendAllText(transcript,
            Row("u1", ToolResult(Tagged("Zara", new string('x', 400)))) + "\n");
        Assert.Single(Watched(bridge, bridge.Pump));

        // Replaced by something shorter — a fresh conversation.
        File.WriteAllText(transcript,
            Row("u2", ToolResult(Tagged("Kai", "after the clear"))) + "\n");

        var seen = Watched(bridge, bridge.Pump);

        var message = Assert.Single(seen);
        Assert.Equal("after the clear", message.Body);
    }

    // A row still being written arrives as a partial line. It has to be held
    // until its newline shows up, not parsed as-is — the relay is reading a file
    // another process is appending to.
    [Fact]
    public void APartialRowIsHeldUntilItIsComplete()
    {
        var transcript = NewTranscript();
        using var bridge = new RemoteControlBridge(".claude");
        bridge.Adopt(StatusFile("s-partial", TranscriptStatus(transcript)));

        var row = Row("u1", ToolResult(Tagged("Zara", "half written")));
        var cut = row.Length / 2;

        File.AppendAllText(transcript, row[..cut]);
        Assert.Empty(Watched(bridge, bridge.Pump));

        File.AppendAllText(transcript, row[cut..] + "\n");
        var seen = Watched(bridge, bridge.Pump);

        Assert.Equal("half written", Assert.Single(seen).Body);
    }

    // An empty transcript is the state right after adoption — the file exists
    // because the hook made it, and nothing has been written yet.
    [Fact]
    public void PumpingAnEmptyTranscriptProducesNothing()
    {
        var transcript = NewTranscript();
        using var bridge = new RemoteControlBridge(".claude");
        bridge.Adopt(StatusFile("s-empty", TranscriptStatus(transcript)));

        Assert.Empty(Watched(bridge, bridge.Pump));
    }

    private string NewTranscript()
    {
        var path = Path.Combine(_root, "t-" + Guid.NewGuid() + ".jsonl");
        File.WriteAllText(path, "");
        return path;
    }

    private static string TranscriptStatus(string transcriptPath) =>
        "{\"state\":\"idle\",\"transcript_path\":"
        + JsonSerializer.Serialize(transcriptPath)
        + ",\"tmux_pane\":\"%30\",\"tmux_socket\":\"/tmp/tmux-501/default\"}";

    // --- Args: which tmux server every command is aimed at ---

    // A pane id is only unique within one tmux server, and several can coexist —
    // the relay runs its own. So every command carries -S <socket> once one is
    // known, and getting that wrong does not fail: it sends keystrokes to a pane
    // of the same number on somebody else's server.
    //
    // TerminalScripts.TmuxArgs makes the same decision for the local CLI and is
    // tested separately. This is the bridge's own copy, and two copies of a rule
    // is exactly when you want both asserted.
    [Fact]
    public void WithNoSocketKnownTheArgumentsArePassedThrough()
    {
        using var bridge = new RemoteControlBridge(".claude");

        Assert.Equal(new[] { "send-keys", "-t", "%30" },
            bridge.Args("send-keys", "-t", "%30"));
    }

    [Fact]
    public void OnceASocketIsKnownEveryCommandNamesIt()
    {
        using var bridge = new RemoteControlBridge(".claude");
        var transcript = NewTranscript();
        Assert.True(bridge.Adopt(StatusFile("s-args", TranscriptStatus(transcript))));

        var args = bridge.Args("send-keys", "-t", "%30");

        Assert.Equal(
            new[] { "-S", "/tmp/tmux-501/default", "send-keys", "-t", "%30" },
            args);
    }

    // The socket goes in front, because tmux only accepts -S before the command.
    [Fact]
    public void TheSocketIsPrefixedRatherThanAppended()
    {
        using var bridge = new RemoteControlBridge(".claude");
        var transcript = NewTranscript();
        bridge.Adopt(StatusFile("s-args2", TranscriptStatus(transcript)));

        var args = bridge.Args("kill-session");

        Assert.Equal("-S", args[0]);
        Assert.Equal("kill-session", args[^1]);
    }

    [Fact]
    public void AnEmptyCommandStillGetsItsSocket()
    {
        using var bridge = new RemoteControlBridge(".claude");
        var transcript = NewTranscript();
        bridge.Adopt(StatusFile("s-args3", TranscriptStatus(transcript)));

        Assert.Equal(new[] { "-S", "/tmp/tmux-501/default" }, bridge.Args());
    }

    private static List<BridgeProtocol.InboundMessage> Watched(
        RemoteControlBridge bridge, Action act)
    {
        var seen = new List<BridgeProtocol.InboundMessage>();
        void Watch(BridgeProtocol.InboundMessage m) => seen.Add(m);

        bridge.MessageReceived += Watch;
        try
        {
            act();
        }
        finally
        {
            bridge.MessageReceived -= Watch;
        }

        return seen;
    }

    // A fresh bridge has nothing to warn about. Warning is the line the settings
    // window shows beside the relay's state — a login expiring, most often — and
    // it has to start empty rather than carrying whatever the last bridge said.
    [Fact]
    public void AFreshBridgeHasNoWarning()
    {
        using var bridge = new RemoteControlBridge(".claude");

        Assert.Null(bridge.Warning);
    }

    // A content block with no "type" is skipped rather than taken as some default
    // kind. These rows come off another machine's transcript, so a shape this
    // version has not seen is an ordinary event and not a reason to stop reading
    // the rest of the message.
    [Fact]
    public void AContentBlockWithNoTypeIsSkipped()
    {
        using var bridge = new RemoteControlBridge(".claude");

        var row = "{\"uuid\":\"u1\",\"type\":\"user\",\"message\":{\"role\":\"user\","
                + "\"content\":[{\"text\":\"no type here\"}]}}";

        Assert.Empty(Collect(bridge, row));
    }

    // And a block whose type is not a string takes the same route rather than
    // throwing on the cast.
    [Fact]
    public void AContentBlockWhoseTypeIsNotAStringIsSkipped()
    {
        using var bridge = new RemoteControlBridge(".claude");

        var row = "{\"uuid\":\"u1\",\"type\":\"user\",\"message\":{\"role\":\"user\","
                + "\"content\":[{\"type\":7}]}}";

        Assert.Empty(Collect(bridge, row));
    }

    // --- CB-41: a message the relay absorbed into a turn already running ---

    // The shape Claude Code 2.1.251 actually writes, copied from the transcript
    // this bug was found in. Note there is no `message` property at all — which
    // is how these were being dropped: Route required one and returned before it
    // ever reached the row-type rule.
    private static string Absorbed(string uuid, string prompt, string kind = "queued_command") =>
        "{\"parentUuid\":\"p1\",\"isSidechain\":false,\"attachment\":{\"type\":\"" + kind
        + "\",\"prompt\":" + JsonSerializer.Serialize(prompt)
        + ",\"source_uuid\":\"s1\",\"commandMode\":\"prompt\",\"origin\":{\"kind\":\"human\"}},"
        + "\"type\":\"attachment\",\"uuid\":\"" + uuid + "\",\"userType\":\"external\"}";

    // The regression test. A relay is mid-turn most of the time — Buddy's own
    // poll keeps it working — so a reply arriving from the far machine is
    // usually queued and folded into the running turn rather than delivered as
    // a turn of its own. Every one of those was lost, which is why the panel
    // said "no live view" while the far Buddy was demonstrably answering.
    [Fact]
    public void AMessageAbsorbedIntoARunningTurnIsDelivered()
    {
        using var bridge = new RemoteControlBridge(".claude");
        var row = Absorbed("a1", Tagged("mac-mini", "the roster you asked for"));

        var seen = Collect(bridge, row);

        Assert.Single(seen);
        Assert.Equal("mac-mini", seen[0].FromName);
        Assert.Contains("the roster you asked for", seen[0].Body);
    }

    // `attachment` is a catch-all row type — a token reminder wears it, and so
    // does a file-history snapshot. The nested attachment.type is what actually
    // says this is a queued command, and anything else is left alone however
    // much of a message its text looks like.
    [Theory]
    [InlineData("total_tokens_reminder")]
    [InlineData("file_history_snapshot")]
    [InlineData("")]
    public void AnotherKindOfAttachmentIsNotAMessage(string kind)
    {
        using var bridge = new RemoteControlBridge(".claude");

        Assert.Empty(Collect(bridge, Absorbed("a1", Tagged("mac-mini", "not from here"), kind)));
    }

    // The malformed shapes, each stepped over rather than throwing on the cast.
    // A transcript carries plenty of rows this code has no business reading, and
    // an exception here would end the pump for every row after it.
    [Theory]
    // No attachment at all.
    [InlineData("""{"uuid":"a1","type":"attachment"}""")]
    // An attachment that is not an object.
    [InlineData("""{"uuid":"a1","type":"attachment","attachment":"nope"}""")]
    // No type on the attachment.
    [InlineData("""{"uuid":"a1","type":"attachment","attachment":{"prompt":"x"}}""")]
    // A type that is not a string.
    [InlineData("""{"uuid":"a1","type":"attachment","attachment":{"type":7,"prompt":"x"}}""")]
    // The right type, but no prompt to read.
    [InlineData("""{"uuid":"a1","type":"attachment","attachment":{"type":"queued_command"}}""")]
    // A prompt that is not a string.
    [InlineData("""{"uuid":"a1","type":"attachment","attachment":{"type":"queued_command","prompt":7}}""")]
    public void AnUnreadableAttachmentIsIgnored(string line)
    {
        using var bridge = new RemoteControlBridge(".claude");

        Assert.Empty(Collect(bridge, line));
    }

    // An absorbed row carrying no tag is Buddy's own queued prompt being folded
    // in — "Call the ListAgents tool now", which is what keeps the relay busy in
    // the first place. It is not a message and must not become a chat bubble.
    [Fact]
    public void AnAbsorbedPromptWithNoTagIsNotAMessage()
    {
        using var bridge = new RemoteControlBridge(".claude");

        Assert.Empty(Collect(bridge, Absorbed("a1", "Call the ListAgents tool now.")));
    }

    // The uuid dedup covers these the same way it covers a user row: the pump
    // re-reads from the start of the file after a restart.
    [Fact]
    public void AnAbsorbedRowSeenTwiceIsDeliveredOnce()
    {
        using var bridge = new RemoteControlBridge(".claude");
        var row = Absorbed("a1", Tagged("mac-mini", "only once"));

        Assert.Single(Collect(bridge, row, row, row));
    }

    // Two peers answering into one absorbed prompt is ordinary once frames are
    // in flight, and reading only the first would drop the rest silently — the
    // same bug ParseInboundMessages was made plural for.
    [Fact]
    public void EveryMessageInAnAbsorbedPromptIsDelivered()
    {
        using var bridge = new RemoteControlBridge(".claude");
        var row = Absorbed("a1", Tagged("mac-mini", "first") + "\n" + Tagged("avatar", "second"));

        var seen = Collect(bridge, row);

        Assert.Equal(2, seen.Count);
        Assert.Equal("mac-mini", seen[0].FromName);
        Assert.Equal("avatar", seen[1].FromName);
    }

    // --- CB-41 end to end, on the bytes it was found on -----------------------

    // The real roster reply from the mini, copied verbatim out of the client
    // relay's transcript — the exact frame that arrived three times on the
    // afternoon this was diagnosed and was dropped all three times.
    //
    // Kept whole rather than rebuilt from a formatter, because a fixture written
    // from memory is what this repository has been caught by before: the
    // permission-dialog parser was first written against an invented one and
    // failed on every real dialog. The payload carries its own SHA-256, so a
    // fixture that had been retyped or reflowed would fail its own integrity
    // check here rather than passing quietly.
    private const string RealAbsorbedFrame =
        "CB-MIRROR:v1;t=CHUNK;id=d06dd46b;seq=0;of=1;H=389e729755ece3c54e2193dbc3e00612302947e3cd0721e5370574d8ea8e77d1;p=H4sIAAAAAAAAE1VTy3LkIAz8F59D5Z5fSeWAQba1A4ji4Rlna/99ZQlPJqfuFgJaQnz+nZKNMH1Mf2g2W08NionWmYgJp7fJBeQ1F2z3wLIVm6ormNv00UqHtynbxLsXGyoLR4EK568FIJ27KUabfJ0+Pqd3673xWDjMdMdKSldIrQrLORxPYtAJLw0X60ZGb2QieTAVWs9XiG/JnHLJBR8my9mzdbe1UE9eVXObEC5isHYX6OsJTtLcViiCMKna2IyqwMqp7jRQYEe4qwzeYGqFhqKR9TTlKC24DtbgMYL5UKyivUUuuvT0S4wueNvsjt9CYbj1AJltVHalxXiouCYTaMX0ouuR3JC3RtI0j8uiWB3tUF65Oc/9FRgWyDWtDJaFiniGBw7MI7JYrWYB8Gf3ld/5lAwlYq1IiR+He6MvupDrgxRJXiOXbp6jsJINJ25gs+8xKw+KRDfZizKa7xgvFzy6A7kDm97EvNkQzIpt6/N5w2u0BjZ7BW9wzJg8plV2BqzN/Izps79MqDdl2tfoFCBSEfuRZgygzINUkm2tIAf9dERlsEmxjwsyceN0zHPB3brjnPt2+SrWo8xcAWezkkgNzDllhYJG5HcLq/1i1+gWuKP+jQq8yRu38c+AEegF2/Ey6JUbHHCRwuoNQxAX3L+mT1j73GyVVzxRYg0CXK/SNlADPfM8A/+ZmR6qV65Flyo3+iQ8eOOqHeMJd5inr39f/wHxv0qosAQAAA==;h=389e729755ece3c54e2193dbc3e00612302947e3cd0721e5370574d8ea8e77d1";

    private const string RealAbsorbedPrompt =
        "<cross-session-message from=\"bridge:session_01Usy6tz755EvmaQpkKGARMA\" from-name=\"claude-buddy-rc--claude-board-avatar\" from-mode=\"prompting\">\n"
        + RealAbsorbedFrame
        + "\n</cross-session-message>";

    // The whole hop, on real bytes: the row Claude Code wrote when the reply
    // landed mid-turn, through Route, out as a message, recognised as a frame,
    // reassembled, hash-checked and decoded into the roster the panel needed.
    //
    // Every one of those steps already worked. The first one is the only one
    // that did not, and it is the reason the live view never opened.
    [Fact]
    public void TheRealRosterReplyThatWasBeingDroppedArrivesAndDecodes()
    {
        using var bridge = new RemoteControlBridge(".claude");

        var seen = Collect(bridge, Absorbed("a1", RealAbsorbedPrompt));

        var only = Assert.Single(seen);
        Assert.Equal("claude-buddy-rc--claude-board-avatar", only.FromName);

        // Recognised as a frame rather than as something to show a person —
        // the test that RemoteControlSessions applies before handing it on.
        Assert.True(MirrorProtocol.IsFrame(only.Body));

        var frame = MirrorProtocol.TryParseFrame(only.Body);
        Assert.NotNull(frame);
        Assert.Equal(MirrorProtocol.Chunk, frame!.Type);
        Assert.Equal("d06dd46b", frame.Id);

        // Its own integrity check, which is also what proves the fixture is the
        // original bytes and not a retyping of them.
        var assembly = new MirrorProtocol.MirrorAssembly();
        var result = assembly.Offer(frame);
        Assert.Equal(MirrorProtocol.AssemblyState.Complete, result.State);

        var entries = MirrorProtocol.DecodeRoster(result.Payload!);
        var entry = Assert.Single(entries!);

        Assert.Equal("job-hunter-mac-mini", entry.Name);

        // The field the panel actually turns on: without it the session is
        // settled as unavailable and the panel says there is no live view.
        Assert.True(entry.HasTranscript);
    }
}
