using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static ClaudeBuddy.TextToSpeech;

namespace ClaudeBuddy.Tests;

// CB-200, second review: the Speech volume row has to know when an orb's own
// voice runs on a different engine from the global one. Warren's vibe
// summaries went through his F5-TTS custom command, chosen by an orb's
// persona, while the global engine said "system" — and the row, reading only
// the global setting, said nothing about a command that might ignore the
// level.
//
// SessionIdentity.OrbEngines resolves the persona voices to engines;
// AudioVolume.SpeechVolumeNote turns global + orb engines into the note.
// Both pure. PersonaVoiceRequests gathers the voices from the three registries
// a persona can live in, and is covered here through their test seams.
//
// In the Settings collection: the gateway half reads OpenClawFingerprint.
[Collection("Settings")]
public class OrbVoiceEngineTests
{
    private static readonly List<VoiceOption> Options = new()
    {
        new(SpeakEngine.System, "Samantha", "Samantha (system)"),
        new(SpeakEngine.Neural, "af_sky", "af_sky (Kokoro)"),
        new(SpeakEngine.Neural, "af_nicole", "af_nicole (Kokoro)"),
        new(SpeakEngine.Custom, "female_03", "female_03 (custom)"),
    };

    // --- which engine a persona's voice lands on ------------------------------

    [Theory]
    [InlineData("female_03", SpeakEngine.Custom)]      // Warren's F5-TTS voice
    [InlineData("Samantha", SpeakEngine.System)]
    [InlineData("af_sky", SpeakEngine.Neural)]
    [InlineData("50% sky and 50% nicole", SpeakEngine.Neural)]   // a blend is always Kokoro
    public void APersonaVoiceResolvesToItsEngine(string requested, SpeakEngine expected) =>
        Assert.Equal(expected, SessionIdentity.EngineOf(requested, Options));

    // A voice that matches nothing speaks in the global voice, so it adds no
    // engine; nor does a blend with a part this machine lacks.
    [Theory]
    [InlineData("nobody-has-this-voice")]
    [InlineData("50% sky and 50% nonexistent")]
    public void AnUnresolvableVoiceHasNoEngine(string requested) =>
        Assert.Null(SessionIdentity.EngineOf(requested, Options));

    [Fact]
    public void NoOrbVoicesMeansNoOrbEngines() =>
        Assert.Empty(SessionIdentity.OrbEngines(Array.Empty<string>(), Options)!);

    // Voices exist but no list has been built: unknown, not "none".
    [Fact]
    public void OrbVoicesWithNoVoiceListAreUnknown() =>
        Assert.Null(SessionIdentity.OrbEngines(new[] { "female_03" }, options: null));

    [Fact]
    public void OrbEnginesAreTheSetOfResolvedEngines() =>
        Assert.Equal(new[] { SpeakEngine.System, SpeakEngine.Custom },
            SessionIdentity.OrbEngines(new[] { "female_03", "Samantha", "samantha", "nope" }, Options)!
                .OrderBy(e => e));

    // --- the note, global and per orb ------------------------------------------

    // Warren's case: global system, an orb on his custom command.
    [Fact]
    public void AnOrbOnACustomCommandIsNotedWhenTheGlobalEngineIsNot() =>
        Assert.Equal(AudioVolume.OrbCustomCommandNote,
            AudioVolume.SpeechVolumeNote(SpeakEngine.System, false, new[] { SpeakEngine.Custom }));

    [Fact]
    public void AnOrbOnAFallbackKokoroIsNotedWhenTheGlobalEngineIsNot() =>
        Assert.Equal(AudioVolume.OrbFallbackEngineNote,
            AudioVolume.SpeechVolumeNote(SpeakEngine.System, true, new[] { SpeakEngine.Neural }));

    // An orb on the global engine adds nothing; the global line covers it.
    [Fact]
    public void AnOrbOnTheGlobalEngineAddsNothing() =>
        Assert.Equal(AudioVolume.CustomCommandNote,
            AudioVolume.SpeechVolumeNote(SpeakEngine.Custom, false, new[] { SpeakEngine.Custom }));

    // Engines with no caveat — a system voice, or Kokoro on this build's own
    // engine — add nothing either.
    [Fact]
    public void AnOrbOnAnEngineWithNoCaveatAddsNothing() =>
        Assert.Null(AudioVolume.SpeechVolumeNote(SpeakEngine.System, usingFallbackEngine: false,
            new[] { SpeakEngine.Neural, SpeakEngine.System }));

    // Global first, then each orb caveat once, in engine order.
    [Fact]
    public void GlobalAndOrbCaveatsAreOneLineEach() =>
        Assert.Equal(
            AudioVolume.FallbackEngineNote + "\n" + AudioVolume.OrbCustomCommandNote,
            AudioVolume.SpeechVolumeNote(SpeakEngine.Neural, true,
                new[] { SpeakEngine.Custom, SpeakEngine.Custom, SpeakEngine.Neural }));

    [Fact]
    public void UnknownOrbEnginesStateBothRules()
    {
        Assert.Equal(AudioVolume.OrbVoicesUnknownNote,
            AudioVolume.SpeechVolumeNote(SpeakEngine.System, false, orbEngines: null));
        Assert.Contains(SpeechEngineContract.VolumeEnvVar, AudioVolume.OrbVoicesUnknownNote);
        Assert.Contains("updated voice engine", AudioVolume.OrbVoicesUnknownNote);
    }

    // --- where the voices come from --------------------------------------------

    [Fact]
    public void PersonaVoicesAreGatheredFromEveryRegistry()
    {
        const string pin = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var savedPin = ClaudeBuddySettings.OpenClawFingerprint;
        try
        {
            ClaudeBuddySettings.OpenClawFingerprint = pin;
            LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>
            {
                ["local-1"] = new("Jen", "female_03", null, null, null, Array.Empty<string>()),
                ["local-2"] = new("NoVoice", null, null, null, null, Array.Empty<string>()),
                ["local-3"] = new("Dup", "FEMALE_03", null, null, null, Array.Empty<string>()),
            });
            PeerPersonas.SetForTests(new Dictionary<string, MirrorProtocol.PeerPersona>
            {
                ["peer-1"] = new(Name: "Mini", Voice: "Samantha"),
            });
            OpenClawSessions.SetIdentitiesForTests(new Dictionary<string, OpenClawSessions.AgentIdentity>
            {
                ["claw"] = new("Claw", null, null, Voice: "af_sky"),
                ["quiet"] = new("Quiet", null, null),
            });
            OpenClawSessions.ApplyPeerProfileVoices("paired", pin,
                new[] { new OpenClawPeerIdentity.Row("quiet", "af_nicole", null) });

            var voices = SessionIdentity.PersonaVoiceRequests();

            Assert.Equal(4, voices.Count);
            Assert.Contains("female_03", voices, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("Samantha", voices);
            Assert.Contains("af_sky", voices);
            Assert.Contains("af_nicole", voices);
        }
        finally
        {
            LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>());
            PeerPersonas.SetForTests(new Dictionary<string, MirrorProtocol.PeerPersona>());
            OpenClawSessions.SetIdentitiesForTests(new Dictionary<string, OpenClawSessions.AgentIdentity>());
            ClaudeBuddySettings.OpenClawFingerprint = savedPin;
        }
    }

    // CachedVoiceOptions never builds the list: null until something else has.
    [Fact]
    public void TheCachedVoiceListIsNeverBuiltJustToBeRead()
    {
        InvalidateVoiceCache();
        Assert.Null(CachedVoiceOptions);
    }
}
