using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// OpenClawSessions.ChatFor and RoomChatFor: what the chat panel gets back when it
// asks for a session.
//
// Both are gates before they are factories, and the gates are the part worth
// asserting — a panel that gets a session back for a feature that is switched off
// is a panel showing a conversation with something that is not connected. Both
// also cache, and returning a *different* session object for the same id would
// silently detach whatever the panel had already subscribed to.
//
// The history load each one kicks off is fire-and-forget and needs a gateway, so
// it simply fails in the background here. That is the same thing it does on a
// machine whose gateway is down, and it is why these are safe to call.
//
// Serialised: reads OpenClawEnabled and writes the process-wide session registry.
[Collection("Settings")]
public class OpenClawSessionLookupTests
{
    private static void Enabled(bool on)
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.OpenClawEnabled = on;
    }

    // ---- the gate --------------------------------------------------------

    [Fact]
    public void NothingIsHandedBackWhileTheFeatureIsOff()
    {
        Enabled(false);

        Assert.Null(OpenClawSessions.ChatFor("openclaw:agent:abc:main", "worker"));
    }

    // The prefix is what distinguishes an OpenClaw session id from every other
    // kind the app handles, so an id without it is not this class's business.
    [Fact]
    public void AnIdWithoutTheOpenClawPrefixIsNotOurs()
    {
        Enabled(true);

        Assert.Null(OpenClawSessions.ChatFor("agent:abc:main", "worker"));
        Assert.Null(OpenClawSessions.ChatFor("remote:abc", "worker"));
    }

    // ---- caching ---------------------------------------------------------

    // The same id must hand back the same object. A fresh one each time would
    // leave the panel subscribed to a session nothing is updating any more —
    // the transcript would simply stop moving, with nothing on screen to say why.
    [Fact]
    public void AskingTwiceForTheSameSessionGivesTheSameObject()
    {
        Enabled(true);

        var first = OpenClawSessions.ChatFor("openclaw:agent:cache-test:main", "worker");
        var second = OpenClawSessions.ChatFor("openclaw:agent:cache-test:main", "worker");

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void DifferentSessionsAreDifferentObjects()
    {
        Enabled(true);

        var one = OpenClawSessions.ChatFor("openclaw:agent:one:main", "worker");
        var two = OpenClawSessions.ChatFor("openclaw:agent:two:main", "worker");

        Assert.NotSame(one, two);
    }

    // A better display name arriving later is applied to the cached session
    // rather than being dropped — agents.list lands after the first connection,
    // so the first name is often a placeholder.
    [Fact]
    public void ALaterDisplayNameIsAppliedToTheCachedSession()
    {
        Enabled(true);

        OpenClawSessions.ChatFor("openclaw:agent:rename-test:main", "agent-1");
        var session = OpenClawSessions.ChatFor("openclaw:agent:rename-test:main", "Zara");

        Assert.Equal("Zara", Assert.IsType<OpenClawChatSession>(session).DisplayName);
    }

    // ...but a blank one is not, or a scan that has not learned the name yet
    // would wipe the name that was already known.
    [Fact]
    public void ABlankDisplayNameDoesNotWipeAKnownOne()
    {
        Enabled(true);

        OpenClawSessions.ChatFor("openclaw:agent:blank-test:main", "Zara");
        var session = OpenClawSessions.ChatFor("openclaw:agent:blank-test:main", "   ");

        Assert.Equal("Zara", Assert.IsType<OpenClawChatSession>(session).DisplayName);
    }

    // ---- rooms -----------------------------------------------------------

    [Fact]
    public void NoRoomIsHandedBackWhileTheFeatureIsOff()
    {
        Enabled(false);

        Assert.Null(OpenClawSessions.RoomChatFor(
            "openclaw:room:discord:1", "#general", new[] { "agent:abc:main" }));
    }

    // A room with nobody in it is not a room. Returning one would give the panel
    // an empty transcript with a composer, which reads as a conversation that has
    // not started rather than as a channel with no agents in it.
    [Fact]
    public void ARoomWithNoMembersIsNotARoom()
    {
        Enabled(true);

        Assert.Null(OpenClawSessions.RoomChatFor(
            "openclaw:room:discord:2", "#empty", new List<string>()));
    }

    [Fact]
    public void ARoomWithMembersIsBuiltAndCached()
    {
        Enabled(true);

        var first = OpenClawSessions.RoomChatFor(
            "openclaw:room:discord:3", "#general", new[] { "agent:zara:main" });
        var second = OpenClawSessions.RoomChatFor(
            "openclaw:room:discord:3", "#general", new[] { "agent:zara:main" });

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    // The channel's name can improve between scans the same way an agent's can.
    [Fact]
    public void ARoomsDisplayNameIsRefreshed()
    {
        Enabled(true);

        OpenClawSessions.RoomChatFor(
            "openclaw:room:discord:4", "#old-name", new[] { "agent:zara:main" });
        var room = OpenClawSessions.RoomChatFor(
            "openclaw:room:discord:4", "#new-name", new[] { "agent:zara:main" });

        Assert.Equal("#new-name",
            Assert.IsType<OpenClawRoomChatSession>(room).DisplayName);
    }

    // Membership is re-applied on every call, because who is in a channel
    // changes — so a member added on a later scan has to reach the room the
    // panel is already holding.
    [Fact]
    public void AMemberAddedOnALaterScanReachesTheCachedRoom()
    {
        Enabled(true);

        var first = OpenClawSessions.RoomChatFor(
            "openclaw:room:discord:5", "#general", new[] { "agent:zara:main" });
        var second = OpenClawSessions.RoomChatFor(
            "openclaw:room:discord:5", "#general",
            new[] { "agent:zara:main", "agent:kai:main" });

        Assert.Same(first, second);
    }
}
