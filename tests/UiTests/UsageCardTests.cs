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
        DateTimeOffset? readAt = null) =>
        new(
            ConfigDir: null,
            Label: "board",
            Available: available,
            SubscriptionType: "team",
            Session: session is null ? null : new UsageWindow(session.Value, Now.AddHours(3)),
            Weekly: weekly is null ? null : new UsageWindow(weekly.Value, Now.AddDays(3)),
            Extra: extra,
            ReadAt: readAt ?? Now);

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

    // The reason matters more than the absence. "Your organisation turned this
    // off" and "you never switched it on" are different problems with different
    // people to talk to, and the reason code is the only thing separating them.
    [AvaloniaFact]
    public void ExtraUsageWithoutACapGetsASentenceSayingWhy()
    {
        var card = new UsageCard();

        var off = new ExtraUsage(false, 0, null, "USD", 2, "org_level_disabled_until");
        card.UpdateFrom(Usage(extra: off), null, Now);

        Assert.False(card.ShowsExtraBar);
        Assert.Equal("Extra usage is off for your organisation.", card.ExtraNoteText);
    }

    [AvaloniaTheory]
    [InlineData(null, "Extra usage is off.")]
    [InlineData("", "Extra usage is off.")]
    [InlineData("org_level_disabled_until", "Extra usage is off for your organisation.")]
    [InlineData("something_new", "Extra usage is off (something_new).")]
    public void AnUnfamiliarReasonIsShownRatherThanSwallowed(string? reason, string expected)
    {
        // An unrecognised reason code is still the only information available
        // about why the bar is missing. Printing it raw is ugly and beats
        // "Extra usage is off" with no explanation for a state nobody has seen.
        var extra = new ExtraUsage(false, null, null, "USD", 2, reason);

        Assert.Equal(expected, UsageCard.ExtraSentence(extra));
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
        Assert.Equal("Last read 2h ago.", card.StaleText);
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
