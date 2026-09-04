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
