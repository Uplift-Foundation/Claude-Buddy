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
    public void AClaudeCodeOrbWearsTheTerracottaSpark()
    {
        var status = PlainStatus();
        status.Cli = "";
        status.Source = SessionManager.SourceOf(status);
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(status);
        ScreenshotHelper.Capture(orb, "orb-window-cli-mark-claude.png");
    }

    [AvaloniaFact]
    public void ACodexOrbWearsTheGreenStar()
    {
        var status = PlainStatus();
        status.Cli = "codex";
        status.Source = SessionManager.SourceOf(status);
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(status);
        ScreenshotHelper.Capture(orb, "orb-window-cli-mark-codex.png");
    }

    [AvaloniaFact]
    public void AGrokOrbWearsTheBlackX()
    {
        var status = PlainStatus();
        status.Cli = "grok";
        status.Source = SessionManager.SourceOf(status);
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(status);
        ScreenshotHelper.Capture(orb, "orb-window-cli-mark-grok.png");
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
    // The whole presence vocabulary, one capture per look, so the PR comment shows
    // them side by side. That is the point of these four rather than one: each is
    // the others with a single channel changed, and what a reviewer has to judge
    // is whether the differences read at a glance across a screen.
    private static SessionStatus BackgroundStatus(OrbPresence presence, string state = "idle")
    {
        var status = PlainStatus();
        status.State = state;
        status.Kind = SessionKind.Background;
        status.Shape = LocalSessionShape.Background;
        status.Presence = presence;
        return status;
    }

    [AvaloniaFact]
    public void AWorkingBackgroundJobWearsTheGearAtFullOpacity()
    {
        // The reference the other three are read against: the badge says what the
        // session is, and full opacity says something is happening in it.
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(BackgroundStatus(OrbPresence.Present, state: "generating"));

        ScreenshotHelper.Capture(orb, "orb-window-background-job-working.png");
    }

    [AvaloniaFact]
    public void AJobNeedingInputIsDimmedAndMarked()
    {
        // Dim because it is not competing with live work; marked because the
        // daemon calls this "needs input" and several of them are holding a
        // question, which is the opposite of what dimming alone says.
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(BackgroundStatus(OrbPresence.NeedsInput));

        ScreenshotHelper.Capture(orb, "orb-window-background-job-needs-input.png");
    }

    [AvaloniaFact]
    public void AFinishedJobIsDimmedAndMarkedDifferently()
    {
        // The same dimming with the opposite instruction on it. It stays only as
        // long as its status file does, so this is a look with a ten-minute life.
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(BackgroundStatus(OrbPresence.Finished));

        ScreenshotHelper.Capture(orb, "orb-window-background-job-finished.png");
    }

    [AvaloniaFact]
    public void AParkedJobAcknowledgingAClickWearsAHalo()
    {
        // The click that deliberately does nothing, saying so. Round eight exists
        // because round seven worked and nobody could tell: doing nothing is the
        // right answer to a click on a session already on screen, and it is
        // indistinguishable from the broken clicks this whole ticket is about
        // unless the orb answers.
        //
        // Captured mid-flight rather than at rest, because at rest there is
        // nothing to see — the halo is 280ms of growing and fading. Two ticks in,
        // the glow is wider than the orb and part-faded, which is the whole point:
        // it reads as a ripple leaving the orb rather than as the orb changing
        // state. Scale is deliberately untouched, since that channel is the breath
        // and this says nothing about what the session is doing.
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(BackgroundStatus(OrbPresence.NeedsInput));

        orb.Acknowledge();
        orb.TickAcknowledgement();
        orb.TickAcknowledgement();

        ScreenshotHelper.Capture(orb, "orb-window-acknowledging-a-click.png");
    }

    [AvaloniaFact]
    public void AParkedJobSomebodyIsAttachedToLooksOrdinary()
    {
        // A parked job with a `claude attach` client sitting in it. The daemon
        // still says "blocked" and the status file still says nothing, so this
        // *is* the parked look with one input changed — which is exactly what the
        // user hit: they attached to all three and the orbs stayed grey.
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(BackgroundStatus(OrbPresence.Present));

        ScreenshotHelper.Capture(orb, "orb-window-background-job-attached.png");
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
