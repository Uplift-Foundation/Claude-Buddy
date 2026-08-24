using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace ClaudeBuddy.Tests;

// UpdateFrom(SessionStatus) is OrbWindow's one big "apply everything the
// hook file said" method (OrbWindow.axaml.cs, ~line 207). It does not
// require the orb to be Shown — none of the branches it can reach touch
// anything window-lifecycle-shaped for a plain status update — so every
// test here just constructs an OrbWindow headless and calls it directly.
//
// Deliberately not testing a click anywhere in this file: OrbWindow's
// pointer handling reaches TerminalFocuser.Focus, which is not OS-guarded at
// its entry point and would fire real tmux/ps/osascript processes off-thread
// on a real machine. Out of scope for this pass.
public class OrbWindowUpdateFromTests
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
        // The brief this suite was written from expected Cli="codex" vs
        // Cli="" to change what KindLabel/KindGlyphText show. Reading
        // OrbWindow.axaml.cs end to end shows that is not what those two
        // properties key off — both are derived solely from status.Kind
        // (BadgeFor: Cron/Direct/Channel/Unknown), which has nothing to do
        // with which CLI wrote the status file. status.Cli only ever
        // decides status.Source (via SessionManager.SourceOf, exercised
        // below with the real production method rather than a hand-rolled
        // copy of its rule), and Source only changes OrbWindow's behaviour
        // at the OpenClaw boundary (ApplyAvatar's `status.Source !=
        // SessionSource.OpenClaw` branch) — Codex and ClaudeCode both take
        // that same non-OpenClaw path. So the honest claim to test is the
        // opposite of what was assumed: a Codex session and a Claude Code
        // session are visually indistinguishable on the orb itself, because
        // OrbWindow has no reason to tell them apart — TerminalFocuser and
        // TranscriptReader are where the actual CLI-specific behaviour
        // lives, and both are out of scope here (process-spawning /
        // transcript-file reads).
        var claudeCode = PlainStatus();
        claudeCode.Cli = "";
        claudeCode.Source = SessionManager.SourceOf(claudeCode);
        Assert.Equal(SessionSource.ClaudeCode, claudeCode.Source);

        var codex = PlainStatus();
        codex.Cli = "codex";
        codex.Source = SessionManager.SourceOf(codex);
        Assert.Equal(SessionSource.Codex, codex.Source);

        var claudeOrb = new OrbWindow(Guid.NewGuid().ToString());
        var codexOrb = new OrbWindow(Guid.NewGuid().ToString());

        claudeOrb.UpdateFrom(claudeCode);
        codexOrb.UpdateFrom(codex);

        Assert.Equal(claudeOrb.KindLabel, codexOrb.KindLabel);
        Assert.Equal(claudeOrb.KindGlyphText, codexOrb.KindGlyphText);
        Assert.Equal(claudeOrb.GlyphText, codexOrb.GlyphText);
        Assert.Equal(claudeOrb.AccentColor, codexOrb.AccentColor);
        Assert.Null(claudeOrb.KindLabel);
        Assert.Null(codexOrb.KindLabel);
    }

    [AvaloniaFact]
    public void UnknownKindShowsNoBadge()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Kind = SessionKind.Unknown;

        orb.UpdateFrom(status);

        Assert.Null(orb.KindLabel);
        Assert.Null(orb.KindGlyphText);
    }

    [AvaloniaFact]
    public void ChannelKindShowsTheHashBadge()
    {
        // BadgeFor(SessionKind.Channel) => ("#", "channel") — read directly
        // off OrbWindow.axaml.cs.
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Kind = SessionKind.Channel;

        orb.UpdateFrom(status);

        Assert.Equal("channel", orb.KindLabel);
        Assert.Equal("#", orb.KindGlyphText);
    }

    [AvaloniaFact]
    public void DirectKindShowsTheAtBadge()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Kind = SessionKind.Direct;

        orb.UpdateFrom(status);

        Assert.Equal("direct message", orb.KindLabel);
        Assert.Equal("@", orb.KindGlyphText);
    }

    [AvaloniaFact]
    public void CronKindShowsTheClockBadge()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Kind = SessionKind.Cron;

        orb.UpdateFrom(status);

        Assert.Equal("cron", orb.KindLabel);
        Assert.Equal("⏱", orb.KindGlyphText);
    }

    // --- the heartbeat heart ---------------------------------------------
    // Which sessions get one is OpenClawHeartbeat's decision and is tested
    // without a screen in tests/TranscriptTests. What is worth asserting here is
    // the other half: that the status flag actually reaches the badge, and that
    // the badge is *independent* of the kind badge beside it — the two answer
    // different questions and a heartbeat session can want both.

    private static Border Heart(OrbWindow orb) => orb.FindControl<Border>("HeartBadge")!;

    [AvaloniaFact]
    public void AHeartbeatSessionWearsABeatingHeart()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Heartbeat = true;

        orb.UpdateFrom(status);

        Assert.True(Heart(orb).IsVisible);
        Assert.True(orb.IsHeartbeat);
    }

    [AvaloniaFact]
    public void AnOrdinarySessionWearsNoHeart()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        orb.UpdateFrom(PlainStatus());

        Assert.False(Heart(orb).IsVisible);
        Assert.False(orb.IsHeartbeat);
    }

    [AvaloniaFact]
    public void TheHeartGoesAwayWhenTheSessionStopsBeingOne()
    {
        // The gateway can stop reporting a session as heartbeat-driven — the
        // switch in Settings does exactly that. ApplyHeartbeat returns early when
        // nothing moved, so this is the path that early return has to not break.
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Heartbeat = true;
        orb.UpdateFrom(status);

        status.Heartbeat = false;
        orb.UpdateFrom(status);

        Assert.False(Heart(orb).IsVisible);
        Assert.False(orb.IsHeartbeat);
    }

    [AvaloniaFact]
    public void TheHeartAndTheKindBadgeAreIndependent()
    {
        // A heartbeat retargeted at a channel is still a channel, and a channel
        // that isn't heartbeat-driven must not grow a heart. Asserted together
        // because the two badges share a parent grid and nothing else.
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Kind = SessionKind.Channel;
        status.Heartbeat = true;

        orb.UpdateFrom(status);

        Assert.Equal("channel", orb.KindLabel);
        Assert.True(Heart(orb).IsVisible);

        status.Heartbeat = false;
        orb.UpdateFrom(status);

        Assert.Equal("channel", orb.KindLabel);
        Assert.False(Heart(orb).IsVisible);
    }

    [AvaloniaFact]
    public void AHeartbeatOrbInATeamKeepsItsHeartOnTheSmallerRim()
    {
        // A team member is drawn at 0.72, and both badges are repositioned onto
        // the smaller circle's rim by hand (SetTeamRole). The heart's margin is
        // the kind badge's sum mirrored into the opposite corner, so if one is
        // right and the other was left behind, these two disagree.
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Heartbeat = true;
        status.Lead = "some-other-session";

        orb.UpdateFrom(status);

        var heart = Heart(orb);
        var kind = orb.FindControl<Border>("KindBadge")!;

        Assert.True(heart.IsVisible);
        Assert.Equal(kind.Width, heart.Width);
        Assert.Equal(kind.Margin.Right, heart.Margin.Right);

        // Mirrored: the kind badge hangs off the bottom, the heart off the top.
        Assert.Equal(kind.Margin.Bottom, heart.Margin.Top);
        Assert.Equal(0, heart.Margin.Bottom);
    }

    [AvaloniaFact]
    public void KnownColorNameSetsTheOrbsAccentColor()
    {
        // AgentColors["green"] = #00AF5F (OrbWindow.axaml.cs's own table,
        // confirmed against a real Claude Code terminal per its comment).
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Color = "green";

        orb.UpdateFrom(status);

        Assert.Equal(Color.Parse("#00AF5F"), orb.AccentColor);
        Assert.Equal(Color.Parse("#00AF5F"), orb.LinkColor);
    }

    [AvaloniaFact]
    public void UnknownColorNameLeavesTheAccentUnset()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Color = "not-a-real-color-name";

        orb.UpdateFrom(status);

        Assert.Null(orb.AccentColor);
    }

    [AvaloniaFact]
    public void EmptyColorLeavesTheAccentUnset()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Color = "";

        orb.UpdateFrom(status);

        Assert.Null(orb.AccentColor);
    }

    [AvaloniaFact]
    public void HexColorIsAcceptedTheSameAsANamedColor()
    {
        // ApplyAccent explicitly accepts "#RRGGBB" too — that's how a
        // gateway agent with no /color gets an accent derived from its id
        // (AgentPalette), taken through the same field as a Claude Code
        // /color name.
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Color = "#5FD7A1";

        orb.UpdateFrom(status);

        Assert.Equal(Color.Parse("#5FD7A1"), orb.AccentColor);
    }

    // SetTeamRole(bool) is what UpdateFrom calls with
    // !string.IsNullOrEmpty(status.Lead) — a non-empty Lead is the app's
    // only "this is a team member" signal (SessionStatus.Lead's own
    // comment). OrbRadius is the one bit of that method's effect exposed
    // publicly (used by TeamLinks to stop an arrow at the orb's edge): 18
    // DIPs normally, 18*0.72 for a team member (MemberScale, read directly
    // off SetTeamRole).
    [AvaloniaFact]
    public void TeamMemberStatusShrinksTheOrbsDrawnRadius()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Lead = "";

        orb.UpdateFrom(status);
        Assert.Equal(18.0, orb.OrbRadius, precision: 6);

        status.Lead = "lead-session-id";
        orb.UpdateFrom(status);
        Assert.Equal(18.0 * 0.72, orb.OrbRadius, precision: 6);
    }

    [AvaloniaFact]
    public void LosingTeamMembershipRestoresTheFullSizeRadius()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Lead = "lead-session-id";

        orb.UpdateFrom(status);
        Assert.Equal(18.0 * 0.72, orb.OrbRadius, precision: 6);

        status.Lead = "";
        orb.UpdateFrom(status);
        Assert.Equal(18.0, orb.OrbRadius, precision: 6);
    }

    [AvaloniaFact]
    public void AgentNameOverridesTitleInTheGlyphSource()
    {
        // UpdateFrom prefers status.Agent over Title/folder for what gets
        // glyphed — a team member is called by its own agent name
        // ("MenuUX"), not the team session's shared title, or every member
        // would draw the same letters. GlyphText is what the header borrows
        // (ChatPanel.BorrowedLetters), so it is the real observable surface.
        var withTitleOnly = PlainStatus();
        withTitleOnly.Title = "some-title";
        withTitleOnly.Agent = "";

        var withAgent = PlainStatus();
        withAgent.Title = "some-title";
        withAgent.Agent = "MenuUX";

        var titleOrb = new OrbWindow(Guid.NewGuid().ToString());
        var agentOrb = new OrbWindow(Guid.NewGuid().ToString());

        titleOrb.UpdateFrom(withTitleOnly);
        agentOrb.UpdateFrom(withAgent);

        Assert.NotEqual(titleOrb.GlyphText, agentOrb.GlyphText);
    }
}
