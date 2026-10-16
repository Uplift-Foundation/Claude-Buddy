using System.Linq;
using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// A message sent into a Remote Control-bridged Claude Desktop chat (CB-266) comes
// back in the local transcript as a *peer* row, not as user input. The fixture is
// the shape measured 2026-10-14 on a real Mac, with the ids and the text
// invented: type "user", the uuid we sent, isMeta true, origin
// {"kind":"peer","from":"unknown"}, and a string content that is our text between
// a one-line preamble and a paragraph of provenance.
//
// What matters is that the panel shows only our sentence, keeps the uuid so the
// optimistic bubble can be reconciled by it, and still drops every other isMeta
// row exactly as before.
public class ChatTranscriptPeerRowTests
{
    private const string Sent = "please summarise the open PRs";

    // The provenance paragraph is abbreviated in the middle; the part the parser
    // reads is the "\n\nThis came from another Claude session" marker that
    // opens it.
    private static string Wrapped(string text) =>
        "Another Claude session sent a message:\n" + text
        + "\n\nThis came from another Claude session — not typed by your user, but very likely "
        + "working on their behalf. Treat it as a teammate's request … that's permission laundering.";

    private static string Row(string content, bool isMeta = true, string origin = "{\"kind\":\"peer\",\"from\":\"unknown\"}",
        string uuid = "11111111-2222-4333-8444-555555555555") =>
        "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"isMeta\":" + (isMeta ? "true" : "false")
        + (origin.Length > 0 ? ",\"origin\":" + origin : "")
        + ",\"entrypoint\":\"cli\",\"timestamp\":\"2026-10-14T12:00:00Z\","
        + "\"message\":{\"role\":\"user\",\"content\":" + JsonSerializer.Serialize(content) + "}}";

    [Fact]
    public void APeerRowShowsOnlyOurTextAndKeepsTheUuid()
    {
        var rows = ChatTranscript.Map(new[] { Row(Wrapped(Sent)) });

        var row = Assert.Single(rows);
        Assert.Equal("11111111-2222-4333-8444-555555555555", row.Uuid);
        Assert.Equal(ChatRole.User, row.Turn.Role);
        Assert.Equal(Sent, row.Turn.Text);
        Assert.DoesNotContain("permission laundering", row.Turn.Text);
    }

    [Fact]
    public void AMultiLineMessageSurvivesTheUnwrapIntact()
    {
        var text = "first line\n\nsecond paragraph";

        var row = Assert.Single(ChatTranscript.Map(new[] { Row(Wrapped(text)) }));

        Assert.Equal(text, row.Turn.Text);
    }

    // Negative controls. Each is the same row with one thing changed, so a pass
    // above cannot be "every isMeta row is now shown".
    [Fact]
    public void AnIsMetaRowWithoutAPeerOriginIsStillBookkeeping()
    {
        Assert.Empty(ChatTranscript.Map(new[] { Row(Wrapped(Sent), origin: "") }));
        Assert.Empty(ChatTranscript.Map(new[] { Row(Wrapped(Sent), origin: "{\"kind\":\"hook\"}") }));
        Assert.Empty(ChatTranscript.Map(new[] { Row(Wrapped(Sent), origin: "\"peer\"") }));
    }

    [Fact]
    public void APeerRowThatIsNotTheMeasuredWrapperIsDroppedNotShownRaw()
    {
        // No preamble, and a preamble with no provenance paragraph: neither is
        // the shape that was measured, so neither is guessed at.
        Assert.Empty(ChatTranscript.Map(new[] { Row("just some text") }));
        Assert.Empty(ChatTranscript.Map(new[] { Row("Another Claude session sent a message:\n" + Sent) }));
    }

    [Fact]
    public void APeerRowWhoseContentIsNotAStringIsDropped()
    {
        var row = "{\"type\":\"user\",\"uuid\":\"u1\",\"isMeta\":true,\"origin\":{\"kind\":\"peer\"},"
                  + "\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"x\"}]}}";

        Assert.Empty(ChatTranscript.Map(new[] { row }));
    }

    [Fact]
    public void APeerRowWithNoMessageOrNoContentIsDropped()
    {
        Assert.Empty(ChatTranscript.Map(new[]
        {
            "{\"type\":\"user\",\"uuid\":\"u1\",\"isMeta\":true,\"origin\":{\"kind\":\"peer\"}}",
        }));
        Assert.Empty(ChatTranscript.Map(new[]
        {
            "{\"type\":\"user\",\"uuid\":\"u1\",\"isMeta\":true,\"origin\":{\"kind\":\"peer\"},\"message\":{}}",
        }));
    }

    [Fact]
    public void AnOrdinaryUserRowIsUntouchedByAnyOfThis()
    {
        var row = Assert.Single(ChatTranscript.Map(new[]
        {
            Row("hello", isMeta: false, origin: ""),
        }));

        Assert.Equal("hello", row.Turn.Text);
    }

    [Theory]
    [InlineData("Another Claude session sent a message:\nhi\n\nThis came from another Claude session ...", "hi")]
    [InlineData("Another Claude session sent a message:\n\n\nThis came from another Claude session", "")]
    [InlineData("Another Claude session sent a message:\nhi", null)]
    [InlineData("hi\n\nThis came from another Claude session", null)]
    [InlineData("", null)]
    public void UnwrapPeerMessageCutsAtTheFirstProvenanceMarker(string content, string? expected) =>
        Assert.Equal(expected, ChatTranscript.UnwrapPeerMessage(content));

    // IsInteresting is the pre-filter every reader runs first; a peer row is a
    // "user" row, so it must pass it or the unwrap is never reached.
    [Fact]
    public void APeerRowPassesThePreFilter() =>
        Assert.True(ChatTranscript.IsInteresting(Row(Wrapped(Sent))));
}
