using Xunit;

namespace Orbweaver.Tests;

// CB-266: the pure rules for finding a Claude Desktop chat's Remote Control
// bridge from the record Desktop keeps for it. Every id is invented, with the
// key set measured on a real Mac (docs/claude-desktop-sessions-findings.md);
// nothing here names a real chat.
public class DesktopSessionRecordsTests
{
    private const string Host = "local_00000000-aaaa-bbbb-cccc-000000000010";
    private const string Cli = "22222222-3333-4444-5555-666666666666";
    private const string Bridge = "session_01FixtureBridgeCCCC";

    private static string Json(
        string sessionId = Host, string? cli = Cli, string bridges = "[\"" + Bridge + "\"]",
        bool archived = false) =>
        "{\"sessionId\":\"" + sessionId + "\"," +
        (cli is null ? "" : "\"cliSessionId\":\"" + cli + "\",") +
        "\"bridgeSessionIds\":" + bridges + "," +
        "\"remoteControlAutoEligible\":true,\"steeredByRemoteClient\":false," +
        "\"isArchived\":" + (archived ? "true" : "false") + "," +
        "\"cwd\":\"/Users/user/project\",\"title\":\"A chat\",\"lastActivityAt\":1}";

    // ---- IsWellFormedHostId / FileNameFor -----------------------------------

