using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-267: Scan stamps each entry with the config root it was read from, so the
// chat panel can find the settings.json to edit for a session whose transcript
// path names no root.
public class SessionRegistryConfigRootTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cb267-registry-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Json(int pid, string id, string sock) =>
        new JsonObject
        {
            ["pid"] = pid, ["sessionId"] = id, ["peerProtocol"] = 1, ["kind"] = "bg",
            ["messagingSocketPath"] = sock,
        }.ToJsonString();

    private string Root(string name, int pid, string id, bool withKey)
    {
        var root = Path.Combine(_dir, name);
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var sock = $"/tmp/{name}-{pid}.sock";
        File.WriteAllText(Path.Combine(sessions, pid + ".json"), Json(pid, id, sock));
        if (withKey) File.WriteAllText(Path.Combine(sessions, SessionRegistry.KeyFileName(pid, sock)), "k");
        return root;
    }

    [Fact]
    public void EachEntryCarriesTheRootItWasReadFrom()
    {
        var a = Root(".claude", 101, "id-a", withKey: true);
        var b = Root(".claude-board", 202, "id-b", withKey: false);

        var entries = SessionRegistry.Scan(new[] { a, b });

        var ea = Assert.Single(entries, e => e.SessionId == "id-a");
        var eb = Assert.Single(entries, e => e.SessionId == "id-b");
        Assert.Equal(a, ea.ConfigRoot);
        Assert.NotNull(ea.KeyPath);          // the with-key arm
        Assert.Equal(b, eb.ConfigRoot);
        Assert.Null(eb.KeyPath);             // the without-key arm
    }

    [Fact]
    public void ParseAloneHasNoRootToName()
    {
        var e = SessionRegistry.Parse(Json(1, "x", "/tmp/x.sock"), null);
        Assert.Null(e!.Value.ConfigRoot);
    }
}
