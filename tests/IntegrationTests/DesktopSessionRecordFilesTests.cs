using Xunit;

namespace Orbweaver.Tests;

// Serialises the classes that point ClaudeDesktopManager.ProfileRoot at a
// scratch directory: the variable is process-wide, and a sibling reading the
// real root mid-test would see the scratch one.
[CollectionDefinition("ProfileRootEnv")]
public class ProfileRootEnvCollection
{
}

// CB-266: DesktopBridgeLookup against real files. The unit tests drive its
// rules through fake seams; this is the seam with what this process does not
// own, Claude Desktop's on-disk records, laid out the way they were measured on
// a real Mac: <root>/<profile>/claude-code-sessions/<uuid>/<uuid>/local_<id>.json
// under "Claude" and "Claude-<Name>" profile folders, with "Claude-3p" being
// Desktop's own sidecar directory that Discover() must skip. Every id here is
// invented; the key set is the measured one.
[Collection("ProfileRootEnv")]
public class DesktopSessionRecordFilesTests : IDisposable
{
    private const string Host = "local_00000000-aaaa-bbbb-cccc-000000000040";
    private const string Cli = "44444444-5555-6666-7777-888888888888";
    private const string Bridge = "session_01DiskFixtureGGGG";
    private const string RootVariable = "ORBWEAVER_PROFILE_ROOT";

    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _before = Environment.GetEnvironmentVariable(RootVariable) ?? "";
    private readonly string _root;

    public DesktopSessionRecordFilesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cb-records-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(RootVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RootVariable, _before.Length == 0 ? null : _before);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // A directory Discover() will adopt: two of the marker files it looks for.
    private string Profile(string folder)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "Cookies"), "");
        return dir;
    }

    private static string Json(
        string bridges = "[\"" + Bridge + "\"]", string cli = Cli, bool archived = false) =>
        "{\"sessionId\":\"" + Host + "\",\"cliSessionId\":\"" + cli + "\",\"bridgeSessionIds\":" + bridges
        + ",\"remoteControlAutoEligible\":true,\"steeredByRemoteClient\":false,\"isArchived\":"
        + (archived ? "true" : "false") + ",\"cwd\":\"/Users/user/project\",\"title\":\"A chat\"}";

    private static string WriteRecord(string profileDir, string json, string host = Host)
    {
        var dir = Path.Combine(profileDir, "claude-code-sessions",
            "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, host + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private static SessionStatus Chat(string host = Host) => new()
    {
        Entrypoint = SessionStatus.DesktopEntrypoint,
        HostSessionId = host,
        SessionId = Cli,
    };

    [Fact]
    public void TheDefaultProfilesRecordYieldsItsBridge()
    {
        WriteRecord(Profile("Claude"), Json());

        Assert.Equal(Bridge, new DesktopBridgeLookup().BridgeFor(Chat(), Now));
    }

    [Fact]
    public void ACloneProfilesRecordIsFoundWhateverProfileTheChatIsHostedFrom()
    {
        Profile("Claude");
        WriteRecord(Profile("Claude-Board"), Json());

        Assert.Equal(Bridge, new DesktopBridgeLookup().BridgeFor(Chat(), Now));
    }

    [Fact]
    public void TheSidecarDirectoryIsNotAProfileSoARecordThereIsIgnored()
    {
        Profile("Claude");
        WriteRecord(Profile("Claude-3p"), Json());

        Assert.Null(new DesktopBridgeLookup().BridgeFor(Chat(), Now));
    }

    [Fact]
    public void ARecordRewrittenWithADifferentBridgeIsReReadOnceItsMtimeMoves()
    {
        var path = WriteRecord(Profile("Claude"), Json(bridges: "[]"));
        File.SetLastWriteTimeUtc(path, Now);
        var lookup = new DesktopBridgeLookup();

        Assert.Null(lookup.BridgeFor(Chat(), Now));

        // Same mtime, new content: the memo is by mtime, so this is still the
        // old answer. That is the documented cost of not reading every tick.
        File.WriteAllText(path, Json());
        File.SetLastWriteTimeUtc(path, Now);
        Assert.Null(lookup.BridgeFor(Chat(), Now.AddSeconds(2)));

        File.SetLastWriteTimeUtc(path, Now.AddMinutes(1));
        Assert.Equal(Bridge, lookup.BridgeFor(Chat(), Now.AddSeconds(4)));
    }

    [Fact]
    public void ARecordWhoseBridgeArrayWasEmptiedAnswersNullAfterBeingBridged()
    {
        var path = WriteRecord(Profile("Claude"), Json());
        File.SetLastWriteTimeUtc(path, Now);
        var lookup = new DesktopBridgeLookup();
        Assert.Equal(Bridge, lookup.BridgeFor(Chat(), Now));

        File.WriteAllText(path, Json(bridges: "[]"));
        File.SetLastWriteTimeUtc(path, Now.AddMinutes(1));

        Assert.Null(lookup.BridgeFor(Chat(), Now.AddSeconds(2)));
    }

    [Fact]
    public void AnArchivedRecordAndAnotherSessionsRecordAreNoBridge()
    {
        var path = WriteRecord(Profile("Claude"), Json(archived: true));
        Assert.Null(new DesktopBridgeLookup().BridgeFor(Chat(), Now));

        File.WriteAllText(path, Json(cli: "some-other-cli-session"));
        Assert.Null(new DesktopBridgeLookup().BridgeFor(Chat(), Now));
    }

    [Fact]
    public void AStatusWithoutAHostIdIsMatchedByItsCliSessionIdAcrossProfiles()
    {
        Profile("Claude");
        WriteRecord(Profile("Claude-Board"), Json());

        Assert.Equal(Bridge, new DesktopBridgeLookup().BridgeFor(Chat(host: ""), Now));
    }

    [Fact]
    public void NoProfilesAtAllIsNoBridgeRatherThanAnError()
    {
        // The scratch root exists and holds nothing: a machine without Claude
        // Desktop, or one where Desktop has never run a Code chat.
        Assert.Null(new DesktopBridgeLookup().BridgeFor(Chat(), Now));

        Profile("Claude");
        Assert.Null(new DesktopBridgeLookup().BridgeFor(Chat(), Now));
    }

    // The disk helpers on their failure paths, which a status the scan hands
    // them reaches only when Desktop is mid-write or the folder is unreadable.

    [Fact]
    public void ADirectoryOrAMissingFileReadsAsNothingAndAMissingFileHasNoMtime()
    {
        var dir = Profile("Claude");
        var missing = Path.Combine(dir, "nope.json");

        Assert.Null(DesktopBridgeLookup.DiskRead(dir));
        Assert.Null(DesktopBridgeLookup.DiskRead(missing));
        Assert.Null(DesktopBridgeLookup.DiskMtime(missing));
        Assert.NotNull(DesktopBridgeLookup.DiskMtime(Path.Combine(dir, "config.json")));
    }

    [Fact]
    public void ListingAMissingFolderIsEmptyAndRecordFilesAreFoundTwoLevelsDown()
    {
        var dir = Profile("Claude");
        var missing = Path.Combine(dir, "claude-code-sessions");

        Assert.Empty(DesktopBridgeLookup.DiskSubdirectories(missing));
        Assert.Empty(DesktopBridgeLookup.DiskFilesIn(missing));
        Assert.Empty(DesktopBridgeLookup.DiskRecordFiles(missing));

        var path = WriteRecord(dir, Json());
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "notes.txt"), "not a record");

        Assert.Equal(new[] { path }, DesktopBridgeLookup.DiskRecordFiles(missing));
    }
}
