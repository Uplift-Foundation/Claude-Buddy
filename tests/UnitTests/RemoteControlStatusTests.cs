using System;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// What RemoteControlSessions says about itself: the settings window's one status
// line, whether every relay has finished a poll, and whether the poll should be
// running fast.
//
// None of this needs a bridge. Starting one means a real subprocess talking to a
// live account, which RemoteControlBridgeLiveTests does deliberately and a unit
// test must not — so the relay table is seeded through SetRelayForTests and the
// questions asked directly.
//
// Serialised on the settings lane: the relay table is process-wide static, same
// as the settings model, and two of these running at once would see each other's
// relays.
[Collection("Settings")]
public class RemoteControlStatusTests : IDisposable
{
    public RemoteControlStatusTests() => RemoteControlSessions.ClearRelaysForTests();

    public void Dispose() => RemoteControlSessions.ClearRelaysForTests();

    // ---- Compose ---------------------------------------------------------

    // Composed from two independent facts rather than one string. The first
    // version wrote `warning ?? count`, which hid the count from anyone who had a
    // warning — and that is eventually everybody, since the login-expiry notice
    // starts three days out. "Your login expires in 3 days" is useful; not being
    // able to tell whether it also found anything is not.
    [Fact]
    public void AStateWithNoWarningIsJustTheState()
    {
        Assert.Equal("2 sessions", RemoteControlSessions.Compose("2 sessions", null));
    }

    [Fact]
    public void AWarningIsShownAlongsideTheStateNotInsteadOfIt()
    {
        var composed = RemoteControlSessions.Compose("2 sessions", "login expires in 3 days");

        Assert.Contains("2 sessions", composed);
        Assert.Contains("login expires in 3 days", composed);
    }

    // "off · login expires in 3 days" would be noise: there is no count to
    // protect, so the warning stands alone. Same for "starting", which is a
    // transient the user does not need told twice.
    [Theory]
    [InlineData("off")]
    [InlineData("starting")]
    public void AStateWithNothingToReportIsReplacedByTheWarning(string state)
    {
        Assert.Equal("no token", RemoteControlSessions.Compose(state, "no token"));
    }

    // ---- StatusText ------------------------------------------------------

    [Fact]
    public void NoRelaysReadsAsOff()
    {
        Assert.Equal("off", RemoteControlSessions.StatusText);
    }

    // One relay is not named, because there is no ambiguity to resolve.
    [Fact]
    public void ASingleRelayIsNotNamed()
    {
        RemoteControlSessions.SetRelayForTests("work@example.com", "3 sessions");

        Assert.Equal("3 sessions", RemoteControlSessions.StatusText);
    }

    [Fact]
    public void ASingleRelaysWarningIsComposedIn()
    {
        RemoteControlSessions.SetRelayForTests("work@example.com", "3 sessions", "login expires soon");

        var text = RemoteControlSessions.StatusText;

        Assert.Contains("3 sessions", text);
        Assert.Contains("login expires soon", text);
        Assert.DoesNotContain("work@example.com", text);
    }

    // With more than one, each is named — "connected" is no use when the question
    // is which of them is connected.
    [Fact]
    public void SeveralRelaysAreEachNamed()
    {
        RemoteControlSessions.SetRelayForTests("work@example.com", "3 sessions");
        RemoteControlSessions.SetRelayForTests("home@example.com", "off");

        var text = RemoteControlSessions.StatusText;

        Assert.Contains("work@example.com: 3 sessions", text);
        Assert.Contains("home@example.com: off", text);
    }

    [Fact]
    public void SeveralRelaysAreSeparatedRatherThanRunTogether()
    {
        RemoteControlSessions.SetRelayForTests("a@example.com", "off");
        RemoteControlSessions.SetRelayForTests("b@example.com", "off");

        Assert.Contains("·", RemoteControlSessions.StatusText);
    }

    // ---- HasPolled -------------------------------------------------------

    // "Up, and has looked" versus "up, about to look". The status line cannot
    // make that distinction — it reads as connected the moment a process starts —
    // and conflating them is why the first live test of this passed while
    // measuring nothing.
    [Fact]
    public void NoRelaysHaveNotPolled()
    {
        Assert.False(RemoteControlSessions.HasPolled);
    }

    [Fact]
    public void ARelayThatHasNotLookedYetDoesNotCount()
    {
        RemoteControlSessions.SetRelayForTests("work@example.com", "starting", polled: false);

        Assert.False(RemoteControlSessions.HasPolled);
    }

    [Fact]
    public void EveryRelayHasToHavePolledNotJustOne()
    {
        RemoteControlSessions.SetRelayForTests("a@example.com", "1 session", polled: true);
        RemoteControlSessions.SetRelayForTests("b@example.com", "starting", polled: false);

        Assert.False(RemoteControlSessions.HasPolled);

        RemoteControlSessions.SetRelayForTests("b@example.com", "off", polled: true);

        Assert.True(RemoteControlSessions.HasPolled);
    }

    // ---- ShouldPollFast --------------------------------------------------

    // The fast cadence is held for a grace period after the user sends something,
    // so the reply does not wait for the slow poll to come round. A send long ago
    // must not hold it there forever.
    [Fact]
    public void AnOldSendDoesNotKeepThePollFast()
    {
        RemoteControlSessions.SetLastSendForTests(DateTime.UtcNow - TimeSpan.FromHours(1));

        Assert.False(RemoteControlSessions.ShouldPollFast());
    }

    [Fact]
    public void AJustSentMessageKeepsThePollFast()
    {
        RemoteControlSessions.SetLastSendForTests(DateTime.UtcNow);

        Assert.True(RemoteControlSessions.ShouldPollFast());
    }

    // DateTime.MinValue is the initial value, and it must not read as recent —
    // which it would if the comparison were on the wrong side of the subtraction.
    [Fact]
    public void HavingNeverSentAnythingIsNotBusy()
    {
        RemoteControlSessions.SetLastSendForTests(DateTime.MinValue);

        Assert.False(RemoteControlSessions.ShouldPollFast());
    }
}
