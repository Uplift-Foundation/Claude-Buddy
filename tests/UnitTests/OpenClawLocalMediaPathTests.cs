using Xunit;

namespace ClaudeBuddy.UnitTests;

// CB-88: an agent's own generated picture, named by its own path on the
// gateway host rather than a fetchable URL. Two real shapes, both captured
// from a live gateway via tools/openclaw-probe rather than assumed — see
// OpenClawSessions.LocalMediaPathFrom's own comment.
public class OpenClawLocalMediaPathTests
{
    // The real captured example: two paragraphs of in-character reply, then
    // the MEDIA: line last — not the first line of the message.
    [Fact]
    public void AMediaLineAfterOtherParagraphsIsFound()
    {
        var text = "got her path — delivering now 🌸\n\n"
                  + "hey War War... caught me in the golden hour 💛✨\n\n"
                  + "MEDIA:/Users/warrenthompson/.openclaw/workspace-comfyui-zara/outputs/"
                  + "lilibeth/lilibeth_drop_204143557_1788477875_00001_.png";

        Assert.Equal(
            "/Users/warrenthompson/.openclaw/workspace-comfyui-zara/outputs/"
            + "lilibeth/lilibeth_drop_204143557_1788477875_00001_.png",
            OpenClawSessions.LocalMediaPathFrom(text));
    }

    [Fact]
    public void AMediaLineAsTheWholeMessageIsFound()
    {
        Assert.Equal("/tmp/pic.png", OpenClawSessions.LocalMediaPathFrom("MEDIA:/tmp/pic.png"));
    }

    [Fact]
    public void WhitespaceAroundTheMediaLineIsTrimmed()
    {
        Assert.Equal("/tmp/pic.png", OpenClawSessions.LocalMediaPathFrom("MEDIA:  /tmp/pic.png  "));
    }

    [Fact]
    public void AMediaMarkerWithNoPathIsNotAPicture()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("MEDIA:"));
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("MEDIA:   "));
    }

    [Fact]
    public void OrdinaryTextIsNeverMistakenForAMarker()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("just an ordinary reply, nothing attached"));
    }

    // Mentioning "MEDIA:" mid-sentence, not as its own line, is not the
    // marker — only a line that starts with it counts.
    [Fact]
    public void MediaMentionedMidLineIsNotTheMarker()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("the SOCIAL MEDIA:/path.png thing you mentioned"));
    }

    // The other real shape: the same automation's duplicate-post bug (before
    // it was fixed) left a bare path as an entire assistant turn, no MEDIA:
    // prefix at all.
    [Fact]
    public void ABarePathThatIsTheWholeMessageIsFound()
    {
        Assert.Equal(
            "/Users/warrenthompson/.openclaw/workspace-comfyui-zara/outputs/lilibeth/lilibeth_drop.png",
            OpenClawSessions.LocalMediaPathFrom(
                "/Users/warrenthompson/.openclaw/workspace-comfyui-zara/outputs/lilibeth/lilibeth_drop.png"));
    }

    [Theory]
    [InlineData("/tmp/pic.png")]
    [InlineData("/tmp/pic.PNG")]
    [InlineData("/tmp/pic.jpg")]
    [InlineData("/tmp/pic.jpeg")]
    [InlineData("/tmp/pic.gif")]
    [InlineData("/tmp/pic.webp")]
    public void EveryKnownImageExtensionIsRecognisedAsABarePath(string path)
    {
        Assert.Equal(path, OpenClawSessions.LocalMediaPathFrom(path));
    }

    // A relative-looking path is not what this matches — every real example
    // captured is absolute, and a relative one is more likely a citation or
    // a filename mentioned in conversation than a picture to fetch.
    [Fact]
    public void ARelativeBarePathIsNotAPicture()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("outputs/lilibeth/pic.png"));
    }

    // A sentence that happens to end in something that looks like a
    // filename is not a bare path — the whole trimmed message has to be
    // nothing else.
    [Fact]
    public void ASentenceEndingInAFilenameIsNotABarePath()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("saved it to /tmp/pic.png just now"));
    }

    [Fact]
    public void ABarePathToAnUnknownExtensionIsNotAPicture()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("/tmp/notes.txt"));
    }
}
