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
        File.WriteAllText(Path.Combine(_workspace, "IDENTITY.md"), "- Name: Mica\n- Voice: Karen\n- Avatar: mica.png");
        File.WriteAllBytes(Path.Combine(_workspace, "mica.png"), Png());
        var agent = JsonDocument.Parse($$"""
            { "id": "mica", "workspace": "{{_workspace}}", "displayName": "Gateway Mica" }
            """).RootElement;

        var identity = OpenClawSessions.IdentityFrom(agent);

        Assert.Equal("Mica", identity.Name);
        Assert.Equal("Karen", identity.Voice);
        Assert.Equal(Png(), identity.Avatar);
    }
}
