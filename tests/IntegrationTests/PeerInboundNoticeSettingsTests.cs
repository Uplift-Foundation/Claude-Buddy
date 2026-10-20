using System;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-267's one new setting, through a real file, in the shape
// AccountUsageSettingsTests uses: the failure worth catching is a missing
// KnownKeys entry, which makes the key round-trip through _unknownKeys as well
// as the model and either throws on the duplicate or quietly doubles it, and
// which no in-memory property test can see.
[Collection("Settings")]
public class PeerInboundNoticeSettingsTests
{
    private static string NewSettingsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-integrationtests-inbound-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PointSettingsAt(string dir)
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    [Fact]
    public void TheNoticeIsUnseenUntilSomebodyActsOnIt()
    {
        PointSettingsAt(NewSettingsDir());

        Assert.False(OrbweaverSettings.PeerInboundNoticeSeen);
    }

    [Fact]
    public void SeenReachesDiskAndComesBackAfterARestart()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.PeerInboundNoticeSeen = true;

        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json"))) as JsonObject;
        Assert.NotNull(root);
        Assert.True(root!["peerInboundNoticeSeen"]!.GetValue<bool>());

        PointSettingsAt(dir);
        Assert.True(OrbweaverSettings.PeerInboundNoticeSeen);
    }

    [Fact]
    public void WritingItDoesNotDisturbKeysThisBuildHasNeverHeardOf()
    {
        var dir = NewSettingsDir();
        var path = Path.Combine(dir, "settings.json");

        File.WriteAllText(path, """
        {
          "peerInboundNoticeSeen": true,
          "somethingFromANewerBuild": {"nested": [1, 2, 3]},
          "aScalarFromTheFuture": "keep me"
        }
        """);

        PointSettingsAt(dir);
        Assert.True(OrbweaverSettings.PeerInboundNoticeSeen);

        // A save of any kind rewrites the file; a missing KnownKeys entry would
        // throw on the duplicate here rather than quietly dropping the stranger.
        OrbweaverSettings.PeerInboundNoticeSeen = false;

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        Assert.NotNull(root);
        Assert.False(root!["peerInboundNoticeSeen"]!.GetValue<bool>());
        Assert.Equal("keep me", root["aScalarFromTheFuture"]!.GetValue<string>());
        Assert.Equal(3, root["somethingFromANewerBuild"]!["nested"]!.AsArray().Count);
    }
}
