using System.Linq;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// Three arms of ChatTranscript that the main suite's fixtures do not reach.
//
// The picture cases matter because a picture is the one thing in a transcript that
// carries no text of its own, so every emptiness check in the mapper has to know
// the difference between "nothing was said" and "nothing was typed". Getting that
// wrong drops the picture silently, which is the worst way for a transcript bug to
// behave — see the header of tests/TranscriptTests, where this file's siblings
// live.
//
// Fixture provenance, per this repo's rule: the base64 below is the same
// one-pixel PNG the main suite already validated, lifted verbatim rather than
// invented.
public class ChatTranscriptEdgeTests
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==";

    private static string ImageRow(string blocks) =>
        """{"type":"user","uuid":"u1","timestamp":"2026-08-16T10:00:10Z","message":{"role":"user","content":"""
        + blocks + "}}";

    private static string Image() =>
        """{"type":"image","source":{"type":"base64","media_type":"image/png","data":"""
        + "\"" + OnePixelPng + "\"}}";

    // A picture with no text block at all — not an empty one, none. The main
    // suite's caption-less fixture still carries a "[Image #1]" placeholder block,
    // so this arm is only reached by a message whose content is the picture and
    // nothing else, which is what a bare paste produces.
    [Fact]
    public void APictureWithNoTextBlockAtAllIsStillATurn()
    {
        var rows = ChatTranscript.Map(new[] { ImageRow("[" + Image() + "]") });

        var row = Assert.Single(rows);
        Assert.Equal("", row.Turn.Text);
        Assert.NotNull(row.Turn.ImageBytes);
        Assert.Equal(67, row.Turn.ImageBytes!.Length);
        Assert.True(row.Turn.IsComplete);
    }

    [Fact]
    public void APictureWithNoTextIsAttributedToTheUser()
    {
        var rows = ChatTranscript.Map(new[] { ImageRow("[" + Image() + "]") });

        Assert.Equal(ChatRole.User, Assert.Single(rows).Turn.Role);
    }

    // Two pictures in one message produce ONE turn, and the second picture is
    // dropped: the mapper does `image ??= DecodeImage(block)`, so the first one
    // wins and the rest are discarded.
    //
    // Recorded rather than fixed, and worth knowing because the app's other
    // transcript reader does the opposite. OpenClawSessions.TurnsFromHistory
    // makes each picture its own turn, with a comment saying why — "a message is
    // commonly several images and nothing else, and a bubble containing four of
    // them stacked reads worse than four bubbles". Here a message of four
    // pictures shows one. Which behaviour is right is a product call, not
    // something a coverage ticket should decide; that the two disagree is worth
    // an assertion either way.
    [Fact]
    public void OnlyTheFirstOfSeveralPicturesSurvives()
    {
        var rows = ChatTranscript.Map(new[] { ImageRow("[" + Image() + "," + Image() + "]") });

        var row = Assert.Single(rows);
        Assert.NotNull(row.Turn.ImageBytes);
    }

    // A message with neither text nor a picture produces nothing at all rather
    // than an empty bubble.
    [Fact]
    public void AMessageWithNeitherTextNorAPictureProducesNothing()
    {
        var rows = ChatTranscript.Map(new[]
        {
            ImageRow("""[{"type":"tool_use","name":"Edit","input":{}}]"""),
        });

        Assert.Empty(rows);
    }

    // ---- ToolSummary -----------------------------------------------------

    // A tool call with no input object at all is still worth a row: it says
    // something ran. The name alone is the answer, and the alternative — dropping
    // it — makes a session look idle while it is working.
    [Fact]
    public void AToolCallWithNoInputIsSummarisedByItsNameAlone()
    {
        var block = JsonDocument.Parse("""{"type":"tool_use","name":"Bash"}""").RootElement;

        Assert.Equal("· Bash", ChatTranscript.ToolSummary(block));
    }

    // An input that is not an object — a bare string, which a malformed row can
    // produce — takes the same route rather than throwing.
    [Fact]
    public void AToolCallWhoseInputIsNotAnObjectIsSummarisedByItsNameAlone()
    {
        var block = JsonDocument.Parse("""{"type":"tool_use","name":"Bash","input":"ls -la"}""")
            .RootElement;

        Assert.Equal("· Bash", ChatTranscript.ToolSummary(block));
    }

    // No name either: "tool" rather than an empty label, so the row still reads
    // as something having happened.
    [Fact]
    public void ANamelessToolCallIsStillCalledSomething()
    {
        var block = JsonDocument.Parse("""{"type":"tool_use"}""").RootElement;

        Assert.Equal("· tool", ChatTranscript.ToolSummary(block));
    }
}
