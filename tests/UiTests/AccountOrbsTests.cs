using System;
using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// AccountOrbs driven by an in-memory source instead of the CLI.
//
// This is what IUsageSource exists for, and the same argument RemoteChat.cs
// makes for IRemoteChatSession: a surface fed by another process is untestable
// until the arrival is a seam. Nothing here starts a subprocess, so nothing
// here depends on which accounts the machine running the suite happens to have
// logged in.
//
// **What is deliberately not covered here is the hover timing.** The bridge is
// two DispatcherTimers and a confirmation that reads IsPointerOver on two
// separate top-level windows; headless can synthesize a click but cannot park a
// pointer over one window for 450ms and then move it to another. The decisions
// the timers guard — which card is open, what a pin does to it — are driven
// directly below; the delays themselves are named as uncovered in the PR.
[Collection("Settings")]
public class AccountOrbsTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeUsageSource : IUsageSource
    {
        internal List<AccountUsage> Readings { get; } = new();

        internal int Reads { get; private set; }

        public IReadOnlyList<AccountUsage> Read()
        {
            Reads++;
            return Readings;
        }
    }

    private static AccountUsage Usage(string? configDir, string label, double weekly = 40) =>
        new(
            ConfigDir: configDir,
            Label: label,
            Available: true,
            SubscriptionType: "team",
            Session: new UsageWindow(10, Now.AddHours(3)),
            Weekly: new UsageWindow(weekly, Now.AddDays(3)),
            Extra: null,
            ReadAt: Now);

    // Namespaced the way the gateway and room ids are, so an account's saved
    // position can never collide with a session's — those are keyed by cwd, and
    // a config directory is a path too.
    [Fact]
    public void PositionKeysAreNamespacedAwayFromSessions()
    {
        Assert.Equal("account:~", AccountOrbs.PositionKey(null));
        Assert.Equal(
            "account:/Users/x/.claude-work",
            AccountOrbs.PositionKey("/Users/x/.claude-work"));
    }

    [AvaloniaFact]
    public void OneOrbPerAccountThatAnswered()
    {
        var source = new FakeUsageSource();
        var orbs = new AccountOrbs(source);

        orbs.Apply(new[]
        {
            Usage(null, "wthompson"),
            Usage("/Users/x/.claude-board", "board")
        }, Now);

        Assert.Equal(2, orbs.Orbs.Count);
        Assert.True(orbs.Orbs.ContainsKey(string.Empty));
        Assert.True(orbs.Orbs.ContainsKey("/Users/x/.claude-board"));

        orbs.CloseAll();
    }

    [AvaloniaFact]
    public void AReadingUpdatesTheOrbItAlreadyHasRatherThanMakingASecond()
    {
        var orbs = new AccountOrbs(new FakeUsageSource());

        orbs.Apply(new[] { Usage(null, "wthompson", weekly: 10) }, Now);
        var first = orbs.Orbs[string.Empty];

        orbs.Apply(new[] { Usage(null, "wthompson", weekly: 95) }, Now);

        Assert.Single(orbs.Orbs);
        Assert.Same(first, orbs.Orbs[string.Empty]);
        Assert.Equal(AccountOrbWindow.DangerHex, first.WeeklyColour);

        orbs.CloseAll();
    }

    // A poll that failed is not news about usage. Removing the orb would make a
    // network blink look like an account being deleted, and an account orb that
    // comes and goes is one nobody can learn the position of.
    [AvaloniaFact]
    public void AnAccountThatAnswersNothingKeepsTheOrbItHad()
    {
        var orbs = new AccountOrbs(new FakeUsageSource());

        orbs.Apply(new[] { Usage(null, "wthompson") }, Now);
        Assert.Single(orbs.Orbs);

        orbs.Apply(Array.Empty<AccountUsage>(), Now);

        Assert.Single(orbs.Orbs);

        orbs.CloseAll();
    }

    [AvaloniaFact]
    public void PinningKeepsTheCardAndMarksTheOrb()
    {
        var orbs = new AccountOrbs(new FakeUsageSource());
        orbs.Apply(new[] { Usage(null, "wthompson") }, Now);

        orbs.TogglePin(string.Empty);

        Assert.True(orbs.Orbs[string.Empty].IsPinned);

        orbs.TogglePin(string.Empty);

        Assert.False(orbs.Orbs[string.Empty].IsPinned);

        orbs.CloseAll();
    }

    [AvaloniaFact]
    public void PinningSurvivesTheNextPoll()
    {
        // The poll redraws every orb from its reading. A pin that did not
        // survive that would come undone every five minutes on its own, which
        // is the one failure a pin cannot have.
        var orbs = new AccountOrbs(new FakeUsageSource());
        orbs.Apply(new[] { Usage(null, "wthompson") }, Now);

        orbs.TogglePin(string.Empty);
        orbs.Apply(new[] { Usage(null, "wthompson", weekly: 88) }, Now.AddMinutes(5));

        Assert.True(orbs.Orbs[string.Empty].IsPinned);

        orbs.CloseAll();
    }

    [AvaloniaFact]
    public void HidingTakesTheCardsWithTheOrbs()
    {
        var orbs = new AccountOrbs(new FakeUsageSource());
        orbs.Apply(new[] { Usage(null, "wthompson") }, Now);
        orbs.TogglePin(string.Empty);

        orbs.SetVisible(false);

        Assert.Empty(orbs.Cards);

        orbs.CloseAll();
    }

    [AvaloniaFact]
    public void ClosingEverythingLeavesNothingBehind()
    {
        var orbs = new AccountOrbs(new FakeUsageSource());
        orbs.Apply(new[]
        {
            Usage(null, "wthompson"),
            Usage("/Users/x/.claude-board", "board")
        }, Now);

        orbs.CloseAll();

        Assert.Empty(orbs.Orbs);
        Assert.Empty(orbs.Cards);
    }

    // The floor is the whole reason the poll is affordable: Claude Code caches
    // the underlying fetch for five minutes, so asking sooner spends a process
    // per account to be told the same thing.
    [AvaloniaFact]
    public void TheSettingBeingOffMeansNothingIsAsked()
    {
        var source = new FakeUsageSource();
        var orbs = new AccountOrbs(source);

        ClaudeBuddySettings.AccountUsageEnabled = false;
        orbs.Tick(Now);

        Assert.Equal(0, source.Reads);
    }
}
