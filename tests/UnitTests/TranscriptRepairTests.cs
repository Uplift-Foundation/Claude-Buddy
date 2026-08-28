using System;
using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.Tests;

// The two halves of the scan's transcript repair: which status files are worth
// hunting for (SessionManager.WantsTranscriptRepair) and when the hunt is worth
// running again (TranscriptHunts.Locate).
//
// The shape both exist for is a real one: a finished background job's worker,
// respawned by the daemon from the job's original directory, whose hook then
// recorded a transcript_path computed from that directory — while the
// conversation lives in the projects directory keyed by the worktree the
// session actually ran in. Job b0633b77 on the machine this was written on:
// 3.6MB of transcript, an orb with no name, and a chat panel that opened blank
// because every reader of the path agreed the file wasn't there.
public class TranscriptRepairTests
{
    private static readonly Func<string, bool> IsThere = _ => true;
    private static readonly Func<string, bool> IsGone = _ => false;

    private static SessionStatus Mislocated(
        SessionSource source = SessionSource.ClaudeCode,
        string transcriptPath =
            "/Users/x/.claude/projects/-Users-x-Source-App/b0633b77.jsonl") =>
        new() { Source = source, TranscriptPath = transcriptPath };

    // --- WantsTranscriptRepair ------------------------------------------------

    [Fact]
    public void ARecordedPathThatIsMissingIsWorthHuntingFor()
    {
        Assert.True(SessionManager.WantsTranscriptRepair(Mislocated(), IsGone));
    }

    [Fact]
    public void APathThatExistsIsNeverSecondGuessed()
    {
        // The hook's record is right for every healthy session, and the repair
        // must cost those nothing beyond the stat the rule already pays.
        Assert.False(SessionManager.WantsTranscriptRepair(Mislocated(), IsThere));
    }

    [Fact]
    public void AnEmptyPathIsNotRepairedHere()
    {
        // Empty means "the hook never said" — the shape the chat panel already
        // hunts for itself, and the reading every scan rule treats as unknown
        // rather than wrong. The disk is not asked at all.
        var asked = new List<string>();
        var noPath = Mislocated(transcriptPath: "");

        Assert.False(SessionManager.WantsTranscriptRepair(
            noPath, path => { asked.Add(path); return false; }));
        Assert.Empty(asked);
    }

    [Theory]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void OnlyAClaudeCodeSessionIsRepaired(SessionSource source)
    {
        // The hunt walks Claude Code's own config roots, so for anything else
        // it is a directory walk that can only answer nothing — and for a
        // gateway session the transcript is on another machine entirely.
        Assert.False(SessionManager.WantsTranscriptRepair(Mislocated(source), IsGone));
    }

    // --- TranscriptHunts.Locate -------------------------------------------------

    private static readonly DateTime Now = new(2026, 8, 28, 11, 0, 0, DateTimeKind.Utc);

    private const string Found =
        "/Users/x/.claude/projects/-Users-x-Source-App--wt/b0633b77.jsonl";

    [Fact]
    public void AFoundPathIsRememberedWhileTheFileExists()
    {
        var hunts = new TranscriptHunts();
        var walks = 0;

        Func<string, string?> hunt = _ => { walks++; return Found; };

        Assert.Equal(Found, hunts.Locate("b0633b77", Now, hunt, IsThere));
        Assert.Equal(Found, hunts.Locate("b0633b77", Now.AddHours(1), hunt, IsThere));
        Assert.Equal(1, walks);
    }

    [Fact]
    public void AFoundPathThatVanishesGetsOneFreshHuntNotAStaleAnswer()
    {
        var hunts = new TranscriptHunts();
        var answers = new Queue<string?>(new[] { Found, null });

        Func<string, string?> hunt = _ => answers.Dequeue();

        Assert.Equal(Found, hunts.Locate("b0633b77", Now, hunt, IsThere));

        // The cached path no longer exists, so the memo is not an answer any
        // more; the hunt runs again and its "not found" replaces the memo.
        Assert.Null(hunts.Locate("b0633b77", Now.AddSeconds(2), hunt, IsGone));
        Assert.Empty(answers);
    }

    [Fact]
    public void NotFoundIsNotAskedAgainUntilRetryHasPassed()
    {
        // The hunt is a recursive walk of every projects directory and the scan
        // runs every two seconds; a phantom session with no transcript anywhere
        // must not turn that walk into a permanent per-scan cost.
        var hunts = new TranscriptHunts();
        var walks = 0;

        Func<string, string?> hunt = _ => { walks++; return null; };

        Assert.Null(hunts.Locate("de995bd9", Now, hunt, IsGone));
        Assert.Null(hunts.Locate(
            "de995bd9", Now + TranscriptHunts.Retry - TimeSpan.FromSeconds(1), hunt, IsGone));
        Assert.Equal(1, walks);
    }

    [Fact]
    public void NotFoundIsAskedAgainOnceRetryHasPassed()
    {
        // Forever would hide a brand-new session's orb after its conversation
        // had started: the transcript it is about to write has to be noticed.
        var hunts = new TranscriptHunts();
        var answers = new Queue<string?>(new string?[] { null, Found });

        Func<string, string?> hunt = _ => answers.Dequeue();

        Assert.Null(hunts.Locate("b0633b77", Now, hunt, IsThere));
        Assert.Equal(Found, hunts.Locate("b0633b77", Now + TranscriptHunts.Retry, hunt, IsThere));
        Assert.Empty(answers);
    }

    [Fact]
    public void SessionsAreRememberedSeparately()
    {
        // One phantom's cached "not found" must not answer for a different
        // session whose transcript is sitting right there.
        var hunts = new TranscriptHunts();

        Assert.Null(hunts.Locate("de995bd9", Now, _ => null, IsGone));
        Assert.Equal(Found, hunts.Locate("b0633b77", Now, _ => Found, IsThere));
    }
}
