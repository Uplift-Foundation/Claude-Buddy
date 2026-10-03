using System.IO;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-223: an agent team on another machine, as this one draws it over the
// direct link — the lead's orb and a member's, from exactly the statuses
// SessionManager's remote block builds for rows that carry team shape.
//
// What a reviewer needs to see: the member drawn as a member (the smaller
// circle), wearing its own team colour and its agent name's initials rather
// than the title every member inherits from its lead, with the remote badge
// on both. The arrow between them is a separate window (TeamLinks) that a
// single orb capture cannot contain; that the pair is made is pinned in
// tests/UiTests/RemoteScanTests instead. The two captures are also stitched
// side by side into remote-team-orbs.png so they can be read together.
[Collection("Settings")]
public class RemoteTeamOrbScreenshots
{
    // As SessionManager's remote block fills them for a row with team shape.
    private static SessionStatus Remote(string lead, string agent, string color) => new()
    {
        Source = SessionSource.RemoteControl,
        RemoteCli = MirrorProtocol.CliClaudeCode,
        Kind = SessionKind.Remote,
        State = "idle",
        Title = "backlog status check",
        Color = color,
        Lead = lead,
        Agent = agent,
        AgentColor = string.IsNullOrEmpty(lead) ? "" : color,
    };

    [AvaloniaFact]
    public void AFarTeamsLeadAndMemberAsTheyAreDrawnHere()
    {
        var lead = new OrbWindow("rc:.claude:sid:lead");
        lead.UpdateFrom(Remote("", "", "orange"));
        ScreenshotHelper.Capture(lead, "remote-team-orb-lead.png");

        var member = new OrbWindow("rc:.claude:sid:a");
        member.UpdateFrom(Remote("rc:.claude:sid:lead", "wren-asare", "blue"));
        ScreenshotHelper.Capture(member, "remote-team-orb-member.png");

        Stitch("remote-team-orb-lead.png", "remote-team-orb-member.png", "remote-team-orbs.png");
    }

    // Two captures, left to right on a transparent ground, so the pair reads as
    // one picture in the PR comment — at three times size, because an orb is
    // 56 points across and a reviewer is looking for which initials it wears.
    // The native captures beside it are the ones to judge pixels by.
    private static void Stitch(string left, string right, string output)
    {
        using var a = SKBitmap.Decode(Path.Combine(ScreenshotHelper.OutputDir, left));
        using var b = SKBitmap.Decode(Path.Combine(ScreenshotHelper.OutputDir, right));
        const int gap = 16;
        const float scale = 3;

        using var surface = SKSurface.Create(new SKImageInfo((int)((a.Width + gap + b.Width) * scale),
            (int)(System.Math.Max(a.Height, b.Height) * scale), SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(scale);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
        surface.Canvas.DrawBitmap(a, 0, 0, paint);
        surface.Canvas.DrawBitmap(b, a.Width + gap, 0, paint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.Combine(ScreenshotHelper.OutputDir, output));
        data.SaveTo(file);
    }
}
