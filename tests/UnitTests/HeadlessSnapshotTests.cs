using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers SessionManager.HeadlessSnapshot — the UI-less composition of the scan
// rules that answers a far machine's roster request before SessionManager has
// started (CB-24) — and RemoteControlSessions.LocalSessions falling back to it.
//
// Every case hands in all four seams (directory, job listing, process
// liveness, clock), because the defaults read this machine: the real temp
// directory, the real process table, and a shell-out to `claude agents
// --json`. Those default arms are the change's named coverage gap, the same
// way the live scan's constructor defaults are.
//
// The two LocalSessions cases mutate process-wide statics (the provider and
// the fallback), so they live in this one class — classes are xunit's
// parallelism unit — and put both back in a finally.
public class HeadlessSnapshotTests
{
    private static readonly Func<Dictionary<string, string>?> NoJobs =
        () => new Dictionary<string, string>();

    private static string NewStatusDir()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "cb-headless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteStatus(string dir, string id, SessionStatus status) =>
        File.WriteAllText(
            Path.Combine(dir, id + ".txt"), JsonSerializer.Serialize(status));

    [Fact]
    public void KeepsALiveSessionAndCarriesItsStatusThrough()
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "abc123", new SessionStatus
            {
                State = "idle",
                Title = "job-hunter",
                Cwd = "/tmp/somewhere",
                SessionPid = 4242
            });

            var kept = SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow);

            var one = Assert.Single(kept);
            Assert.Equal("abc123", one.SessionId);
            Assert.Equal("job-hunter", one.Status.Title);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DropsThisAppsOwnRelay()
    {
        var dir = NewStatusDir();
        try
        {
            // The own-relay test keys on the *leaf* of the cwd — see
            // RemoteControlBridge.IsOwnRelayCwd — so any path whose last
            // segment wears the relay prefix is one.
            WriteStatus(dir, "relay", new SessionStatus
            {
                State = "idle",
                Cwd = "/tmp/anywhere/claude-buddy-rc--claude-somebox",
                SessionPid = 4242
            });

            var kept = SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow);

            Assert.Empty(kept);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DropsASessionWhoseProcessIsGone()
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "gone", new SessionStatus
            {
                State = "idle",
                Cwd = "/tmp/somewhere",
                SessionPid = 4242
            });

            var kept = SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => false, nowUtc: DateTime.UtcNow);

            Assert.Empty(kept);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SkipsAFileStillBeingWritten()
    {
        var dir = NewStatusDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "half.txt"), "{ \"State\": \"id");
            WriteStatus(dir, "whole", new SessionStatus
            {
                State = "idle",
                Cwd = "/tmp/somewhere",
                SessionPid = 4242
            });

            var kept = SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow);

            Assert.Equal("whole", Assert.Single(kept).SessionId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // JSON's literal `null` deserializes without throwing, to a null status —
    // a different arm than the mid-write case above, which lands in the catch.
    [Fact]
    public void SkipsAFileHoldingTheJsonNullLiteral()
    {
        var dir = NewStatusDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "nul.txt"), "null");

            var kept = SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow);

            Assert.Empty(kept);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AnswersEmptyWhenTheDirectoryDoesNotExistYet()
    {
        var kept = SessionManager.HeadlessSnapshot(
            Path.Combine(Path.GetTempPath(), "cb-headless-never-" + Guid.NewGuid().ToString("N")),
            NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow);

        Assert.Empty(kept);
    }

    // --- the husk, and the two rules agreeing about it ---

    // The row a backgrounded turn leaves in its transcript, and one row of the
    // housekeeping Claude Code writes after it. Shortened from the captures in
    // TranscriptHandoffTests, which is where the full fixtures and the reasoning
    // about each field live; what matters here is only that the tail reads as
    // handed off, not why.
    private const string BackgroundingRow =
        @"{""type"":""system"",""subtype"":""informational"","
      + @"""content"":""Backgrounding after the current tool finishes…"","
      + @"""timestamp"":""2026-08-28T17:53:15.295Z"",""level"":""warning""}";

    private const string ConversationRow =
        @"{""type"":""assistant"",""message"":{""role"":""assistant"","
      + @"""content"":[{""type"":""text"",""text"":""on it""}]},"
      + @"""timestamp"":""2026-08-28T17:53:11.000Z""}";

    // A transcript on disk, since the rule this exercises stats and reads a real
    // file — it is the closure behind the verdict, not the verdict's own logic,
    // that this is about.
    private static string WriteTranscript(string dir, params string[] rows)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, rows);
        return path;
    }

    // A husk: healthy by every other measure — live process, file written a
    // moment ago, real title — and handed away according to its own transcript.
    private static SessionStatus Husk(string transcriptPath) => new()
    {
        State = "generating",
        Title = "job-hunter",
        Cwd = "/tmp/somewhere",
        SessionPid = 4242,
        TranscriptPath = transcriptPath
    };

    // The verdict the live scan reaches for the same file, built the way
    // SessionManager builds it: the same closure, over the same status.
    private static SessionManager.ScanVerdict LiveScanVerdict(
        string sessionId, SessionStatus status, DateTime written, DateTime now)
    {
        status.Source = SessionManager.SourceOf(status);

        var phase = status.Source == SessionSource.ClaudeCode
            ? BackgroundJobs.Phase(new Dictionary<string, string>(), sessionId)
            : JobPhase.Unknown;

        Func<bool> handedToBackground = () =>
            SessionPresence.CouldBeABackgroundedHusk(status, phase)
            && TranscriptHandoff.EndsBackgrounded(status.TranscriptPath);

        return SessionManager.JudgeLiveness(
            sessionId, status, written, now, SessionManager.StaleAfter,
            new HashSet<string>(), _ => true, handedToBackground);
    }

    // The two must not disagree, and this is the one thing they can disagree
    // about.
    //
    // HeadlessSnapshot's own comment says it is composed from the same rules as
    // the live scan, in the same order, "so the two cannot disagree about which
    // sessions exist" — and every rule but this one is a pure function of the
    // file. The husk check is the exception: it is a closure, so a caller can
    // pass one that answers nothing and still compile. That is exactly what
    // happened. JudgeLiveness gained the parameter on one branch and this call
    // site arrived on another, the two merged with no textual conflict, and for
    // three hours `develop` did not build at all.
    //
    // Stubbing it with `() => false` is the version of that mistake that *does*
    // compile, and it is the one this case exists to catch: a far machine would
    // then be served an orb for a session whose conversation has moved to a
    // fork — a duplicate wearing the same title, frozen at "generating", that no
    // hook will ever correct. Which is the bug the husk rule was written for,
    // reintroduced on the one path it did not cover.
    [Fact]
    public void TheHeadlessSnapshotDropsAHuskJustAsTheLiveScanDoes()
    {
        var dir = NewStatusDir();
        try
        {
            var transcript = WriteTranscript(dir, ConversationRow, BackgroundingRow);
            var status = Husk(transcript);
            WriteStatus(dir, "husk1", status);

            var now = DateTime.UtcNow;

            // The live scan's answer for this file.
            Assert.Equal(
                SessionManager.ScanVerdict.Backgrounded,
                LiveScanVerdict("husk1", status, now, now));

            // ...and the headless one's, which must agree.
            Assert.Empty(SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: now));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The other direction, so "dropped" above is about the transcript rather
    // than about anything else this status happens to carry. The same session,
    // the same live pid, the same recent write — and a transcript whose last
    // word is conversation, which means the session lived on past whatever the
    // tail held earlier.
    [Fact]
    public void ASessionThatWasNotHandedAwayIsKeptByBoth()
    {
        var dir = NewStatusDir();
        try
        {
            var transcript = WriteTranscript(dir, BackgroundingRow, ConversationRow);
            var status = Husk(transcript);
            WriteStatus(dir, "husk2", status);

            var now = DateTime.UtcNow;

            Assert.Equal(
                SessionManager.ScanVerdict.Keep,
                LiveScanVerdict("husk2", status, now, now));

            Assert.Equal("husk2", Assert.Single(SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: now)).SessionId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The gate in front of the transcript read, from the other side.
    //
    // The two cases above both consult the closure — one ends backgrounded and
    // one does not. This is the session where it is never consulted at all: no
    // transcript path, so there is nothing to read and CouldBeABackgroundedHusk
    // says so before TranscriptHandoff is asked. Worth pinning separately
    // because it is the arm that keeps a scan from statting a file for every
    // session that has no transcript to stat, and because "kept" here has to
    // mean "the question did not arise" rather than "the answer was no".
    [Fact]
    public void ASessionWithNoTranscriptIsKeptWithoutTheQuestionBeingAsked()
    {
        var dir = NewStatusDir();
        try
        {
            var status = Husk(transcriptPath: "");
            WriteStatus(dir, "notranscript", status);

            var now = DateTime.UtcNow;

            Assert.Equal(
                SessionManager.ScanVerdict.Keep,
                LiveScanVerdict("notranscript", status, now, now));

            Assert.Equal("notranscript", Assert.Single(SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: now)).SessionId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LocalSessionsFallsBackToTheHeadlessSnapshot()
    {
        RemoteControlSessions.ForgetLocalSessionsForTests();
        var was = RemoteControlSessions.HeadlessFallback;
        try
        {
            RemoteControlSessions.HeadlessFallback = () =>
                new List<(string, SessionStatus)> { ("fallback", new SessionStatus()) };

            Assert.Equal(
                "fallback", Assert.Single(RemoteControlSessions.LocalSessions()).SessionId);
        }
        finally
        {
            RemoteControlSessions.HeadlessFallback = was;
        }
    }

    [Fact]
    public void LocalSessionsPrefersTheLiveProviderOverTheFallback()
    {
        var was = RemoteControlSessions.HeadlessFallback;
        try
        {
            RemoteControlSessions.HeadlessFallback = () =>
                new List<(string, SessionStatus)> { ("fallback", new SessionStatus()) };
            RemoteControlSessions.ProvideLocalSessions(() =>
                new List<(string, SessionStatus)> { ("live", new SessionStatus()) });

            Assert.Equal(
                "live", Assert.Single(RemoteControlSessions.LocalSessions()).SessionId);
        }
        finally
        {
            RemoteControlSessions.ForgetLocalSessionsForTests();
            RemoteControlSessions.HeadlessFallback = was;
        }
    }
}
