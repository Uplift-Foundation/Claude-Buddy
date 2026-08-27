using Avalonia.Headless.XUnit;

namespace ClaudeBuddy.Tests;

// One capture per scenario in tests/UiTests/OrbWindowUpdateFromTests.cs. No
// clicks anywhere here, for the same reason that suite has none: OrbWindow's
// pointer handling reaches TerminalFocuser.Focus, unguarded at its own
// entry point, which would fire real tmux/ps/osascript processes off-thread
// on whatever machine runs this.
public class OrbWindowScreenshots
{
    private static SessionStatus PlainStatus() => new()
    {
        State = "idle",
        Cwd = "/Users/test/project",
        Title = "",
        Color = "",
        Cli = "",
    };

    [AvaloniaFact]
    public void ClaudeCodeAndCodexStatusesRenderIdenticallyOnTheOrb()
    {
        // The source test asserts these two render identically — this
        // captures the Claude Code one; a second image for Codex would show
        // nothing a diff tool wouldn't call "no change" against this one.
        var claudeCode = PlainStatus();
        claudeCode.Cli = "";
        claudeCode.Source = SessionManager.SourceOf(claudeCode);

        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(claudeCode);

        ScreenshotHelper.Capture(orb, "orb-window-claude-code-and-codex-render-identically.png");
    }

    [AvaloniaFact]
    public void UnknownKindShowsNoBadge()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Kind = SessionKind.Unknown;
        orb.UpdateFrom(status);

        ScreenshotHelper.Capture(orb, "orb-window-unknown-kind-no-badge.png");
    }

    [AvaloniaFact]
    public void ChannelKindShowsTheHashBadge()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Kind = SessionKind.Channel;
        orb.UpdateFrom(status);

        ScreenshotHelper.Capture(orb, "orb-window-channel-kind-hash-badge.png");
    }

    [AvaloniaFact]
    public void DirectKindShowsTheAtBadge()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Kind = SessionKind.Direct;
        orb.UpdateFrom(status);

        ScreenshotHelper.Capture(orb, "orb-window-direct-kind-at-badge.png");
    }

    [AvaloniaFact]
    public void CronKindShowsTheClockBadge()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Kind = SessionKind.Cron;
        orb.UpdateFrom(status);

        ScreenshotHelper.Capture(orb, "orb-window-cron-kind-clock-badge.png");
    }

    [AvaloniaFact]
    public void AHeartbeatSessionWearsABeatingHeart()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Heartbeat = true;
        orb.UpdateFrom(status);

        ScreenshotHelper.Capture(orb, "orb-window-heartbeat-heart-badge.png");
    }

    [AvaloniaFact]
    public void TheHeartAndTheKindBadgeAreIndependent()
    {
        // Both corners at once — the case that would show a collision if the
        // heart had been put in the kind badge's slot.
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Kind = SessionKind.Channel;
        status.Heartbeat = true;
        orb.UpdateFrom(status);

        ScreenshotHelper.Capture(orb, "orb-window-heartbeat-and-channel-badges.png");
    }

    [AvaloniaFact]
    public void AHeartbeatOrbInATeamKeepsItsHeartOnTheSmallerRim()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Heartbeat = true;
        status.Kind = SessionKind.Channel;
        status.Lead = "lead-session-id";
        orb.UpdateFrom(status);

        ScreenshotHelper.Capture(orb, "orb-window-heartbeat-team-member.png");
    }

    // The two CB-13 scenarios, and the pair is the point: these are the only
    // captures in this suite where the *difference between them* is the whole
    // review. A parked job and a working one differ in one channel — opacity —
    // and nothing else, so a reviewer comparing the two images is looking at
    // exactly what the ticket claims to have changed. One image alone would show
    // a dim orb with no evidence that anything else stayed put.
    [AvaloniaFact]
    public void AParkedBackgroundJobIsDimmedAndWearsTheGear()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Kind = SessionKind.Background;
        status.Shape = LocalSessionShape.Background;
        status.Parked = true;
        orb.UpdateFrom(status);

        ScreenshotHelper.Capture(orb, "orb-window-background-job-parked.png");
    }

    [AvaloniaFact]
    public void AWorkingBackgroundJobWearsTheGearAtFullOpacity()
    {
        // Same badge, same colours, full opacity — the badge says what the
        // session is and the opacity says whether anything is happening in it.
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.State = "generating";
        status.Kind = SessionKind.Background;
        status.Shape = LocalSessionShape.Background;
        status.Parked = false;
        orb.UpdateFrom(status);

        ScreenshotHelper.Capture(orb, "orb-window-background-job-working.png");
    }

    [AvaloniaFact]
    public void KnownColorNameSetsTheOrbsAccentColor()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Color = "green";
        orb.UpdateFrom(status);

        ScreenshotHelper.Capture(orb, "orb-window-known-color-name-green-accent.png");
    }

    [AvaloniaFact]
    public void UnknownColorNameLeavesTheAccentUnset()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Color = "not-a-real-color-name";
        orb.UpdateFrom(status);

        ScreenshotHelper.Capture(orb, "orb-window-unknown-color-name-no-accent.png");
    }

    [AvaloniaFact]
    public void EmptyColorLeavesTheAccentUnset()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Color = "";
        orb.UpdateFrom(status);

        ScreenshotHelper.Capture(orb, "orb-window-empty-color-no-accent.png");
    }

    [AvaloniaFact]
    public void HexColorIsAcceptedTheSameAsANamedColor()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Color = "#5FD7A1";
        orb.UpdateFrom(status);

        ScreenshotHelper.Capture(orb, "orb-window-hex-color-accent.png");
    }

    [AvaloniaFact]
    public void TeamMemberStatusShrinksTheOrbsDrawnRadius()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Lead = "lead-session-id";
        orb.UpdateFrom(status);

        ScreenshotHelper.Capture(orb, "orb-window-team-member-shrunk-radius.png");
    }

    [AvaloniaFact]
    public void LosingTeamMembershipRestoresTheFullSizeRadius()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Lead = "lead-session-id";
        orb.UpdateFrom(status);
        status.Lead = "";
        orb.UpdateFrom(status);

        ScreenshotHelper.Capture(orb, "orb-window-lost-team-membership-full-radius.png");
    }

    [AvaloniaFact]
    public void AgentNameOverridesTitleInTheGlyphSource()
    {
        var withAgent = PlainStatus();
        withAgent.Title = "some-title";
        withAgent.Agent = "MenuUX";

        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(withAgent);

        ScreenshotHelper.Capture(orb, "orb-window-agent-name-overrides-title-glyph.png");
    }
}
