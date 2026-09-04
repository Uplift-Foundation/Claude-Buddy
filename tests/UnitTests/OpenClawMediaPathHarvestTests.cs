using Xunit;

namespace ClaudeBuddy.UnitTests;

// CB-94, second round. A delivery mirror names a file and strips its
// directory, so gluing the name to a constant media directory is a guess —
// QA measured it right for the drop that prompted the ticket and wrong for a
// browser capture one directory deeper. These cover recovering the real
// directory from the page the picture was delivered on.
public class OpenClawMediaPathHarvestTests
{
    // ---- AbsoluteImagePathIn: what counts as a path worth fetching --------

    [Theory]
    [InlineData("/Users/w/.openclaw/media/lilibeth_cozy_621662447.png")]
    [InlineData("~/.openclaw/media/browser/03a1be83-aaaa-bbbb-cccc-ddddddddddddd.png")]
    [InlineData("/tmp/a.JPEG")]
    public void ARootedPathToAPictureIsAccepted(string token)
    {
        Assert.Equal(token, OpenClawSessions.AbsoluteImagePathIn(token));
    }

    // Prose punctuation, which is how a path usually arrives when an agent
    // mentions one mid-sentence.
    [Theory]
    [InlineData("\"/tmp/a.png\"", "/tmp/a.png")]
    [InlineData("(/tmp/a.png)", "/tmp/a.png")]
    [InlineData("`/tmp/a.png`", "/tmp/a.png")]
    [InlineData("/tmp/a.png,", "/tmp/a.png")]
    [InlineData("<~/pics/a.png>", "~/pics/a.png")]
    public void SurroundingPunctuationIsTrimmed(string token, string expected)
    {
        Assert.Equal(expected, OpenClawSessions.AbsoluteImagePathIn(token));
    }

    // The traversal refusal. This builds a gateway request out of a string an
    // agent wrote, so `..` is refused outright rather than reasoned about —
    // the same concern CB-89 has open against the sibling path.
    [Theory]
    [InlineData("/Users/w/../../etc/passwd.png")]
    [InlineData("~/.openclaw/media/../../../secret.png")]
    [InlineData("/a/../b.png")]
    public void ATraversalIsRefused(string token)
    {
        Assert.Null(OpenClawSessions.AbsoluteImagePathIn(token));
    }

    [Theory]
    [InlineData("media/a.png")]         // relative — nothing here to resolve it against
    [InlineData("a.png")]               // a bare name is the mirror's own shape, not a path
    [InlineData("/Users/w/notes.txt")]  // not a picture
    [InlineData("/Users/w/media")]      // a directory
    [InlineData("")]
    [InlineData("\"\"")]
    [InlineData("https://example.com/a.png")]
    public void AnythingElseIsRefused(string token)
    {
        Assert.Null(OpenClawSessions.AbsoluteImagePathIn(token));
    }

    // ---- MediaPathsByFileName: the page-wide index ------------------------

    // The case QA measured as a live 404: the mirror says
    // `03a1be83-….png`, the guess looks in ~/.openclaw/media/, and the file
    // is really one directory deeper. The page carries the real path, so the
    // index finds it.
    [Fact]
    public void APathAnywhereOnThePageIsFoundByItsFileName()
    {
        var index = OpenClawSessions.MediaPathsByFileName(new[]
        {
            "saved the capture to ~/.openclaw/media/browser/03a1be83.png for you",
            "03a1be83.png"
        });

        Assert.Equal("~/.openclaw/media/browser/03a1be83.png", index["03a1be83.png"]);
    }

    [Fact]
    public void APathThatIsTheWholeMessageIsFoundToo()
    {
        var index = OpenClawSessions.MediaPathsByFileName(new[]
        {
            "/Users/warrenthompson/.openclaw/media/lilibeth_cozy_621662447.png"
        });

        Assert.Equal(
            "/Users/warrenthompson/.openclaw/media/lilibeth_cozy_621662447.png",
            index["lilibeth_cozy_621662447.png"]);
    }

    // The inter-session envelope, whose last line is the path. Its own turn is
    // deliberately not rendered as the picture — that would put it on the
    // wrong bubble — but the path in it is still the right answer for where
    // the file is.
    [Fact]
    public void ThePathOnAnEnvelopesLastLineIsFound()
    {
        var index = OpenClawSessions.MediaPathsByFileName(new[]
        {
            "[Inter-session message] sourceSession=agent:comfyui:main\n"
            + "This content was routed by OpenClaw from another session.\n"
            + "/Users/warrenthompson/.openclaw/media/lilibeth_cozy_621662447.png"
        });

        Assert.Single(index);
        Assert.Equal(
            "/Users/warrenthompson/.openclaw/media/lilibeth_cozy_621662447.png",
            index["lilibeth_cozy_621662447.png"]);
    }

