using System;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// One capture per scenario in tests/UiTests's AccountOrbWindowTests and
// UsageCardTests. Hand-written, because adding a UiTests case does not add its
// screenshot — there is no list anywhere that generates these.
//
// Worth capturing rather than trusting the assertions: everything about this
// feature is a shape. A ring whose large-arc flag is inverted draws 70% as 30%
// and asserts perfectly well on its colour; a full ring drawn as an arc instead
// of an ellipse disappears entirely while every number behind it stays right.
// These are the two failures the unit suite reasons about and only a picture
// actually shows.
//
// No clicks anywhere here. The orb's pointer path reaches AccountOrbs, which
// starts a `claude` subprocess per account — the same reason
// OrbWindowScreenshots has none.
[Collection("Settings")]
public class AccountUsageScreenshots
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static AccountUsage Usage(
        double? session,
        double? weekly,
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

    private static AccountOrbWindow Orb(AccountUsage usage)
    {
        var orb = new AccountOrbWindow("k");
        orb.UpdateFrom(usage, Now);
        return orb;
    }

    [AvaloniaFact]
    public void AnAccountWithRoomToSpare()
    {
        ScreenshotHelper.Capture(
            Orb(Usage(12, 30)), "account-orb-calm.png");
    }

    [AvaloniaFact]
    public void ACodexAccountWearsTheGreenStar()
    {
        ScreenshotHelper.Capture(
            Orb(Usage(12, 30, source: AccountUsageSource.Codex)),
            "account-orb-cli-mark-codex.png");
    }

    [AvaloniaFact]
    public void AGrokAccountWearsTheBlackX()
    {
        ScreenshotHelper.Capture(
            Orb(Usage(12, 30, source: AccountUsageSource.Grok)),
            "account-orb-cli-mark-grok.png");
    }

    [AvaloniaFact]
    public void AnAccountGettingOn()
    {
        ScreenshotHelper.Capture(
            Orb(Usage(64, 71)), "account-orb-warn.png");
    }

    // The case the colour scale exists for, and the one where the breathing
    // ring should be visible as a lower-opacity arc if the animation started.
    [AvaloniaFact]
    public void AnAccountNearlyOutOfWeek()
    {
        ScreenshotHelper.Capture(
            Orb(Usage(20, 94)), "account-orb-danger.png");
    }

    // A fresh five hours inside an exhausted week: two rings on one orb saying
    // different things, which is the whole argument for concentric rings over a
    // single number.
    [AvaloniaFact]
    public void FreshSessionInsideAnExhaustedWeek()
    {
        ScreenshotHelper.Capture(
            Orb(Usage(4, 96)), "account-orb-split.png");
    }

    // Full, and therefore an ellipse rather than an arc. If this capture is
    // ever an empty orb, the IsFull branch has been lost.
    [AvaloniaFact]
    public void AnAccountThatHasRunOut()
    {
        ScreenshotHelper.Capture(
            Orb(Usage(100, 100)), "account-orb-full.png");
    }

    // Over the cap, which the API really does report. Should look identical to
    // the full ring above, not like an almost-empty one.
    [AvaloniaFact]
    public void AnAccountPastItsCap()
    {
        ScreenshotHelper.Capture(
            Orb(Usage(30, 104.5)), "account-orb-past-cap.png");
    }

    [AvaloniaFact]
    public void AReadingNobodyHasRefreshed()
    {
        ScreenshotHelper.Capture(
            Orb(Usage(40, 88, readAt: Now - AccountUsage.StaleAfter)),
            "account-orb-stale.png");
    }

    // The inner ring as an absence — a dotted outline, not a gauge at zero.
    [AvaloniaFact]
    public void AnAccountWithNoExtraUsageAtAll()
    {
        var off = new ExtraUsage(false, 0, null, "USD", 2, null);

        ScreenshotHelper.Capture(
            Orb(Usage(25, 50, off)), "account-orb-extra-disabled.png");
    }

    // ...against a budget that has been spent, which is a full red ring. These
    // two captures are the pair: they were once identical, and that identity was
    // the bug — an account that had used every penny looked like one that had
    // never had any.
    [AvaloniaFact]
    public void AnAccountThatHasSpentItsExtraUsage()
    {
        var spent = new ExtraUsage(
            Enabled: false, UsedMinor: null, LimitMinor: null, Currency: "USD",
            DecimalPlaces: 2, DisabledReason: "org_level_disabled_until",
            UserDisabled: false, SpendLimitReached: true);

        ScreenshotHelper.Capture(
            Orb(Usage(25, 50, spent)), "account-orb-extra-spent.png");
    }

    [AvaloniaFact]
    public void ExtraUsageWithACapToSpend()
    {
        var on = new ExtraUsage(true, 1400, 2000, "USD", 2, null);

        ScreenshotHelper.Capture(
            Orb(Usage(25, 50, on)), "account-orb-extra-gauge.png");
    }

    [AvaloniaFact]
    public void TheCardBehindTheRings()
    {
        var card = new UsageCard();
        card.UpdateFrom(Usage(33, 84), "board@example.org", Now);

        ScreenshotHelper.Capture(card, "usage-card.png");
    }

    [AvaloniaFact]
    public void TheCardWithMoneyInIt()
    {
        var card = new UsageCard();
        var on = new ExtraUsage(true, 6114, 2000, "USD", 2, null);
        card.UpdateFrom(Usage(33, 84, on), "board@example.org", Now);

        ScreenshotHelper.Capture(card, "usage-card-extra.png");
    }

    [AvaloniaFact]
    public void TheCardSayingWhyThereIsNoExtraUsage()
    {
        var card = new UsageCard();
        var off = new ExtraUsage(false, 0, null, "USD", 2, null);
        card.UpdateFrom(Usage(33, 84, off), "board@example.org", Now);

        ScreenshotHelper.Capture(card, "usage-card-extra-off.png");
    }

    // The sentence that replaced the wrong one. Captured so the words are
    // reviewable rather than only asserted — this is the state a real account
    // was in when the first version told its owner his organisation had turned
    // extra usage off.
    [AvaloniaFact]
    public void TheCardForASpentMonthlyBudget()
    {
        var card = new UsageCard();
        var spent = new ExtraUsage(
            Enabled: false, UsedMinor: null, LimitMinor: null, Currency: "USD",
            DecimalPlaces: 2, DisabledReason: "org_level_disabled_until",
            UserDisabled: false, SpendLimitReached: true);
        card.UpdateFrom(Usage(8, 85, spent), "board@example.org", Now);

        ScreenshotHelper.Capture(card, "usage-card-extra-spent.png");
    }

    [AvaloniaFact]
    public void TheCardPinnedOpen()
    {
        var card = new UsageCard();
        card.UpdateFrom(Usage(33, 84), "board@example.org", Now);
        card.SetPinned(true);

        ScreenshotHelper.Capture(card, "usage-card-pinned.png");
    }

    [AvaloniaFact]
    public void TheCardForAnAccountWithNoSubscriptionLimits()
    {
        var card = new UsageCard();
        card.UpdateFrom(Usage(null, null, available: false), null, Now);

        ScreenshotHelper.Capture(card, "usage-card-no-limits.png");
    }

    [AvaloniaFact]
    public void TheCardForAColdReading()
    {
        var card = new UsageCard();
        card.UpdateFrom(Usage(33, 84, readAt: Now.AddHours(-2)), "board@example.org", Now);

        ScreenshotHelper.Capture(card, "usage-card-stale.png");
    }
}
