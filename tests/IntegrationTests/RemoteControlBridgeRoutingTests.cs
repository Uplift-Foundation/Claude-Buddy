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

        // The bug this prevents, stated as the condition that caused it: one
        // session name really is a prefix of the other, so without the "=" the
        // shorter target would resolve to the longer session.
        Assert.StartsWith(claude.Session, board.Session);
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
        Assert.StartsWith(app.Session, test.Session);
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
    private static string Row(string uuid, string content) =>
        "{\"uuid\":\"" + uuid + "\",\"type\":\"assistant\","
        + "\"message\":{\"role\":\"assistant\",\"content\":" + content + "}}";

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
        var row = "{\"type\":\"assistant\",\"message\":{\"content\":"
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
}
