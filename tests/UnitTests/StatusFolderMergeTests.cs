using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using StatusFileRead = Orbweaver.SessionManager.StatusFileRead;

namespace Orbweaver.Tests;

// CB-255 §1: the app reads two status folders while the legacy one is
// watched — the Orbweaver hooks' and the pre-Orbweaver ones' — because a
// session alive across the upgrade keeps calling the old script, which keeps
// writing the old folder. One file per session comes out:
// **the newest write wins, and the loser is ignored, not deleted.**
//
// NewestPerSession is the rule and is pure; ReadStatusDirectory and
// HeadlessSnapshot are the disk halves, driven here against two scratch
// folders. The live scan composes the same two and is driven in
// tests/UiTests/SessionScanTests.
//
// "Settings" because HeadlessSnapshot reads the per-CLI switches, which are
// process-wide; see HeadlessSnapshotTests' header for the flake that cost.
[Collection("Settings")]
public class StatusFolderMergeTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Func<Dictionary<string, string>?> NoJobs =
        () => new Dictionary<string, string>();

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-statusmerge-" + Guid.NewGuid().ToString("N"));

    private string Current => Path.Combine(_root, "orbweaver");
    private string Legacy => Path.Combine(_root, "claude_buddy");

    public StatusFolderMergeTests()
    {
        OrbweaverSettings.ClaudeCodeEnabled = true;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static StatusFileRead Read(string id, string folder, DateTime written) =>
        new(id, Path.Combine(folder, id + ".txt"), new SessionStatus { Title = folder }, written);

    private static string Write(string dir, string id, string title, DateTime written)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, id + ".txt");
        File.WriteAllText(path, JsonSerializer.Serialize(new SessionStatus
        {
            State = "idle",
            Title = title,
            Cwd = "/tmp/somewhere",
            // One pid per session id, so Superseded — which collapses two
            // files sharing a pid and a CLI — never decides a case here.
            // A character sum rather than GetHashCode, which is randomised
            // per process and would make a collision a coin toss.
            SessionPid = 4000 + id.Sum(c => c)
        }));
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    private List<(string SessionId, SessionStatus Status)> Snapshot(string? legacy) =>
        SessionManager.HeadlessSnapshot(
            Current, NoJobs, isRunning: _ => true, nowUtc: T0.AddMinutes(1),
            honourOrbLifetime: false, legacyStatusDir: legacy);

    // --- NewestPerSession: the rule ---------------------------------------

    [Fact]
    public void ANewerLegacyCopyBeatsAnOlderCurrentOne()
    {
        var winner = Assert.Single(SessionManager.NewestPerSession(
            new[] { Read("s1", "current", T0) },
            new[] { Read("s1", "legacy", T0.AddSeconds(1)) }));

        Assert.Equal("legacy", winner.Status.Title);
    }

    [Fact]
    public void ANewerCurrentCopyBeatsAnOlderLegacyOne()
    {
        var winner = Assert.Single(SessionManager.NewestPerSession(
            new[] { Read("s1", "current", T0.AddSeconds(1)) },
            new[] { Read("s1", "legacy", T0) }));

        Assert.Equal("current", winner.Status.Title);
    }

    // A tie goes to the new folder, so the answer never depends on which
    // folder happened to be enumerated first.
    [Fact]
    public void ATieGoesToTheCurrentFolder()
    {
        var winner = Assert.Single(SessionManager.NewestPerSession(
            new[] { Read("s1", "current", T0) },
            new[] { Read("s1", "legacy", T0) }));

        Assert.Equal("current", winner.Status.Title);
    }

    // Distinct sessions are all kept, the current folder's own order first
    // and then whatever only the legacy folder had.
    [Fact]
    public void DistinctSessionsAreAllKeptCurrentFirst()
    {
        var merged = SessionManager.NewestPerSession(
            new[] { Read("b", "current", T0), Read("a", "current", T0) },
            new[] { Read("c", "legacy", T0), Read("a", "legacy", T0.AddSeconds(5)) });

        Assert.Equal(new[] { "b", "a", "c" }, merged.Select(r => r.SessionId));
        Assert.Equal("legacy", merged[1].Status.Title);
    }

    [Fact]
    public void EitherSideEmptyIsTheOtherSideUnchanged()
    {
        var one = new[] { Read("x", "current", T0), Read("y", "current", T0) };

        Assert.Equal(one, SessionManager.NewestPerSession(one, Array.Empty<StatusFileRead>()));
        Assert.Equal(one, SessionManager.NewestPerSession(Array.Empty<StatusFileRead>(), one));
    }

    // Ids are file names, compared exactly: two ids that differ only in case
    // are two sessions, as they are to the hooks that wrote them.
    [Fact]
    public void IdsAreComparedOrdinally()
    {
        Assert.Equal(2, SessionManager.NewestPerSession(
            new[] { Read("abc", "current", T0) },
            new[] { Read("ABC", "legacy", T0) }).Count);
    }

    // --- ReadStatusDirectory: one folder off disk -------------------------

    [Fact]
    public void AFolderThatCannotBeListedIsNullNotEmpty()
    {
        Assert.Null(SessionManager.ReadStatusDirectory(Path.Combine(_root, "never-created")));

        Directory.CreateDirectory(Current);
        Assert.Empty(SessionManager.ReadStatusDirectory(Current)!);
    }

    // A half-written file and a literal JSON null are skipped, and a file
    // that is not *.txt is not a status file at all.
    [Fact]
    public void UnparseableFilesAreSkippedAndTheRestAreRead()
    {
        var good = Write(Current, "good", "kept", T0);
        File.WriteAllText(Path.Combine(Current, "torn.txt"), "{\"state\":\"id");
        File.WriteAllText(Path.Combine(Current, "nothing.txt"), "null");
        File.WriteAllText(Path.Combine(Current, ".auto-color"), "");

        var read = Assert.Single(SessionManager.ReadStatusDirectory(Current)!);

        Assert.Equal("good", read.SessionId);
        Assert.Equal(good, read.Path);
        Assert.Equal("kept", read.Status.Title);
        Assert.Equal(T0, read.Written);
    }

    // --- HeadlessSnapshot over both folders -------------------------------

    [Fact]
    public void ASessionOnlyTheLegacyHookWroteIsServed()
    {
        Write(Current, "new", "after-upgrade", T0);
        Write(Legacy, "old", "before-upgrade", T0);

        var kept = Snapshot(Legacy);

        Assert.Equal(new[] { "new", "old" }, kept.Select(k => k.SessionId));
    }

    // Both orders, through the real mtime on disk.
    [Theory]
    [InlineData(1, "legacy-copy")]
    [InlineData(-1, "current-copy")]
    public void TheNewerFileOnDiskWinsEitherWay(int legacyLeadSeconds, string expected)
    {
        Write(Current, "s1", "current-copy", T0);
        Write(Legacy, "s1", "legacy-copy", T0.AddSeconds(legacyLeadSeconds));

        var kept = Assert.Single(Snapshot(Legacy));

        Assert.Equal(expected, kept.Status.Title);
    }

    [Fact]
    public void AnEqualMtimeOnDiskGoesToTheCurrentFolder()
    {
        Write(Current, "s1", "current-copy", T0);
        Write(Legacy, "s1", "legacy-copy", T0);

        Assert.Equal("current-copy", Assert.Single(Snapshot(Legacy)).Status.Title);
    }

    // The loser is ignored, never deleted: the scan is not allowed side
    // effects on a file a hook may be mid-write on.
    [Fact]
    public void TheLosingFileIsLeftOnDisk()
    {
        var older = Write(Current, "s1", "current-copy", T0);
        var newer = Write(Legacy, "s1", "legacy-copy", T0.AddSeconds(1));

        Snapshot(Legacy);

        Assert.True(File.Exists(older));
        Assert.True(File.Exists(newer));
    }

    // One side missing, each way: no new folder yet (nothing post-upgrade
    // has run a hook), or no legacy folder (every old session has ended).
    [Fact]
    public void AMissingCurrentFolderStillServesTheLegacyOne()
    {
        Write(Legacy, "old", "before-upgrade", T0);

        Assert.Equal("old", Assert.Single(Snapshot(Legacy)).SessionId);
    }

    [Fact]
    public void AMissingLegacyFolderStillServesTheCurrentOne()
    {
        Write(Current, "new", "after-upgrade", T0);

        Assert.Equal("new", Assert.Single(Snapshot(Path.Combine(_root, "never-created"))).SessionId);
    }

    [Fact]
    public void BothFoldersMissingIsNoSessions()
    {
        Assert.Empty(Snapshot(Path.Combine(_root, "never-created")));
    }

    // One side unreadable: the newer copy is torn mid-write, so the older,
    // readable copy in the other folder answers rather than nothing.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATornCopyInOneFolderIsAnsweredByTheOther(bool legacyIsTorn)
    {
        var torn = legacyIsTorn ? Legacy : Current;
        var whole = legacyIsTorn ? Current : Legacy;

        Write(whole, "s1", "whole", T0);
        Directory.CreateDirectory(torn);
        var tornPath = Path.Combine(torn, "s1.txt");
        File.WriteAllText(tornPath, "{\"state\":\"gen");
        File.SetLastWriteTimeUtc(tornPath, T0.AddSeconds(5));

        Assert.Equal("whole", Assert.Single(Snapshot(Legacy)).Status.Title);
    }

    // The production call — no statusDir at all — reads the real legacy
    // folder too, so a headless machine serving a session that predates the
    // upgrade does not report it missing. Safe to ask here: every suite's
    // TestBootstrap points ORBWEAVER_STATUS_ROOT at a sandbox, so "the
    // real legacy folder" is a scratch directory of this run's own. The id is
    // unique, and the file is removed afterwards for whoever reads it next.
    [Fact]
    public void TheDefaultCallReadsTheRealLegacyFolderToo()
    {
        var id = "legacy-default-" + Guid.NewGuid().ToString("N");
        var path = Write(StatusDirectory.LegacyPath(), id, "before-upgrade", DateTime.UtcNow);
        try
        {
            var kept = SessionManager.HeadlessSnapshot(
                jobListing: NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow.AddMinutes(1),
                honourOrbLifetime: false);

            Assert.Contains(kept, k => k.SessionId == id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The default the tests rely on: a statusDir handed in with no legacy
    // folder reads exactly one folder, so a file in a sibling legacy folder
    // is invisible — the negative control for every case above.
    [Fact]
    public void WithNoLegacyFolderNamedOnlyTheCurrentOneIsRead()
    {
        Write(Current, "new", "after-upgrade", T0);
        Write(Legacy, "old", "before-upgrade", T0);

        Assert.Equal("new", Assert.Single(Snapshot(legacy: null)).SessionId);
    }
}
