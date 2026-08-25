using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// What an orb does when its session changes state, and what its letters do when
// the setting behind them changes.
//
// OrbWindowUpdateFromTests next door covers the badges, the accent colours, the
// heart and the team-member sizing. This covers the two things it does not: the
// breathing, which is how a screenful of orbs says which sessions are busy, and
// ReapplyGlyph, which is the UI half of a bug that made every kebab-case name
// wear the wrong two letters for a year.
//
// Orbs are never closed here, for the reason ChatPanelTests documents at length:
// closing a headless window corrupts a process-wide font resource shared with
// every other one. They are never shown either, so nothing is left on screen.
[Collection("Settings")]
public class OrbWindowStateTests
{
    // The pulse amplitude, which is what actually differs between states — the
    // colour is already covered next door. Read by reflection because it is
    // private and because a test seam for one number would be a worse trade than
    // reading it.
    //
    // Driven through ApplyState rather than UpdateFrom, and that is not a
    // shortcut: UpdateFrom deliberately stores the state and leaves Loaded or
    // Opened to apply it, because Avalonia fires Loaded *after* the first
    // UpdateFrom. These windows are never shown — closing a headless one corrupts
    // a process-wide font resource — so neither event ever fires and nothing
    // would be applied. The mapping from state to motion is the rule; when
    // Avalonia chooses to run it is not.
    private static double PulseTargetOf(OrbWindow orb) =>
        (double)typeof(OrbWindow)
            .GetField("_pulseTo", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(orb)!;

    private static SessionStatus Status(string state, string title = "claude-buddy") => new()
    {
        Source = SessionSource.ClaudeCode,
        State = state,
        Title = title,
        Cwd = "/Users/warren/project",
    };

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    // --- how hard each state breathes ---

    // Three amplitudes, one per state, and the ordering is the whole point: an
    // orb that wants an answer has to be more insistent than one that is merely
    // working, which has to be more insistent than one sitting idle. A screenful
    // of orbs is read at a glance, and the amplitude is what that glance sees.
    [AvaloniaFact]
    public void WaitingBreathesHarderThanWorkingWhichBreathesHarderThanIdle()
    {
        var orb = NewOrb();

        orb.ApplyState("idle");
        var idle = PulseTargetOf(orb);

        orb.ApplyState("generating");
        var working = PulseTargetOf(orb);

        orb.ApplyState("waiting");
        var waiting = PulseTargetOf(orb);

        Assert.True(idle < working, $"idle {idle} should be gentler than working {working}");
        Assert.True(working < waiting, $"working {working} should be gentler than waiting {waiting}");
    }

    // Idle still breathes rather than sitting perfectly still — an orb that
    // stopped moving entirely reads as a crashed app rather than a quiet session.
    [AvaloniaFact]
    public void AnIdleOrbStillBreathes()
    {
        var orb = NewOrb();

        orb.ApplyState("idle");

        Assert.True(PulseTargetOf(orb) > 1.0, "an idle orb should still move a little");
    }

    // A state this app has never heard of is treated as idle rather than left at
    // whatever the last one was. A CLI that grows a new state should produce a
    // calm orb, not a permanently insistent one.
    [AvaloniaFact]
    public void AnUnknownStateSettlesToTheIdleBreath()
    {
        var orb = NewOrb();

        orb.ApplyState("waiting");
        var waiting = PulseTargetOf(orb);

        orb.ApplyState("some-state-from-a-later-cli");

        Assert.NotEqual(waiting, PulseTargetOf(orb));
        Assert.Equal(PulseTargetOfIdle(), PulseTargetOf(orb), 3);
    }

    private static double PulseTargetOfIdle()
    {
        var orb = NewOrb();
        orb.ApplyState("idle");
        return PulseTargetOf(orb);
    }

    // Going quiet again returns the orb to its idle breath, which is what makes a
    // screenful of them readable over time rather than only at the moment
    // something happened.
    [AvaloniaFact]
    public void FinishingReturnsTheOrbToItsIdleBreath()
    {
        var orb = NewOrb();

        orb.ApplyState("generating");
        var working = PulseTargetOf(orb);

        orb.ApplyState("idle");

        Assert.NotEqual(working, PulseTargetOf(orb));
        Assert.Equal(PulseTargetOfIdle(), PulseTargetOf(orb), 3);
    }

    // --- the letters ---

    // ReapplyGlyph re-derives the letters without touching anything else, which
    // is what the settings toggle needs: nothing about the orb changes except the
    // text and the size sitting under it.
    [AvaloniaFact]
    public void ReapplyingTheGlyphFollowsTheTwoLetterSetting()
    {
        var orb = NewOrb();
        orb.UpdateFrom(Status("idle", title: "claude-buddy"));

        ClaudeBuddySettings.TwoLetterGlyphs = true;
        orb.ReapplyGlyph();
        var two = orb.Glyph.Text;

        ClaudeBuddySettings.TwoLetterGlyphs = false;
        orb.ReapplyGlyph();
        var one = orb.Glyph.Text;

        // The bug this pins: every kebab-case name used to draw two letters off
        // the front of its first word, so "claude-buddy" was "Cl" rather than
        // "Cb" — invisible unless the two halves start with different letters.
        Assert.Equal("Cb", two);
        Assert.Equal("C", one);
    }

    // The setting is read at reapply time rather than captured when the orb was
    // built, which is the whole reason the method exists — a toggle has to change
    // orbs that already exist.
    [AvaloniaFact]
    public void ReapplyingPicksUpASettingChangedAfterTheOrbWasBuilt()
    {
        ClaudeBuddySettings.TwoLetterGlyphs = false;

        var orb = NewOrb();
        orb.UpdateFrom(Status("idle", title: "my_cool_project"));
        Assert.Equal("M", orb.Glyph.Text);

        ClaudeBuddySettings.TwoLetterGlyphs = true;
        orb.ReapplyGlyph();

        Assert.Equal("Mc", orb.Glyph.Text);
    }

    // A team member's letters are drawn smaller, because its orb is smaller — and
    // reapplying must keep that rather than resetting to the full size.
    [AvaloniaFact]
    public void ReapplyingKeepsATeamMembersSmallerLettering()
    {
        ClaudeBuddySettings.TwoLetterGlyphs = true;

        var lead = NewOrb();
        var member = NewOrb();

        lead.UpdateFrom(Status("idle", title: "lead-session"));

        var memberStatus = Status("idle", title: "member-session");
        memberStatus.Lead = "some-lead-session-id";
        member.UpdateFrom(memberStatus);

        var before = member.Glyph.FontSize;
        member.ReapplyGlyph();

        Assert.Equal(before, member.Glyph.FontSize);
        Assert.True(
            member.Glyph.FontSize < lead.Glyph.FontSize,
            $"a member's lettering ({member.Glyph.FontSize}) should be smaller than a lead's "
                + $"({lead.Glyph.FontSize})");
    }

    // Reapplying with nothing yet known must not throw or blank the orb: the
    // settings toggle walks every orb, and one may have been created a moment
    // before its first status arrived.
    [AvaloniaFact]
    public void ReapplyingBeforeAnyStatusIsHarmless()
    {
        var orb = NewOrb();

        orb.ReapplyGlyph();

        Assert.NotNull(orb.Glyph.Text);
    }
}
