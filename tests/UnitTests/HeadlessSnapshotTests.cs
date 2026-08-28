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
