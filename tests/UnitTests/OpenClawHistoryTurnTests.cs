using System;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// One page of the gateway's chat.history, turned into turns the panel can draw.
//
// A format nobody here controls, which is the whole reason this is worth testing
// against fixtures rather than reading — the same argument that keeps
// ChatTranscript and CodexTranscript pure and separately covered.
//
// The bug this parser's own comment records is the one to hold onto: the two roles
// are shaped differently. An assistant turn carries `content` as a list of
// blocks; a user turn carries it as a plain string. Reading only the block form
// showed an agent talking to nobody — half the conversation silently gone, with
// nothing on screen to suggest anything was missing.
//
// Serialised because the parser resolves speaker names and colours through the
// process-wide identity table.
[Collection("Settings")]
public class OpenClawHistoryTurnTests
{
    private static JsonElement Messages(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static System.Collections.Generic.List<(ChatRole Role, string Text,
        string? ImageUrl, string ImageAlt, DateTimeOffset At, string? Speaker,
        string? SpeakerColor)> Turns(string json) =>
        OpenClawSessions.TurnsFromHistory(Messages(json));

    // ---- the two role shapes --------------------------------------------

    // A user turn's content is a plain string. This is the half that was being
    // dropped.
    [Fact]
    public void AUserTurnCarriesItsContentAsAString()
    {
        var turns = Turns("""[{"role":"user","content":"fix the arrangement test"}]""");

        var turn = Assert.Single(turns);
        Assert.Equal(ChatRole.User, turn.Role);
        Assert.Equal("fix the arrangement test", turn.Text);
    }

    // An assistant turn's content is a list of blocks, and only the text ones are
    // worth showing — a replayed tool_use block would be a wall of JSON, and tool
    // calls arrive live as their own turns anyway.
    [Fact]
    public void AnAssistantTurnCarriesBlocksAndOnlyTheTextOnesAreShown()
    {
        var turns = Turns("""
        [{"role":"assistant","content":[
            {"type":"text","text":"Fixed the nested-team case."},
            {"type":"tool_use","name":"Edit","input":{"file":"a.cs"}}]}]
        """);

        var turn = Assert.Single(turns);
        Assert.Equal(ChatRole.Assistant, turn.Role);
        Assert.Equal("Fixed the nested-team case.", turn.Text);
        Assert.DoesNotContain("tool_use", turn.Text);
    }

    // Both shapes in one page, which is what a real conversation is.
    [Fact]
    public void BothShapesInOnePageAreBothRead()
    {
        var turns = Turns("""
        [{"role":"user","content":"is it green?"},
         {"role":"assistant","content":[{"type":"text","text":"yes"}]}]
        """);

        Assert.Equal(2, turns.Count);
        Assert.Equal(ChatRole.User, turns[0].Role);
        Assert.Equal(ChatRole.Assistant, turns[1].Role);
    }

    // Several text blocks in one message are joined rather than becoming several
    // bubbles — they are one thing the agent said.
    [Fact]
    public void SeveralTextBlocksAreJoinedIntoOneTurn()
    {
        var turns = Turns("""
        [{"role":"assistant","content":[
            {"type":"text","text":"first line"},
            {"type":"text","text":"second line"}]}]
        """);

        var turn = Assert.Single(turns);
        Assert.Contains("first line", turn.Text);
        Assert.Contains("second line", turn.Text);
    }

    // A third shape, tolerated: content as a single object with a text field.
    [Fact]
    public void ContentAsASingleObjectIsRead()
    {
        var turns = Turns("""[{"role":"assistant","content":{"text":"just this"}}]""");

        Assert.Equal("just this", Assert.Single(turns).Text);
    }

    // Any role that is not "user" is treated as the assistant, so a role this
    // version has not seen still shows up rather than vanishing.
    [Fact]
    public void AnUnknownRoleIsTreatedAsTheAssistant()
    {
        var turns = Turns("""[{"role":"tool","content":"something"}]""");

        Assert.Equal(ChatRole.Assistant, Assert.Single(turns).Role);
    }

    // ---- what is dropped -------------------------------------------------

    [Fact]
    public void AMessageWithNoContentAtAllIsSkipped()
    {
        Assert.Empty(Turns("""[{"role":"user"}]"""));
    }

    [Fact]
    public void AMessageWhoseTextIsBlankIsSkipped()
    {
        Assert.Empty(Turns("""[{"role":"user","content":"   "}]"""));
        Assert.Empty(Turns("""[{"role":"assistant","content":[{"type":"text","text":""}]}]"""));
    }

    // A message with only non-text blocks produces nothing rather than an empty
    // bubble.
    [Fact]
    public void AMessageOfOnlyToolBlocksIsSkipped()
    {
        Assert.Empty(Turns("""
        [{"role":"assistant","content":[{"type":"tool_use","name":"Edit"}]}]
        """));
    }

    [Fact]
    public void AnEmptyPageProducesNoTurns()
    {
        Assert.Empty(Turns("[]"));
    }

    // The resumed-session notice goes through Readable, which drops it — so it is
    // not drawn even though it arrives as an ordinary user-role message.
    [Fact]
    public void TheResumedSessionNoticeDoesNotBecomeATurn()
    {
        Assert.Empty(Turns(
            """[{"role":"user","content":"OpenClaw resumed this CLI session after a restart"}]"""));
    }

    // ---- pictures --------------------------------------------------------

    // A picture is its own turn rather than being folded into the text of one. A
    // message is commonly several images and nothing else, and one bubble holding
    // four of them stacked reads worse than four bubbles.
    [Fact]
    public void AnImageBlockBecomesItsOwnTurn()
    {
        var turns = Turns("""
        [{"role":"user","content":[{"type":"image","url":"https://x/a.png","alt":"a graph"}]}]
        """);

        var turn = Assert.Single(turns);
        Assert.Equal("https://x/a.png", turn.ImageUrl);
        Assert.Equal("a graph", turn.ImageAlt);
        Assert.Equal("", turn.Text);
    }

    [Fact]
    public void SeveralImagesBecomeSeveralTurns()
    {
        var turns = Turns("""
        [{"role":"user","content":[
            {"type":"image","url":"https://x/a.png"},
            {"type":"image","url":"https://x/b.png"},
            {"type":"image","url":"https://x/c.png"}]}]
        """);

        Assert.Equal(3, turns.Count);
        Assert.All(turns, t => Assert.NotNull(t.ImageUrl));
    }

    // Text and a picture in one message produce both, with the picture first —
    // and the text turn carries no image, so the panel does not draw it twice.
    [Fact]
    public void TextAndAPictureBecomeTwoTurns()
    {
        var turns = Turns("""
        [{"role":"assistant","content":[
            {"type":"image","url":"https://x/a.png"},
            {"type":"text","text":"here it is"}]}]
        """);

        Assert.Equal(2, turns.Count);
        Assert.Equal("https://x/a.png", turns[0].ImageUrl);
        Assert.Equal("here it is", turns[1].Text);
        Assert.Null(turns[1].ImageUrl);
    }

    // An image block with no url is skipped rather than becoming a turn the panel
    // would try and fail to fetch.
    [Fact]
    public void AnImageWithNoUrlIsSkipped()
    {
        Assert.Empty(Turns("""
        [{"role":"user","content":[{"type":"image","alt":"nothing"}]}]
        """));
    }

    [Fact]
    public void AnImageWithNoAltGetsAnEmptyAltRatherThanNull()
    {
        var turns = Turns("""
        [{"role":"user","content":[{"type":"image","url":"https://x/a.png"}]}]
        """);

        Assert.Equal("", Assert.Single(turns).ImageAlt);
    }

    // ---- timestamps ------------------------------------------------------

    [Fact]
    public void AUnixMillisecondTimestampIsRead()
    {
        var turns = Turns("""
        [{"role":"user","content":"hello","timestamp":1787000000000}]
        """);

        var expected = DateTimeOffset.FromUnixTimeMilliseconds(1787000000000).ToLocalTime();
        Assert.Equal(expected, Assert.Single(turns).At);
    }

    // No timestamp falls back to now rather than to 1970, which would sort the
    // whole page to the top of a merged room view and be read as the oldest thing
    // anyone said.
    [Fact]
    public void AMissingTimestampFallsBackToNowRatherThanTheEpoch()
    {
        var before = DateTimeOffset.Now.AddMinutes(-1);

        var turns = Turns("""[{"role":"user","content":"hello"}]""");

        Assert.True(Assert.Single(turns).At > before);
    }

    [Fact]
    public void AZeroTimestampAlsoFallsBackToNow()
    {
        var before = DateTimeOffset.Now.AddMinutes(-1);

        var turns = Turns("""[{"role":"user","content":"hello","timestamp":0}]""");

        Assert.True(Assert.Single(turns).At > before);
    }

    // ---- attribution -----------------------------------------------------

    // An inter-session message is unwrapped and attributed, which is the point of
    // routing it through Readable here rather than in the panel.
    [Fact]
    public void AnInterSessionMessageIsUnwrappedAndAttributed()
    {
        OpenClawSessions.SetIdentitiesForTests(
            new System.Collections.Generic.Dictionary<string, OpenClawSessions.AgentIdentity>
            {
                ["comfyui"] = new("ComfyUI", null, null),
            },
            new System.Collections.Generic.Dictionary<string, string> { ["comfyui"] = "ComfyUI" });

        var turns = Turns("""
        [{"role":"user","content":"[Inter-session message] sourceSession=agent:comfyui:discord:direct:1 isUser=false the render finished"}]
        """);

        var turn = Assert.Single(turns);
        Assert.Equal("the render finished", turn.Text);
        Assert.Equal("ComfyUI", turn.Speaker);
    }

    // An ordinary message has no speaker, so the panel draws it as whoever the
    // session belongs to rather than labelling it.
    [Fact]
    public void AnOrdinaryMessageHasNoSpeaker()
    {
        var turns = Turns("""[{"role":"user","content":"just me talking"}]""");

        Assert.Null(Assert.Single(turns).Speaker);
        Assert.Null(turns[0].SpeakerColor);
    }

    // ---- order and trimming ---------------------------------------------

    [Fact]
    public void TurnsComeBackInThePagesOwnOrder()
    {
        var turns = Turns("""
        [{"role":"user","content":"first"},
         {"role":"user","content":"second"},
         {"role":"user","content":"third"}]
        """);

        Assert.Equal(new[] { "first", "second", "third" }, turns.Select(t => t.Text));
    }

    [Fact]
    public void SurroundingWhitespaceIsTrimmedFromTheText()
    {
        var turns = Turns("""[{"role":"user","content":"  padded  "}]""");

        Assert.Equal("padded", Assert.Single(turns).Text);
    }
}
