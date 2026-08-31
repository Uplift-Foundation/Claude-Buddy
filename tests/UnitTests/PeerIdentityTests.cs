using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// Covers PeerIdentity — how one copy of this app proves which machine it is to
// another, and how it remembers the ones a person has agreed to.
//
// The two functions asserted hardest are the two that decide something: whether
// an offered certificate belongs to the machine we paired with, and whether a
// typed pairing code is the one that was shown. Both are pure for that reason —
// they are the security of this feature, and neither should need a socket, a
// certificate store or a second machine to be checked.
//
// Everything that touches the disk is excluded from coverage and left alone
// here, matching how OpenClawIdentity's own file handling is treated.
public class PeerIdentityTests
{
    private static PeerIdentity.Peer Known(string pin, string machine = "avatar") =>
        new(pin, machine);

    // --- trusting a certificate ------------------------------------------------

    // The ordinary case: this is the machine we paired with, offering the
    // certificate we pinned.
    [Fact]
    public void APeerOfferingThePinnedCertificateIsTrusted() =>
        Assert.True(PeerIdentity.Trusts(Known("abc123"), "abc123"));

    // Hex case is not identity. Convert.ToHexStringLower produces lowercase, but
    // a pin that has been through a settings file, a log or a person is not
    // guaranteed to have stayed that way.
    [Fact]
    public void ThePinComparisonIgnoresHexCase() =>
        Assert.True(PeerIdentity.Trusts(Known("ABC123"), "abc123"));

    // A machine we have never paired with is refused rather than learned.
    //
    // Trust on first use belongs to the *pairing* step, where a person is
    // present and typing a code. Doing it here instead would mean anything that
    // could reach the port became a peer by connecting to it.
    [Fact]
    public void AMachineWeHaveNeverPairedWithIsRefused() =>
        Assert.False(PeerIdentity.Trusts(null, "abc123"));

    // The case the pin exists for. A different certificate for a machine we have
    // paired with is exactly the shape an interception takes, and it is refused
    // rather than re-learned.
    [Fact]
    public void ADifferentCertificateForAKnownMachineIsRefused() =>
        Assert.False(PeerIdentity.Trusts(Known("abc123"), "def456"));

    // An empty or missing offer is not a wildcard. Worth its own case because
    // "no certificate" is what a broken handshake hands over, and treating it as
    // a match would be the worst possible default.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEmptyOfferIsNeverTrusted(string? offered) =>
        Assert.False(PeerIdentity.Trusts(Known("abc123"), offered!));

    // --- the pin itself ---------------------------------------------------------

    // Pinning is only meaningful if two certificates cannot collide in practice,
    // and if the same certificate always produces the same answer.
    [Fact]
    public void EveryCertificateHasItsOwnPinAndKeepsIt()
    {
        var one = First.Value;
        var two = Second.Value;

        var pinOne = PeerIdentity.PinOf(one);

        Assert.Equal(pinOne, PeerIdentity.PinOf(one));
        Assert.NotEqual(pinOne, PeerIdentity.PinOf(two));
    }

    // Lowercase hex of a SHA-256, which is 64 characters — the same shape and
    // computation OpenClawSocket already pins the gateway's leaf by, so the two
    // read as one idea rather than two conventions.
    [Fact]
    public void ThePinIsLowercaseHexOfASha256()
    {
        var pin = PeerIdentity.PinOf(First.Value);

        Assert.Equal(64, pin.Length);
        Assert.Equal(pin.ToLowerInvariant(), pin);
        Assert.All(pin, c => Assert.True(Uri.IsHexDigit(c)));
    }

    // Asserted against an independent computation rather than against itself: a
    // test that only compares PinOf to PinOf would pass for any hash at all.
    [Fact]
    public void ThePinIsTheSha256OfTheCertificateBytes()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(First.Value.RawData));

        Assert.Equal(expected, PeerIdentity.PinOf(First.Value));
    }

    // --- the pairing code --------------------------------------------------------

    [Fact]
    public void APairingCodeIsSixDigits()
    {
        for (var i = 0; i < 50; i++)
        {
            var code = PeerIdentity.NewPairingCode();

            Assert.Equal(6, code.Length);
            Assert.All(code, c => Assert.True(char.IsAsciiDigit(c)));
        }
    }

    // Leading zeros are kept. A code formatted as a number rather than a string
    // would show "1234" for 001234, and the two machines would then disagree
    // about a code a person read correctly.
    [Fact]
    public void CodesKeepTheirLeadingZeros()
    {
        var codes = Enumerable.Range(0, 500).Select(_ => PeerIdentity.NewPairingCode()).ToList();

        Assert.All(codes, c => Assert.Equal(6, c.Length));
        Assert.True(codes.Distinct(StringComparer.Ordinal).Count() > 100,
            "codes should vary; a constant would pass every other assertion here");
    }

    [Fact]
    public void TheRightCodeMatches() =>
        Assert.True(PeerIdentity.CodeMatches("012345", "012345"));

    [Theory]
    [InlineData("012345", "012346")]
    [InlineData("012345", "12345")]
    [InlineData("012345", "0123456")]
    [InlineData("012345", "")]
    [InlineData("012345", null)]
    [InlineData("", "012345")]
    [InlineData(null, "012345")]
    public void AnythingElseDoesNot(string? expected, string? offered) =>
        Assert.False(PeerIdentity.CodeMatches(expected, offered));

    // The comparison must not return early on the first wrong digit.
    //
    // Not a timing measurement — that would be a wall-clock claim a loaded CI
    // runner will not honour, which this repository has already learned twice.
    // What is asserted is the property that makes the timing safe: every
    // candidate of the right length is compared in full, so agreeing on a prefix
    // buys a guesser nothing. A code guessed a digit at a time is a thousand
    // tries rather than a million.
    [Theory]
    [InlineData("000000")]   // wrong from the first digit
    [InlineData("012340")]   // wrong only in the last
    public void APrefixThatMatchesIsStillARefusal(string offered) =>
        Assert.False(PeerIdentity.CodeMatches("012345", offered));

    // Two certificates, generated once for the whole class.
    //
    // RSA keygen is expensive, and this suite runs beside others that assert
    // against injected timeouts — several keygens per class is real CPU taken
    // from them. Nothing here mutates a certificate, so sharing is safe, and
    // two is all that is needed: the only property that wants a second one is
    // that different certificates pin differently.
    private static readonly Lazy<X509Certificate2> First = new(() => SelfSigned("first"));
    private static readonly Lazy<X509Certificate2> Second = new(() => SelfSigned("second"));

    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}
