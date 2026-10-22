using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-277: which session a file is asked for under.
//
// FIXTURES: structure from the real gateway (agents.list rows carry `id`,
// `name`, `identity`, `workspace`; session keys are agent:<id>:<surface>…),
// values invented — this repository is public.
//
// Serialised: the end-to-end cases at the bottom swap process-wide tables
// (agent workspaces, the session snapshot) that every other OpenClaw test
// also reads.
[Collection("Settings")]
public class OpenClawMediaOwnerTests
{
    private const string Zara = "/Users/sample/.openclaw/workspace-sample-zara";
    private const string Main = "/Users/sample/.openclaw/workspace";
    private const string Clip = Zara + "/outputs/sample/clip.mp4";

    private const string AskKey = "agent:main:discord:direct:1";
    private const string ZaraDm = "agent:comfyui:discord:direct:1";
    private const string ZaraMain = "agent:comfyui:main";

    private static readonly Dictionary<string, string> Spaces = new()
    {
        ["main"] = Main,
        ["comfyui"] = Zara,
    };

    private static OpenClawSessions.Session Session(string key) =>
        new(key, "t", "", "idle", System.DateTime.UtcNow, null, SessionKind.Direct, false, "sid-1");

    private static OwnerIndex Index(params string[] keys) => new(Spaces, keys);

    [Fact]
    public void AClipInAnotherAgentsWorkspaceIsAskedForUnderThatAgentsSameConversation()
    {
        Assert.Equal(ZaraDm, OpenClawMediaOwner.SessionKeyFor(Clip, AskKey, Index(AskKey, ZaraDm, ZaraMain)));
    }

    [Fact]
    public void WithoutASameConversationSiblingTheOwnersMainSessionIsUsed()
    {
        Assert.Equal(ZaraMain, OpenClawMediaOwner.SessionKeyFor(Clip, AskKey, Index(AskKey, ZaraMain)));
    }

    [Fact]
    public void WithNoKnownSessionOfTheOwnerTheAskingKeyIsKeptAndNothingIsGuessed()
    {
        Assert.Equal(AskKey, OpenClawMediaOwner.SessionKeyFor(Clip, AskKey, Index(AskKey, "agent:social:main")));
    }

    [Fact]
    public void AFileInTheAskersOwnWorkspaceKeepsTheAskingKeyByteForByte()
    {
        var own = Main + "/outputs/pic.png";
        var key = OpenClawMediaOwner.SessionKeyFor(own, AskKey, Index(AskKey, ZaraDm, ZaraMain));
        Assert.Same(AskKey, key);
        Assert.Equal(
            new OpenClawMediaSource(own, AskKey).Route,
            new OpenClawMediaSource(own, key).Route);
    }

    [Fact]
    public void ASharedMediaPathAndAnUnknownPathHaveNoOwner()
    {
        var index = Index(AskKey, ZaraDm);
        Assert.Equal(AskKey, OpenClawMediaOwner.SessionKeyFor("~/.openclaw/media/clip.mp4", AskKey, index));
        Assert.Equal(AskKey, OpenClawMediaOwner.SessionKeyFor("/tmp/elsewhere/clip.mp4", AskKey, index));
        Assert.Equal(AskKey, OpenClawMediaOwner.SessionKeyFor("", AskKey, index));
    }

    [Fact]
    public void AnEmptyIndexIsANoOpForEverything()
    {
        Assert.Equal(AskKey, OpenClawMediaOwner.SessionKeyFor(Clip, AskKey, OwnerIndex.Empty));
        Assert.Empty(OwnerIndex.Empty.AgentWorkspaces);
        Assert.Empty(OwnerIndex.Empty.SessionKeys);
    }

    [Fact]
    public void TheLongestWorkspaceWinsAndAPrefixMustEndOnASlash()
    {
        var nested = new Dictionary<string, string>
        {
            ["outer"] = "/x/ws",
            ["inner"] = "/x/ws/inner",
            ["sib"] = "/x/ws-a",
        };

        Assert.Equal("inner", OpenClawMediaOwner.OwningAgent("/x/ws/inner/f.mp4", nested));
        Assert.Equal("outer", OpenClawMediaOwner.OwningAgent("/x/ws/other/f.mp4", nested));
        // /x/ws-a must not be claimed by /x/ws, and /x/ws-ab by neither.
        Assert.Equal("sib", OpenClawMediaOwner.OwningAgent("/x/ws-a/f.mp4", nested));
        Assert.Null(OpenClawMediaOwner.OwningAgent("/x/ws-ab/f.mp4", nested));
        // The workspace itself is a folder, not a file inside it.
        Assert.Null(OpenClawMediaOwner.OwningAgent("/x/ws", nested));
    }

