using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-218, acceptance criterion 2: an animated persona still visibly plays.
//
// The UI suite already asserts that AvatarImage.Source moves on to another
// frame. That is a claim about a property, and this project has been caught
// before by a property that updated while nothing new reached the screen. So
// this draws a visible orb through real Skia at one frame, waits for the
// avatar timer to advance, draws it again, and asserts the pixels at the
// middle of the picture are different. Both captures are written out, so the
// two frames can be looked at side by side in the PR's screenshot comment.
[Collection("Settings")]
public class AnimatedAvatarScreenshots
{
    // A 1x1 GIF of two frames: palette entry 0 (black), then palette entry 1
    // (white). Hand-encoded so the two frames are guaranteed to differ. The
    // LZW data for one pixel of index 0 is 0x44 0x01, and for index 1 it is
    // 0x4C 0x01 (clear, the index, end, three bits each at minimum code size 2).
    private static readonly byte[] BlackThenWhite =
    {
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61,
        0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,
        0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF,

        0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00,   // 100 ms
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0x02, 0x02, 0x44, 0x01, 0x00,

        0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00,   // 100 ms
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0x02, 0x02, 0x4C, 0x01, 0x00,

        0x3B,
    };

    private static SessionStatus Remote() => new()
    {
        Source = SessionSource.RemoteControl,
        RemoteCli = MirrorProtocol.CliClaudeCode,
        State = "idle",
        Title = "job-hunter",
        Color = "",
        Kind = SessionKind.Remote,
    };

    [AvaloniaFact]
    public async Task AnAnimatedPersonaDrawsADifferentPictureOnItsNextFrame()
    {
        var sessionId = "rc:studio-mac:" + Guid.NewGuid();

        try
        {
            PeerPersonas.SetForTests(new Dictionary<string, MirrorProtocol.PeerPersona>
            {
                [sessionId] = new("Faraday", Avatar: BlackThenWhite),
            });

            var orb = new OrbWindow(sessionId);
            orb.Show();
            orb.UpdateFrom(Remote());
            ScreenshotHelper.Flush();

            var first = orb.AvatarImage.Source;
            var before = CentrePixel(orb);
            ScreenshotHelper.CaptureAlreadyShown(orb, "animated-avatar-frame-a.png");

            // Wall-clock bounded, for the reason OrbAvatarTests gives: every
            // headless window in the run shares one dispatcher.
            var deadline = Environment.TickCount64 + 10_000;
            while (ReferenceEquals(orb.AvatarImage.Source, first))
            {
                Assert.True(Environment.TickCount64 < deadline, "the avatar never advanced");
                ScreenshotHelper.Flush();
                await Task.Delay(10);
            }

            ScreenshotHelper.Flush();
            var after = CentrePixel(orb);
            ScreenshotHelper.CaptureAlreadyShown(orb, "animated-avatar-frame-b.png");

            Assert.NotEqual(before, after);
            orb.Close();
        }
        finally
        {
            PeerPersonas.SetForTests(new Dictionary<string, MirrorProtocol.PeerPersona>());
            OpenClawAvatars.Forget(PeerPersonas.AvatarKey(sessionId));
        }
    }

    // The pixel at the middle of the orb, rendered through the same path every
    // screenshot in this suite uses. The picture fills the circle, so the
    // middle is the picture and never the ring.
    private static uint CentrePixel(OrbWindow orb)
    {
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(orb.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(orb.Bounds.Height)));

        using var bitmap = new RenderTargetBitmap(size);
        ScreenshotHelper.Render(orb, bitmap);

        // Read back the way ScreenshotHelper.Check does: encode, decode with
        // Skia, and look at the pixel.
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        using var decoded = SKBitmap.Decode(stream);

        return (uint)decoded.GetPixel(decoded.Width / 2, decoded.Height / 2);
    }
}
