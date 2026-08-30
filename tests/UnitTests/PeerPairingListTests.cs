using Xunit;

namespace ClaudeBuddy.UnitTests;

// What the pairing list shows, and what each row says about itself.
//
// Pure because the interesting part is which machines appear at all: a paired
// machine that has gone quiet has to keep its row, and a machine announcing
// itself that nobody has paired with has to get one — and those two are exactly
// the rows a "list what is connected" implementation would drop.
public class PeerPairingListTests
{
    private static PeerDiscovery.Seen Seen(string machine) =>
        new(machine, "192.168.1.20", 7677, "pin", DateTime.UnixEpoch);

    private static IReadOnlyList<PeerSessions.Listed> List(
        IEnumerable<string>? announcing = null,
        IEnumerable<string>? paired = null,
        IEnumerable<string>? connected = null)
    {
        var live = (connected ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return PeerSessions.Listing(
            (announcing ?? Array.Empty<string>()).Select(Seen).ToList(),
            paired ?? Array.Empty<string>(),
            live.Contains);
    }

    // --- who gets a row ------------------------------------------------------

    [Fact]
    public void AMachineAnnouncingItselfGetsARowEvenUnpaired()
    {
        // Without this there is nothing to type a code next to, so pairing has
        // no first step.
        var row = Assert.Single(List(announcing: new[] { "mac-mini" }));

        Assert.Equal("mac-mini", row.Machine);
        Assert.False(row.Paired);
        Assert.True(row.Seen);
    }

    [Fact]
    public void APairedMachineKeepsItsRowWhileItIsOff()
    {
        // The row *is* the answer: "paired, not here". Dropping it would say
        // the pairing had been lost, which is a different and much worse claim.
        var row = Assert.Single(List(paired: new[] { "mac-mini" }));

        Assert.True(row.Paired);
        Assert.False(row.Seen);
        Assert.False(row.Connected);
    }

    [Fact]
    public void AMachineBothPairedAndAnnouncingGetsOneRowRatherThanTwo()
    {
        var row = Assert.Single(List(
            announcing: new[] { "mac-mini" }, paired: new[] { "mac-mini" }));

        Assert.True(row.Paired);
        Assert.True(row.Seen);
    }

    [Fact]
    public void MachineNamesAreMatchedWithoutRegardToCase()
    {
        // Discovery reports whatever Environment.MachineName says, and the
        // identity file holds whatever was written when pairing happened. macOS
        // and Windows disagree about the case of a host name often enough that
        // matching exactly would produce two rows for one machine.
        var row = Assert.Single(List(
            announcing: new[] { "MAC-MINI" }, paired: new[] { "mac-mini" }));

        Assert.True(row.Paired);
        Assert.True(row.Seen);
    }

    [Fact]
    public void RowsAreInAStableOrderRatherThanDiscoveryOrder()
    {
        // The list is rebuilt every time the pane is, so an order that followed
        // whichever machine last announced would reshuffle under the cursor.
        var rows = List(announcing: new[] { "zeta", "alpha" }, paired: new[] { "mid" });

        Assert.Equal(new[] { "alpha", "mid", "zeta" }, rows.Select(r => r.Machine));
    }

    [Fact]
    public void NothingAnywhereIsNoRows()
    {
        Assert.Empty(List());
    }

    // --- what a row says -----------------------------------------------------

    [Fact]
    public void AConnectedMachineSaysSo()
    {
        var row = Assert.Single(List(
            announcing: new[] { "mini" }, paired: new[] { "mini" }, connected: new[] { "mini" }));

        Assert.True(row.Connected);
        Assert.Equal("Connected", PeerSessions.RowStatus(row));
    }

    [Fact]
    public void APairedMachineOnTheNetworkIsConnecting()
    {
        var row = Assert.Single(List(announcing: new[] { "mini" }, paired: new[] { "mini" }));

        Assert.Equal("Paired — connecting…", PeerSessions.RowStatus(row));
    }

    [Fact]
    public void APairedMachineOffTheNetworkSaysWhichProblemItIs()
    {
        var row = Assert.Single(List(paired: new[] { "mini" }));

        Assert.Equal("Paired — not on this network", PeerSessions.RowStatus(row));
    }

    [Fact]
    public void AnUnpairedMachineSaysWhatIsMissing()
    {
        var row = Assert.Single(List(announcing: new[] { "mini" }));

        Assert.Equal("Found — not paired", PeerSessions.RowStatus(row));
    }

    // --- whether a typed code is worth a dial --------------------------------

    [Fact]
    public void ASixDigitCodeIsWorthTrying()
    {
        Assert.True(SettingsWindow.PairingWorthTrying("123456"));
    }

    [Fact]
    public void SurroundingSpaceIsForgiven()
    {
        // Codes get read aloud and pasted. A trailing space is not a wrong code.
        Assert.True(SettingsWindow.PairingWorthTrying("  123456 "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("12 456")]
    public void AnythingElseIsNotWorthSpendingAConnectionOn(string? code)
    {
        // A half-typed code that dials comes back refused, which reads as the
        // pairing having failed rather than as not having been attempted.
        Assert.False(SettingsWindow.PairingWorthTrying(code));
    }
}
