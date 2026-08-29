using System.IO;
using Xunit;

namespace ClaudeBuddy.Tests;

// SessionPark's I/O half against real files on disk: finding the record under
// whichever account owns it, the stat, and the cache keyed on it. The decision
// over the record's text is pure and covered in tests/UnitTests/SessionParkTests;
// what can only be asserted here is that a second account's sessions directory
// is searched at all — the whole bug — and that a record which changes is
// re-read rather than answered from a stale cache.
//
// In the Settings collection because pointing the walk at a scratch account
// means writing ClaudeCodeProfileDirs, which is one static for the process.
[Collection("Settings")]
public class SessionParkRecordTests
{
    private const string Owner = "746496c9-d663-42fc-96bd-92a67b843f48";

    private static string Record(string sessionId, string? parkedJobId) =>
        parkedJobId is null
            ? $$"""{"pid":70580,"sessionId":"{{sessionId}}","kind":"interactive","status":"idle"}"""
            : $$"""{"pid":70580,"sessionId":"{{sessionId}}","kind":"interactive","status":"idle","parkedJobId":"{{parkedJobId}}"}""";

    // The default account, which is where a single-account machine's records
    // live and the only place the app would have looked before this existed.
    [Fact]
    public void ARecordUnderTheDefaultAccountIsFound()
    {
        var home = NewHome();
        WriteRecord(home, ".claude", 70580, Record(Owner, "e4f5c5e4"));

        SessionPark.ClearCacheForTests();
        Assert.True(SessionPark.IsParked(70580, Owner, home));
    }

    // The bug, in one test. The session doing the asking belonged to a second
    // account, so its record was never under ~/.claude at all and the rule had
    // nothing to read — which reads as "not parked", which leaves the husk orb
    // on screen beside the fork's.
    [Fact]
    public void ARecordUnderASecondAccountIsFound()
    {
        var home = NewHome();
        Directory.CreateDirectory(Path.Combine(home, ".claude", "sessions"));
        WriteRecord(home, ".claude-board", 70580, Record(Owner, "e4f5c5e4"));

        ClaudeBuddySettings.AddClaudeCodeProfileDir(".claude-board");
        try
        {
            SessionPark.ClearCacheForTests();
            Assert.True(SessionPark.IsParked(70580, Owner, home));
        }
        finally
        {
            ClaudeBuddySettings.RemoveClaudeCodeProfileDir(".claude-board");
        }
    }

    [Fact]
    public void ASecondAccountThatIsNotConfiguredIsNotSearched()
    {
        // The same layout as above with the setting left off: the record is
        // there on disk and the app has no reason to know about it. Stated so
        // the previous test is proving the setting is what reaches it, rather
        // than an accidental deep walk.
        var home = NewHome();
        WriteRecord(home, ".claude-board", 70580, Record(Owner, "e4f5c5e4"));

        SessionPark.ClearCacheForTests();
        Assert.False(SessionPark.IsParked(70580, Owner, home));
    }

    [Fact]
    public void NoRecordAtAllLeavesTheOrbAlone()
    {
        var home = NewHome();
        Directory.CreateDirectory(Path.Combine(home, ".claude", "sessions"));

        SessionPark.ClearCacheForTests();
        Assert.False(SessionPark.IsParked(70580, Owner, home));
    }

    // The orb has to come back when the user returns to the window, and the
    // only thing that says so is the record being rewritten without the field.
    // A cache that answered from the first read would strand the session
    // invisible for as long as the app ran.
    [Fact]
    public void ARecordThatChangesIsReReadAndTheOrbComesBack()
    {
        var home = NewHome();
        var path = WriteRecord(home, ".claude", 70580, Record(Owner, "e4f5c5e4"));

        SessionPark.ClearCacheForTests();
        Assert.True(SessionPark.IsParked(70580, Owner, home));

        // Asked again unchanged: the cache answers, and answers the same.
        Assert.True(SessionPark.IsParked(70580, Owner, home));

        // Coming back out of the agents view clears the field. Length differs
        // as well as mtime, which is what the cache key actually compares —
        // mtime alone has a resolution a fast test can lose.
        File.WriteAllText(path, Record(Owner, null));
        Assert.False(SessionPark.IsParked(70580, Owner, home));
    }

    [Fact]
    public void AMalformedRecordOnDiskLeavesTheOrbAlone()
    {
        // A record caught mid-write is the ordinary case: these files are
        // rewritten by a live session while the scan reads them.
        var home = NewHome();
        WriteRecord(home, ".claude", 70580, @"{""sessionId"":""746");

        SessionPark.ClearCacheForTests();
        Assert.False(SessionPark.IsParked(70580, Owner, home));
    }

    private static string NewHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "cb-park-" + Path.GetRandomFileName());
        Directory.CreateDirectory(home);
        return home;
    }

    private static string WriteRecord(string home, string account, int pid, string json)
    {
        var dir = Path.Combine(home, account, "sessions");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, pid + ".json");
        File.WriteAllText(path, json);
        return path;
    }
}
