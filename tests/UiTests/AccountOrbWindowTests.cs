using System;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// AccountOrbWindow driven through the one method the poll calls, and asserted
// on what a person would have seen.
//
// No Show(), no clicks. UpdateFrom needs neither, and the click path here
// reaches AccountOrbs, which starts a `claude` subprocess per account — the
// same reason OrbWindowUpdateFromTests refuses to synthesize a click on a
// session orb, where the pointer path reaches TerminalFocuser. The card's
// hover and pin behaviour is covered in AccountOrbsTests against a fake source
// instead, which is where the decisions actually live.
//
// Joins the Settings collection because constructing the window reads
// TwoLetterGlyphs while deciding its letters, and a sibling test flipping that
// setting mid-run would otherwise change what this one draws.
[Collection("Settings")]
public class AccountOrbWindowTests : IDisposable
{
    // CB-219: a ring in the danger band puts its orb on a process-wide 20 fps
    // ticker, and most cases here leave their orb open. Left running, that
    // ticker's queued ticks were executed by an unrelated [Fact] that pumps the
    // dispatcher from a pool thread, which threw "a different thread owns it".
    // Each case leaves nothing breathing behind it.
    public void Dispose() => AccountOrbWindow.ClearBreathingForTests();

    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static AccountUsage Usage(
        double? session = 20,
        double? weekly = 30,
        ExtraUsage? extra = null,
        bool available = true,
        DateTimeOffset? readAt = null,
        string label = "board",
        AccountUsageSource source = AccountUsageSource.ClaudeCode) =>
        new(
            ConfigDir: null,
            Label: label,
            Available: available,
            SubscriptionType: "team",
            Session: session is null ? null : new UsageWindow(session.Value, Now.AddHours(3)),
            Weekly: weekly is null ? null : new UsageWindow(weekly.Value, Now.AddDays(3)),
            Extra: extra,
            ReadAt: readAt ?? Now,
            Source: source);

    [AvaloniaFact]
    public void ConstructsHeadlessWithNoException()
    {
        Assert.NotNull(new AccountOrbWindow("k"));
    }

