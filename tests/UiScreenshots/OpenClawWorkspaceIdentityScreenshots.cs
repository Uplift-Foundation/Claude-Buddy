using System.Text.Json;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.Tests;

// A real workspace-shaped fixture rather than a hand-filled identity table:
// the capture is meant to review the full visible consequence of the Markdown
// convention — its chosen name and portrait — not merely the existing avatar
// renderer that consumes an AgentIdentity.
[Collection("Settings")]
public class OpenClawWorkspaceIdentityScreenshots
{
    private static byte[] Portrait()
    {
        var info = new SKImageInfo(128, 128, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(0x3A, 0x2A, 0x68));

        using var paint = new SKPaint { Color = new SKColor(0xF1, 0xD4, 0xFF), IsAntialias = true };
        canvas.DrawCircle(64, 48, 24, paint);
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(24, 76, 104, 132), 24), paint);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [AvaloniaFact]
    public void AWorkspaceIdentityNamesAndPortraitsAnOpenClawOrb()
    {
        var agent = "workspace-capture-" + Guid.NewGuid().ToString("N");
        var workspace = Path.Combine(Path.GetTempPath(), "cb-workspace-capture-" + Guid.NewGuid());
        Directory.CreateDirectory(workspace);

        try
        {
            File.WriteAllText(Path.Combine(workspace, "IDENTITY.md"),
                "- Name: Workspace Aurora\n- Avatar: aurora.png");
            File.WriteAllBytes(Path.Combine(workspace, "aurora.png"), Portrait());

            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                id = agent,
                workspace,
                displayName = "Gateway placeholder",
            }));
            var identity = OpenClawSessions.IdentityFrom(document.RootElement);
            OpenClawSessions.SetIdentitiesForTests(
                new Dictionary<string, OpenClawSessions.AgentIdentity> { [agent] = identity });

            Assert.Equal("Workspace Aurora", identity.Name);
            var orb = new OrbWindow($"openclaw:agent:{agent}:main");
            orb.UpdateFrom(new SessionStatus
            {
                Source = SessionSource.OpenClaw,
                State = "idle",
                Title = identity.Name,
                Kind = SessionKind.Direct,
            });

            ScreenshotHelper.Capture(orb, "openclaw-workspace-identity-orb.png");
        }
        finally
        {
            OpenClawSessions.SetIdentitiesForTests(
                new Dictionary<string, OpenClawSessions.AgentIdentity>());
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
    }
}
