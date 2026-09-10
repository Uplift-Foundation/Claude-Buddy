using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.Tests;

// The two visible surfaces a CLAUDE.md persona changes, drawn through real
// Skia rather than the null renderer.
//
// Built from a real temp tree and a real PNG, the same shape
// OpenClawWorkspaceIdentityScreenshots uses and for the same reason its own
// comment gives: what wants reviewing is the whole visible consequence of the
// Markdown convention — a name somebody wrote in prose, ending up as the
// letters and the face on an orb — not the avatar renderer that was already
// there. The prose sentence in the fixture is deliberate: the bullet grammar is
// what an OpenClaw workspace file uses, and the sentence is what a CLAUDE.md
// actually reads like.
[Collection("Settings")]
public class LocalPersonaScreenshots
{
    // A portrait rather than a flat colour block. The orb is a 72dip circle
    // with the picture cropped UniformToFill, so a solid square says nothing
    // about whether the crop, the ring or the badge landed anywhere sensible —
    // which is the entire question a reviewer opens this image to answer.
    private static byte[] Portrait()
    {
        var info = new SKImageInfo(160, 160, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(0x14, 0x2B, 0x24));

        using var mist = new SKPaint { Color = new SKColor(0x2E, 0x5C, 0x4C), IsAntialias = true };
        canvas.DrawCircle(80, 96, 62, mist);

        using var pale = new SKPaint { Color = new SKColor(0xDC, 0xF3, 0xE4), IsAntialias = true };
        canvas.DrawCircle(80, 62, 30, pale);
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(34, 96, 126, 164), 30), pale);

        using var dark = new SKPaint { Color = new SKColor(0x14, 0x2B, 0x24), IsAntialias = true };
        canvas.DrawCircle(69, 58, 4, dark);
        canvas.DrawCircle(91, 58, 4, dark);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SessionStatus Local(string cwd) => new()
    {
        Source = SessionSource.ClaudeCode,
        State = "idle",
        Title = "haunted-mansion",
        Cwd = cwd,
        Color = "",
        Cli = "",
    };

    // Resolved through LocalPersona itself rather than published by hand, so
    // this capture fails if the reading half stops working — the same choice
    // the OpenClaw workspace capture makes, and the reason that one is worth
    // more than a hand-filled identity table would be.
    private static string Publish(string sessionId, string project)
    {
        var persona = LocalPersona.Resolve(
            project, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal("Leota", persona.Name);
        Assert.NotNull(persona.AvatarPath);

        LocalPersonas.SetForTests(
            new Dictionary<string, LocalPersona.Persona> { [sessionId] = persona });

        return sessionId;
    }

    private static string WriteFixture()
    {
        var project = Path.Combine(Path.GetTempPath(), "cb-persona-capture-" + Guid.NewGuid());
        Directory.CreateDirectory(project);

        // One sentence per line, which is the grammar rather than a formatting
        // preference: PersonaMarkdown reads a line at a time and its prose arm
        // is anchored to the end of one, so two sentences sharing a line make a
        // value seven words long that the bounds correctly refuse. Worth
        // knowing, and worth a fixture that shows the shape that works.
        File.WriteAllText(Path.Combine(project, "CLAUDE.md"), """
            # Haunted Mansion Terminal Theme

            Her name is Leota.
            Her profile picture is leota.png.

            The rest of this file is ordinary project notes, which is the point:
            a persona is written in the file that was already there.
            """);
        File.WriteAllBytes(Path.Combine(project, "leota.png"), Portrait());

        return project;
    }

    [AvaloniaFact]
    public void ALocalClaudeCodeOrbWearsItsMarkdownPersona()
    {
        var project = WriteFixture();
        var sessionId = "local-persona-capture-" + Guid.NewGuid();

        try
        {
            Publish(sessionId, project);

            var orb = new OrbWindow(sessionId);
            orb.UpdateFrom(Local(project));

            ScreenshotHelper.Capture(orb, "local-persona-orb.png");
        }
        finally
        {
            LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>());
            OpenClawAvatars.Forget(LocalPersonas.AvatarKey(sessionId));
            if (Directory.Exists(project)) Directory.Delete(project, true);
        }
    }

    [AvaloniaFact]
    public void ALocalChatPanelHeaderWearsThePersonasNameAndFace()
    {
        var project = WriteFixture();
        var sessionId = "local-persona-header-" + Guid.NewGuid();

        try
        {
            Publish(sessionId, project);

            var orb = new OrbWindow(sessionId);
            orb.UpdateFrom(Local(project));

            var fake = new FakeChatSession(new[]
            {
                new ChatTurn { Role = ChatRole.User, Text = "Who am I talking to?" },
                new ChatTurn { Role = ChatRole.Assistant, Text = "Leota. The name is in the CLAUDE.md." },
            })
            {
                SessionId = sessionId,

                // What the header would have said without a persona: the folder
                // the session is running in. Set deliberately so the capture
                // shows the substitution rather than a name that could have come
                // from either place.
                DisplayName = "haunted-mansion",
            };

            ChatPanel.OpenFor(orb, fake);
            ScreenshotHelper.Flush();
            ScreenshotHelper.CaptureAlreadyShown(
                ChatPanelTestAccess.Instance!, "local-persona-chat-header.png");
        }
        finally
        {
            ChatPanel.HideFor(sessionId);
            LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>());
            OpenClawAvatars.Forget(LocalPersonas.AvatarKey(sessionId));
            if (Directory.Exists(project)) Directory.Delete(project, true);
        }
    }
}