    [AvaloniaFact]
    public void WearsTheAccountsInitials()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(label: "board"), Now);

        // Whatever OrbGlyph decides for this machine's glyph setting — asserted
        // against OrbGlyph rather than a hardcoded string so this test says "the
        // orb uses the app's letters" instead of quietly re-implementing them.
        Assert.Equal(
            OrbGlyph.For("board", OrbweaverSettings.TwoLetterGlyphs),
            orb.GlyphText);
    }

    [AvaloniaFact]
    public void WearsTheCliMarkForItsSource()
    {
        var claude = new AccountOrbWindow("c");
        var codex = new AccountOrbWindow("x");
        var grok = new AccountOrbWindow("g");

        claude.UpdateFrom(Usage(source: AccountUsageSource.ClaudeCode), Now);
        codex.UpdateFrom(Usage(source: AccountUsageSource.Codex), Now);
        grok.UpdateFrom(Usage(source: AccountUsageSource.Grok), Now);

        Assert.Equal("claude", claude.CliMarkName);
        Assert.Equal("codex", codex.CliMarkName);
        Assert.Equal("grok", grok.CliMarkName);
        Assert.True(claude.CliMarkVisible);
        Assert.True(codex.CliMarkVisible);
        Assert.True(grok.CliMarkVisible);
        Assert.NotEqual(claude.CliMarkFill, codex.CliMarkFill);
        Assert.NotEqual(claude.CliMarkFill, grok.CliMarkFill);
        Assert.NotEqual(codex.CliMarkFill, grok.CliMarkFill);
    }

    [AvaloniaTheory]
    [InlineData(10, AccountOrbWindow.CalmHex)]
    [InlineData(70, AccountOrbWindow.WarnHex)]
    [InlineData(95, AccountOrbWindow.DangerHex)]
    public void RingColourFollowsHeadroom(double weekly, string expected)
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(weekly: weekly), Now);

        Assert.Equal(expected, orb.WeeklyColour);
    }

    [AvaloniaFact]
    public void TheTwoRingsAreColouredIndependently()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(session: 5, weekly: 92), Now);

        // The case the whole design is for: a fresh five hours inside an
        // exhausted week. One ring calm, one alarming, on the same orb.
        Assert.Equal(AccountOrbWindow.CalmHex, orb.SessionColour);
        Assert.Equal(AccountOrbWindow.DangerHex, orb.WeeklyColour);
    }

    // A window past its reset is not a stale number, it is a number about a
    // period that has ended. Drawing it would be a confident wrong answer.
    [AvaloniaFact]
    public void AnExpiredWindowIsNotDrawn()
    {
        var orb = new AccountOrbWindow("k");

        var usage = Usage() with
        {
            Weekly = new UsageWindow(89, Now.AddMinutes(-1))
        };

        orb.UpdateFrom(usage, Now);

        Assert.Null(orb.WeeklyColour);
    }

    [AvaloniaFact]
    public void AReadingNobodyHasRefreshedIsDimmed()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(readAt: Now - AccountUsage.StaleAfter), Now);

        Assert.True(orb.IsDimmed);
    }

    [AvaloniaFact]
    public void AFreshReadingIsNotDimmed()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(readAt: Now.AddMinutes(-1)), Now);

        Assert.False(orb.IsDimmed);
    }

    // CB-85. A Codex reading now arrives two ways, and only one of them has an
    // age: the app-server answers about *now* and carries no ObservedAt, while
    // a rollout snapshot carries the moment the last session wrote it. Same
    // percentage, same orb, opposite verdicts — and dimming is the only thing
    // on screen that tells the two apart, so it is worth pinning rather than
    // inferring from the AsOf tests.
    [AvaloniaFact]
    public void ALiveCodexReadingIsNotDimmedWhereTheSameSnapshotWouldBe()
    {
        var live = new AccountOrbWindow("k");
        var fromDisk = new AccountOrbWindow("k");

        var reading = Usage(readAt: Now) with { Source = AccountUsageSource.Codex };

        live.UpdateFrom(reading, Now);
        fromDisk.UpdateFrom(reading with { ObservedAt = Now - AccountUsage.StaleAfter }, Now);

        Assert.False(live.IsDimmed);
        Assert.True(fromDisk.IsDimmed);
    }

    // The inner ring is a gauge only when there is a cap to be a share of.
    [AvaloniaFact]
    public void ExtraUsageDisabledDrawsAnAbsenceRatherThanAZero()
    {
        var orb = new AccountOrbWindow("k");

        var off = new ExtraUsage(false, 0, null, "USD", 2, "never_enabled");
        orb.UpdateFrom(Usage(extra: off), Now);

        Assert.True(orb.ExtraIsAbsent);
    }

    // ...but a budget that has been *spent* is the opposite of an absent one,
    // and the first version drew them the same. An account that had used every
    // penny of its extra usage looked exactly like one that had never had any.
    [AvaloniaFact]
    public void ASpentBudgetIsAFullRingNotAnAbsentOne()
    {
        var orb = new AccountOrbWindow("k");

        var spent = new ExtraUsage(
            Enabled: false, UsedMinor: null, LimitMinor: null, Currency: "USD",
            DecimalPlaces: 2, DisabledReason: "org_level_disabled_until",
            UserDisabled: false, SpendLimitReached: true);

        orb.UpdateFrom(Usage(extra: spent), Now);

        Assert.False(orb.ExtraIsAbsent);
    }

    [AvaloniaFact]
    public void ExtraUsageWithACapIsAGauge()
    {
        var orb = new AccountOrbWindow("k");

        var on = new ExtraUsage(true, 1000, 2000, "USD", 2, null);
        orb.UpdateFrom(Usage(extra: on), Now);

        Assert.False(orb.ExtraIsAbsent);
    }

    [AvaloniaFact]
    public void ThePinBadgeFollowsThePin()
    {
        var orb = new AccountOrbWindow("k");

        Assert.False(orb.IsPinned);

        orb.SetPinned(true);
        Assert.True(orb.IsPinned);

        orb.SetPinned(false);
        Assert.False(orb.IsPinned);
    }

    // The tooltip is the only place the rings are spelled out in words, and it
    // has to distinguish the three silences: an account with no limits, one
    // nobody has read yet, and one whose reading has gone cold.
    [AvaloniaFact]
    public void TheSummarySaysWhichKindOfNothingItIs()
    {
        Assert.Equal(
            "no subscription limits on this account",
            AccountOrbWindow.Summary(Usage(available: false), Now));

        Assert.Equal(
            "no reading yet",
            AccountOrbWindow.Summary(Usage(session: null, weekly: null), Now));

        Assert.Equal("5h 20% · 7d 30%", AccountOrbWindow.Summary(Usage(), Now));

        Assert.Equal(
            "5h 20% · 7d 30% · stale",
            AccountOrbWindow.Summary(Usage(readAt: Now - AccountUsage.StaleAfter), Now));
    }

    [AvaloniaFact]
    public void TheSummaryFloorsTheWayTheCliDoes()
    {
        // 84.9 prints as 84 in `claude`'s own /usage, and two tools disagreeing
        // by a point about the same number is the kind of thing that costs an
        // afternoon.
        Assert.Equal("5h 84% · 7d 0%", AccountOrbWindow.Summary(Usage(84.9, 0.4), Now));
    }

    // --- the thought bubble tooltip --------------------------------------
    // Same guard as OrbWindowUpdateFromTests' own section, and for the same
    // reason: UpdateFrom used to call ToolTip.SetTip with a brand-new Border
    // on every poll, whether or not the label/summary had changed. This is a
    // separate window class from OrbWindow (see the class comment at the top
    // of this file) and so carried its own, unfixed copy of the same call —
    // CB-104's first round only touched OrbWindow. An account orb's poll is
    // a minute to five minutes apart (UsagePollCadence) rather than two
    // seconds, so the same flicker was real here too, just far rarer.

    [AvaloniaFact]
    public void RepeatedIdenticalUpdatesReuseTheSameTooltipInstance()
    {
        var orb = new AccountOrbWindow("k");
        var usage = Usage();

        orb.UpdateFrom(usage, Now);
        var first = orb.CurrentThoughtBubble;
        Assert.NotNull(first);

        // Same label, same summary, same everything the tooltip reads — a
        // typical re-poll that answered with the same reading as before.
        orb.UpdateFrom(usage, Now);
        var second = orb.CurrentThoughtBubble;

        Assert.Same(first, second);
    }

    [AvaloniaFact]
    public void ALabelChangeRebuildsTheTooltip()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(label: "board"), Now);
        var first = orb.CurrentThoughtBubble;

        orb.UpdateFrom(Usage(label: "other"), Now);
        var second = orb.CurrentThoughtBubble;

        Assert.NotSame(first, second);
    }

    [AvaloniaFact]
    public void ASummaryChangeRebuildsTheTooltip()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(session: 20), Now);
        var first = orb.CurrentThoughtBubble;

        // Same label, but a fresh poll reporting the reading moved — the
        // tooltip's second line (Summary) changes even though nothing else
        // about the orb's identity did.
        orb.UpdateFrom(Usage(session: 21), Now);
        var second = orb.CurrentThoughtBubble;

        Assert.NotSame(first, second);
    }

    // --- breathing ---------------------------------------------------------
    // A ring in the danger band pulses. It used to do so through
    // Animation.RunAsync on an IterationCount.Infinite animation, which Avalonia
    // answers with InvalidOperationException("Looping animations must not use
    // the Run method.") — a looping animation is *applied* by a style, never
    // run. Fire-and-forget, so the throw reached only
    // ~/Library/Logs/Orbweaver/crash.log, as an unobserved task exception —
    // twenty-two entries across twelve separate runs of the app, counted on
    // 7 Sep 2026. AccountOrbWindow.axaml.cs explains that count; this is the
    // third file carrying it, and all three move together.
    //
    // The *decision* — which readings breathe, and which rings must be left
    // strictly alone — is not tested here at all. It lives in
    // UsageRingGeometry.BreathChangeFor, with a case per outcome in
    // tests/UnitTests, for the same reason OrbArrangement and OrbGlyph do. What
    // is left for this file is the half that needs a window: that the class
    // actually lands on the right Path through a real UpdateFrom, and that
    // Avalonia's own styling really hands the arc's opacity to an animation
    // when it does.
    //
    // Since CB-219 the breath advancing is covered here too. It used to be an
    // Avalonia style animation, whose headless clock never moves, so no test
    // could see it; it is now a DispatcherTimer stepping UsageRingBreath's
    // curve, which runs on real time. See ABreathingRingsOpacityMovesOverRealTime.

    [AvaloniaFact]
    public void ARingEnteringTheDangerBandBreathes()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(session: 20, weekly: 90), Now);

        // Only the ring that earned it. Two rings breathing out of phase on one
        // orb is noise, and the calm one has nothing to say.
        Assert.True(orb.WeeklyIsBreathing);
        Assert.False(orb.SessionIsBreathing);
    }

    // The boundary itself, on both sides. DangerAtPercent is 85 and the rule is
    // >=, so 85 breathes and 84.9 does not — the same off-by-one the ring's
    // colour already has a case for.
    [AvaloniaTheory]
    [InlineData(84.9, false)]
    [InlineData(85, true)]
    [InlineData(100, true)]
    public void BreathingStartsWhereTheDangerBandDoes(double weekly, bool expected)
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(weekly: weekly), Now);

        Assert.Equal(expected, orb.WeeklyIsBreathing);
    }

    // The half that matters more. A green ring left pulsing like an emergency is
    // a worse lie than one that never pulsed, and it is what happens if stopping
    // is forgotten — a weekly window resets to near zero every seven days, so
    // every account eventually crosses this boundary downwards.
    [AvaloniaFact]
    public void ARingLeavingTheDangerBandStopsAndComesBackToFullOpacity()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(weekly: 92), Now);
        Assert.True(orb.WeeklyIsBreathing);

        orb.UpdateFrom(Usage(weekly: 40), Now);

        Assert.False(orb.WeeklyIsBreathing);
        Assert.Equal(1, orb.WeeklyArc.Opacity);
    }

    // Five minutes apart, the poll answers with the same reading it did last
    // time, which is the ordinary case rather than the exception. Restarting the
    // animation on each of those would reset its phase every five minutes — not
    // visible as a restart so much as a stutter nobody can explain.
    //
    // Asserted by watching the class collection rather than by reading the class
    // back, because "still breathing" is true either way. Nothing may change on
    // the second update.
    [AvaloniaFact]
    public void ARingAlreadyBreathingIsNotRestartedOnTheNextPoll()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(weekly: 92), Now);
        Assert.True(orb.WeeklyIsBreathing);

        var churn = 0;
        orb.WeeklyArc.Classes.CollectionChanged += (_, _) => churn++;

        orb.UpdateFrom(Usage(weekly: 92), Now);
        orb.UpdateFrom(Usage(weekly: 93), Now);

        Assert.Equal(0, churn);
        Assert.True(orb.WeeklyIsBreathing);
    }

    // A window past its reset stops being drawn at all (AnExpiredWindowIsNotDrawn
    // above), and a ring that is not drawn must not still be breathing — an
    // invisible arc pulsing its opacity is a shape with no colour animating
    // nothing, and the moment a reading came back it would come back mid-phase.
    [AvaloniaFact]
    public void AReadingThatGoesAbsentStopsBreathing()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(weekly: 92), Now);
        Assert.True(orb.WeeklyIsBreathing);

        orb.UpdateFrom(Usage(weekly: null), Now);

        Assert.False(orb.WeeklyIsBreathing);
        Assert.Null(orb.WeeklyColour);
        Assert.Equal(1, orb.WeeklyArc.Opacity);
    }

    // The inner ring gets there by a different road, and it is the road the bug
    // was reported on: `spend_limit_reached` maps to a full ring rather than to a
    // percentage anybody sent, so an account that has spent its extra-usage
    // budget sits at 100% and is exactly the orb that was reported as frozen.
    [AvaloniaFact]
    public void ASpentExtraUsageBudgetBreathes()
    {
        var orb = new AccountOrbWindow("k");

        var spent = new ExtraUsage(
            Enabled: false, UsedMinor: null, LimitMinor: null, Currency: "USD",
            DecimalPlaces: 2, DisabledReason: "org_level_disabled_until",
            UserDisabled: false, SpendLimitReached: true);

        orb.UpdateFrom(Usage(extra: spent), Now);

        Assert.True(orb.ExtraIsBreathing);
    }

    [AvaloniaFact]
    public void AnExtraUsageRingWithHeadroomDoesNotBreathe()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(extra: new ExtraUsage(true, 1000, 2000, "USD", 2, null)), Now);

        Assert.False(orb.ExtraIsBreathing);
    }

    // Extra usage being switched off is the inner ring's own version of a
    // reading going absent: the arc is cleared and the track becomes a dotted
    // outline, and a cleared arc must not be left breathing.
    [AvaloniaFact]
    public void ExtraUsageGoingAbsentStopsBreathing()
    {
        var orb = new AccountOrbWindow("k");

        var spent = new ExtraUsage(
            Enabled: false, UsedMinor: null, LimitMinor: null, Currency: "USD",
            DecimalPlaces: 2, DisabledReason: "org_level_disabled_until",
            UserDisabled: false, SpendLimitReached: true);

        orb.UpdateFrom(Usage(extra: spent), Now);
        Assert.True(orb.ExtraIsBreathing);

        orb.UpdateFrom(Usage(extra: new ExtraUsage(false, 0, null, "USD", 2, "never_enabled")), Now);

        Assert.True(orb.ExtraIsAbsent);
        Assert.False(orb.ExtraIsBreathing);
        Assert.Equal(1, orb.ExtraArc.Opacity);
    }

    // CB-219: the breath is no longer a looping style animation. An infinite
    // animation keeps the compositor rendering on every vsync, and on an
    // account in the danger band that measured as most of Buddy's idle CPU
    // (34.8% breathing against 9.5% held still). This guards against one
    // coming back: the window declares no animation at all.
    [AvaloniaFact]
    public void TheWindowDeclaresNoLoopingAnimation()
    {
        var orb = new AccountOrbWindow("k");

        Assert.DoesNotContain(orb.Styles.OfType<Style>(), style => style.Animations.Count > 0);
    }

    // What replaced it: the shared ticker writes the curve's opacity onto a
    // breathing ring, and leaves a calm ring's opacity entirely unwritten. A
    // ring that has never breathed should carry no value of ours whatsoever.
    [AvaloniaFact]
    public void TheTickerStepsABreathingRingAndLeavesACalmOneAlone()
    {
        var orb = new AccountOrbWindow("k");
        orb.Show();
        orb.UpdateFrom(Usage(session: 20, weekly: 92), Now);

        var started = orb.BreathStartedAt(orb.WeeklyArc)!.Value;
        orb.TickBreath(started + 1300);

        Assert.Equal(UsageRingBreath.OpacityAt(1300), orb.WeeklyArc.Opacity, precision: 9);
        Assert.Equal(
            BindingPriority.Unset,
            orb.SessionArc.GetDiagnostic(Visual.OpacityProperty).Priority);
        Assert.Equal(1, orb.SessionArc.Opacity);

        // Leaving the band gives full opacity back rather than leaving the ring
        // at whatever fraction of a breath it had reached, and the ticker no
        // longer touches it.
        orb.UpdateFrom(Usage(session: 20, weekly: 10), Now);
        orb.TickBreath(started + 2600);

        Assert.Equal(1, orb.WeeklyArc.Opacity);
        Assert.Null(orb.BreathStartedAt(orb.WeeklyArc));
        orb.Close();
    }

    // The part the old style animation could not show under headless Avalonia,
    // whose animation clock never moves: the breath really advances with real
    // time, because a DispatcherTimer drives it now.
    [AvaloniaFact]
    public async Task ABreathingRingsOpacityMovesOverRealTime()
    {
        var orb = new AccountOrbWindow("k");
        orb.Show();
        orb.UpdateFrom(Usage(weekly: 95), Now);
        Assert.True(AccountOrbWindow.BreathTickerRunning);

        var seen = new HashSet<double>();
        var deadline = Environment.TickCount64 + 5_000;
        while (seen.Count < 3 && Environment.TickCount64 < deadline)
        {
            Pump();
            seen.Add(Math.Round(orb.WeeklyArc.Opacity, 3));
            await Task.Delay(60);
        }

        Assert.True(seen.Count >= 3, "the ring's opacity never moved");
        Assert.All(seen, opacity => Assert.InRange(opacity, UsageRingBreath.Floor, 1.0));
        orb.Close();
    }

    // QA's finding on 762aedf2: a ring that starts breathing while another is
    // already mid-breath must begin at full opacity and fade, as each ring's own
    // style animation did, rather than popping straight to the other ring's
    // phase on its first tick.
    [AvaloniaFact]
    public async Task ARingThatStartsBreathingLaterBeginsAtFullOpacity()
    {
        var early = new AccountOrbWindow("a");
        early.Show();
        early.UpdateFrom(Usage(weekly: 95), Now);
        await Task.Delay(400);

        var late = new AccountOrbWindow("b");
        late.Show();
        late.UpdateFrom(Usage(weekly: 95), Now);
        Assert.Equal(1, late.WeeklyArc.Opacity);

        AccountOrbWindow.TickAllBreaths();

        // The late ring is at most a few milliseconds into its own breath, so it
        // is still at (almost exactly) full opacity; the early one is visibly
        // not, being ~400 ms in.
        Assert.True(late.WeeklyArc.Opacity > 0.99, $"late ring popped to {late.WeeklyArc.Opacity}");
        Assert.True(early.WeeklyArc.Opacity < late.WeeklyArc.Opacity);

        early.Close();
        late.Close();
    }

    // Two rings on one orb that start breathing at different times keep their
    // own phases, as two separately started animations did.
    [AvaloniaFact]
    public void EachRingKeepsItsOwnBreath()
    {
        var orb = new AccountOrbWindow("k");
        orb.Show();
        orb.UpdateFrom(Usage(session: 10, weekly: 95), Now);
        var weeklyStarted = orb.BreathStartedAt(orb.WeeklyArc)!.Value;

        orb.UpdateFrom(Usage(session: 95, weekly: 95), Now);
        var sessionStarted = orb.BreathStartedAt(orb.SessionArc)!.Value;
        Assert.Equal(weeklyStarted, orb.BreathStartedAt(orb.WeeklyArc));

        var now = Math.Max(weeklyStarted, sessionStarted) + 1000;
        orb.TickBreath(now);

        Assert.Equal(UsageRingBreath.OpacityAt(now - weeklyStarted), orb.WeeklyArc.Opacity, precision: 9);
        Assert.Equal(UsageRingBreath.OpacityAt(now - sessionStarted), orb.SessionArc.Opacity, precision: 9);
        orb.Close();
    }

    // A hidden orb's rings are not stepped: nobody can see them. They pick the
    // breath up again, in step with the rest, when the orb shows.
    [AvaloniaFact]
    public void AHiddenOrbsRingsAreNotStepped()
    {
        var orb = new AccountOrbWindow("k");
        orb.UpdateFrom(Usage(weekly: 92), Now);

        var started = orb.BreathStartedAt(orb.WeeklyArc)!.Value;
        orb.TickBreath(started + 2600);
        Assert.Equal(1, orb.WeeklyArc.Opacity);

        orb.Show();
        orb.TickBreath(started + 2600);
        Assert.Equal(UsageRingBreath.Floor, orb.WeeklyArc.Opacity, precision: 9);
        orb.Close();
    }

    // The ticker runs only while some ring anywhere is breathing, and a closed
    // or calmed orb leaves it.
    [AvaloniaFact]
    public void TheTickerRunsOnlyWhileSomeRingBreathes()
    {
        // From nothing: no ticker has been created yet, and clearing again with
        // none is harmless.
        AccountOrbWindow.ClearBreathingForTests();
        AccountOrbWindow.ClearBreathingForTests();
        Assert.False(AccountOrbWindow.BreathTickerRunning);

        var first = new AccountOrbWindow("a");
        var second = new AccountOrbWindow("b");

        first.UpdateFrom(Usage(weekly: 92), Now);
        second.UpdateFrom(Usage(session: 90, weekly: 95), Now);
        Assert.True(first.OnBreathTicker && second.OnBreathTicker);
        Assert.True(AccountOrbWindow.BreathTickerRunning);

        // One of two rings calming keeps the orb on the ticker...
        second.UpdateFrom(Usage(session: 10, weekly: 95), Now);
        Assert.True(second.OnBreathTicker);

        // ...its last one calming takes it off, and the other orb keeps it going.
        second.UpdateFrom(Usage(session: 10, weekly: 10), Now);
        Assert.False(second.OnBreathTicker);
        Assert.True(AccountOrbWindow.BreathTickerRunning);

        // Closing the last breathing orb stops it.
        first.Close();
        Assert.False(first.OnBreathTicker);
        Assert.False(AccountOrbWindow.BreathTickerRunning);

        // And a ring starting again starts it again.
        second.UpdateFrom(Usage(weekly: 90), Now);
        Assert.True(AccountOrbWindow.BreathTickerRunning);
        second.Close();
    }

    // The ticker's own tick applies one curve value to every orb on it.
    [AvaloniaFact]
    public void TickingAllBreathsStepsEveryVisibleOrbOnTheTicker()
    {
        AccountOrbWindow.ClearBreathingForTests();
        var orb = new AccountOrbWindow("k");
        orb.Show();
        orb.UpdateFrom(Usage(weekly: 92), Now);

        AccountOrbWindow.TickAllBreaths();

        Assert.InRange(orb.WeeklyArc.Opacity, UsageRingBreath.Floor, 1.0);
        Assert.Equal(BindingPriority.LocalValue, orb.WeeklyArc.GetDiagnostic(Visual.OpacityProperty).Priority);
        orb.Close();
    }

    // Styles are applied on the dispatcher, so nothing above is true until it
    // has run. The render tick is what makes an applied animation write its
    // first frame; it does not advance the animation's own clock.
    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
