using Xunit;

namespace ClaudeBuddy.UnitTests;

// OpenClawSessions.Readable: turning what OpenClaw actually writes into a
// transcript into what a person should read.
//
// In a multi-agent setup, a message arrives wrapped in a run of routing metadata —
// "[Inter-session message] sourceSession=agent:comfyui:discord:direct:1
// sourceChannel=discord sourceTool=sessions_send isUser=false <the message>".
// Left alone, the transcript is mostly that. It is not noise to be dropped
// though — it is one of your own agents talking — so the header is replaced by
// what was being said, attributed to whoever said it.
//
// The header is parsed BY SHAPE rather than against a list of known keys, which
// is the decision worth testing: a key nobody here has heard of must be consumed
// as metadata rather than leaking into the body, and a body that happens to
// contain an equals sign must not be eaten as metadata. Those two pull in
// opposite directions and the parser has to get both right.
public class OpenClawReadableTests
{
    private const string Marker = "[Inter-session message]";

    private static string Readable(string text) => OpenClawSessions.Readable(text);

    private static (string Text, string? Speaker) WithSpeaker(string text)
    {
        var result = OpenClawSessions.Readable(text, out var speaker);
        return (result, speaker);
    }

    // ---- ordinary text ---------------------------------------------------

    [Fact]
    public void PlainTextIsLeftAlone()
    {
        Assert.Equal("just a message", Readable("just a message"));
    }

    [Fact]
    public void PlainTextHasNoSpeaker()
    {
        Assert.Null(WithSpeaker("just a message").Speaker);
    }

    // ---- the resumed-session notice --------------------------------------

    // Not something a person said: OpenClaw writes this into the user role when
    // it restarts a CLI session under the covers. Dropped entirely rather than
    // shortened, because there is nothing in it for the reader — and an empty
    // result is what the caller skips on.
    [Fact]
    public void TheResumedSessionNoticeIsDroppedEntirely()
    {
        Assert.Equal("", Readable("OpenClaw resumed this CLI session after a restart"));
    }

    // Matched at the start, so a person quoting the notice mid-sentence keeps
    // their message.
    [Fact]
    public void TheNoticeQuotedInsideAMessageIsNotDropped()
    {
        const string said = "why does it say OpenClaw resumed this CLI session?";

        Assert.Equal(said, Readable(said));
    }

    // ---- the inter-session header ---------------------------------------

    [Fact]
    public void TheHeaderIsReplacedByWhatWasBeingSaid()
    {
        var text = Marker + " sourceSession=agent:comfyui:discord:direct:1"
                 + " sourceChannel=discord sourceTool=sessions_send isUser=false"
                 + " the build is green";

        Assert.Equal("the build is green", Readable(text));
    }

    // The agent's name is the one part of the session id a person recognises,
    // and it is reported as a field rather than glued to the front of the text:
    // in the string it can only be drawn as part of the sentence, as a field it
    // can be a label above the bubble and can colour it.
    [Fact]
    public void TheSpeakerIsReportedSeparatelyFromTheText()
    {
        var text = Marker + " sourceSession=agent:comfyui:discord:direct:1"
                 + " isUser=false the build is green";

        var (body, speaker) = WithSpeaker(text);

        Assert.Equal("comfyui", speaker);
        Assert.Equal("the build is green", body);
        Assert.DoesNotContain("comfyui", body);
    }

    // A key this version has never seen is consumed as metadata rather than
    // leaking into the body. That is the point of parsing by shape.
    [Fact]
    public void AnUnknownMetadataKeyIsStillConsumed()
    {
        var text = Marker + " sourceSession=agent:zara:discord:direct:1"
                 + " somethingBrandNew=42 the build is green";

        Assert.Equal("the build is green", Readable(text));
    }

    // The other direction: the run of tokens stops at the first thing that is
    // not key=value, so a message beginning with an ordinary word survives.
    [Fact]
    public void TheBodyStartsAtTheFirstTokenThatIsNotMetadata()
    {
        var text = Marker + " sourceSession=agent:zara:d:d:1 hello there friend";

        Assert.Equal("hello there friend", Readable(text));
    }

    // And a body that itself contains an equals sign keeps it, because by then
    // the loop has already stopped.
    [Fact]
    public void AnEqualsSignInsideTheBodyIsNotEaten()
    {
        var text = Marker + " sourceSession=agent:zara:d:d:1 set x=3 and run it";

        Assert.Equal("set x=3 and run it", Readable(text));
    }

    // A header with no message after it is left exactly as it arrived rather
    // than becoming an empty bubble — an empty result means "drop this", and a
    // header-only row is not nothing, it is something unexpected worth seeing.
    [Fact]
    public void AHeaderWithNoMessageIsLeftAsItArrived()
    {
        var text = Marker + " sourceSession=agent:zara:d:d:1 isUser=false ";

        Assert.Equal(text, Readable(text));
    }

