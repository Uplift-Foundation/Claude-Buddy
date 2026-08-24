using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// OpenClawChatSession.SendAsync: what the transcript looks like after someone
// types a sentence and presses return.
//
// Both outcomes are reachable with no gateway anywhere near the test. Replying
// off is a pure settings check, and replying on with nothing to talk to takes the
// catch — which is the arm worth having, because the alternative to catching is
// the user's sentence disappearing with no explanation.
//
// Serialised: reads OpenClawReplyEnabled off the process-wide settings model.
[Collection("Settings")]
public class OpenClawChatSendTests
{
    private static OpenClawChatSession Session() =>
        new("agent:abc:main", "gateway-key", "worker");

    private static void Replying(bool enabled)
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.OpenClawReplyEnabled = enabled;
    }

    // A System turn rather than an exception: the person has just typed a
    // sentence, and losing it behind a dialog is a poor answer to "why didn't
    // that send". The note also has to say where to turn it on.
    [Fact]
    public async Task WithReplyingOffTheMessageIsRefusedInTheTranscript()
    {
        Replying(false);
        var session = Session();

        await session.SendAsync("hello?");

        var turn = Assert.Single(session.History);
        Assert.Equal(ChatRole.System, turn.Role);
        Assert.Contains("Replying is off", turn.Text);
        Assert.Contains("Settings", turn.Text);
    }

    // And the typed text is NOT added as a user turn, so nothing on screen
    // claims to have been sent.
    //
    // RemoteControlChatSession does the opposite — it adds the user's turn first
    // and puts the refusal underneath, so the typed text survives — while its
    // comment says "same reasoning as OpenClawChatSession's". See
    // RemoteControlChatSessionTurnTests.WithRemoteControlOffTheMessageIsRefusedButKept.
    // Both behaviours are asserted where they are, rather than one of them being
    // quietly changed to match the other: which is right is a product call.
    [Fact]
    public async Task WithReplyingOffNothingLooksLikeItWasSent()
    {
        Replying(false);
        var session = Session();

        await session.SendAsync("hello?");

        Assert.DoesNotContain(session.History, t => t.Role == ChatRole.User);
    }

    // The user's own turn is added by the session rather than the panel, so one
    // thing owns the transcript: a send that fails leaves the message on screen
    // with an explanation under it rather than a ghost.
    [Fact]
    public async Task AFailedSendLeavesTheMessageOnScreenWithAReasonUnderIt()
    {
        Replying(true);
        var session = Session();

        await session.SendAsync("hello?");

        Assert.Collection(session.History,
            first =>
            {
                Assert.Equal(ChatRole.User, first.Role);
                Assert.Equal("hello?", first.Text);
                Assert.True(first.IsComplete);
            },
            second =>
            {
                Assert.Equal(ChatRole.System, second.Role);
                Assert.StartsWith("Couldn't send:", second.Text);
            });
    }

    // Every turn this class adds is complete on arrival — none of them stream —
    // so a panel must never render one as still being written.
    [Fact]
    public async Task EveryTurnThisSessionAddsIsAlreadyComplete()
    {
        Replying(true);
        var session = Session();

        await session.SendAsync("hello?");

        Assert.All(session.History, t => Assert.True(t.IsComplete));
    }

    [Fact]
    public async Task SendingRaisesTurnAddedForEachTurn()
    {
        Replying(true);
        var session = Session();
        var seen = 0;
        session.TurnAdded += _ => seen++;

        await session.SendAsync("hello?");

        Assert.Equal(session.History.Count, seen);
    }

    // Cancel is deliberately a no-op: stopping someone else's run — one started
    // from Discord or a cron schedule — is not something a viewer should be able
    // to do by accident. Asserted so that "does nothing" is a decision on record
    // rather than an empty method someone fills in.
    [Fact]
    public async Task CancelDoesNothingAndDisturbsNothing()
    {
        Replying(true);
        var session = Session();
        await session.SendAsync("hello?");
        var before = session.History.Count;

        session.Cancel();

        Assert.Equal(before, session.History.Count);
    }
}