    // Two real files with the same name in different directories. Choosing
    // between them would draw a picture that is not the delivered one, which
    // is worse than drawing none — so the name is dropped and the caller
    // falls back.
    [Fact]
    public void ANameUnderTwoDirectoriesIsDroppedRatherThanGuessedBetween()
    {
        var index = OpenClawSessions.MediaPathsByFileName(new[]
        {
            "/Users/w/.openclaw/media/a.png",
            "/Users/w/.openclaw/media/browser/a.png"
        });

        Assert.Empty(index);
    }

    // The same path twice is not ambiguity, and this is the common case: QA
    // found the browser capture's path repeated four times on its page.
    [Fact]
    public void TheSamePathRepeatedIsNotAmbiguous()
    {
        var index = OpenClawSessions.MediaPathsByFileName(new[]
        {
            "wrote /Users/w/media/a.png",
            "reading /Users/w/media/a.png",
            "/Users/w/media/a.png"
        });

        Assert.Equal("/Users/w/media/a.png", index["a.png"]);
    }

    // ---- the raw-JSON shapes, which is where the paths actually are --------

    // The index is fed each message's raw JSON rather than the text the parser
    // renders, because TurnsFromHistory deliberately skips tool_use blocks and
    // that is exactly where the paths live. QA measured the difference over
    // every delivery-mirror record on the gateway host: 3 of 41 from rendered
    // text, 27 of 41 from raw JSON.
    [Fact]
    public void APathInsideAToolInvocationIsFound()
    {
        var index = OpenClawSessions.MediaPathsByFileName(new[]
        {
            """
            {"role":"assistant","content":[{"type":"tool_use","name":"bash",
             "input":{"command":"openclaw message send --media ~/.openclaw/media/browser/03a1be83.png"}}]}
            """
        });

        Assert.Equal("~/.openclaw/media/browser/03a1be83.png", index["03a1be83.png"]);
    }

    // A JSON string value with no whitespace around it at all — the whole
    // object is one whitespace-delimited token, so the double quote has to be
    // a separator or nothing is found.
    [Fact]
    public void APathAsABareJsonValueIsFound()
    {
        var index = OpenClawSessions.MediaPathsByFileName(new[]
        {
            """{"aggregated":"/Users/w/.openclaw/media/a.png","n":1}"""
        });

        Assert.Equal("/Users/w/.openclaw/media/a.png", index["a.png"]);
    }

    // The reason `:` is not a separator. Split on it and this token becomes
    // `//example.com/a.png`, which looks rooted and would be fetched off the
    // local disk.
    [Fact]
    public void AUrlInsideJsonIsNotMistakenForAPath()
    {
        var index = OpenClawSessions.MediaPathsByFileName(new[]
        {
            """{"url":"https://cdn.example.com/attachments/a.png"}"""
        });

        Assert.Empty(index);
    }

    // An inline picture's base64 is a single token megabytes long. It is
    // skipped on length rather than trimmed and tested — and, being one
    // token, it cannot contribute a path either way.
    [Fact]
    public void AnInlineImagesBase64IsSkippedRatherThanScanned()
    {
        var big = "/" + new string('A', 5000) + ".png";

        var index = OpenClawSessions.MediaPathsByFileName(new[]
        {
            $"{{\"type\":\"image\",\"data\":\"{big}\"}}"
        });

        Assert.Empty(index);
    }

    [Fact]
    public void APageWithNoPathsYieldsNothing()
    {
        var index = OpenClawSessions.MediaPathsByFileName(new[]
        {
            "hello", "", "a.png", "see notes.txt"
        });

        Assert.Empty(index);
    }

    // A dropped name and a kept one on the same page: the ambiguity removes
    // one entry without taking the other with it.
    [Fact]
    public void AmbiguityRemovesOnlyTheAmbiguousName()
    {
        var index = OpenClawSessions.MediaPathsByFileName(new[]
        {
            "/one/a.png /two/a.png /one/b.png"
        });

        Assert.False(index.ContainsKey("a.png"));
        Assert.Equal("/one/b.png", index["b.png"]);
    }
}
