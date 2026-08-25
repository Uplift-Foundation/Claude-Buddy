using System.Linq;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// Two arms of ChatTranscript's picture handling that its other suites do not
// reach, both about a picture arriving alongside something that is not a
// message.
//
// Fixture provenance, per this repo's rule: the base64 below is the same
// one-pixel PNG the main suite already validated, lifted verbatim rather than
// invented. The broken one is that string with characters removed, which is
// what a truncated write produces.
public class ChatTranscriptImageRefusalTests
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==";

    private static string UserRow(string blocks) =>
        """{"type":"user","uuid":"u1","timestamp":"2026-08-16T10:00:10Z","message":{"role":"user","content":"""
        + blocks + "}}";

    private static string Image(string data) =>
        """{"type":"image","source":{"type":"base64","media_type":"image/png","data":"""
        + "\"" + data + "\"}}";

    private static string Text(string text) =>
        """{"type":"text","text":""" + System.Text.Json.JsonSerializer.Serialize(text) + "}";

    // A picture whose base64 is not base64 does not take the transcript down
    // with it. Convert.FromBase64String throws on a length that is not a
    // multiple of four, and a transcript is a file this app does not write —
    // a half-flushed line is a normal thing to read, not a corruption to
    // report.
    //
    // The turn still lands, carrying its caption and no picture, which is the
    // right answer: the sentence somebody typed is not lost because the
    // attachment beside it was unreadable.
    [Fact]
    public void APictureThatIsNotValidBase64LeavesTheCaptionBehind()
    {
        var rows = ChatTranscript.Map(new[]
        {
            UserRow("[" + Image("not base64 at all!") + "," + Text("look at this") + "]")
        });

        var row = Assert.Single(rows);
        Assert.Equal("look at this", row.Turn.Text);
        Assert.Null(row.Turn.ImageBytes);
    }

    // And with no caption either, the whole turn goes: an unreadable picture
    // with nothing said about it is not a message.
    [Fact]
    public void APictureThatIsNotValidBase64AndSaysNothingIsNotATurn()
    {
        Assert.Empty(ChatTranscript.Map(new[] { UserRow("[" + Image("!!!") + "]") }));
    }

    // A real picture attached to a caption that is one of Claude Code's own
    // injected blocks is still dropped.
    //
    // This is the arm that only exists because pictures exist. Everywhere else
    // the noise check runs on a message with nothing but text in it; here the
    // message has a picture too, and the tempting shortcut — "it has a
    // picture, so keep it" — would put a system-reminder on screen with a
    // screenshot stapled to it every time someone pasted one into a session
    // that had just fired a hook.
    [Fact]
    public void ARealPictureUnderAnInjectedBlockIsStillDropped()
    {
        var rows = ChatTranscript.Map(new[]
        {
            UserRow("[" + Image(OnePixelPng) + ","
                  + Text("<system-reminder>the user opened a file</system-reminder>") + "]")
        });

        Assert.Empty(rows);
    }

    // The same picture under something a person actually typed survives, so
    // the case above is the noise check doing its job rather than pictures
    // being dropped whenever they arrive with words.
    [Fact]
    public void TheSamePictureUnderATypedCaptionSurvives()
    {
        var rows = ChatTranscript.Map(new[]
        {
            UserRow("[" + Image(OnePixelPng) + "," + Text("here is the screenshot") + "]")
        });

        var row = Assert.Single(rows);
        Assert.Equal("here is the screenshot", row.Turn.Text);
        Assert.NotNull(row.Turn.ImageBytes);
    }

    // Assistant messages are not filtered for noise at all — the prefixes are
    // things injected into what the *user* appears to have said — so the same
    // text from the other side is kept. Asserted so the role check in that
    // condition is a decision on record rather than an accident of where it
    // was written.
    [Fact]
    public void TheNoiseCheckOnlyAppliesToWhatTheUserAppearedToSay()
    {
        Assert.True(ChatTranscript.IsNoise("<system-reminder>anything</system-reminder>"));
        Assert.False(ChatTranscript.IsNoise("here is the screenshot"));
    }
}
