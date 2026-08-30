using System.Text;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// Covers PeerDiscovery's decisions — what an announcement is allowed to be, and
// how long a machine stays listed after it stops announcing.
//
// The socket itself is excluded from coverage, as everything that opens one in
// this app is. What is asserted here is the part that reads a datagram *anybody
// on the network can send*, and the part that would otherwise need a test to sit
// through a real minute of wall clock — a shape this repository has already
// fixed four flakes over.
public class PeerDiscoveryTests
{
    private static readonly DateTime Now = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    private static byte[] Announcement(
        int version = PeerProtocol.Version, string machine = "avatar",
        int port = PeerLink.DefaultPort, string pin = "abc") =>
        Encoding.UTF8.GetBytes(
            $"{{\"v\":{version},\"machine\":\"{machine}\",\"port\":{port},\"pin\":\"{pin}\"}}");

    // --- reading what arrives ------------------------------------------------

    [Fact]
    public void AWellFormedAnnouncementIsHeard()
    {
        var peer = PeerDiscovery.Read(Announcement(), "192.168.0.127", "warrens-mbp", Now);

        Assert.NotNull(peer);
        Assert.Equal("avatar", peer!.Machine);
        Assert.Equal("192.168.0.127", peer.Address);
        Assert.Equal(PeerLink.DefaultPort, peer.Port);
        Assert.Equal("abc", peer.Pin);
    }

    // Multicast is delivered back to the sender, so without this a machine
    // lists itself and the user gets an orb pointing at the computer they are
    // sitting at.
    [Fact]
    public void OurOwnAnnouncementComingBackIsIgnored() =>
        Assert.Null(PeerDiscovery.Read(
            Announcement(machine: "warrens-mbp"), "192.168.0.5", "warrens-mbp", Now));

    [Fact]
    public void TheComparisonWithOurselvesIgnoresCase() =>
        Assert.Null(PeerDiscovery.Read(
            Announcement(machine: "Warrens-MBP"), "192.168.0.5", "warrens-mbp", Now));

    // A version we do not understand is dropped rather than half-read. The
    // field exists precisely so a later format is ignorable instead of being
    // misinterpreted as this one.
    [Fact]
    public void AnAnnouncementFromAnotherVersionIsIgnored() =>
        Assert.Null(PeerDiscovery.Read(
            Announcement(version: PeerProtocol.Version + 1), "192.168.0.127", "warrens-mbp", Now));

