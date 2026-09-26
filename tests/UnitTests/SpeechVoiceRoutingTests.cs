using Xunit;

namespace ClaudeBuddy.UnitTests;

// Whose voice reads a reply out loud, per kind of session.
//
// This is where CB-165's second defect lived, and it lived there invisibly. The
// chat panel had a resolver with three arms — peer, gateway agent, everything
// else is null — and "everything else" is every ordinary local Claude Code
// session, which is the common case and the one with a CLAUDE.md persona beside
// it. The orb had a different resolver that asked LocalPersonas and nothing
// else. Both were covered. Neither was wrong about the arms it had. The bug was
// entirely in the arms one of them did not have, which is a thing no test of
// either resolver could see.
//
// So the arms are enumerated here, against the one function both buttons now
// ask, with a case for every kind of session id this app mints — including the
// two that deliberately answer "the user's global voice" rather than a persona.
//
// The registries are set through their own test seams and restored afterwards:
// they are process-wide, and the ids are freshly generated per case so nothing
// here depends on what another suite left behind.
//
// In the Settings collection for the same reason LocalPersonaTests is: every
// SetForTests call below replaces the *whole* registry, not just this test's
// key, so running alongside another class doing the same is a wipe, not an
// overlap. CB-188 is a class left out of this collection that raced exactly
// that way against LocalPersonaTests.
[Collection("Settings")]
public class SpeechVoiceRoutingTests : IDisposable
{
    private static readonly TextToSpeech.VoiceOption Bella =
        new(TextToSpeech.SpeakEngine.Neural, "af_bella", "af_bella (Kokoro)");

    private static readonly TextToSpeech.VoiceOption Sky =
        new(TextToSpeech.SpeakEngine.Neural, "af_sky", "af_sky (Kokoro)");

    private static readonly TextToSpeech.VoiceOption[] Installed = { Bella, Sky };

