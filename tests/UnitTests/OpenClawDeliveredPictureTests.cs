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

    // The newline half of that same rejection, which nothing reached before:
    // the trimming case above hands the check a string whose newline Trim has
    // already taken off, so the arm was never actually asked a question.
    //
    // It is not a hypothetical arm. A mirrored *text* message carries whatever
    // the sender wrote — the gateway mirrors `text.trim()` verbatim — so a
    // reply whose last line happens to end in an extension is the shape this
    // stops, and without it a paragraph of prose would be sent off to be
    // fetched as a file.
    [Fact]
    public void AMultiLineMessageEndingInAFilenameIsNotAccepted()
    {
        Assert.Null(OpenClawSessions.DeliveredPictureName(
            Mirror, "here you go\nlilibeth_cozy_621662447.png"));
    }

    // Two pictures in one delivery. The gateway builds the mirror text as
    // `mediaUrls.map(basename).join(", ")` (resolveMirroredTranscriptText, read
    // out of the running gateway's own bundle), so a two-picture drop mirrors
    // as one record naming both.
    //
    // Rejected, and asserted here so the limit is written down rather than
    // discovered: the comma-and-space form is not a filename, and picking one
    // of the two names would draw one picture and silently drop the other.
    // Both stay as text, which is what they do today.
    [Fact]
    public void AMultiPictureDeliveryNamesBothAndIsNotAccepted()
    {
        Assert.Null(OpenClawSessions.DeliveredPictureName(
            Mirror, "lilibeth_cozy_621662447.png, lilibeth_y2k_799321457.png"));
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
