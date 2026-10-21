using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Orbweaver.Tests;

// ClaudeCliCredentials.SourcesFor over real credential files — the half of
// ClaudeCloudCredentialsFileTests that decides *which roots* are accounts rather
// than how one file is read.
//
// **In the Settings collection, and split out of the file tests for that reason
// alone.** SourcesFor asks ClaudeConfigRoots.All for every root, and All also
// walks OrbweaverSettings.ClaudeCodeProfileDirs — one process-wide static.
// SettingsListsAndProfilesTests adds ".claude-work" to that list, so when the
// two classes overlapped the answer here was three accounts where two were
// written to disk. It surfaced on CB-226's PR: TwoRootsAreTwoAccountsOneFoundOneSignedOut
// failed on one macOS run with "Expected 2, Actual 3" while recent develop runs
// were green. The overlap is a scheduling accident, so the failure is
// intermittent; the mechanism is not. Measured: adding ".claude-work" to the
// list and calling SourcesFor over the same two roots returns three. Being in
// the collection means no other Settings class runs at the same time, so the
// list is whatever this class's own bootstrap left it.
//
// A separate class rather than the whole file tests joining the collection: those
// only read a file they own and stay parallel.
[Collection("Settings")]
public class ClaudeCloudCredentialRootsTests : IDisposable
{
    private const string FakeAccess = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

    private readonly string _dir;

    public ClaudeCloudCredentialRootsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(),
            "cb-cloud-credential-roots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A scratch directory that outlives the run is not a test failure.
        }
    }

    private static string Blob(long expiresAtMillis) =>
        $$$"""
        {"claudeAiOauth":{"accessToken":"{{{FakeAccess}}}","refreshToken":"r","expiresAt":{{{expiresAtMillis}}}}}
        """;

    private static long InAnHour =>
        DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();

    // Two accounts on disk: the default root holds a live login and another root
    // holds one the CLI blanked. Each is its own account, read on its own, so the
    // live one is not hidden behind the dead one and the dead one is not hidden
    // behind the live one.
    [Fact]
    public void TwoRootsAreTwoAccountsOneFoundOneSignedOut()
    {
        var defaultRoot = System.IO.Path.Combine(_dir, ".claude");
        var boardRoot = System.IO.Path.Combine(_dir, ".claude-board");
        Directory.CreateDirectory(defaultRoot);
        Directory.CreateDirectory(boardRoot);
        File.WriteAllText(System.IO.Path.Combine(defaultRoot, ".credentials.json"), Blob(InAnHour));
        File.WriteAllText(System.IO.Path.Combine(boardRoot, ".credentials.json"),
            """{"claudeAiOauth":{"accessToken":"","refreshToken":"","expiresAt":0}}""");

        var accounts = ClaudeCliCredentials.SourcesFor(isMacOS: false, _dir, boardRoot);

        Assert.Equal(2, accounts.Count);
        Assert.Equal(new[] { "default", "board" }, accounts.Select(a => a.Label));
        var live = accounts[0].Source.Read();
        var dead = accounts[1].Source.Read();
        Assert.Equal(CredentialOutcome.Found, live.Outcome);
        Assert.Equal(FakeAccess, live.AccessToken);
        Assert.Equal(CredentialOutcome.NotLoggedIn, dead.Outcome);
        Assert.Contains("signed this login out", dead.Detail);
    }
}