    public void Dispose()
    {
        LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>());
        PeerPersonas.SetForTests(new Dictionary<string, MirrorProtocol.PeerPersona>());
        OpenClawSessions.SetIdentitiesForTests(
            new Dictionary<string, OpenClawSessions.AgentIdentity>());
    }

    private static LocalPersona.Persona Local(string? voice, double? rate) =>
        new(Name: "Jennifer", Voice: voice, Rate: rate,
            AvatarSource: null, AvatarPath: null, Files: Array.Empty<string>());

    // --- which registry answers ---------------------------------------------

    // One case per arm, in a table rather than as [Theory] rows: VoiceSource is
    // internal (this suite sees it through InternalsVisibleTo), and xUnit
    // requires a test method's parameters to be as public as the class. The
    // alternative was making the enum public purely so a test could name it,
    // which is a worse trade than one loop.
    [Fact]
    public void EachKindOfSessionIdPicksItsOwnRegistry()
    {
        var cases = new (string Id, SessionIdentity.VoiceSource Expected)[]
        {
            ("rc:.claude:mac-mini:abc", SessionIdentity.VoiceSource.Peer),
            ("openclaw:agent:nova:discord:direct:1", SessionIdentity.VoiceSource.GatewayAgent),

            // A room, which has no single agent to borrow a voice from.
            // Deliberately the global setting rather than the first member's
            // voice — the same reasoning AvatarForSession uses when it draws a
            // composite instead of picking somebody.
            ("openclaw:room:general", SessionIdentity.VoiceSource.Global),

            // A cloud session has no persona registry on this disk (CB-164).
            ("cloud:session_01abc", SessionIdentity.VoiceSource.Global),

            // The arm the panel was missing: an ordinary local session id is a
            // bare uuid with no prefix on it whatsoever.
            ("3f2a9c14-0b6e-4e43-9d1a-77a1b0c2d3e4", SessionIdentity.VoiceSource.Local),
            ("", SessionIdentity.VoiceSource.Global),
        };

        foreach (var (id, expected) in cases)
            Assert.Equal(expected, SessionIdentity.VoiceSourceFor(id));
    }

    // A panel between binds has no session at all, and every caller reaches
    // this through a `_session?.SessionId`.
    [Fact]
    public void NoSessionIsTheGlobalVoice() =>
        Assert.Equal(SessionIdentity.VoiceSource.Global, SessionIdentity.VoiceSourceFor(null));

    // --- the voice and rate that come back ----------------------------------

    // The case this ticket exists for. A local session whose CLAUDE.md names a
    // voice speaks in it — from either button, because there is only one
    // function left to ask.
    [Fact]
    public void ALocalPersonaSpeaksInItsOwnVoice()
    {
        var id = "local-" + Guid.NewGuid();
        LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>
        {
            [id] = Local("af_sky", 1.2),
        });

        Assert.Equal(Sky, SessionIdentity.VoiceFor(id, Installed));
        Assert.Equal(1.2, SessionIdentity.RateFor(id));
    }

    // Three different ways of having no answer, all of which mean "the user's
    // own setting" rather than silence: no persona, a persona naming no voice,
    // and a persona naming one this machine cannot build.
    [Fact]
    public void ALocalSessionWithNothingToSayAboutVoiceFallsBackToTheGlobalOne()
    {
        var none = "local-none-" + Guid.NewGuid();
        var silent = "local-silent-" + Guid.NewGuid();
        var missing = "local-missing-" + Guid.NewGuid();

        LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>
        {
            [silent] = Local(null, null),
            [missing] = Local("af_nobody", null),
        });

        Assert.Null(SessionIdentity.VoiceFor(none, Installed));
        Assert.Null(SessionIdentity.VoiceFor(silent, Installed));
        Assert.Null(SessionIdentity.VoiceFor(missing, Installed));
        Assert.Null(SessionIdentity.RateFor(none));
    }

    // A mirrored peer's persona travelled over the wire with the session, so
    // the sender's own voice is the one that reads their reply.
    [Fact]
    public void APeerSessionSpeaksInTheVoiceThatCameOverTheWire()
    {
        var id = "rc:.claude:mac-mini:" + Guid.NewGuid();
        PeerPersonas.SetForTests(new Dictionary<string, MirrorProtocol.PeerPersona>
        {
            [id] = new(Name: "Jennifer", Voice: "af_bella", Rate: 0.9),
        });

        Assert.Equal(Bella, SessionIdentity.VoiceFor(id, Installed));
        Assert.Equal(0.9, SessionIdentity.RateFor(id));
    }

    [Fact]
    public void AGatewayAgentSpeaksInItsWorkspaceVoice()
    {
        var agent = "agent" + Guid.NewGuid().ToString("N");
        var id = $"openclaw:agent:{agent}:discord:direct:1";
        OpenClawSessions.SetIdentitiesForTests(
            new Dictionary<string, OpenClawSessions.AgentIdentity>
            {
                [agent] = new("Nova", null, null, "af_bella", 1.4),
            });

        Assert.Equal(Bella, SessionIdentity.VoiceFor(id, Installed));
        Assert.Equal(1.4, SessionIdentity.RateFor(id));
    }

    // A room keeps the user's global voice even when the agents in it have one
    // of their own, which is the distinction a naive "starts with openclaw:"
    // would lose.
    [Fact]
    public void ARoomKeepsTheGlobalVoiceEvenWhenItsMembersHaveOne()
    {
        var agent = "agent" + Guid.NewGuid().ToString("N");
        OpenClawSessions.SetIdentitiesForTests(
            new Dictionary<string, OpenClawSessions.AgentIdentity>
            {
                [agent] = new("Nova", null, null, "af_bella", 1.4),
            });

        Assert.Null(SessionIdentity.VoiceFor("openclaw:room:general", Installed));
        Assert.Null(SessionIdentity.RateFor("openclaw:room:general"));
    }

    // A cloud session has no registry to ask. Asserted rather than assumed
    // because the id is not obviously distinguishable from a local one at a
    // glance, and the wrong answer here would be a silent lookup miss rather
    // than an error.
    [Fact]
    public void ACloudSessionHasNoPersonaToSpeakAs()
    {
        Assert.Null(SessionIdentity.VoiceFor("cloud:session_01abc", Installed));
        Assert.Null(SessionIdentity.RateFor("cloud:session_01abc"));
        Assert.Null(SessionIdentity.VoiceFor(null, Installed));
        Assert.Null(SessionIdentity.RateFor(null));
    }
}
