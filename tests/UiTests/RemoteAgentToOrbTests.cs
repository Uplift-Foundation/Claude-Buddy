using System;
using System.Linq;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.UiTests;

// Which entries in a relay's peer list become orbs, and what they carry.
//
// The filter is the part that matters. A peer list is not a list of sessions
// anyone wants on screen: it holds local peers, it holds Buddy's own relays, and
// it holds registrations belonging to processes that are already gone. Let any of
// those through and the screen fills with orbs that cannot be clicked usefully.
//
// Serialised because the colour table it reads is process-wide.
[Collection("Settings")]
public class RemoteAgentToOrbTests
{
    private const string Account = "work@example.com";

    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private static BridgeProtocol.RemoteAgent Agent(
        string name, string kind = "Remote Control", string status = "idle") =>
        new(Name: name, Ref: "bridge:session_01", Kind: kind, Status: status);

    private static System.Collections.Generic.List<RemoteControlSessions.Remote> Map(
        params BridgeProtocol.RemoteAgent[] agents) =>
        RemoteControlSessions.RemotesFrom(agents, Account, Now);

    [AvaloniaFact]
    public void ARemoteControlSessionBecomesAnOrb()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        var remote = Assert.Single(Map(Agent("zara")));

        Assert.Equal("zara", remote.Name);
        Assert.Equal(Account, remote.Account);
        Assert.Equal(Now, remote.Seen);
    }

    // A local peer is not on another machine, which is the whole distinction this
    // feature exists for — its kind reads "interactive" or "bg" instead.
    [AvaloniaTheory]
    [InlineData("interactive")]
    [InlineData("bg")]
    public void ALocalPeerIsNotAnOrb(string kind)
    {
        RemoteControlSessions.ForgetAnswersForTests();

        Assert.Empty(Map(Agent("zara", kind: kind)));
    }

    // Buddy's own relay is plumbing, not a session. The current one excludes
    // itself from the list it returns, but a PREVIOUS one does not — its
    // registration outlives its process, so an earlier relay turns up as an
    // offline peer. That was observed, and it would have put a phantom orb on
    // screen named after Buddy's own machinery.
    [AvaloniaFact]
    public void BuddysOwnRelayIsNotAnOrb()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        Assert.Empty(Map(Agent("claude-buddy-rc-mac-mini")));
    }

    [AvaloniaFact]
    public void TheOwnRelayPrefixIsMatchedWhateverItsCase()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        Assert.Empty(Map(Agent("CLAUDE-BUDDY-RC-mac-mini")));
    }

    // Offline is the state a registration sits in once its process is gone, so an
    // orb for it would be an orb for something nothing can be sent to.
    [AvaloniaFact]
    public void AnOfflineSessionIsNotAnOrb()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        Assert.Empty(Map(Agent("zara", status: "offline")));
    }

    [AvaloniaFact]
    public void TheGoodAndTheBadCanArriveTogether()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        var remotes = Map(
            Agent("zara"),
            Agent("local", kind: "interactive"),
            Agent("claude-buddy-rc-old"),
            Agent("gone", status: "offline"),
            Agent("kai"));

        Assert.Equal(new[] { "zara", "kai" }, remotes.Select(r => r.Name));
    }

    // A colour the session has answered with is attached as the orb is built, so
    // the next scan draws it without waiting for another poll.
    [AvaloniaFact]
    public void AKnownColourIsAttached()
    {
        RemoteControlSessions.ForgetAnswersForTests();
        RemoteControlSessions.OnMessage(Account,
            new BridgeProtocol.InboundMessage(
                FromName: "zara", From: "bridge:session_01", Mode: "prompting",
                Body: BridgeProtocol.InfoMarker + " color=#ff0000"));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("#ff0000", Assert.Single(Map(Agent("zara"))).Color);
    }

    // A session that has not answered gets no colour rather than a guess — the
    // orb then falls back to whatever it would show without one.
    [AvaloniaFact]
    public void AnUnansweredSessionGetsNoColour()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        Assert.Null(Assert.Single(Map(Agent("never-answered"))).Color);
    }

    // Keyed by account as well as name: two accounts can hold identically-named
    // sessions, and one answering must not colour the other's orb.
    [AvaloniaFact]
    public void AColourFromOneAccountDoesNotReachAnother()
    {
        RemoteControlSessions.ForgetAnswersForTests();
        RemoteControlSessions.OnMessage("home@example.com",
            new BridgeProtocol.InboundMessage(
                FromName: "zara", From: "bridge:session_01", Mode: "prompting",
                Body: BridgeProtocol.InfoMarker + " color=#00ff00"));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Null(Assert.Single(Map(Agent("zara"))).Color);
    }

    [AvaloniaFact]
    public void AnEmptyPeerListMakesNoOrbs()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        Assert.Empty(Map());
    }
}
