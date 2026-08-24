using Avalonia.Headless.XUnit;
using Avalonia.Media;
using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.Tests;

// An agent's own picture, drawn as the orb itself.
//
// Unreachable until now for one reason: the only thing that fills the identity
// table is an agents.list request over a live gateway connection, which is
// excluded. OpenClawSessions.SetIdentitiesForTests publishes one the way a real
// response would, matching the snapshot seam beside it.
//
// The rules here are about *replacing* the orb rather than decorating it — the
// picture becomes the fill and the letters go away — so getting them wrong leaves
// an orb with both, or with neither.
[Collection("Settings")]
public class OrbAvatarTests
{
    private static byte[] Png(byte r = 0x5A, byte g = 0xF7, byte b = 0x8E)
    {
        var info = new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(new SKColor(r, g, b, 0xFF));

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }

    private static string Publish(string agent, byte[]? picture, string? emoji = null)
    {
        OpenClawSessions.SetIdentitiesForTests(
            new Dictionary<string, OpenClawSessions.AgentIdentity>
            {
                [agent] = new(agent, emoji, picture),
            });

        return $"openclaw:agent:{agent}:discord:channel:1";
    }

    private static void PublishNothing() =>
        OpenClawSessions.SetIdentitiesForTests(
            new Dictionary<string, OpenClawSessions.AgentIdentity>());

    private static SessionStatus Gateway(string title = "Zara") => new()
    {
        Source = SessionSource.OpenClaw,
        State = "idle",
        Title = title,
        Kind = SessionKind.Channel,
    };

    private static SessionStatus Local() => new()
    {
        Source = SessionSource.ClaudeCode,
        State = "idle",
        Title = "claude-buddy",
        Cwd = "/Users/warren/project",
    };

    // Agent ids are unique per case: the decoded-avatar cache is process-wide and
    // keyed by agent, so two cases sharing one would answer each other.
    private static string Agent() => "zara" + Guid.NewGuid().ToString("N")[..8];

    // --- when there is a picture ---

    // The picture becomes the orb's fill, and the letters go away. An orb showing
    // both would be unreadable.
    [AvaloniaFact]
    public void APictureReplacesTheLetters()
    {
        try
        {
            var sessionId = Publish(Agent(), Png());
            var orb = new OrbWindow(sessionId);

            orb.UpdateFrom(Gateway());

            Assert.False(orb.Glyph.IsVisible, "the letters should give way to the picture");
            Assert.IsType<ImageBrush>(orb.Orb.Fill);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Applying the same picture twice is a no-op rather than a rebuild: the scan
    // runs a couple of times a second, and rebuilding the brush on every tick
    // would restart an animated avatar continuously.
    [AvaloniaFact]
    public void ApplyingTheSamePictureTwiceKeepsTheSameBrush()
    {
        try
        {
            var sessionId = Publish(Agent(), Png());
            var orb = new OrbWindow(sessionId);

            orb.UpdateFrom(Gateway());
            var first = orb.Orb.Fill;

            orb.UpdateFrom(Gateway());

            Assert.Same(first, orb.Orb.Fill);
        }
        finally
        {
            PublishNothing();
        }
    }

    // --- when there is not ---

    // A local session never has one, whatever the identity table holds — the
    // picture belongs to a gateway agent, and a Claude Code session is not one.
    [AvaloniaFact]
    public void ALocalSessionKeepsItsLetters()
    {
        try
        {
            Publish(Agent(), Png());
            var orb = new OrbWindow("95eddb0e-99a5-4e5a-ba63-d9775a21b81f");

            orb.UpdateFrom(Local());

            Assert.True(orb.Glyph.IsVisible);
            Assert.IsNotType<ImageBrush>(orb.Orb.Fill);
        }
        finally
        {
            PublishNothing();
        }
    }

    // A gateway agent with no picture keeps its letters rather than showing an
    // empty circle. Most agents have none, so this is the ordinary case.
    [AvaloniaFact]
    public void AGatewayAgentWithNoPictureKeepsItsLetters()
    {
        try
        {
            var sessionId = Publish(Agent(), picture: null);
            var orb = new OrbWindow(sessionId);

            orb.UpdateFrom(Gateway());

            Assert.True(orb.Glyph.IsVisible);
            Assert.IsNotType<ImageBrush>(orb.Orb.Fill);
        }
        finally
        {
            PublishNothing();
        }
    }

    // An agent the table has never heard of is the state during a reconnect,
    // when the identity list has been emptied and not yet refilled. Letters, not
    // a blank orb.
    [AvaloniaFact]
    public void AnUnknownAgentKeepsItsLetters()
    {
        PublishNothing();

        var orb = new OrbWindow("openclaw:agent:nobody:discord:channel:1");

        orb.UpdateFrom(Gateway());

        Assert.True(orb.Glyph.IsVisible);
    }

    // Bytes that will not decode fall back to letters rather than losing the orb —
    // the same "fall back rather than fail" rule the decoder itself follows.
    [AvaloniaFact]
    public void APictureThatWillNotDecodeFallsBackToLetters()
    {
        try
        {
            var sessionId = Publish(Agent(), new byte[] { 1, 2, 3, 4 });
            var orb = new OrbWindow(sessionId);

            orb.UpdateFrom(Gateway());

            Assert.True(orb.Glyph.IsVisible);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Losing the picture puts the letters back. A gateway that stops reporting an
    // avatar — or an agent that removes one — must not leave the orb showing a
    // stale face.
    [AvaloniaFact]
    public void LosingThePictureRestoresTheLetters()
    {
        var agent = Agent();
        try
        {
            var sessionId = Publish(agent, Png());
            var orb = new OrbWindow(sessionId);

            orb.UpdateFrom(Gateway());
            Assert.False(orb.Glyph.IsVisible);

            // The identity goes away, and the decoded copy with it.
            OpenClawAvatars.Forget(agent);
            PublishNothing();

            orb.UpdateFrom(Gateway());

            Assert.True(orb.Glyph.IsVisible);
            Assert.IsNotType<ImageBrush>(orb.Orb.Fill);
        }
        finally
        {
            PublishNothing();
        }
    }

    // The same session becoming local again also restores them, which is the path
    // a source change takes rather than an identity change.
    [AvaloniaFact]
    public void BecomingALocalSessionRestoresTheLetters()
    {
        try
        {
            var sessionId = Publish(Agent(), Png());
            var orb = new OrbWindow(sessionId);

            orb.UpdateFrom(Gateway());
            Assert.False(orb.Glyph.IsVisible);

            orb.UpdateFrom(Local());

            Assert.True(orb.Glyph.IsVisible);
        }
        finally
        {
            PublishNothing();
        }
    }
}
