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
    [InlineData("Unreadable", "unreadable")]
    // CB-139's, and the one this table exists for. All twenty-four refusals on
    // one Mac mini said "unreadable — it is missing" about values that were
    // never files at all, three of which named perfectly good pictures. A
    // reason that sends somebody looking on disk for a file they never wrote
    // costs more than no reason would have. Renamed by CB-140 from
    // NotRelativePath, because an absolute path is legal now provided it
    // stays inside the root — the category is the same, "you have named
    // something that is not a picture at all", not a relative-path complaint.
    [InlineData("NotAPicturePath", "not a picture path")]
    public void EveryOtherRefusalNamesItsCategoryAndTheCapToo(string reason, string category)
    {
        var message = Message(Enum.Parse<PersonaFiles.AvatarRejection>(reason));

        Assert.Contains("cto.png", message);
        Assert.Contains(category, message);
        Assert.Contains("8,388,608 bytes", message);
    }

    // The two categories a reader has to be able to tell apart by eye, because
    // the real-machine check for this ticket is somebody reading a fresh
    // `persona.log` and counting which lines are which. "Unreadable" sends you
    // to a file; "not a picture path" tells you there was never a file to go
    // to. Neither string may appear inside the other's line.
    [Fact]
    public void AValueThatWasNeverAPathIsNotConfusableWithAFileThatIsMissing()
    {
        var unusable = Message(PersonaFiles.AvatarRejection.NotAPicturePath);
        var missing = Message(PersonaFiles.AvatarRejection.Unreadable);

        Assert.Contains("not a picture path", unusable);
        Assert.DoesNotContain("unreadable", unusable);
        Assert.DoesNotContain("it is missing", unusable);

        Assert.Contains("it is missing", missing);
        Assert.DoesNotContain("not a picture path", missing);

        // And it names what a picture is allowed to be, because the reader is
        // holding a value this app refused and has to work out what to write
        // instead.
        Assert.Contains(".webp", unusable);
        Assert.Contains("data:", unusable);
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

    // --- the value the line quotes back ------------------------------------

    // Found by reading this log on a real machine rather than by imagining a
    // case: the explicit bullet grammar accepts whatever follows a picture
    // label, `data:` URIs included, and one line on the Mac mini was a
    // five-kilobyte base64 WebP quoted in full — against a 64 KiB ceiling for
    // the whole file. An unbounded value written verbatim is a log that one
    // bad entry can spend.
    [Fact]
    public void AnEnormousValueIsTruncatedRatherThanQuotedWhole()
    {
        var uri = "data:image/webp;base64," + new string('U', 5000);

        var message = PersonaFiles.RejectionMessage(PersonaFiles.AvatarRejection.Unreadable, uri, 0);

        Assert.True(message.Length < 300, "one bad value must not be able to fill the log");
        Assert.Contains("data:image/webp;base64,", message);
        Assert.Contains("…", message);
        // The real length is named, so a reader can tell a truncated monster
        // from a path that merely happens to be long.
        Assert.Contains("5,023 characters", message);
    }

    // The tail survives, which is the half that matters and the half the first
    // version of this threw away — a picture resolved out of a temp tree is a
    // very long directory followed by the filename, and six integration tests
    // failed on exactly that before this was cut from the middle instead.
    [Fact]
    public void TruncationKeepsTheFilenameOnTheEnd()
    {
        var path = "/var/folders/" + new string('d', 200) + "/project/.claude/cto.png";

        var quoted = PersonaFiles.Quoted(path);

        Assert.StartsWith("/var/folders/", quoted);
        Assert.Contains("cto.png", quoted);
        Assert.Contains("…", quoted);
    }

    // A path anybody would actually write is quoted exactly as written —
    // truncation that trims ordinary values would cost more than it saves.
    [Theory]
    [InlineData("cto.png")]
    [InlineData("avatars/some/deeply/nested/portrait-with-a-long-name.png")]
    [InlineData("`avatars/annabel-lee.gif` (animated, updated 2026-09-09)")]
    public void AnOrdinaryValueIsQuotedWhole(string picture)
    {
        Assert.Equal(picture, PersonaFiles.Quoted(picture));
        Assert.Contains(picture, PersonaFiles.RejectionMessage(PersonaFiles.AvatarRejection.Unreadable, picture, 0));
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
