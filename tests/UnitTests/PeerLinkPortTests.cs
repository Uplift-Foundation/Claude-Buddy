using Xunit;

namespace ClaudeBuddy.UnitTests;

// Which port the link listens on.
//
// **This had no test at all, and that is exactly how the bug shipped.** The
// stored default was 0, 0 asks the OS to pick, and a fresh install therefore
// listened on an ephemeral port. Discovery announces whatever it bound, so the
// discovered path kept working and hid it — while "add a machine by address",
// which exists precisely for when discovery does not work, was left dialling
// 7677 at a machine listening somewhere else entirely.
//
// It was found by installing the app on a real machine and running lsof against
// it. Nothing in three suites could have caught it, because nothing anywhere
// asked what port a default install binds.
public class PeerLinkPortTests
{
    [Fact]
    public void NothingStoredMeansThePortEverybodyExpects()
    {
        // The case that shipped wrong. A settings.json with no peerLinkPort key
        // deserialises to 0, which is every install that has never touched the
        // setting — that is to say, all of them.
        Assert.Equal(PeerLink.DefaultPort, ClaudeBuddySettings.PortToBind(0));
    }

    [Fact]
    public void AChosenPortIsHonoured()
    {
        Assert.Equal(9100, ClaudeBuddySettings.PortToBind(9100));
    }

    [Fact]
    public void TheDefaultItselfIsHonoured()
    {
        Assert.Equal(PeerLink.DefaultPort, ClaudeBuddySettings.PortToBind(PeerLink.DefaultPort));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-7677)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void APortThatCannotBeBoundFallsBackRatherThanThrowing(int stored)
    {
        // A settings file edited by hand is the ordinary way one of these
        // arrives — the headless case in this project is administered exactly
        // that way. Falling back beats refusing to listen at all, which would
        // read as the feature being broken rather than the number being wrong.
        Assert.Equal(PeerLink.DefaultPort, ClaudeBuddySettings.PortToBind(stored));
    }

    [Fact]
    public void TheBoundariesAreWhereTheyShouldBe()
    {
        Assert.Equal(1, ClaudeBuddySettings.PortToBind(1));
        Assert.Equal(65535, ClaudeBuddySettings.PortToBind(65535));
    }
}
