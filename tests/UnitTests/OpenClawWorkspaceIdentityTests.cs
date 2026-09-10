using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests;

[Collection("Settings")]
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

    // The picture labels the local grammar accepts are the same labels here,
    // because they are now one grammar. A user who writes `- Profile picture:
    // x.png` in an IDENTITY.md and "Her profile picture is x.png" in a
    // CLAUDE.md has said the same thing twice, and being told only one of them
    // counts is exactly the drift PersonaMarkdown exists to prevent.
    [Theory]
    [InlineData("- Avatar: me.png")]
    [InlineData("- Profile picture: me.png")]
    [InlineData("- Profile pic: me.png")]
    [InlineData("- Profile image: me.png")]
    [InlineData("- Picture: me.png")]
    [InlineData("- Portrait: me.png")]
    [InlineData("- Image: me.png")]
    [InlineData("- **Profile picture:** me.png")]
    public void EveryWordForAPictureNamesTheWorkspacePicture(string line)
    {
        Assert.Equal("me.png", OpenClawWorkspaceIdentity.Parse(new[] { line }).Avatar);
    }

    // A workspace profile written as prose is read the same way a CLAUDE.md's
    // is. The bounds are PersonaMarkdownProseTests' subject; what is asserted
    // here is only that the workspace path goes through the same parser.
    [Fact]
    public void AWorkspaceProfileWrittenAsProseIsRead()
    {
        var fields = OpenClawWorkspaceIdentity.Parse(new[]
        {
            "Her name is Leota",
            "Her profile picture is leota.png",
            "Her voice is Bella",
        });

        Assert.Equal("Leota", fields.Name);
        Assert.Equal("leota.png", fields.Avatar);
        Assert.Equal("Bella", fields.Voice);
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
    public void AProfileKokoroAnnotationIsNotPartOfItsVoiceIdentifier()
    {
        var fields = OpenClawWorkspaceIdentity.Parse(new[]
        {
            "# [redacted agent]",
            "",
            "**Voice:** af_bella (Kokoro TTS)",
            "This profile deliberately has no other metadata fields.",
        });

        Assert.Equal("af_bella", fields.Voice);
        Assert.Null(fields.Rate);
    }

    [Theory]
    [InlineData("- **Voice:** `af_nicole` (Kokoro TTS, rate 1.3)", 1.3)]
    [InlineData("- **Voice:** `af_nicole` (Kokoro TTS, speed 1.3x)", 1.3)]
    [InlineData("- **Voice:** `af_nicole` (Kokoro TTS, Rate: 0.8)", 0.8)]
    public void ARateQualifierAfterTheEngineNameIsRecognisedForAnyAgent(string line, double expected)
    {
        // Real redacted profile shape (CB-131): the engine annotation carries
        // a trailing speed qualifier after a comma. Generic, not special-cased
        // to this agent or this value — any profile's Voice line qualifies.
        var fields = OpenClawWorkspaceIdentity.Parse(new[] { line });

        Assert.Equal("af_nicole", fields.Voice);
        Assert.Equal(expected, fields.Rate);
    }

    [Theory]
    [InlineData("- **Voice:** `af_nicole` (Kokoro TTS, rate 0.1)")]   // below MinRate
    [InlineData("- **Voice:** `af_nicole` (Kokoro TTS, rate 9.0)")]   // above MaxRate
    [InlineData("- **Voice:** `af_nicole` (Kokoro TTS, rate fast)")]  // not a number
    [InlineData("- **Voice:** `af_nicole` (Kokoro TTS)")]             // no rate at all
    public void AnUnusableRateLeavesTheVoiceResolvedAndTheRateAbsent(string line)
    {
        var fields = OpenClawWorkspaceIdentity.Parse(new[] { line });

        Assert.Equal("af_nicole", fields.Voice);
        Assert.Null(fields.Rate);
    }

    [Fact]
    public void ABulletedBoldVoiceFieldCarriesNoStrayMarkupInAnyField()
    {
        // The real fixture this parser has to survive: every field in a real
        // IDENTITY.md is a bulleted bold label ("- **Name:** ..."), not the
        // bare bold ("**Name:** ...") the rest of this file's fixtures use.
        // Before this, a bulleted bold field parsed through the plain-bullet
        // path, which does not know the closing "**" falls after the colon —
        // every one of them, not just Voice, read back with a stray "** " on
        // the front.
        var fields = OpenClawWorkspaceIdentity.Parse(new[]
        {
            "- **Name:** Annabel Lee",
            "- **Voice:** `af_nicole` (Kokoro TTS, rate 1.3)",
            "- **Avatar:** avatars/annabel-lee.png",
        });

        Assert.Equal("Annabel Lee", fields.Name);
        Assert.Equal("af_nicole", fields.Voice);
        Assert.Equal(1.3, fields.Rate);
        Assert.Equal("avatars/annabel-lee.png", fields.Avatar);
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
    public void AMarkdownTableHeaderDoesNotWinOverItsVoiceDataRow()
    {
        var fields = OpenClawWorkspaceIdentity.Parse(new[]
        {
            "| Voice | Value |",
            "| --- | :--- |",
            "| TTS Voice | Ava (Premium) |",
        });

        Assert.Equal("Ava (Premium)", fields.Voice);
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
        var savedVoice = ClaudeBuddySettings.SpeakVoice;
        try
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
        finally
        {
            ClaudeBuddySettings.SpeakVoice = savedVoice;
            OpenClawSessions.SetIdentitiesForTests(
                new Dictionary<string, OpenClawSessions.AgentIdentity>());
        }
    }
}
