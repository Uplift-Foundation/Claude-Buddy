using Xunit;

namespace ClaudeBuddy.Tests;

// What persona.log actually says, as text.
//
// Pure, and worth asserting on its own rather than only through the file it
// ends up in: the entire value of this log is that somebody reading it in six
// months learns why their picture is not on their orb, and a line that names a
// reason nobody can act on is the same as no line. The reason category and the
// cap are the two things that make it actionable, so both are asserted in
// every arm.
public class PersonaRejectionMessageTests
{
    private static string Message(PersonaFiles.AvatarRejection reason, long bytes = 0) =>
        PersonaFiles.RejectionMessage(reason, "cto.png", bytes);

    [Fact]
    public void AnOversizedPictureSaysHowBigItIsAndWhatTheCapIs()
    {
        var message = Message(PersonaFiles.AvatarRejection.TooLarge, 9_437_184);

        Assert.Contains("cto.png", message);
        Assert.Contains("too large", message);
        Assert.Contains("9,437,184 bytes", message);
        Assert.Contains("8,388,608 bytes", message);
    }

    [Theory]
    [InlineData("EscapesRoot", "escapes root")]
    [InlineData("Rooted", "rooted path")]
    [InlineData("Unreadable", "unreadable")]
    public void EveryOtherRefusalNamesItsCategoryAndTheCapToo(string reason, string category)
    {
        var message = Message(Enum.Parse<PersonaFiles.AvatarRejection>(reason));

        Assert.Contains("cto.png", message);
        Assert.Contains(category, message);
        Assert.Contains("8,388,608 bytes", message);
    }

    // The cap is spelled with invariant separators rather than the machine's,
    // so a runner in a comma-decimal locale writes the same line as a runner
    // that is not — and so an assertion about it is about the message rather
    // than about where the test ran.
    [Fact]
    public void TheSizesAreFormattedTheSameWayOnEveryMachine()
    {
        var was = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Contains("8,388,608 bytes", Message(PersonaFiles.AvatarRejection.Unreadable));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = was;
        }
    }

    // One line, timestamp first, because the reader is scanning for the most
    // recent answer to "why is there no picture".
    [Fact]
    public void AnEntryIsOneTimestampedLine()
    {
        var entry = PersonaLog.Format(
            new DateTimeOffset(2026, 9, 10, 11, 12, 13, TimeSpan.FromHours(-7)), "picture ignored");

        Assert.StartsWith("2026-09-10 11:12:13 -07:00  picture ignored", entry);
        Assert.EndsWith(Environment.NewLine, entry);
        Assert.Single(entry.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }
}