    // No sourceSession means no speaker to attribute to, but the body is still
    // worth unwrapping.
    [Fact]
    public void AHeaderWithNoSourceSessionStillUnwrapsTheBody()
    {
        var text = Marker + " sourceChannel=discord isUser=false the build is green";

        var (body, speaker) = WithSpeaker(text);

        Assert.Equal("the build is green", body);
        Assert.Null(speaker);
    }

    // A sourceSession too short to hold an agent name yields no speaker rather
    // than indexing past the end of the split.
    [Fact]
    public void AShortSourceSessionYieldsNoSpeaker()
    {
        var text = Marker + " sourceSession=agent isUser=false the build is green";

        var (body, speaker) = WithSpeaker(text);

        Assert.Equal("the build is green", body);
        Assert.Null(speaker);
    }

    // Text that merely mentions the marker later on is not a header.
    [Fact]
    public void TheMarkerMustBeAtTheStartToCount()
    {
        const string said = "I saw an " + Marker + " in the log";

        Assert.Equal(said, Readable(said));
    }

    // ---- trailing instructions ------------------------------------------

    // A bracketed instruction appended after a blank line is machinery, not
    // something the sender typed, so it comes off the end.
    [Fact]
    public void ATrailingBracketedInstructionIsRemoved()
    {
        var text = "the build is green\n\n[Reply using sessions_send]";

        Assert.Equal("the build is green", Readable(text));
    }

    // A message that is nothing BUT the instruction keeps it: stripping it would
    // leave an empty bubble, and the caller reads empty as "drop this".
    [Fact]
    public void AMessageThatIsOnlyAnInstructionIsKept()
    {
        const string text = "\n\n[Reply using sessions_send]";

        Assert.Equal(text, Readable(text));
    }

    // A bracket at the end with no blank line before it is part of the message —
    // a person writing "[done]" on its own line meant to.
    [Fact]
    public void ABracketOnTheNextLineIsPartOfTheMessage()
    {
        const string text = "the build is green\n[done]";

        Assert.Equal(text, Readable(text));
    }

    [Fact]
    public void TextNotEndingInABracketIsUntouched()
    {
        const string text = "the build is green";

        Assert.Equal(text, Readable(text));
    }

    // ---- attachments -----------------------------------------------------

    // An attachment arrives as an absolute path, which is both long and about
    // the sender's filesystem rather than about the picture. Shortened to the
    // file name.
    [Fact]
    public void AnAttachmentIsShortenedToItsFileName()
    {
        var text = Readable("look at this [media attached: /Users/someone/Pictures/graph.png]");

        Assert.Contains("graph.png", text);
        Assert.DoesNotContain("/Users/someone", text);
    }

    // Several in one message all get shortened, not just the first.
    [Fact]
    public void EveryAttachmentIsShortened()
    {
        var text = Readable(
            "two [media attached: /a/b/one.png] and [media attached: /c/d/two.png]");

        Assert.Contains("one.png", text);
        Assert.Contains("two.png", text);
        Assert.DoesNotContain("/a/b", text);
        Assert.DoesNotContain("/c/d", text);
    }

    // Windows separators too, since the sender may be on another platform
    // entirely — the path in the message is theirs, not this machine's.
    [Fact]
    public void AWindowsPathedAttachmentIsShortenedToo()
    {
        var text = Readable(@"look [media attached: C:\Users\someone\Pictures\graph.png]");

        Assert.Contains("graph.png", text);
        Assert.DoesNotContain("Users", text);
    }

    // An unterminated marker is left alone rather than consuming the rest of the
    // message looking for a bracket that never comes.
    [Fact]
    public void AnUnterminatedAttachmentMarkerIsLeftAlone()
    {
        const string text = "look at this [media attached: /a/b/one.png";

        Assert.Equal(text, Readable(text));
    }

    // Both cleaners run before the header is looked for, so a message can need
    // all three and get all three.
    [Fact]
    public void AHeaderAnAttachmentAndAnInstructionAreAllHandledTogether()
    {
        var text = Marker + " sourceSession=agent:zara:d:d:1 isUser=false"
                 + " see [media attached: /Users/someone/graph.png]"
                 + "\n\n[Reply using sessions_send]";

        var (body, speaker) = WithSpeaker(text);

        Assert.Equal("zara", speaker);
        Assert.Contains("graph.png", body);
        Assert.DoesNotContain("/Users/someone", body);
        Assert.DoesNotContain("Reply using sessions_send", body);
        Assert.DoesNotContain(Marker, body);
    }
}
