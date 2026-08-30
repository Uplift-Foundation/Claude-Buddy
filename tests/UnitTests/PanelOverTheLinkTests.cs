using Xunit;

namespace ClaudeBuddy.UnitTests;

// The rules that decide whether the panel talks over the link or the relay,
// and whether anything is drawn at all.
//
// Every one of them used to be an implicit "the relay is the only transport",
// which is why they are worth pinning: none announced itself as a decision, and
// each would fail in a way that reads as the link not working rather than as a
// switch being consulted about the wrong thing.
public class PanelOverTheLinkTests
{
    private static RemoteMirrorClient Client(string account) =>
        new(account, new RemoteMirrorClient.Seams((_, _) => Task.FromResult(true)));

    // --- which client a panel gets ------------------------------------------

    [Fact]
    public void TheDirectLinkWinsWhenBothExist()
    {
        var direct = Client("direct");
        var relayed = Client("relayed");

        Assert.Same(direct, RemoteControlSessions.Prefer(direct, relayed));
    }

    [Fact]
    public void TheRelayIsUsedWhenThereIsNoLink()
    {
        var relayed = Client("relayed");

        Assert.Same(relayed, RemoteControlSessions.Prefer(null, relayed));
    }

    [Fact]
    public void TheLinkIsUsedWhenThereIsNoRelay()
    {
        var direct = Client("direct");

        Assert.Same(direct, RemoteControlSessions.Prefer(direct, null));
    }

    [Fact]
    public void NeitherMeansNothing()
    {
        Assert.Null(RemoteControlSessions.Prefer(null, null));
    }

    // --- whether remote orbs are drawn at all --------------------------------

    private static readonly IReadOnlyList<RemoteControlSessions.Remote> OneRow =
        new[] { new RemoteControlSessions.Remote("jh", "mini", "idle", DateTime.UnixEpoch, "acct") };

    [Fact]
    public void TheLinkAloneIsEnoughToShowRemoteOrbs()
    {
        // The case the old gate got wrong: relay off, link on, real rows.
        Assert.Same(OneRow, RemoteControlSessions.Visible(
            OneRow, relayOn: false, relaySupported: false, linkOn: true));
    }

    [Fact]
    public void TheRelayAloneIsStillEnough()
    {
        Assert.Same(OneRow, RemoteControlSessions.Visible(
            OneRow, relayOn: true, relaySupported: true, linkOn: false));
    }

    [Fact]
    public void AnUnsupportedRelayWithNoLinkShowsNothing()
    {
        Assert.Empty(RemoteControlSessions.Visible(
            OneRow, relayOn: true, relaySupported: false, linkOn: false));
    }

    [Fact]
    public void BothOffShowsNothing()
    {
        Assert.Empty(RemoteControlSessions.Visible(
            OneRow, relayOn: false, relaySupported: false, linkOn: false));
    }

    // --- whether a send is even attempted ------------------------------------

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EitherTransportIsEnoughToSend(bool relayOn, bool linkOn)
    {
        Assert.True(RemoteControlChatSession.CanReachRemotes(relayOn, linkOn));
    }

    [Fact]
    public void NoTransportRefusesTheSend()
    {
        Assert.False(RemoteControlChatSession.CanReachRemotes(false, false));
    }

    // --- roster rows become orb rows -----------------------------------------

    private static MirrorProtocol.MirrorRosterEntry Entry(
        string name, string? colour = null, string? status = null) =>
        new(name, MirrorProtocol.CliClaudeCode, true, true, colour, null, status);

    [Fact]
    public void ARosterEntryBecomesAnOrbRowOnTheMachineThatServedIt()
    {
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        var rows = RemoteControlSessions.RemotesFromRoster(
            "acct", new[] { ("mac-mini", Entry("job-hunter")) }, now);

        var row = Assert.Single(rows);

        Assert.Equal("job-hunter", row.Name);
        Assert.Equal("mac-mini", row.Ref);
        Assert.Equal("acct", row.Account);
        Assert.Equal(now, row.Seen);
    }

    [Fact]
    public void AWorkingSessionDrawsAsWorking()
    {
        var rows = RemoteControlSessions.RemotesFromRoster(
            "acct", new[] { ("mini", Entry("jh", status: "working")) }, DateTime.UnixEpoch);

        Assert.True(Assert.Single(rows).Working);
    }

    [Fact]
    public void AnEntryWithNoStatusReadsAsIdleRatherThanAsNothing()
    {
        // An older Buddy on the far end answers without the field. It should
        // still get an orb — one that is wrong about its pulse, not absent.
        var rows = RemoteControlSessions.RemotesFromRoster(
            "acct", new[] { ("mini", Entry("jh")) }, DateTime.UnixEpoch);

        var row = Assert.Single(rows);

        Assert.Equal("idle", row.Status);
        Assert.False(row.Working);
    }

    [Fact]
    public void ColourCarriesAcrossAndBlankIsNotAColour()
    {
        var rows = RemoteControlSessions.RemotesFromRoster(
            "acct",
            new[] { ("mini", Entry("green-one", "green")), ("mini", Entry("blank-one", "  ")) },
            DateTime.UnixEpoch);

        Assert.Equal("green", rows.Single(r => r.Name == "green-one").Color);
        Assert.Null(rows.Single(r => r.Name == "blank-one").Color);
    }

    [Fact]
    public void AnEntryNobodyServesIsDroppedRatherThanDrawnWithNoMachine()
    {
        // A row with no machine is an orb the panel could not then ask anyone
        // about — worse than no orb, because it looks like a session that is
        // there and unreachable.
        var rows = RemoteControlSessions.RemotesFromRoster(
            "acct",
            new[] { ("", Entry("orphan")), ("mini", Entry("real")) },
            DateTime.UnixEpoch);

        Assert.Equal("real", Assert.Single(rows).Name);
    }

    [Fact]
    public void EveryMachineIsRepresented()
    {
        var rows = RemoteControlSessions.RemotesFromRoster(
            "acct",
            new[] { ("mini", Entry("a")), ("laptop", Entry("b")) },
            DateTime.UnixEpoch);

        Assert.Equal(new[] { "laptop", "mini" }, rows.Select(r => r.Ref).OrderBy(x => x));
    }

    [Fact]
    public void NothingKnownIsNoRows()
    {
        Assert.Empty(RemoteControlSessions.RemotesFromRoster(
            "acct",
            Array.Empty<(string, MirrorProtocol.MirrorRosterEntry)>(),
            DateTime.UnixEpoch));
    }
}
