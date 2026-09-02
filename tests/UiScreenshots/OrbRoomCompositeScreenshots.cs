using System.Text.Json;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.Tests;

// What a room orb looks like once it is wearing the people in it.
//
// One capture per scenario in tests/UiTests/OrbRoomAvatarTests.cs, and the one
// place the feature can actually be *reviewed*: whether two portraits split an
// orb legibly at 36 points is a judgement somebody makes in a second from an
// image and cannot make at all from an assertion about a pixel.
//
// The stand-in portraits below are a bold ring on a flat ground rather than
// solid colour, so a capture shows where each picture was cropped as well as
// which wedge it landed in — a face fitted to the wrong rectangle shows up as a
// ring sliced off-centre.
[Collection("Settings")]
public class OrbRoomCompositeScreenshots
{
    private static byte[] Portrait(SKColor ground, SKColor mark)
    {
        var info = new SKImageInfo(128, 128, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);

        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(ground);

            using var paint = new SKPaint
            {
                Color = mark,
                IsAntialias = true,
                IsStroke = true,
                StrokeWidth = 14,
            };

            canvas.DrawCircle(64, 64, 34, paint);
            canvas.DrawLine(64, 8, 64, 30, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }

    private static readonly (string Name, byte[] Picture)[] Cast =
    {
        ("cb86-zara", Portrait(new SKColor(0x8E, 0x2F, 0x4F), new SKColor(0xFF, 0xD9, 0xE2))),
        ("cb86-annabel", Portrait(new SKColor(0x1F, 0x4F, 0x7A), new SKColor(0xCF, 0xEA, 0xFF))),
        ("cb86-amber", Portrait(new SKColor(0x6B, 0x4F, 0x12), new SKColor(0xFF, 0xE7, 0xB0))),
        ("cb86-quill", Portrait(new SKColor(0x2C, 0x5F, 0x3A), new SKColor(0xCF, 0xFF, 0xDD))),
    };

    private static string Room() => "discord:" + Guid.NewGuid().ToString("N")[..12];

    private static void Standing(string room, int members, bool pictures = true)
    {
        var cast = Cast.Take(members).ToArray();

        OpenClawSessions.SetIdentitiesForTests(
            cast.ToDictionary(
                agent => agent.Name,
                agent => new OpenClawSessions.AgentIdentity(
                    agent.Name, null, pictures ? agent.Picture : null)));

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = room["discord:".Length..];

        var rows = cast.Select((agent, i) =>
            $$"""{"key":"agent:{{agent.Name}}:discord:channel:{{id}}","lastActivityAt":{{now - i * 1000}}}""");

        ClaudeBuddySettings.OpenClawActiveWithinMinutes = ClaudeBuddySettings.OpenClawActiveWithinAll;

        OpenClawSessions.Parse(
            JsonDocument.Parse($$"""{"sessions":[{{string.Join(",", rows)}}]}""").RootElement,
            DateTime.UtcNow);
    }

    private static SessionStatus RoomStatus() => new()
    {
        Source = SessionSource.OpenClaw,
        IsRoom = true,
        State = "generating",
        Title = "smoke-test",
        Kind = SessionKind.Channel,
        Color = "#5F87D7",
    };

    private static void Capture(int members, string file)
    {
        try
        {
            var room = Room();
            Standing(room, members);

            var orb = new OrbWindow(SessionManager.RoomId(room));
            orb.UpdateFrom(RoomStatus());

            ScreenshotHelper.Capture(orb, file);
        }
        finally
        {
            OpenClawSessions.SetIdentitiesForTests(
                new Dictionary<string, OpenClawSessions.AgentIdentity>());
        }
    }

    // The case the feature was asked for: two agents working in one channel,
    // and the orb that stands for the channel split between them.
    [AvaloniaFact]
    public void TwoAgentsSplitTheRoomOrb() =>
        Capture(2, "orb-room-composite-two.png");

    [AvaloniaFact]
    public void ThreeAgentsTakeAThirdEach() =>
        Capture(3, "orb-room-composite-three.png");

    [AvaloniaFact]
    public void FourAgentsTakeAQuadrantEach() =>
        Capture(4, "orb-room-composite-four.png");

    // The fallback, captured beside the others so a reviewer can see that it is
    // still a legible orb rather than an empty circle: nobody in the channel has
    // a picture, so it keeps the channel's initials.
    [AvaloniaFact]
    public void ARoomWhereNobodyHasAPictureKeepsItsInitials()
    {
        try
        {
            var room = Room();
            Standing(room, 2, pictures: false);

            var orb = new OrbWindow(SessionManager.RoomId(room));
            orb.UpdateFrom(RoomStatus());

            ScreenshotHelper.Capture(orb, "orb-room-composite-none.png");
        }
        finally
        {
            OpenClawSessions.SetIdentitiesForTests(
                new Dictionary<string, OpenClawSessions.AgentIdentity>());
        }
    }
}
