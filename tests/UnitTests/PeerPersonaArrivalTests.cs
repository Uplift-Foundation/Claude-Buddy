using Xunit;

namespace ClaudeBuddy.Tests;

// What this machine does with a persona another machine resolved.
//
// The wire format itself is covered in MirrorProtocolTests, and the round trip
// through a real serving Buddy in tests/IntegrationTests. This suite is the one
// rule in between: what arrives is not what was sent until it has been through
// the arrival check, and that check is pure so it can be asserted without a
// socket, a peer or a registry.
public class PeerPersonaArrivalTests
{
    private static MirrorProtocol.PeerPersona Persona(byte[]? avatar = null) =>
        new("Faraday", "Samantha", 1.2, avatar);

    // An ordinary persona is passed through unchanged. The negative control for
    // every case below: without it, a Sanitize that returned null for
    // everything would satisfy all the rejection assertions.
    [Fact]
    public void AnOrdinaryPersonaArrivesIntact()
    {
        var portrait = new byte[] { 137, 80, 78, 71 };

        var arrived = PeerPersonas.Sanitize(Persona(portrait));

        Assert.NotNull(arrived);
        Assert.Equal("Faraday", arrived!.Name);
        Assert.Equal("Samantha", arrived.Voice);
        Assert.Equal(1.2, arrived.Rate);
        Assert.Equal(portrait, arrived.Avatar);
    }

    // The cap the sender applied to its own file, applied again to the bytes on
    // the socket — and the name and voice survive it. Losing all three over an
    // oversized image would be a worse orb than one wearing its letters.
    [Fact]
    public void AnOversizedPortraitIsDroppedAndTheRestOfThePersonaSurvives()
    {
        var oversized = new byte[PersonaFiles.MaxAvatarBytes + 1];

        var arrived = PeerPersonas.Sanitize(Persona(oversized));

        Assert.NotNull(arrived);
        Assert.Null(arrived!.Avatar);
        Assert.Equal("Faraday", arrived.Name);
        Assert.Equal("Samantha", arrived.Voice);
    }

    // Exactly at the cap is admitted, not refused. An off-by-one here is the
    // difference between the rule the sender enforces and a stricter one, and
    // CB-146 is what a 0.04% overshoot already cost this project once.
    [Fact]
    public void APortraitExactlyAtTheCapIsAdmitted()
    {
        var atCap = new byte[PersonaFiles.MaxAvatarBytes];

        Assert.Equal(atCap, PeerPersonas.Sanitize(Persona(atCap))!.Avatar);
    }

    // A persona whose *only* content was an oversized picture has nothing left
    // to say, so it becomes no persona at all rather than an empty one holding
    // a registry slot.
    [Fact]
    public void APersonaThatWasOnlyAnOversizedPortraitBecomesNothing()
    {
        var onlyPicture = new MirrorProtocol.PeerPersona(
            Avatar: new byte[PersonaFiles.MaxAvatarBytes + 1]);

        Assert.Null(PeerPersonas.Sanitize(onlyPicture));
    }

    [Fact]
    public void NullAndEmptyPersonasBothArriveAsNothing()
    {
        Assert.Null(PeerPersonas.Sanitize(null));
        Assert.Null(PeerPersonas.Sanitize(new MirrorProtocol.PeerPersona()));
    }

    // The registry half, kept to one case: Set applies the same rule rather
    // than storing what it was handed, and Forget takes it away again. The
    // session id is unique per run because the registry is process-wide.
    [Fact]
    public void SetStoresTheSanitizedPersonaAndForgetRemovesIt()
    {
        var sessionId = "rc:test:" + Guid.NewGuid();
        try
        {
            PeerPersonas.Set(sessionId, Persona(new byte[PersonaFiles.MaxAvatarBytes + 1]));

            var stored = PeerPersonas.For(sessionId);
            Assert.NotNull(stored);
            Assert.Null(stored!.Avatar);
            Assert.Equal("Faraday", stored.Name);
        }
        finally
        {
            PeerPersonas.Forget(sessionId);
        }

        Assert.Null(PeerPersonas.For(sessionId));
    }

    // Setting an empty persona over an existing one is a removal, not a
    // no-op. A far machine that deletes its CLAUDE.md sends nothing, and an
    // orb that kept wearing the deleted persona would be the staleness this
    // ticket's own acceptance criteria rule out.
    [Fact]
    public void AnEmptyPersonaClearsOneAlreadyStored()
    {
        var sessionId = "rc:test:" + Guid.NewGuid();
        try
        {
            PeerPersonas.Set(sessionId, Persona());
            Assert.NotNull(PeerPersonas.For(sessionId));

            PeerPersonas.Set(sessionId, null);

            Assert.Null(PeerPersonas.For(sessionId));
        }
        finally
        {
            PeerPersonas.Forget(sessionId);
        }
    }
}
