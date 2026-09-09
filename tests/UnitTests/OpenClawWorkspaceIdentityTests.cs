using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests;

public class OpenClawWorkspaceIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cb-workspace-identity-" + Guid.NewGuid());

    public OpenClawWorkspaceIdentityTests() => Directory.CreateDirectory(_root);

    private static byte[] Png() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void OnlyExplicitBulletFieldsAreReadAndPlaceholdersDoNotWin()
    {
        var fields = OpenClawWorkspaceIdentity.Parse(new[]
        {
            "Name: prose is not metadata",
            "- name: <your name>",
            "- VOICE: Samantha",
            "- Avatar: portraits/me.png",
            "- Name: Aurora",
            "- Other: ignored",
        });

        Assert.Equal("Aurora", fields.Name);
        Assert.Equal("Samantha", fields.Voice);
        Assert.Equal("portraits/me.png", fields.Avatar);
    }

    [Theory]
    [InlineData("- Voice Name: Ava (Premium)")]
    [InlineData("- Speech Voice: Ava (Premium)")]
    [InlineData("- TTS Voice: Ava (Premium)")]
    [InlineData("**Voice**: Ava (Premium)")]
    [InlineData("**Voice:** Ava (Premium)")]
    [InlineData("| Voice | Ava (Premium) |")]
    public void DeliberateMarkdownVoiceFieldVariantsAreRead(string line)
    {
        Assert.Equal("Ava (Premium)", OpenClawWorkspaceIdentity.Parse(new[] { line }).Voice);
    }

    [Fact]
    public void AFrontMatterVoiceIsReadButIncidentalProseIsNot()
    {
        var fields = OpenClawWorkspaceIdentity.Parse(new[]
        {
            "---", "tts voice: Ava (Premium)", "---",
            "The voice: Ava (Premium) is pleasant, but this is prose.",
        });

        Assert.Equal("Ava (Premium)", fields.Voice);
        Assert.Null(OpenClawWorkspaceIdentity.Parse(new[]
        {
            "The voice: Ava (Premium) is pleasant, but this is prose.",
        }).Voice);
    }

    [Fact]
    public void IdentityThenSoulThenOrdinalMarkdownFilesSupplyEachField()
    {
        File.WriteAllText(Path.Combine(_root, "zebra.md"), "- Voice: Zara\n- Avatar: zebra.png");
        File.WriteAllText(Path.Combine(_root, "SOUL.md"), "- Name: Soul name\n- Voice: Soul voice");
        File.WriteAllText(Path.Combine(_root, "IDENTITY.md"), "- Name: Identity name\n- Avatar: avatar.png");
        File.WriteAllBytes(Path.Combine(_root, "avatar.png"), Png());
        File.WriteAllBytes(Path.Combine(_root, "zebra.png"), new byte[] { 2 });

        var identity = OpenClawWorkspaceIdentity.Read(_root);

        Assert.Equal("Identity name", identity.Name);
        Assert.Equal("Soul voice", identity.Voice);
        Assert.Equal(Png(), identity.Avatar);
    }

    [Fact]
    public void AnAvatarCannotEscapeTheWorkspace()
    {
        var outside = Path.Combine(Path.GetTempPath(), "cb-outside-avatar-" + Guid.NewGuid() + ".png");
        try
        {
        File.WriteAllBytes(outside, Png());
            File.WriteAllText(Path.Combine(_root, "IDENTITY.md"), "- Avatar: ../" + Path.GetFileName(outside));

            Assert.Null(OpenClawWorkspaceIdentity.Read(_root).Avatar);
        }
        finally
        {
            if (File.Exists(outside)) File.Delete(outside);
        }
    }

    [Fact]
    public void WorkspaceFieldsOverrideOnlyTheirOwnGatewayFields()
    {
        File.WriteAllText(Path.Combine(_root, "IDENTITY.md"), "- Name: Workspace name\n- Voice: Ava\n- Avatar: avatar.png");
        File.WriteAllBytes(Path.Combine(_root, "avatar.png"), Png());
        var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = "main",
            workspace = _root,
            displayName = "Gateway name",
            identity = new { emoji = "✨", avatarUrl = "data:image/png;base64,AQ==" },
        })).RootElement;

        var identity = OpenClawSessions.IdentityFrom(json);

        Assert.Equal("Workspace name", identity.Name);
        Assert.Equal("Ava", identity.Voice);
        Assert.Equal("✨", identity.Emoji);
        Assert.Equal(Png(), identity.Avatar);
    }

    [Fact]
    public void MissingWorkspaceMetadataLeavesGatewayIdentityAndGlobalVoiceInPlace()
    {
        ClaudeBuddySettings.SpeakVoice = "Global voice";
        var json = JsonDocument.Parse("""
            { "id": "main", "displayName": "Gateway name",
              "identity": { "avatarUrl": "data:image/png;base64,AQ==" } }
            """).RootElement;

        var identity = OpenClawSessions.IdentityFrom(json);
        OpenClawSessions.SetIdentitiesForTests(
            new Dictionary<string, OpenClawSessions.AgentIdentity> { ["main"] = identity });

        Assert.Equal("Gateway name", identity.Name);
        Assert.Null(identity.Voice);
        Assert.Equal(new byte[] { 1 }, identity.Avatar);
        Assert.Null(OpenClawSessions.VoiceForSession("openclaw:agent:main:main"));
    }
}
