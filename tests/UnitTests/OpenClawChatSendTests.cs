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

    // CB-35: the user's own turn goes on *before* the refusal note, the same
    // shape OpenClawRoomChatSession's no-address refusal already had. This
    // used to be a System turn on its own — nothing else in the transcript —
    // which disagreed with the room's own refusal about the one thing
    // neither transport actually decides: where the user's own words go.
    // See RemoteControlChatSessionTurnTests.WithRemoteControlOffTheMessageIsRefusedButKept
    // for the transport that always did it this way.
    [Fact]
    public async Task WithReplyingOffTheMessageIsRefusedInTheTranscript()
    {
        Replying(false);
        var session = Session();

        await session.SendAsync("hello?");

        Assert.Collection(session.History,
            first =>
            {
                Assert.Equal(ChatRole.User, first.Role);
                Assert.Equal("hello?", first.Text);
            },
            second =>
            {
                Assert.Equal(ChatRole.System, second.Role);
                Assert.Contains("Replying is off", second.Text);
                Assert.Contains("Settings", second.Text);
            });
    }

    // CB-35: the typed text IS added as a user turn now — see the previous
    // test's comment for why. This test used to assert the opposite; it now
    // asserts the return value that replaces "nothing looks like it was
    // sent" as the way a caller finds out nothing went anywhere: SendAsync
    // itself says so, which is what lets ChatPanel.Send() retain the typed
    // text in the composer instead of losing it.
    [Fact]
    public async Task WithReplyingOffSendAsyncReportsFailure()
    {
        Replying(false);
        var session = Session();

        var outcome = await session.SendAsync("hello?");

        Assert.Equal(ChatSendOutcome.Failed, outcome);
    }

    // The user's own turn is added by the session rather than the panel, so one
    // thing owns the transcript: a send that fails leaves the message on screen
    // with an explanation under it rather than a ghost.
    [Fact]
    public async Task AFailedSendLeavesTheMessageOnScreenWithAReasonUnderIt()
    {
        Replying(true);
        var session = Session();

        var outcome = await session.SendAsync("hello?");

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

        // CB-35: there is no live gateway in this test, so SendOrFailureAsync's
        // catch always fires and this is the one outcome reachable here —
        // see AFailedSendLeavesTheMessageOnScreenWithAReasonUnderIt's own
        // comment above for why that arm exists at all. The Sent arm has no
        // in-process seam to drive without a real gateway accepting a write.
        Assert.Equal(ChatSendOutcome.Failed, outcome);
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

    // The panel's seam onto the backlog. The fetch itself lives on
    // OpenClawSessions, which owns the connection — this exists so the panel does
    // not have to know that a page comes from a gateway rather than from a file on
    // disk, which is the whole reason a remote session and a local one can share a
    // panel at all.
    //
    // With no gateway there is nothing to fetch, and saying so is the answer the
    // panel needs: it reads it to decide whether it has reached the top.
    [Fact]
    public async Task PagingWithNoGatewayReportsNothingFetched()
    {
        Replying(true);

        Assert.False(await Session().LoadOlderAsync(System.Threading.CancellationToken.None));
    }

    // A session starts believing there is more to fetch, because the alternative
    // is a panel that refuses to scroll back before it has ever asked.
    [Fact]
    public void ASessionStartsAssumingThereIsMoreToFetch()
    {
        Assert.True(Session().HasMore);
    }

    // The composer says where a message goes. This one stays on the machine it is
    // already on, so it gets the plain wording — unlike the room's "Message the
    // channel…" or the remote session's "on the other machine".
    [Fact]
    public void TheComposerHintFollowsTheReplySetting()
    {
        Replying(true);
        Assert.Equal("Message…", Session().ComposerHint);

        Replying(false);
        Assert.Equal("Replying is off", Session().ComposerHint);
    }
}
