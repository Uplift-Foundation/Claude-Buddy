using Xunit;

namespace ClaudeBuddy.UnitTests;

// Covers the two decisions PeerSessions makes — which machines are worth
// dialling, and what the user is told about the link.
//
// Everything that opens a socket or starts a timer is excluded from coverage,
// as the rest of this app treats sockets. What is left is small and worth
// asserting: dialling the wrong set wastes connections, and a status line that
// cannot distinguish "off" from "on but alone" is the exact complaint that
// motivated replacing the transport in the first place.
public class PeerSessionsTests
{
    private static PeerDiscovery.Seen Seen(string machine) =>
        new(machine, "192.168.0.9", 7677, "pin", DateTime.UtcNow);

    // --- who is worth dialling -------------------------------------------------

    [Fact]
    public void APairedMachineWeCanSeeAndAreNotTalkingToIsDialled()
    {
        var worth = PeerSessions.WorthDialling(
            new[] { Seen("avatar") }, paired: _ => true, connected: _ => false);

        Assert.Equal(new[] { "avatar" }, worth.Select(p => p.Machine));
    }

    // Pairing is a deliberate act. A machine announcing itself on the network is
    // not an invitation, and dialling one we have never agreed to would make
    // discovery an attack surface rather than an address book.
    [Fact]
    public void AMachineWeHaveNotPairedWithIsLeftAlone() =>
        Assert.Empty(PeerSessions.WorthDialling(
            new[] { Seen("stranger") }, paired: _ => false, connected: _ => false));

    // One connection between two machines, not one per attempt. The link carries
    // both directions once established, so a second would be waste at best and a
    // pair of half-used sockets at worst.
    [Fact]
    public void AMachineWeAreAlreadyTalkingToIsNotDialledAgain() =>
        Assert.Empty(PeerSessions.WorthDialling(
            new[] { Seen("avatar") }, paired: _ => true, connected: _ => true));

    // A paired machine we have never heard from has no address to dial, which is
    // the reason discovery exists at all.
    [Fact]
    public void NothingSeenMeansNothingToDial() =>
        Assert.Empty(PeerSessions.WorthDialling(
            Array.Empty<PeerDiscovery.Seen>(), paired: _ => true, connected: _ => false));

    [Fact]
    public void OnlyTheOnesThatQualifyAreDialled()
    {
        var worth = PeerSessions.WorthDialling(
            new[] { Seen("paired-and-free"), Seen("paired-and-busy"), Seen("stranger") },
            paired: m => m.StartsWith("paired"),
            connected: m => m.EndsWith("busy"));

        Assert.Equal(new[] { "paired-and-free" }, worth.Select(p => p.Machine));
    }

    // --- what the user is told ---------------------------------------------------

    // The four states are deliberately distinct. "Didn't answer" meaning six
    // different things is what made the old transport undiagnosable, and a
    // status line that collapsed these would repeat that in a smaller way.

    [Fact]
    public void OffSaysOff() =>
        Assert.Equal("Off", PeerSessions.StatusText(
            enabled: false, running: true, listening: 7677, connected: 2, seen: 2));

    [Fact]
    public void EnabledButNotYetUpSaysSo() =>
        Assert.Contains("Starting", PeerSessions.StatusText(
            enabled: true, running: false, listening: 0, connected: 0, seen: 0));

    // Listening and alone is not a failure, and must not read as one — it is
    // what every machine says before the second one is switched on.
    [Fact]
    public void ListeningWithNobodyAroundSaysThatPlainly()
    {
        var said = PeerSessions.StatusText(
            enabled: true, running: true, listening: 7677, connected: 0, seen: 0);

        Assert.Contains("7677", said);
        Assert.Contains("no machines found", said);
    }

    // Found but not paired is a different problem with a different fix — the
    // user has something to do about it, and the line should say so.
    [Fact]
    public void MachinesFoundButUnpairedSaysWhatIsMissing()
    {
        var said = PeerSessions.StatusText(
            enabled: true, running: true, listening: 7677, connected: 0, seen: 2);

        Assert.Contains("2 machines found", said);
        Assert.Contains("none paired yet", said);
    }

    [Fact]
    public void ConnectedSaysHowMany()
    {
        Assert.Contains("Connected to 1 machine,", PeerSessions.StatusText(
            enabled: true, running: true, listening: 7677, connected: 1, seen: 1));

        Assert.Contains("Connected to 3 machines", PeerSessions.StatusText(
            enabled: true, running: true, listening: 7677, connected: 3, seen: 4));
    }

    // Singular and plural, because a status line reading "1 machines" is the
    // kind of thing that makes a careful user distrust the rest of it.
    [Fact]
    public void CountsAreWordedForOneAndForMany()
    {
        Assert.DoesNotContain("1 machines", PeerSessions.StatusText(
            enabled: true, running: true, listening: 7677, connected: 1, seen: 1));

        Assert.DoesNotContain("1 machines found", PeerSessions.StatusText(
            enabled: true, running: true, listening: 7677, connected: 0, seen: 1));
    }

    // Whatever the state, it says something. This line is the only place the
    // user learns the link exists at all.
    [Theory]
    [InlineData(false, false, 0, 0, 0)]
    [InlineData(true, false, 0, 0, 0)]
    [InlineData(true, true, 7677, 0, 0)]
    [InlineData(true, true, 7677, 0, 5)]
    [InlineData(true, true, 7677, 5, 5)]
    public void NoStateIsSilent(bool enabled, bool running, int listening, int connected, int seen) =>
        Assert.NotEmpty(PeerSessions.StatusText(enabled, running, listening, connected, seen));
}