    // Anything malformed is dropped. This parses a datagram from an unknown
    // sender, so every one of these is reachable by anybody on the network.
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"v\":1}")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"v\":1,\"machine\":\"\",\"port\":7677}")]
    [InlineData("{\"v\":1,\"machine\":\"   \",\"port\":7677}")]
    public void RubbishIsIgnored(string body) =>
        Assert.Null(PeerDiscovery.Read(
            Encoding.UTF8.GetBytes(body), "192.168.0.127", "warrens-mbp", Now));

    // A port outside the range cannot be connected to, so a peer offering one
    // is not worth listing.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(999999)]
    public void AnImpossiblePortIsIgnored(int port) =>
        Assert.Null(PeerDiscovery.Read(
            Announcement(port: port), "192.168.0.127", "warrens-mbp", Now));

    // The announcement carries a pin, but hearing one is not trusting it — the
    // pin that matters is the certificate actually presented during the
    // handshake. This is an address book, not a key, so a missing pin still
    // produces a listing.
    [Fact]
    public void AnAnnouncementWithNoPinIsStillHeard()
    {
        var peer = PeerDiscovery.Read(
            Encoding.UTF8.GetBytes("{\"v\":1,\"machine\":\"avatar\",\"port\":7677}"),
            "192.168.0.127", "warrens-mbp", Now);

        Assert.NotNull(peer);
        Assert.Equal("", peer!.Pin);
    }

    // --- what we say ------------------------------------------------------------

    // Round-trips through its own reader, which is the property that actually
    // has to hold between two machines running this code.
    [Fact]
    public void WhatWeSayIsWhatAnotherMachineHears()
    {
        var said = PeerDiscovery.Say("avatar", 7677, "deadbeef");
        var heard = PeerDiscovery.Read(said, "192.168.0.127", "warrens-mbp", Now);

        Assert.NotNull(heard);
        Assert.Equal("avatar", heard!.Machine);
        Assert.Equal(7677, heard.Port);
        Assert.Equal("deadbeef", heard.Pin);
    }

    // --- forgetting -------------------------------------------------------------

    private static Dictionary<string, PeerDiscovery.Seen> Table(
        params (string Machine, DateTime At)[] entries) =>
        entries.ToDictionary(
            e => e.Machine,
            e => new PeerDiscovery.Seen(e.Machine, "192.168.0.9", 7677, "pin", e.At),
            StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AMachineHeardFromRecentlyIsStillListed()
    {
        var table = Table(("avatar", Now - TimeSpan.FromSeconds(10)));

        Assert.Single(PeerDiscovery.Forget(table, Now));
    }

    // Three missed announcements. A machine that has been switched off should
    // stop being offered, or the panel invites a connection that cannot happen.
    [Fact]
    public void AMachineThatStoppedAnnouncingIsDropped()
    {
        var table = Table(("avatar", Now - PeerDiscovery.ForgetAfter - TimeSpan.FromSeconds(1)));

        Assert.Empty(PeerDiscovery.Forget(table, Now));
        Assert.Empty(table);
    }

    // The window has to be a comfortable multiple of the announcement interval,
    // or one dropped datagram on a busy network makes a machine flicker in and
    // out of the list.
    [Fact]
    public void TheForgettingWindowSurvivesAMissedAnnouncementOrTwo() =>
        Assert.True(PeerDiscovery.ForgetAfter > PeerDiscovery.AnnounceEvery * 2);

    [Fact]
    public void MachinesAreListedInAStableOrder()
    {
        var table = Table(("zulu", Now), ("alpha", Now), ("mike", Now));

        Assert.Equal(
            new[] { "alpha", "mike", "zulu" },
            PeerDiscovery.Forget(table, Now).Select(p => p.Machine));
    }

    // --- what counts as news -------------------------------------------------------

    // Only news redraws. Every machine announces every twenty seconds, so
    // treating each one as a change would mean the UI redrawing on a timer for
    // no reason.
    [Fact]
    public void AFirstSightingIsNews() =>
        Assert.True(new PeerDiscovery().Note(
            new PeerDiscovery.Seen("avatar", "192.168.0.127", 7677, "pin", Now)));

    [Fact]
    public void TheSameMachineSayingTheSameThingIsNot()
    {
        var discovery = new PeerDiscovery();
        var peer = new PeerDiscovery.Seen("avatar", "192.168.0.127", 7677, "pin", Now);

        Assert.True(discovery.Note(peer));
        Assert.False(discovery.Note(peer with { At = Now.AddSeconds(20) }));
    }

    // A machine that moved — a new lease, a different interface, a VPN coming
    // up — is news, because the address we would dial has changed.
    [Fact]
    public void AMachineThatChangedAddressIsNews()
    {
        var discovery = new PeerDiscovery();
        var peer = new PeerDiscovery.Seen("avatar", "192.168.0.127", 7677, "pin", Now);

        Assert.True(discovery.Note(peer));
        Assert.True(discovery.Note(peer with { Address = "10.0.0.4" }));
    }

    // A new certificate is news too. It is not trusted on the strength of this —
    // the handshake decides that — but a machine that has been reinstalled needs
    // to be visible as something to pair with again.
    [Fact]
    public void AMachineOfferingANewCertificateIsNews()
    {
        var discovery = new PeerDiscovery();
        var peer = new PeerDiscovery.Seen("avatar", "192.168.0.127", 7677, "old", Now);

        Assert.True(discovery.Note(peer));
        Assert.True(discovery.Note(peer with { Pin = "new" }));
    }

    // --- the group ------------------------------------------------------------------

    // Administratively-scoped multicast: routable within an organisation, never
    // off it. Deliberately not mDNS's 224.0.0.251, which would put this traffic
    // in every Bonjour listener on the network.
    [Fact]
    public void TheGroupIsAdministrativelyScopedAndNotMdns()
    {
        Assert.StartsWith("239.", PeerDiscovery.Group.ToString());
        Assert.NotEqual("224.0.0.251", PeerDiscovery.Group.ToString());
    }
}
