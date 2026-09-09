using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests;

// The local workspace is a seam with another process: this covers the same
// agents.list shape the gateway publishes, plus the files it points us at.
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
}
