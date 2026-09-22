using System;
using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers how long the cloud arm waits before asking again, and when it stops
// asking altogether.
//
// Stop is a distinct answer here rather than a very long wait, and that is the
// requirement rather than a style choice. CB-164 rules out a repeated OS prompt
// in as many words: on macOS, re-reading the credential is what raises the
// Keychain dialog, so a backoff that merely grew would still eventually put that
// dialog in front of someone who already said no. Null means stop, and nothing
// can mistake it for a sleep.
//
// The ten-round walk is the shape CLAUDE.md asks for over a sequence — monotone,
// capped, never below the floor — asserted across the whole run rather than at
// one point, because a single sample cannot tell a correct curve from one that
// happens to agree at round three.
public class ClaudeCloudBackoffTests
{
    private static CloudOutcome Outcome(CloudOutcomeKind kind, TimeSpan? retryAfter = null) =>
        new(kind, 0, retryAfter);

    // The enums are internal, and xUnit needs a public test method, so the cases
    // travel as names rather than as values. Enum.Parse rather than an int cast
    // on purpose: a typo'd name fails the test loudly, where a stale ordinal
    // would silently start testing the wrong arm the next time a member is
    // inserted in the middle.
    private static CloudOutcomeKind Kind(string name) => Enum.Parse<CloudOutcomeKind>(name);

    private static CredentialOutcome Credential(string name) =>
        Enum.Parse<CredentialOutcome>(name);

    [Theory]
    [InlineData("TokenRefused")]
    [InlineData("AuthFailed")]
    [InlineData("Blocked")]
    [InlineData("Ok")]
    public void NothingWaitingWouldFixMeansStop(string name)
    {
        var kind = Kind(name);

        Assert.Null(Backoff.Next(Outcome(kind), null));
        Assert.Null(Backoff.Next(Outcome(kind), TimeSpan.FromSeconds(8)));
    }

    [Theory]
    [InlineData("Denied")]
    [InlineData("NotLoggedIn")]
    [InlineData("Found")]
    public void TheSameRuleHoldsOnTheCredentialSide(string name)
    {
        var outcome = Credential(name);

        Assert.Null(Backoff.Next(outcome, null));
        Assert.Null(Backoff.Next(outcome, TimeSpan.FromSeconds(8)));
    }

    [Theory]
    [InlineData("Unavailable")]
    [InlineData("ShapeChanged")]
    public void ARetryableFailureStartsAtTheFloorAndDoubles(string name)
    {
        var kind = Kind(name);

        Assert.Equal(TimeSpan.FromSeconds(2), Backoff.Next(Outcome(kind), null));
        Assert.Equal(TimeSpan.FromSeconds(4),
            Backoff.Next(Outcome(kind), TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(8),
            Backoff.Next(Outcome(kind), TimeSpan.FromSeconds(4)));
    }

    // A previous shorter than the floor is not a reason to wait less than the
    // floor. This is the arm a caller reaches by handing back a zero or a
    // leftover value from somewhere else.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void APreviousBelowTheFloorIsLiftedToIt(int previousSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(2),
            Backoff.Next(Outcome(CloudOutcomeKind.Unavailable),
                TimeSpan.FromSeconds(previousSeconds)));
    }

    [Fact]
    public void TenConsecutiveFailuresAreMonotoneCappedAndNeverBelowTheFloor()
    {
        var waits = new List<TimeSpan>();
        TimeSpan? previous = null;

        for (var round = 0; round < 10; round++)
        {
            previous = Backoff.Next(Outcome(CloudOutcomeKind.Unavailable), previous);
            Assert.NotNull(previous);
            waits.Add(previous!.Value);
        }

        Assert.Equal(TimeSpan.FromSeconds(2), waits[0]);

        for (var i = 0; i < waits.Count; i++)
        {
            Assert.True(waits[i] >= TimeSpan.FromSeconds(2), $"round {i} fell below the floor");
            Assert.True(waits[i] <= TimeSpan.FromSeconds(60), $"round {i} exceeded the cap");
            if (i > 0) Assert.True(waits[i] >= waits[i - 1], $"round {i} went backwards");
        }

        // 2, 4, 8, 16, 32, then the cap for the rest — so by the tenth it has
        // been parked at 60 for a while rather than still climbing.
        Assert.Equal(TimeSpan.FromSeconds(60), waits[^1]);
        Assert.Equal(TimeSpan.FromSeconds(60), Backoff.Next(
            Outcome(CloudOutcomeKind.Unavailable), TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void TheCredentialSideWalksTheSameCurve()
    {
        TimeSpan? previous = null;
        for (var round = 0; round < 10; round++)
        {
            var next = Backoff.Next(CredentialOutcome.Unreadable, previous);
            Assert.NotNull(next);
            if (previous is { } last) Assert.True(next!.Value >= last);
            Assert.True(next!.Value <= TimeSpan.FromSeconds(60));
            previous = next;
        }

        Assert.Equal(TimeSpan.FromSeconds(60), previous);
        Assert.Equal(TimeSpan.FromSeconds(2), Backoff.Next(CredentialOutcome.Malformed, null));
    }

    // A rate limit honours what the server asked for, but not below a minute. A
    // server that rate-limits us and asks us back in one second is asking for a
    // loop, and this endpoint is one we are a guest on.
    [Fact]
    public void ARateLimitHonoursRetryAfterAboveTheFloor()
    {
        Assert.Equal(TimeSpan.FromSeconds(120),
            Backoff.Next(Outcome(CloudOutcomeKind.RateLimited, TimeSpan.FromSeconds(120)), null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(59)]
    public void ARateLimitNeverWaitsLessThanAMinuteWhateverWasAsked(int asked)
    {
        Assert.Equal(TimeSpan.FromSeconds(60),
            Backoff.Next(Outcome(CloudOutcomeKind.RateLimited, TimeSpan.FromSeconds(asked)),
                TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ARateLimitWithNoRetryAfterUsesTheFloor()
    {
        Assert.Equal(TimeSpan.FromSeconds(60),
            Backoff.Next(Outcome(CloudOutcomeKind.RateLimited), null));
    }

    // The rate-limit wait does not compound with the previous one: the server
    // told us when to come back, and doubling on top of that would drift further
    // out on every 429 for no reason anybody could point at.
    [Fact]
    public void ARateLimitIgnoresTheRunningBackoff()
    {
        Assert.Equal(TimeSpan.FromSeconds(90),
            Backoff.Next(Outcome(CloudOutcomeKind.RateLimited, TimeSpan.FromSeconds(90)),
                TimeSpan.FromSeconds(32)));
    }

    // The arm a cast reaches, which is what the `default` label is for.
    [Fact]
    public void AnUnknownKindIsTreatedAsRetryable()
    {
        Assert.Equal(TimeSpan.FromSeconds(2),
            Backoff.Next(Outcome((CloudOutcomeKind)999), null));
        Assert.Equal(TimeSpan.FromSeconds(2),
            Backoff.Next((CredentialOutcome)999, null));
    }
}