    [Fact]
    public void ATrailingSlashOnAWorkspaceIsIgnoredAndComparisonIsCaseSensitive()
    {
        var withSlash = new Dictionary<string, string> { ["a"] = "/x/Ws/" };
        Assert.Equal("a", OpenClawMediaOwner.OwningAgent("/x/Ws/f.mp4", withSlash));
        Assert.Null(OpenClawMediaOwner.OwningAgent("/x/ws/f.mp4", withSlash));
    }

    [Fact]
    public void AWorkspaceOfTheFilesystemRootClaimsNothing()
    {
        var root = new Dictionary<string, string> { ["a"] = "/", ["b"] = "" };
        Assert.Null(OpenClawMediaOwner.OwningAgent("/any/where.mp4", root));
    }

    [Fact]
    public void AKeyThatIsNotAnAgentKeyFallsBackToTheOwnersMainSession()
    {
        // An asking key with no agent: prefix has no "same conversation" to
        // map, but the owner's main session is still a key the gateway named.
        Assert.Equal(ZaraMain, OpenClawMediaOwner.SiblingKeyFor("comfyui", "weird-key", new[] { ZaraMain }));
        Assert.Null(OpenClawMediaOwner.SiblingKeyFor("comfyui", "weird-key", new[] { "agent:other:main" }));
    }

    // ---- agents.list -> workspaces ---------------------------------------

    [Fact]
    public void WorkspacesAreReadVerbatimAndAgentsWithoutOneAreSkipped()
    {
        var json = JsonDocument.Parse("""
        {"agents":[
          {"id":"main","name":"Sample","identity":{"name":"Sample"},"workspace":"/Users/sample/.openclaw/workspace","workspaceGit":{}},
          {"id":"comfyui","workspace":"/Users/sample/.openclaw/workspace-sample-zara/"},
          {"id":"bare","name":"No folder"},
          {"id":"blank","workspace":"  "},
          {"workspace":"/orphan"}]}
        """).RootElement;

        var map = OpenClawSessions.WorkspacesFrom(json);

        Assert.Equal(2, map.Count);
        Assert.Equal("/Users/sample/.openclaw/workspace", map["main"]);
        Assert.Equal("/Users/sample/.openclaw/workspace-sample-zara/", map["comfyui"]);
    }

    [Fact]
    public void WorkspacesFromAnythingThatIsNotAnAgentsListIsEmpty()
    {
        Assert.Empty(OpenClawSessions.WorkspacesFrom(JsonDocument.Parse("[]").RootElement));
        Assert.Empty(OpenClawSessions.WorkspacesFrom(JsonDocument.Parse("{}").RootElement));
        Assert.Empty(OpenClawSessions.WorkspacesFrom(JsonDocument.Parse("{\"agents\":{}}").RootElement));
    }

    // ---- the index the app builds ----------------------------------------

    [Fact]
    public void TheOwnerIndexSnapshotCarriesTheWorkspacesAndTheSnapshotKeys()
    {
        try
        {
            OpenClawSessions.SetAgentWorkspacesForTests(Spaces);
            OpenClawSessions.SetSnapshotForTests(new[]
            {
                Session(AskKey),
                Session(ZaraDm),
            });

            var index = OpenClawSessions.OwnerIndexSnapshot();

            Assert.Equal(Zara, index.AgentWorkspaces["comfyui"]);
            Assert.Equal(new[] { AskKey, ZaraDm }, index.SessionKeys.ToArray());

            // And it drives the choice end to end.
            var source = OpenClawSessions.MediaSourceFor(Clip, AskKey, index);
            Assert.Equal(ZaraDm, source.SessionKey);
            Assert.Equal(Clip, source.Path);
            Assert.Null(OpenClawSessions.MediaSourceFor(Clip, null, index).SessionKey);
        }
        finally
        {
            OpenClawSessions.SetAgentWorkspacesForTests(new Dictionary<string, string>());
            OpenClawSessions.SetSnapshotForTests(new List<OpenClawSessions.Session>());
        }
    }
}
