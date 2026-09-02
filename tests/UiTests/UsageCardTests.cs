using System;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// The card, including a real synthesized click on its pin.
//
// UsageCard has no OS coupling — confirmed by reading UsageCard.axaml.cs in
// full; the only things it touches outside itself are Screens, in Reposition,
// which is not exercised here. So unlike the orb, its pointer path is safe to
// drive, and the pin is worth driving for real: it is a bare Border wired to
// PointerPressed rather than a Button, so a test that called a method instead
// of clicking would not prove the thing is reachable with a mouse.
//
// Joins the Settings collection because constructing any window in this app
// reads settings on the way up.
[Collection("Settings")]
public class UsageCardTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static AccountUsage Usage(
        double? session = 33,
        double? weekly = 84,
        ExtraUsage? extra = null,
        bool available = true,
        DateTimeOffset? readAt = null,
        DateTimeOffset? observedAt = null) =>
        new(
            ConfigDir: null,
            Label: "board",
            Available: available,
            SubscriptionType: "team",
            Session: session is null ? null : new UsageWindow(session.Value, Now.AddHours(3)),
            Weekly: weekly is null ? null : new UsageWindow(weekly.Value, Now.AddDays(3)),
            Extra: extra,
            ReadAt: readAt ?? Now,
            ObservedAt: observedAt);

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ConstructsHeadlessWithNoException()
    {
        Assert.NotNull(new UsageCard());
    }

    [AvaloniaFact]
    public void ShowsBothPercentagesFlooredTheWayTheCliPrintsThem()
    {
        var card = new UsageCard();

        card.UpdateFrom(Usage(33.7, 84.2), null, Now);

        Assert.Equal("33%", card.SessionText);
        Assert.Equal("84%", card.WeeklyText);
    }

    [AvaloniaFact]
    public void ExtraUsageWithACapGetsABar()
    {
        var card = new UsageCard();

        card.UpdateFrom(Usage(extra: new ExtraUsage(true, 6114, 2000, "USD", 2, null)), null, Now);

        Assert.True(card.ShowsExtraBar);
        Assert.Equal(string.Empty, card.ExtraNoteText);
    }

    // The regression this file exists to prevent.
    //
    // Shipped once: `disabled_reason: "org_level_disabled_until"` was rendered
    // as "Extra usage is off for your organisation" and shown to a user whose
    // organisation had switched nothing off. He had simply spent the month's
    // budget — which the payload says outright, one field away, in
    // `spend_limit_reached`. The fix is not a better translation of the code; it
    // is to stop translating it and read the boolean that means something.
    [AvaloniaFact]
    public void ASpentBudgetSaysSoRatherThanBlamingTheOrganisation()
    {
        var card = new UsageCard();

        // Exactly the shape the live account returns.
        var spent = new ExtraUsage(
            Enabled: false, UsedMinor: null, LimitMinor: null, Currency: "USD",
            DecimalPlaces: 2, DisabledReason: "org_level_disabled_until",
            UserDisabled: false, SpendLimitReached: true);

        card.UpdateFrom(Usage(extra: spent), null, Now);

        Assert.Equal("Extra usage limit reached for this month.", card.ExtraNoteText);
        Assert.DoesNotContain("organisation", card.ExtraNoteText);
    }

    // The explicit booleans win over the opaque string, and they win in this
    // order: a reached limit is the more specific fact, and the one a person can
    // act on.
    [AvaloniaTheory]
    [InlineData(false, false, false, null, "Extra usage is not active.")]
    [InlineData(false, false, false, "", "Extra usage is not active.")]
    [InlineData(false, false, false, "org_level_disabled_until",
        "Extra usage is not active right now (org_level_disabled_until).")]
    [InlineData(false, false, false, "something_new",
        "Extra usage is not active right now (something_new).")]
    [InlineData(false, true, false, "org_level_disabled_until",
        "Extra usage is switched off for this account.")]
    [InlineData(false, false, true, "org_level_disabled_until",
        "Extra usage limit reached for this month.")]
    [InlineData(true, false, false, null, "Extra usage is on, with no limit set.")]
    public void TheSentenceComesFromTheBooleansNotFromTheReasonCode(
        bool enabled, bool userDisabled, bool limitReached, string? reason, string expected)
    {
        var extra = new ExtraUsage(
            enabled, null, null, "USD", 2, reason, userDisabled, limitReached);

        Assert.Equal(expected, UsageCard.ExtraSentence(extra));
    }

    // An unrecognised code is still worth showing — verbatim, in parentheses, so
    // it can be looked up — but never paraphrased into a claim.
    [AvaloniaFact]
    public void AnUnfamiliarReasonIsShownVerbatimRatherThanInterpreted()
    {
        var extra = new ExtraUsage(false, null, null, "USD", 2, "some_future_code");

        var sentence = UsageCard.ExtraSentence(extra);

        Assert.Contains("some_future_code", sentence);
    }

    [AvaloniaFact]
    public void MoneyIsFormattedFromMinorUnits()
    {
        var usd = new ExtraUsage(true, 6114, 2000, "USD", 2, null);

        Assert.Equal("$61.14", UsageCard.Money(6114, usd));
        Assert.Equal("$20.00", UsageCard.Money(2000, usd));
        Assert.Equal("—", UsageCard.Money(null, usd));
    }

    [AvaloniaFact]
    public void ACurrencyWithNoDecimalsIsNotDividedByAHundred()
    {
        // Yen has no minor unit. Dividing it by 100 anyway would report a
        // hundredth of the real spend, in the direction nobody checks.
        var jpy = new ExtraUsage(true, 3000, 5000, "JPY", 0, null);

        Assert.Equal("JPY 3,000", UsageCard.Money(3000, jpy));
    }

    // Three different silences, three different sentences.
    [AvaloniaFact]
    public void AnAccountWithNoLimitsSaysSoRatherThanLookingBroken()
    {
        var card = new UsageCard();

        card.UpdateFrom(Usage(session: null, weekly: null, available: false), null, Now);

        Assert.True(card.ShowsStaleNote);
        Assert.Equal("No subscription limits on this account.", card.StaleText);
    }

    [AvaloniaFact]
    public void AColdReadingSaysHowOldItIs()
    {
        var card = new UsageCard();

        card.UpdateFrom(Usage(readAt: Now.AddHours(-2)), null, Now);

        Assert.True(card.ShowsStaleNote);
        Assert.Equal("Usage as of 2h ago.", card.StaleText);
    }

    // CB-83. A Codex or Grok reading is taken from a file its CLI wrote
    // whenever it last ran, so the read is always "now" and the number can be
    // days old. The card has to date the number, not the read — this one is
    // read a second ago and was true 38 hours ago, which is exactly the shape
    // that used to render as "Last read 0m ago" with no dimming anywhere.
    [AvaloniaFact]
    public void AFreshlyReadButOldSnapshotIsDatedByTheSnapshot()
    {
        var card = new UsageCard();

        card.UpdateFrom(
            Usage(readAt: Now.AddSeconds(-1), observedAt: Now.AddHours(-38)), null, Now);

        Assert.True(card.ShowsStaleNote);
        Assert.Equal("Usage as of 1d ago.", card.StaleText);
    }

    [AvaloniaFact]
    public void AFreshReadingSaysNothingAtAll()
    {
        var card = new UsageCard();

        card.UpdateFrom(Usage(readAt: Now.AddMinutes(-2)), null, Now);

        Assert.False(card.ShowsStaleNote);
    }

    [AvaloniaTheory]
    [InlineData(30, "moments")]
    [InlineData(60 * 7, "7m")]
    [InlineData(60 * 60 * 3, "3h")]
    [InlineData(60 * 60 * 24 * 2, "2d")]
    public void AgeIsRoundedToSomethingAPersonWouldSay(int seconds, string expected)
    {
        Assert.Equal(expected, UsageCard.Ago(TimeSpan.FromSeconds(seconds)));
    }

    // The pin is a Border wired to PointerPressed, not a Button, so this drives
    // a real pointer press at its on-screen centre and lets the headless hit
    // tester find it — the same thing a mouse would do.
    [AvaloniaFact]
    public void ClickingThePinRaisesPinToggledExactlyOnce()
    {
        var card = new UsageCard();
        card.UpdateFrom(Usage(), null, Now);
        card.Show();
        Flush();

        var fired = 0;
        card.PinToggled += _ => fired++;

        var pin = card.PinControl;
        var centre = pin.TranslatePoint(
            new Point(pin.Bounds.Width / 2, pin.Bounds.Height / 2), card);

        Assert.NotNull(centre);

        card.MouseDown(centre!.Value, MouseButton.Left, RawInputModifiers.None);
        Flush();

        Assert.Equal(1, fired);
    }

    [AvaloniaFact]
    public void ThePinReadsAsPressedOnceItIsPinned()
    {
        var card = new UsageCard();

        card.SetPinned(true);
        Assert.True(card.IsPinned);

        card.SetPinned(false);
        Assert.False(card.IsPinned);
    }
}
