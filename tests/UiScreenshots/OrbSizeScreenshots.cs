using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// Captures for CB-198's orb size: the two ends of the range, and a team member
// at the top end wearing every badge a member can. A LayoutTransform scales the
// whole orb as one picture, so what a reviewer is checking is that nothing
// inside it moved relative to anything else — badges still on the rim, the
// member's smaller circle still centred — at a size no test before this ran.
//
// No clicks, for the reason the other orb captures give.
[Collection("Settings")]
public class OrbSizeScreenshots : IDisposable
{
    private readonly double _globalBefore = OrbweaverSettings.OrbSize;

    public void Dispose() => OrbweaverSettings.OrbSize = _globalBefore;

    private static SessionStatus Status() => new()
    {
        State = "idle",
        Cwd = "/Users/test/project",
        Title = "resizable orbs",
        Color = "",
        Cli = "",
    };

    // The platform's own floor, so the Windows leg captures 70% and the macOS
    // leg 60% — which is itself worth seeing side by side in the PR comment.
    [AvaloniaFact]
    public void AnOrbAtTheSmallestSize()
    {
        OrbweaverSettings.OrbSize = OrbSizing.Min;
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        ScreenshotHelper.Capture(orb, "orb-window-size-min.png");
    }

    [AvaloniaFact]
    public void AnOrbAtDoubleSize()
    {
        OrbweaverSettings.OrbSize = 2.0;
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        ScreenshotHelper.Capture(orb, "orb-window-size-2x.png");
    }

    [AvaloniaFact]
    public void ATeamMemberAtDoubleSizeKeepsEveryBadgeOnItsSmallerRim()
    {
        OrbweaverSettings.OrbSize = 2.0;
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = Status();
        status.Heartbeat = true;
        status.Kind = SessionKind.Channel;
        status.Lead = "lead-session-id";
        status.ContextPercent = 64;
        orb.UpdateFrom(status);

        ScreenshotHelper.Capture(orb, "orb-window-size-2x-team-member-badges.png");
    }
}
