using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests;

// The local workspace is a seam with another process: this covers the same
// agents.list shape the gateway publishes, plus the files it points us at.
//
// [Collection("Settings")] because this class writes process-wide state —
// OpenClawSessions.SetIdentitiesForTests — and, in its finally, clears it to an
// empty dictionary. Uncollected, xUnit ran it in parallel with everything else,
// including PeerMirrorEndToEndTests, which reads that same map back.
//
// That is not hypothetical: it turned up as a CI failure in
// APairedHostRoutesOnlySamePinnedKnownAgentVoicesToTheReceiver, where the
// routed row arrived correctly and the read-back one line later returned null —
// `Expected: VoiceOption { ... af_bella ... } / Actual: null`. The clear landed
// in the gap. Every feature of that failure follows from this: correct in
// flight, null on read back, intermittent, and green locally where the
// scheduling differs.
//
// The reader already saved and restored the fingerprint pin in a finally, which
// protects against *sequential* reuse and does nothing whatsoever against a
// parallel writer — the protection that looks like it covers this and doesn't.
//
// Same answer SettingsCollection.cs gives for the same reason; read its comment
// before removing this attribute. A class that calls a method named
// SetIdentitiesForTests on shared state does not get to run alongside its
// readers.
[Collection("Settings")]
public class OpenClawWorkspaceIdentityIntegrationTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "cb-workspace-identity-integration-" + Guid.NewGuid());

    public OpenClawWorkspaceIdentityIntegrationTests() => Directory.CreateDirectory(_workspace);

    private static byte[] Png() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
    }

    [Fact]
    public void AnAgentsListWorkspaceBecomesTheAgentNameVoiceAndPicture()
    {
        File.WriteAllText(Path.Combine(_workspace, "IDENTITY.md"), "- Name: Mica\n- TTS Voice: Karen\n- Avatar: mica.png");
        File.WriteAllBytes(Path.Combine(_workspace, "mica.png"), Png());
        var agent = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = "mica",
            workspace = _workspace,
            displayName = "Gateway Mica",
        })).RootElement;

        var identity = OpenClawSessions.IdentityFrom(agent);

        Assert.Equal("Mica", identity.Name);
        Assert.Equal("Karen", identity.Voice);
        Assert.Equal(Png(), identity.Avatar);
    }

    [Fact]
    public void ARedactedProfileKokoroVoiceReachesTheMatchingNeuralOption()
    {
        File.WriteAllText(Path.Combine(_workspace, "IDENTITY.md"), """
            # [redacted OpenClaw agent]

            **Voice:** af_bella (Kokoro TTS)

            ## Notes

            This is ordinary profile prose and must not be interpreted as metadata.
            """);
        var agentId = "[redacted-agent]";
        var agent = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = agentId,
            workspace = _workspace,
            displayName = "Gateway placeholder",
        })).RootElement;
        var identity = OpenClawSessions.IdentityFrom(agent);
        OpenClawSessions.SetIdentitiesForTests(
            new Dictionary<string, OpenClawSessions.AgentIdentity> { [agentId] = identity });

        try
        {
            var kokoro = new TextToSpeech.VoiceOption(
                TextToSpeech.SpeakEngine.Neural, "af_bella", "af_bella (Kokoro)");

            Assert.Equal("af_bella", identity.Voice);
            Assert.Equal(kokoro, OpenClawSessions.VoiceForSession(
                $"openclaw:agent:{agentId}:discord:direct:1", new[] { kokoro }));
        }
        finally
        {
            OpenClawSessions.SetIdentitiesForTests(
                new Dictionary<string, OpenClawSessions.AgentIdentity>());
        }
    }

    // CB-147, D7: pinning the conclusion that OpenClawWorkspaceIdentity needs
    // no second candidate root, so the reasoning cannot rot silently if
    // Read's own directory walk ever changes shape. Read canonicalises the
    // workspace to `root` and enumerates `*.md` with
    // SearchOption.TopDirectoryOnly in that same root, so the directory of
    // every file it reads and the workspace root are one and the same
    // directory by construction — a subdirectory picture still has to
    // resolve, and an escaping one still has to be refused, with nothing new
    // to add on either side.
    [Fact]
    public void ASubdirectoryPictureStillResolvesUnderOpenClawsSingleRoot()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "avatars"));
        File.WriteAllBytes(Path.Combine(_workspace, "avatars", "mica.png"), Png());
        File.WriteAllText(
            Path.Combine(_workspace, "IDENTITY.md"), "- Name: Mica\n- Avatar: avatars/mica.png\n");

        var identity = OpenClawWorkspaceIdentity.Read(_workspace);

        Assert.Equal("Mica", identity.Name);
        Assert.Equal(Png(), identity.Avatar);
    }

    [Fact]
    public void AnEscapingPictureIsStillRefusedUnderOpenClawsSingleRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), "cb-workspace-outside-" + Guid.NewGuid());
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllBytes(Path.Combine(outside, "leota.png"), Png());
            File.WriteAllText(
                Path.Combine(_workspace, "IDENTITY.md"),
                "- Name: Mica\n- Avatar: ../" + Path.GetFileName(outside) + "/leota.png\n");

            Assert.Null(OpenClawWorkspaceIdentity.Read(_workspace).Avatar);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch (IOException) { }
        }
    }
}
