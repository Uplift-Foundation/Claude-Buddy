using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.Tests;

// The visible half of CB-163: an orb for a session on *another* machine,
// wearing the persona that machine resolved, drawn through real Skia.
//
// The sibling capture next door (LocalPersonaScreenshots) builds a real temp
// tree and resolves a real CLAUDE.md, because the question it exists to answer
// is whether the markdown convention reaches the screen. This one deliberately
// does not, and the difference is the whole point of the ticket: a remote
// persona never touches this machine's disk. It arrives as bytes on the roster,
// and publishing it by hand here is exactly how it arrives in production.
//
// What a reviewer opens this image to answer: whether a portrait that crossed
// the wire lands in the circle the same way a local one does — the same crop,
// ring and badge questions a flat colour block could not settle.
[Collection("Settings")]
public class PeerPersonaScreenshots
{
    private static byte[] Portrait()
    {
        var info = new SKImageInfo(160, 160, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(0x1B, 0x22, 0x3A));

        using var glow = new SKPaint { Color = new SKColor(0x39, 0x4A, 0x7A), IsAntialias = true };
        canvas.DrawCircle(80, 96, 62, glow);

        using var pale = new SKPaint { Color = new SKColor(0xE2, 0xE8, 0xFA), IsAntialias = true };
        canvas.DrawCircle(80, 62, 30, pale);
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(34, 96, 126, 164), 30), pale);

        using var dark = new SKPaint { Color = new SKColor(0x1B, 0x22, 0x3A), IsAntialias = true };
        canvas.DrawCircle(69, 58, 4, dark);
        canvas.DrawCircle(91, 58, 4, dark);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Exactly what SessionManager builds for a peer row. The absent Cwd is part
    // of the fixture rather than an omission — a remote session has no path on
    // this disk, which is the first reason the local walk cannot reach it.
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
    public void ARemoteOrbWearsThePersonaItsOwnMachineResolved()
    {
        // The "rc:" prefix is what SessionIdentity.IsPeer keys on, and a real
        // remote orb's id carries it.
        var sessionId = "rc:studio-mac:" + Guid.NewGuid();

        try
        {
            PeerPersonas.SetForTests(new Dictionary<string, MirrorProtocol.PeerPersona>
            {
                [sessionId] = new("Faraday", Avatar: Portrait()),
            });

            var orb = new OrbWindow(sessionId);
            orb.UpdateFrom(Remote());

            ScreenshotHelper.Capture(orb, "peer-persona-orb.png");
        }
        finally
        {
            PeerPersonas.SetForTests(new Dictionary<string, MirrorProtocol.PeerPersona>());
            OpenClawAvatars.Forget(PeerPersonas.AvatarKey(sessionId));
        }
    }

    // The same orb with a persona that names nobody's picture — the common case
    // for a peer whose far machine has a name written down and no portrait. Its
    // letters come from the persona, not from the title, and that substitution
    // is the thing worth seeing side by side with the capture above.
    [AvaloniaFact]
    public void ARemoteOrbWithANameAndNoPortraitWearsThePersonasLetters()
    {
        var sessionId = "rc:studio-mac:" + Guid.NewGuid();

        try
        {
            PeerPersonas.SetForTests(new Dictionary<string, MirrorProtocol.PeerPersona>
            {
                [sessionId] = new("Faraday"),
            });

            var orb = new OrbWindow(sessionId);
            orb.UpdateFrom(Remote());

            ScreenshotHelper.Capture(orb, "peer-persona-orb-letters.png");
        }
        finally
        {
            PeerPersonas.SetForTests(new Dictionary<string, MirrorProtocol.PeerPersona>());
            OpenClawAvatars.Forget(PeerPersonas.AvatarKey(sessionId));
        }
    }
}
