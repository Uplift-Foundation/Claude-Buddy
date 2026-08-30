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

    // **One switch again, and a different one.** This gate asked about the relay
    // while a relay was the only way a remote row could exist, then briefly
    // about both, and now about the link alone. The middle version is the one
    // worth remembering: it drew nothing on exactly the machine most likely to
    // have rows, because it insisted on a switch users had been told to turn off.
    [Fact]
    public void TheLinkOnShowsRemoteOrbs()
    {
        Assert.Same(OneRow, RemoteControlSessions.Visible(OneRow, linkOn: true));
    }

    [Fact]
    public void TheLinkOffShowsNothing()
    {
        // The scan never has to know *why* the list is empty, which is the whole
        // reason this is a gate rather than a filter further down.
        Assert.Empty(RemoteControlSessions.Visible(OneRow, linkOn: false));
    }

    // --- whether a send is even attempted ------------------------------------

    // The send gate went the same way — one transport, one switch — so
    // CanReachRemotes is gone and SendAsync asks about the link directly. What
    // is worth keeping is the wording, because a refusal that does not name the
    // setting to turn on is a dead end for whoever reads it.
    [Fact]
    public void TheRefusalNamesTheSettingToTurnOn()
    {
        Assert.Contains(
            "Show sessions from other machines",
            RemoteControlChatSession.RemoteControlOffNote);
    }

    // And the other refusal, which is new: a session on screen with no live
    // view can no longer be sent to at all, because the messaging channel that
    // used to answer here went with the relay. Said plainly rather than left as
    // a composer that swallows what you type.
    [Fact]
    public void ASessionWithNoLiveViewSaysWhyItCannotBeWrittenTo()
    {
        var note = RemoteControlChatSession.NoWayToSendNote("job-hunter");

        Assert.Contains("job-hunter", note);
        Assert.Contains("tmux", note);
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
