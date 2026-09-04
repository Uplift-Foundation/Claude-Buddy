using Xunit;

namespace ClaudeBuddy.UnitTests;

// CB-94: when the gateway delivers a picture, the only trace a client sees is
// the gateway's own delivery-mirror record, whose content is the bare
// filename — no directory, no url. Recognising that record is what makes a
// delivered picture appear without depending on the agent having remembered
// to write a MEDIA: line (the automation that prompted this skipped it twice
// while delivering pictures perfectly well).
public class OpenClawDeliveredPictureTests
{
    private const string Mirror = OpenClawSessions.DeliveryMirrorModel;

    [Fact]
    public void ADeliveryMirrorCarryingABareImageFilenameIsADeliveredPicture()
    {
        Assert.Equal("lilibeth_cozy_621662447.png",
            OpenClawSessions.DeliveredPictureName(Mirror, "lilibeth_cozy_621662447.png"));
    }

    [Fact]
    public void SurroundingWhitespaceIsTrimmed()
    {
        Assert.Equal("a.png", OpenClawSessions.DeliveredPictureName(Mirror, "  a.png\n"));
    }

    [Theory]
    [InlineData("a.png")]
    [InlineData("a.PNG")]
    [InlineData("a.jpg")]
    [InlineData("a.jpeg")]
    [InlineData("a.gif")]
    [InlineData("a.webp")]
    public void EveryKnownImageExtensionCounts(string name)
    {
        Assert.Equal(name, OpenClawSessions.DeliveredPictureName(Mirror, name));
    }

    // The load-bearing half of the rule. delivery-mirror is not only used for
    // pictures: an ordinary message sent through this app is mirrored the same
    // way, and this exact string was observed live. Treating the model alone
    // as the signal would send prose off to be fetched as a file.
    [Fact]
    public void AMirroredTextMessageIsNotAPicture()
    {
        Assert.Null(OpenClawSessions.DeliveredPictureName(
            Mirror, "**(via Claude Buddy)** try send me a picture"));
    }

    // The other load-bearing half: a filename on its own means nothing unless
    // the gateway said it delivered something.
    [Fact]
    public void ABareFilenameFromAnOrdinaryModelIsNotAPicture()
    {
        Assert.Null(OpenClawSessions.DeliveredPictureName("claude-sonnet-4-6", "a.png"));
        Assert.Null(OpenClawSessions.DeliveredPictureName(null, "a.png"));
        Assert.Null(OpenClawSessions.DeliveredPictureName("", "a.png"));
    }

    // A path is not what a mirror record carries, and accepting one here would
    // overlap LocalMediaPathFrom's job while inventing a directory for a name
    // that already had one.
    [Fact]
    public void APathIsNotABareFilename()
    {
        Assert.Null(OpenClawSessions.DeliveredPictureName(Mirror, "/Users/w/.openclaw/media/a.png"));
        Assert.Null(OpenClawSessions.DeliveredPictureName(Mirror, "media/a.png"));
        Assert.Null(OpenClawSessions.DeliveredPictureName(Mirror, "C:\\pics\\a.png"));
    }

    [Fact]
    public void AFilenameWithSpacesIsNotAccepted()
    {
        Assert.Null(OpenClawSessions.DeliveredPictureName(Mirror, "my holiday.png"));
    }

    [Fact]
    public void SomethingThatIsNotAPictureIsNotAccepted()
    {
        Assert.Null(OpenClawSessions.DeliveredPictureName(Mirror, "notes.txt"));
        Assert.Null(OpenClawSessions.DeliveredPictureName(Mirror, "done"));
        Assert.Null(OpenClawSessions.DeliveredPictureName(Mirror, ""));
        Assert.Null(OpenClawSessions.DeliveredPictureName(Mirror, "   "));
    }
}
