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
    public void ClaudeCodeCodexGrokAndOpenClawWearDistinctCliMarks()
    {
        var claudeCode = PlainStatus();
        claudeCode.Cli = "";
        claudeCode.Source = SessionManager.SourceOf(claudeCode);

        var codex = PlainStatus();
        codex.Cli = "codex";
        codex.Source = SessionManager.SourceOf(codex);

        var grok = PlainStatus();
        grok.Cli = "grok";
        grok.Source = SessionManager.SourceOf(grok);

        var openclaw = PlainStatus();
        openclaw.Source = SessionSource.OpenClaw;

        var claudeOrb = new OrbWindow(Guid.NewGuid().ToString());
        var codexOrb = new OrbWindow(Guid.NewGuid().ToString());
        var grokOrb = new OrbWindow(Guid.NewGuid().ToString());
        var openclawOrb = new OrbWindow(Guid.NewGuid().ToString());

        claudeOrb.UpdateFrom(claudeCode);
        codexOrb.UpdateFrom(codex);
        grokOrb.UpdateFrom(grok);
        openclawOrb.UpdateFrom(openclaw);

        Assert.Equal("claude", claudeOrb.CliMarkName);
        Assert.Equal("codex", codexOrb.CliMarkName);
        Assert.Equal("grok", grokOrb.CliMarkName);
        Assert.Equal("openclaw", openclawOrb.CliMarkName);
        Assert.True(claudeOrb.CliMarkVisible);
        Assert.True(codexOrb.CliMarkVisible);
        Assert.True(grokOrb.CliMarkVisible);
        Assert.True(openclawOrb.CliMarkVisible);
        Assert.NotEqual(claudeOrb.CliMarkFill, openclawOrb.CliMarkFill);

        // Kind is still independent of CLI — a local session has no kind badge,
        // and an OpenClaw main session doesn't either. The lobster is the
        // OpenClaw signal; a channel/cron badge is extra when the kind is known.
        Assert.Null(claudeOrb.KindLabel);
        Assert.Null(codexOrb.KindLabel);
        Assert.Null(grokOrb.KindLabel);
        Assert.Null(openclawOrb.KindLabel);
    }

    [AvaloniaFact]
    public void AnOpenClawChannelKeepsBothTheLobsterAndTheKindBadge()
    {
        var status = PlainStatus();
        status.Source = SessionSource.OpenClaw;
        status.Kind = SessionKind.Channel;

        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(status);

        Assert.Equal("openclaw", orb.CliMarkName);
        Assert.True(orb.CliMarkVisible);
        Assert.Equal("channel", orb.KindLabel);

        var kind = orb.FindControl<Border>("KindBadge")!;
        var cli = orb.FindControl<Border>("CliBadge")!;
        Assert.Equal(cli.Width, kind.Width);
        Assert.Equal(CliMark.Size, kind.Width);
    }

    [AvaloniaFact]
    public void RemoteSessionsCarryTheMarkForTheirActualCli()
    {
        var remote = PlainStatus();
        remote.Source = SessionSource.RemoteControl;
        remote.RemoteCli = MirrorProtocol.CliGrok;

        var remoteOrb = new OrbWindow(Guid.NewGuid().ToString());
        remoteOrb.UpdateFrom(remote);

        Assert.True(remoteOrb.CliMarkVisible);
        Assert.Equal("grok", remoteOrb.CliMarkName);
        Assert.False(remote.IsLocalCli);
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
    public void RemoteKindShowsTheTwoWayArrowBadge()
    {
        // BadgeFor(SessionKind.Remote) => ("\u21C4", "another machine").
        //
        // This badge carries more weight than the gateway ones beside it: a
        // remote orb is the only orb whose click opens a chat instead of jumping
        // to a terminal, on a screen where almost everything else is local. The
        // mark is how that is knowable before clicking rather than after.
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Kind = SessionKind.Remote;

        orb.UpdateFrom(status);

        Assert.Equal("another machine", orb.KindLabel);
        Assert.Equal("\u21C4", orb.KindGlyphText);
    }

    // A remote session has no terminal on this machine, so the orb must not
    // present one. Focus() already returns early for anything that isn't a
    // local CLI, and IsLocalCli being false is what makes that fire — asserted
    // here as well as in the unit suite because this is the orb's own contract:
    // this status must never be treated as clickable-through to a pane.
    [AvaloniaFact]
    public void ARemoteStatusIsNeverTreatedAsHavingATerminal()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Source = SessionSource.RemoteControl;
        status.Kind = SessionKind.Remote;

        orb.UpdateFrom(status);

        Assert.False(status.IsLocalCli);
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

    // CB-171. The cloud is the one kind that does not wear a character.
    //
    // Not because U+2601 is missing — it is reachable on both platforms
    // through font fallback, which is the half of this question a corrupted
    // Windows screenshot could not settle and the font manager answered
    // directly. It is that the faces that supply it do not draw a cloud at
    // 13px: a white lump on macOS, and on Windows a colour-emoji glyph that
    // brings its own size and its own colours into a badge built for one
    // white mark on near-black.
    //
    // Asserted on the controls rather than on KindGlyphText, because the
    // point is precisely that the two diverge now: the chat panel header
    // still says the character, where it sits beside the words "in the
    // cloud" and has room to be imperfect.
    [AvaloniaFact]
    public void CloudKindDrawsItsBadgeRatherThanTypingIt()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();
        status.Kind = SessionKind.Cloud;

        orb.UpdateFrom(status);

        Assert.Equal("in the cloud", orb.KindLabel);
        Assert.True(orb.FindControl<Border>("KindBadge")!.IsVisible);

        var mark = orb.FindControl<Avalonia.Controls.Shapes.Path>("KindMark")!;
        var glyph = orb.FindControl<TextBlock>("KindGlyph")!;

        Assert.True(mark.IsVisible);
        Assert.NotNull(mark.Data);
        Assert.False(glyph.IsVisible);
        Assert.True(string.IsNullOrEmpty(glyph.Text));
    }

    // The other half of the same contract, and the one that would break
    // quietly: a kind with no drawn mark must still type its character, and
    // an orb that was a cloud a moment ago must stop drawing one. Kinds are
    // applied in place on a live orb, so the two halves of ApplyKind have to
    // undo each other.
    [AvaloniaFact]
    public void AKindWithNoDrawnMarkTypesItsCharacterAndClearsAnyMark()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();

        status.Kind = SessionKind.Cloud;
        orb.UpdateFrom(status);

        status.Kind = SessionKind.Channel;
        orb.UpdateFrom(status);

        var mark = orb.FindControl<Avalonia.Controls.Shapes.Path>("KindMark")!;
        var glyph = orb.FindControl<TextBlock>("KindGlyph")!;

        Assert.False(mark.IsVisible);
        Assert.True(glyph.IsVisible);
        Assert.Equal("#", glyph.Text);
    }

    // Parsed here rather than only at the point of use, and here rather than
    // in the unit suite because StreamGeometry.Parse needs a platform render
    // interface. A typo in path data does not fail to compile and does not
    // fail to lay out — it throws when an orb of that kind first appears,
    // which for this one is a session running in Anthropic's cloud and may be
    // nowhere near a developer.
    //
    // The bounds check is the other half: the mark is stretched uniformly into
    // a 13px square, so a stray point outside the 16x16 box it is drawn in
    // shrinks everything else to fit it. That reads as "the badge looks
    // slightly off", not as a failure, which is exactly the kind of thing
    // nobody files.
    [AvaloniaFact]
    public void EveryDrawnKindMarkParsesAndStaysInsideItsBox()
    {
        var drawn = 0;

        foreach (SessionKind kind in Enum.GetValues<SessionKind>())
        {
            var mark = OrbWindow.KindMarkFor(kind);

            if (mark is null) continue;

            drawn++;

            var bounds = Avalonia.Media.StreamGeometry.Parse(mark).Bounds;

            Assert.True(bounds.Width > 0 && bounds.Height > 0);
            Assert.InRange(bounds.Left, 0, 16);
            Assert.InRange(bounds.Top, 0, 16);
            Assert.InRange(bounds.Right, 0, 16);
            Assert.InRange(bounds.Bottom, 0, 16);
        }

        Assert.True(drawn > 0);
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
        Assert.Equal(CliMark.Size * 0.72, heart.Width, precision: 6);
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

        var badge = orb.FindControl<Border>("CliBadge")!;
        var glyph = orb.FindControl<Avalonia.Controls.Shapes.Path>("CliGlyph")!;
        Assert.Equal(CliMark.Size * 0.72, badge.Width, precision: 6);
        Assert.Equal(CliMark.GlyphSize * 0.72, glyph.Width, precision: 6);
        Assert.True(orb.CliMarkVisible);

        status.Lead = "";
        orb.UpdateFrom(status);
        Assert.Equal(18.0, orb.OrbRadius, precision: 6);
        Assert.Equal(CliMark.Size, badge.Width, precision: 6);
        Assert.Equal(CliMark.GlyphSize, glyph.Width, precision: 6);
    }

    // --- the thought bubble tooltip --------------------------------------
    // UpdateFrom used to call ToolTip.SetTip with a brand-new Border on every
    // poll, whether or not the title/path had changed. With the tooltip
    // already open (pointer resting on the orb), replacing its content object
    // makes Avalonia close and reopen the popup — a visible flicker for as
    // long as the mouse stayed still, since polls keep arriving. CB-104.

    [AvaloniaFact]
    public void RepeatedIdenticalUpdatesReuseTheSameTooltipInstance()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();

        orb.UpdateFrom(status);
        var first = orb.CurrentThoughtBubble;
        Assert.NotNull(first);

        // Same title, same path, same everything the tooltip reads — a
        // typical re-poll with nothing new to say.
        orb.UpdateFrom(status);
        var second = orb.CurrentThoughtBubble;

        Assert.Same(first, second);
    }

    [AvaloniaFact]
    public void ATitleChangeRebuildsTheTooltip()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();

        orb.UpdateFrom(status);
        var first = orb.CurrentThoughtBubble;

        status.Title = "renamed";
        orb.UpdateFrom(status);
        var second = orb.CurrentThoughtBubble;

        Assert.NotSame(first, second);
    }

    [AvaloniaFact]
    public void ACwdChangeRebuildsTheTooltip()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = PlainStatus();

        orb.UpdateFrom(status);
        var first = orb.CurrentThoughtBubble;

        status.Cwd = "/Users/test/other-project";
        orb.UpdateFrom(status);
        var second = orb.CurrentThoughtBubble;

        Assert.NotSame(first, second);
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
