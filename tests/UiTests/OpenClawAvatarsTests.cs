using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.Tests;

// Decoding an agent's picture into the frames an orb wears.
//
// In UiTests because the decoded frames are Avalonia WriteableBitmaps, which want
// the platform initialised. Nothing here talks to a gateway: the bytes are the
// argument, which is the whole reason this class takes them rather than fetching
// them.
//
// Every rule below is a "fall back to the emoji rather than fail" decision, and
// they are worth pinning because the failure they guard against is an orb that
// disappears or an app that spends all afternoon decoding a corrupt header.
public class OpenClawAvatarsTests
{
    // A unique agent id per case: the cache is process-wide and keyed by id, so
    // two cases sharing one would answer each other's question.
    private static string Agent() => "agent-" + Guid.NewGuid().ToString("N");

    private static byte[] Png(int width = 8, int height = 8)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(new SKColor(0x5A, 0xF7, 0x8E, 0xFF));

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }

    // A minimal two-frame GIF89a, written by hand because Skia has no GIF
    // encoder — there is no way to produce one from inside a test otherwise, and
    // the animated path is half of what this class does.
    //
    // 1x1, a two-colour global table, and two frames whose graphic-control
    // extensions both declare a delay of zero. That zero is the point: it is what
    // the 100ms rule exists for.
    private static byte[] AnimatedGif() => new byte[]
    {
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61,             // "GIF89a"
        0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,       // 1x1, global colour table of 2
        0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF,             // black, white

        0x21, 0xF9, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, // frame 1: delay 0
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0x02, 0x02, 0x44, 0x01, 0x00,                   // LZW: clear, index 0, end

        0x21, 0xF9, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, // frame 2: delay 0
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0x02, 0x02, 0x44, 0x01, 0x00,

        0x3B,                                            // trailer
    };

    // --- nothing to decode ---

    // Null and empty both mean "this agent has no picture", which is not a
    // failure — it is the ordinary case for most agents.
    [AvaloniaFact]
    public void NoBytesMeansNoAvatar()
    {
        Assert.Null(OpenClawAvatars.For(Agent(), null));
        Assert.Null(OpenClawAvatars.For(Agent(), Array.Empty<byte>()));
    }

    // A picture that will not decode is not a reason to lose the orb, so it
    // answers null rather than throwing.
    [AvaloniaFact]
    public void BytesThatAreNotAnImageDecodeToNothing()
    {
        Assert.Null(OpenClawAvatars.For(Agent(), new byte[] { 1, 2, 3, 4, 5 }));
        Assert.Null(OpenClawAvatars.For(Agent(), System.Text.Encoding.UTF8.GetBytes("not an image")));
    }

    // A truncated PNG — a header that promises an image and then stops — is the
    // realistic corruption, and it must not throw either.
    [AvaloniaFact]
    public void ATruncatedImageDecodesToNothing()
    {
        var whole = Png();

        Assert.Null(OpenClawAvatars.For(Agent(), whole[..12]));
    }

    // --- a still picture ---

    [AvaloniaFact]
    public void AStillPictureBecomesOneFrame()
    {
        var avatar = OpenClawAvatars.For(Agent(), Png());

        Assert.NotNull(avatar);
        Assert.Single(avatar!.Frames);
        Assert.False(avatar.IsAnimated);
    }

    // Scaled down on the way in, so what is retained is a fixed size rather than
    // the megabyte it arrived as. An orb is drawn small; keeping the original
    // would hold one per agent for the life of the process.
    [AvaloniaFact]
    public void FramesAreScaledToAFixedSize()
    {
        var avatar = OpenClawAvatars.For(Agent(), Png(width: 512, height: 512));

        Assert.NotNull(avatar);
        Assert.Equal(144, avatar!.Frames[0].PixelSize.Width);
        Assert.Equal(144, avatar.Frames[0].PixelSize.Height);
    }

    // A picture smaller than that is scaled *up* to the same size rather than
    // left small, so every orb's avatar fills its circle identically.
    [AvaloniaFact]
    public void ASmallPictureIsScaledUpToTheSameSize()
    {
        var avatar = OpenClawAvatars.For(Agent(), Png(width: 4, height: 4));

        Assert.NotNull(avatar);
        Assert.Equal(144, avatar!.Frames[0].PixelSize.Width);
    }

    // --- an animation ---

    [AvaloniaFact]
    public void AnAnimatedGifBecomesSeveralFrames()
    {
        var avatar = OpenClawAvatars.For(Agent(), AnimatedGif());

        Assert.NotNull(avatar);
        Assert.Equal(2, avatar!.Frames.Count);
        Assert.True(avatar.IsAnimated);
    }

    // The rule the source's comment is about: browsers treat 0 and 10ms as "the
    // author meant 100ms", and a great many GIFs rely on it. Without this the
    // animation runs as fast as the timer will go — "Zara spins", in the words of
    // the comment.
    [AvaloniaFact]
    public void AZeroDelayIsReadAsAHundredMilliseconds()
    {
        var avatar = OpenClawAvatars.For(Agent(), AnimatedGif());

        Assert.NotNull(avatar);
        Assert.All(avatar!.DelaysMs, delay => Assert.Equal(100, delay));
    }

    [AvaloniaFact]
    public void TheTotalDurationIsTheSumOfTheFrameDelays()
    {
        var avatar = OpenClawAvatars.For(Agent(), AnimatedGif());

        Assert.NotNull(avatar);
        Assert.Equal(avatar!.DelaysMs.Sum(), avatar.TotalMs);
        Assert.Equal(200, avatar.TotalMs);
    }

    // One delay per frame, or the animation timer runs off the end of the list.
    [AvaloniaFact]
    public void EveryFrameHasItsOwnDelay()
    {
        var avatar = OpenClawAvatars.For(Agent(), AnimatedGif());

        Assert.NotNull(avatar);
        Assert.Equal(avatar!.Frames.Count, avatar.DelaysMs.Count);
    }

    // --- the cache ---

    // Decoding is expensive and an agent's picture does not change while the app
    // runs, so the answer is kept. Asserted by identity: a second call must not
    // decode again.
    [AvaloniaFact]
    public void APictureIsDecodedOncePerAgent()
    {
        var agent = Agent();
        var first = OpenClawAvatars.For(agent, Png());

        Assert.Same(first, OpenClawAvatars.For(agent, Png()));
    }

    // The cached answer wins over new bytes, which is what makes Forget
    // necessary — and is why the gateway calls it when an agent's picture
    // changes.
    [AvaloniaFact]
    public void ForgettingAnAgentLetsItsPictureBeDecodedAgain()
    {
        var agent = Agent();
        var first = OpenClawAvatars.For(agent, Png());

        OpenClawAvatars.Forget(agent);
        var second = OpenClawAvatars.For(agent, Png());

        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    // "No picture" is cached too, so an agent without one is not re-decoded on
    // every scan. This is the case a naive cache misses, because null reads as
    // "not cached".
    [AvaloniaFact]
    public void HavingNoPictureIsRememberedRatherThanRetriedForever()
    {
        var agent = Agent();

        Assert.Null(OpenClawAvatars.For(agent, null));

        // Real bytes now, and still null: the earlier answer was cached, which is
        // exactly why Forget exists.
        Assert.Null(OpenClawAvatars.For(agent, Png()));
    }

    [AvaloniaFact]
    public void ForgettingAnAgentThatWasNeverSeenIsHarmless()
    {
        OpenClawAvatars.Forget(Agent());
    }

    // Ids are matched without regard to case, because they come from a gateway
    // key and from a settings file and the two have disagreed before.
    [AvaloniaFact]
    public void AgentIdsAreMatchedWithoutRegardToCase()
    {
        var agent = Agent();
        var first = OpenClawAvatars.For(agent.ToLowerInvariant(), Png());

        Assert.Same(first, OpenClawAvatars.For(agent.ToUpperInvariant(), Png()));
    }
}