    [Theory]
    [InlineData("local_00000000-aaaa-bbbb-cccc-000000000010", true)]
    [InlineData("local_abc", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("local_", false)]
    [InlineData("session_abc", false)]
    [InlineData("local_..", false)]
    [InlineData("local_a/b", false)]
    [InlineData("local_a\\b", false)]
    [InlineData("local_a.json", false)]
    [InlineData("local_a b", false)]
    [InlineData("local_a_b", false)]
    public void HostIdShapeIsAnAllowListOfTheMeasuredForm(string? id, bool expected) =>
        Assert.Equal(expected, DesktopSessionRecords.IsWellFormedHostId(id));

    [Fact]
    public void TheRecordFileIsTheHostIdWithAJsonExtension() =>
        Assert.Equal(Host + ".json", DesktopSessionRecords.FileNameFor(Host));

    // ---- Parse ---------------------------------------------------------------

    [Fact]
    public void AMeasuredShapeRecordParsesEveryFieldWeUse()
    {
        var record = DesktopSessionRecords.Parse(Json());

        Assert.NotNull(record);
        Assert.Equal(Host, record!.SessionId);
        Assert.Equal(Cli, record.CliSessionId);
        Assert.Equal(new[] { Bridge }, record.BridgeSessionIds);
        Assert.False(record.IsArchived);
        Assert.True(record.RemoteControlAutoEligible);
        Assert.False(record.SteeredByRemoteClient);
    }

    [Fact]
    public void ArchivedAndSteeredFlagsReadTrue()
    {
        var record = DesktopSessionRecords.Parse(
            Json(archived: true).Replace("\"steeredByRemoteClient\":false", "\"steeredByRemoteClient\":true"));

        Assert.True(record!.IsArchived);
        Assert.True(record.SteeredByRemoteClient);
    }

    [Fact]
    public void AMissingCliSessionIdAndAnEmptiedBridgeArrayParseAsAbsent()
    {
        var record = DesktopSessionRecords.Parse(Json(cli: null, bridges: "[]"));

        Assert.Null(record!.CliSessionId);
        Assert.Empty(record.BridgeSessionIds);
    }

    [Fact]
    public void NonStringAndEmptyBridgeEntriesAreSkippedAndWrongTypedFieldsReadAsAbsent()
    {
        var record = DesktopSessionRecords.Parse(
            "{\"sessionId\":\"" + Host + "\",\"cliSessionId\":7,\"isArchived\":\"yes\"," +
            "\"bridgeSessionIds\":[1,null,\"\",\"" + Bridge + "\"]}");

        Assert.Null(record!.CliSessionId);
        Assert.False(record.IsArchived);
        Assert.Equal(new[] { Bridge }, record.BridgeSessionIds);
    }

    [Fact]
    public void ABridgeIdsFieldThatIsNotAnArrayReadsAsNone()
    {
        var record = DesktopSessionRecords.Parse(
            "{\"sessionId\":\"" + Host + "\",\"bridgeSessionIds\":\"" + Bridge + "\"}");

        Assert.Empty(record!.BridgeSessionIds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{}")]
    [InlineData("{\"sessionId\":\"\"}")]
    [InlineData("{\"sessionId\":5}")]
    public void AnythingThatIsNotARecordParsesAsNull(string? json) =>
        Assert.Null(DesktopSessionRecords.Parse(json));

    // ---- BridgeIdOf ----------------------------------------------------------

    private static DesktopSessionRecord Record(
        string? cli = Cli, bool archived = false, params string[] bridges) =>
        new(Host, cli, bridges, archived, true, false);

    [Fact]
    public void ALiveRecordForThisSessionYieldsItsBridge() =>
        Assert.Equal(Bridge, DesktopSessionRecords.BridgeIdOf(Record(bridges: Bridge), Cli));

    [Fact]
    public void TheLastWellFormedBridgeWinsBecauseReconnectingAppends() =>
        Assert.Equal(
            "session_02Newer",
            DesktopSessionRecords.BridgeIdOf(
                Record(bridges: new[] { Bridge, "session_02Newer", "not-a-session-id" }), Cli));

    [Fact]
    public void NoRecordAnArchivedRecordAnotherSessionsRecordOrNoUsableIdAreNoBridge()
    {
        Assert.Null(DesktopSessionRecords.BridgeIdOf(null, Cli));
        Assert.Null(DesktopSessionRecords.BridgeIdOf(Record(archived: true, bridges: Bridge), Cli));
        Assert.Null(DesktopSessionRecords.BridgeIdOf(Record(cli: "another-cli-id", bridges: Bridge), Cli));
        Assert.Null(DesktopSessionRecords.BridgeIdOf(Record(cli: null, bridges: Bridge), Cli));
        Assert.Null(DesktopSessionRecords.BridgeIdOf(Record(), Cli));
        Assert.Null(DesktopSessionRecords.BridgeIdOf(Record(bridges: new[] { "nope", "session_" }), Cli));
    }

    // ---- RecordPathsFor ------------------------------------------------------

    // A fake tree: profile/claude-code-sessions/<a>/<b>/<file>. Directory
    // listings are keyed by path so the test controls exactly what exists.
    private static Func<string, IEnumerable<string>> Dirs(Dictionary<string, string[]> tree) =>
        path => tree.TryGetValue(path, out var kids) ? kids : Array.Empty<string>();

    [Fact]
    public void RecordPathsAreGlobbedExactlyTwoLevelsDownInEveryProfile()
    {
        var p1 = Path.Combine("root", "Claude");
        var p2 = Path.Combine("root", "Claude-Board");
        var r1 = Path.Combine(p1, "claude-code-sessions");
        var r2 = Path.Combine(p2, "claude-code-sessions");
        var a1 = Path.Combine(r1, "aaa");
        var a2 = Path.Combine(r2, "ccc");
        var tree = new Dictionary<string, string[]>
        {
            [r1] = new[] { a1 },
            [a1] = new[] { Path.Combine(a1, "bbb") },
            [r2] = new[] { a2 },
            [a2] = new[] { Path.Combine(a2, "ddd") },
        };
        var existing = new HashSet<string>
        {
            Path.Combine(a1, "bbb", Host + ".json"),
            Path.Combine(a2, "ddd", Host + ".json"),
        };

        var found = DesktopSessionRecords.RecordPathsFor(
            new[] { p1, p2 }, Host, Dirs(tree), existing.Contains);

        Assert.Equal(new[]
        {
            Path.Combine(a1, "bbb", Host + ".json"),
            Path.Combine(a2, "ddd", Host + ".json"),
        }, found);
    }

    [Fact]
    public void ARecordThatIsNotThereIsNotReportedAndAnIllFormedIdAsksNothing()
    {
        var listed = 0;
        Func<string, IEnumerable<string>> counting = _ => { listed++; return Array.Empty<string>(); };

        Assert.Empty(DesktopSessionRecords.RecordPathsFor(new[] { "p" }, "../escape", counting, _ => true));
        Assert.Equal(0, listed);

        var records = Path.Combine("p", "claude-code-sessions");
        var outer = Path.Combine(records, "aaa");
        var inner = Path.Combine(outer, "bbb");
        var tree = new Dictionary<string, string[]> { [records] = new[] { outer }, [outer] = new[] { inner } };

        Assert.Empty(DesktopSessionRecords.RecordPathsFor(new[] { "p" }, Host, Dirs(tree), _ => false));
        Assert.Empty(DesktopSessionRecords.RecordPathsFor(new[] { "p" }, Host, Dirs(new()), _ => true));
    }

    // ---- FindByCliSessionId --------------------------------------------------

    private static string? Find(
        Dictionary<string, string> filesByPath, string cli = Cli, string[]? profiles = null) =>
        DesktopSessionRecords.FindByCliSessionId(
            profiles ?? new[] { "p" }, cli,
            _ => filesByPath.Keys,
            path => filesByPath.TryGetValue(path, out var json) ? json : null);

    [Fact]
    public void TheOnlyLiveRecordNamingTheCliSessionIsFound() =>
        Assert.Equal("a.json", Find(new()
        {
            ["a.json"] = Json(),
            ["b.json"] = Json(sessionId: "local_other", cli: "someone-else"),
            ["c.json"] = "garbage",
        }));

    [Fact]
    public void TwoLiveRecordsNamingTheSameSessionAreRefusedButAnArchivedOneDoesNotCount()
    {
        Assert.Null(Find(new() { ["a.json"] = Json(), ["b.json"] = Json(sessionId: "local_b") }));

        Assert.Equal("a.json", Find(new()
        {
            ["a.json"] = Json(),
            ["b.json"] = Json(sessionId: "local_b", archived: true),
        }));
    }

    [Fact]
    public void NoMatchAnEmptyIdOrNoProfilesFindNothing()
    {
        Assert.Null(Find(new() { ["a.json"] = Json() }, cli: "unrelated"));
        Assert.Null(Find(new() { ["a.json"] = Json() }, cli: ""));
        Assert.Null(Find(new() { ["a.json"] = Json() }, profiles: Array.Empty<string>()));
    }
}
