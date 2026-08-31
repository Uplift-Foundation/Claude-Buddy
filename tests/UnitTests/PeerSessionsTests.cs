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

    // Paired, with no address on file: the ordinary case, where discovery is
    // what supplies the address.
    private static IReadOnlyDictionary<string, PeerIdentity.Peer> Paired(params string[] names) =>
        names.ToDictionary(n => n, n => new PeerIdentity.Peer("pin", n),
            StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, PeerIdentity.Peer> PairedAt(
        string name, string? address) =>
        new Dictionary<string, PeerIdentity.Peer>(StringComparer.OrdinalIgnoreCase)
        {
            [name] = new PeerIdentity.Peer("pin", name, address)
        };

    // --- who is worth dialling -------------------------------------------------

    [Fact]
    public void APairedMachineWeCanSeeAndAreNotTalkingToIsDialled()
    {
        var worth = PeerSessions.WorthDialling(
            new[] { Seen("avatar") }, Paired("avatar"), connected: _ => false);

        Assert.Equal(new[] { "avatar" }, worth.Select(p => p.Machine));
    }

    // Pairing is a deliberate act. A machine announcing itself on the network is
    // not an invitation, and dialling one we have never agreed to would make
    // discovery an attack surface rather than an address book.
    [Fact]
    public void AMachineWeHaveNotPairedWithIsLeftAlone() =>
        Assert.Empty(PeerSessions.WorthDialling(
            new[] { Seen("stranger") }, Paired(), connected: _ => false));

    // One connection between two machines, not one per attempt. The link carries
    // both directions once established, so a second would be waste at best and a
    // pair of half-used sockets at worst.
    [Fact]
    public void AMachineWeAreAlreadyTalkingToIsNotDialledAgain() =>
        Assert.Empty(PeerSessions.WorthDialling(
            new[] { Seen("avatar") }, Paired("avatar"), connected: _ => true));

    // A paired machine with no announcement and no address on file cannot be
    // dialled, because there is nowhere to dial.
    [Fact]
    public void NothingHeardAndNothingStoredMeansNothingToDial() =>
        Assert.Empty(PeerSessions.WorthDialling(
            Array.Empty<PeerDiscovery.Seen>(), Paired("avatar"), connected: _ => false));

    // **The case that shipped broken.** A machine added by address lives on a
    // network that does not carry the announcements — that is the whole reason
    // it had to be added by hand — so requiring one meant it paired once and was
    // never dialled again, with its address sitting in the identity file the
    // entire time. Found by deploying to two real machines and watching the log
    // say "listening" and then nothing at all.
    [Fact]
    public void APairedMachineWithAStoredAddressIsDialledEvenWhenSilent()
    {
        var worth = PeerSessions.WorthDialling(
            Array.Empty<PeerDiscovery.Seen>(),
            PairedAt("avatar", "192.168.0.127:7677"),
            connected: _ => false);

        var peer = Assert.Single(worth);

        Assert.Equal("avatar", peer.Machine);
        Assert.Equal("192.168.0.127", peer.Address);
        Assert.Equal(7677, peer.Port);
    }

    [Fact]
    public void AStoredAddressWithNoPortUsesTheOneEverybodyExpects()
    {
        var peer = Assert.Single(PeerSessions.WorthDialling(
            Array.Empty<PeerDiscovery.Seen>(),
            PairedAt("avatar", "192.168.0.127"),
            connected: _ => false));

        Assert.Equal(PeerLink.DefaultPort, peer.Port);
    }

    [Fact]
    public void AStoredAddressThatMakesNoSenseIsSkippedRatherThanDialled() =>
        Assert.Empty(PeerSessions.WorthDialling(
            Array.Empty<PeerDiscovery.Seen>(),
            PairedAt("avatar", "not a host"),
            connected: _ => false));

    // A live announcement wins, because it says where the machine is *now*. A
    // stored address is only where it was when we paired, and a DHCP lease
    // outlives neither.
    [Fact]
    public void AnAnnouncementBeatsAStoredAddress()
    {
        var peer = Assert.Single(PeerSessions.WorthDialling(
            new[] { Seen("avatar") },
            PairedAt("avatar", "10.0.0.1:9999"),
            connected: _ => false));

        Assert.Equal("192.168.0.9", peer.Address);
        Assert.Equal(7677, peer.Port);
    }

    [Fact]
    public void OnlyTheOnesThatQualifyAreDialled()
    {
        var worth = PeerSessions.WorthDialling(
            new[] { Seen("paired-and-busy"), Seen("paired-and-free"), Seen("stranger") },
            Paired("paired-and-busy", "paired-and-free"),
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
