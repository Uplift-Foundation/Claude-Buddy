using System;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// CB-101. A picture the agent named by path used to render only while its
// reply was streaming in, because CB-88 wired LocalMediaPathFrom into the live
// path alone. Read the same message back — reopen the panel, reconnect, scroll
// — and the MEDIA: line was just text.
//
// The fixtures here are the real message Warren screenshotted, not an invented
// one: the file was on disk and the gateway answered `available:true` for it,
// so the only thing that failed was this parser never asking.
public class OpenClawNamedPictureOnHistoryTests
{
    private const string RealPath =
        "/Users/warrenthompson/.openclaw/workspace-manager-lilibeth/.openclaw-cli-images/"
        + "ff8ca57e874675a6c539e612448971ca25f907bcab3e1eee860587df5bf79836.jpg";

    private static System.Collections.Generic.List<HistoryTurn> Turns(string json)
        => OpenClawSessions.TurnsFromHistory(JsonDocument.Parse(json).RootElement);

    private static string SourceOf(HistoryTurn turn)
        => Uri.UnescapeDataString(turn.ImageUrl!.Split('=')[^1]);

    // The exact reply that rendered as bare text at 12:49.
    [Fact]
    public void AMediaLineOnAHistoryReadNowDrawsThePicture()
    {
        var turns = Turns($$"""
        [{"role":"assistant","content":[{"type":"text","text":
          "Hey War War 🌸 I'm here! Cute pic, right? That's the one Zara whipped up earlier.\nMEDIA:{{RealPath}}\nWhat's up? 😊"}]}]
        """);

        var turn = Assert.Single(turns);
        Assert.Equal(RealPath, SourceOf(turn));

        // Alt text is the filename, which is all there is to say about it.
        Assert.Equal(
            "ff8ca57e874675a6c539e612448971ca25f907bcab3e1eee860587df5bf79836.jpg",
            turn.ImageAlt);
    }

    // And the prose survives, so a fetch that cannot succeed leaves exactly
    // what was there before rather than an empty bubble. Same shape the live
    // path produces.
    [Fact]
    public void TheMessageAroundTheMediaLineIsStillReadable()
    {
        var turn = Assert.Single(Turns($$"""
        [{"role":"assistant","content":[{"type":"text","text":
          "here you go\nMEDIA:{{RealPath}}\nenjoy"}]}]
        """));

        Assert.Contains("here you go", turn.Text);
        Assert.Contains("enjoy", turn.Text);
    }

    // A bare path as the whole message — the other arm of the same helper,
    // and a real shape (CB-88 found it left by a duplicate-post bug).
    [Fact]
    public void ABarePathAsTheWholeMessageDrawsToo()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"text","text":"/Users/w/pics/a.png"}]}]
        """));

        Assert.Equal("/Users/w/pics/a.png", SourceOf(turn));
    }

    // CB-97, in the shape that matters: the gateway expands a leading tilde
    // itself, and rejecting it threw away pictures it would have served.
    [Fact]
    public void ATildeRootedPathIsAcceptedNow()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"text","text":
          "saved it\nMEDIA:~/.openclaw/media/browser/03a1be83.png"}]}]
        """));

        Assert.Equal("~/.openclaw/media/browser/03a1be83.png", SourceOf(turn));
    }

    // The inter-session envelope stays text. Its path is on the last line with
    // no MEDIA: prefix, and the bare-path arm needs the *whole* message to be
    // the path — so the picture is not drawn on the handoff turn, which would
    // put it on the wrong bubble and then again on the real one.
    [Fact]
    public void TheInterSessionEnvelopeStillStaysText()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"text","text":
          "[Inter-session message] sourceSession=agent:comfyui:main\nrouted by OpenClaw\n/Users/w/.openclaw/media/pic.png"}]}]
        """));

        Assert.Null(turn.ImageUrl);
    }

    // CB-89: traversal refused, so nothing here builds a request out of one.
    [Fact]
    public void ATraversalIsRefused()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"text","text":
          "MEDIA:/Users/w/../../etc/shadow.png"}]}]
        """));

        Assert.Null(turn.ImageUrl);
    }

    [Fact]
    public void AProtocolRelativeUrlIsRefused()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"text","text":
          "MEDIA://cdn.discordapp.com/attachments/1/2/a.png"}]}]
        """));

        Assert.Null(turn.ImageUrl);
    }

    // The prose-mentions-MEDIA case QA found against CB-88, still refused:
    // without validating what follows the prefix, this extracted "is a broad
    // term..." and fired a real request for it.
    [Fact]
    public void ASentenceBeginningMediaIsNotAPath()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"text","text":
          "MEDIA: is a broad term covering images and video."}]}]
        """));

        Assert.Null(turn.ImageUrl);
    }

    // An ordinary sentence that happens to name a file is not a delivery.
    [Fact]
    public void ProseMentioningAPictureIsNotAPicture()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"text","text":
          "I saved it to /Users/w/pics/a.png earlier, have a look."}]}]
        """));

        Assert.Null(turn.ImageUrl);
    }

    // A delivery-mirror record still takes the branch above rather than this
    // one — its text is a bare filename, which is not rooted.
    [Fact]
    public void ADeliveryMirrorIsUnaffected()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"lilibeth_cozy_621662447.png"}]}]
        """));

        Assert.Contains("~/.openclaw/media/lilibeth_cozy_621662447.png", SourceOf(turn));
    }
}
