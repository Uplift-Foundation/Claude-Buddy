using System;
using System.IO;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers the two pieces CB-25 added so a relay can find its own status file in
// the *shared* status directory: TryReadStatus (a read that may race a
// mid-write hook) and IsOwnStatus (whose file is this). The polling loop that
// uses them stays excluded — it watches a live session — but every decision it
// makes comes from here.
//
// The bridge is constructed for real: its constructor only computes names, and
// building the expected name through the same TmuxNames the constructor uses
// (same profile, same machine tag) is what lets the match be asserted on any
// machine.
public class RelayStatusMatchTests
{
    private static RemoteControlBridge NewBridge() => new(".claude");

    private static string OwnName() =>
        RemoteControlBridge.TmuxNames(
            ".claude", Environment.GetEnvironmentVariable("CLAUDE_BUDDY_RC_BRIDGE_TAG")).Session;

    [Fact]
    public void AStatusFileWhoseCwdLeafIsTheRelayNameIsOwn()
    {
        var bridge = NewBridge();

        Assert.True(bridge.IsOwnStatus(new SessionStatus
        {
            Cwd = "/anywhere/at/all/" + OwnName(),
            TranscriptPath = "/tmp/t.jsonl"
        }));
    }

    [Fact]
    public void SomeoneElsesSessionIsNotOwn()
    {
        var bridge = NewBridge();

        Assert.False(bridge.IsOwnStatus(new SessionStatus
        {
            Cwd = "/Users/someone/Source/their-project",
            TranscriptPath = "/tmp/t.jsonl"
        }));
    }

    // Another relay — same prefix, different account or machine — is exactly
    // the near-miss the exact-leaf comparison exists for.
    [Fact]
    public void AnotherRelaysSessionIsNotOwn()
    {
        var bridge = NewBridge();

        Assert.False(bridge.IsOwnStatus(new SessionStatus
        {
            Cwd = "/anywhere/" + OwnName() + "-not-quite",
            TranscriptPath = "/tmp/t.jsonl"
        }));
    }

    [Fact]
    public void AMissingOrTranscriptlessStatusIsNotOwn()
    {
        var bridge = NewBridge();

        Assert.False(bridge.IsOwnStatus(null));
        Assert.False(bridge.IsOwnStatus(new SessionStatus
        {
            Cwd = "/anywhere/" + OwnName(),
            TranscriptPath = ""
        }));
    }

    [Fact]
    public void TryReadStatusReadsARealFile()
    {
        var file = Path.Combine(
            Path.GetTempPath(), "cb-relay-match-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            // The hook's own key spelling — SessionStatus maps them through
            // JsonPropertyName, so the fixture writes what the hook writes.
            File.WriteAllText(file, "{\"state\":\"idle\",\"cwd\":\"/x\"}");

            var status = RemoteControlBridge.TryReadStatus(file);

            Assert.NotNull(status);
            Assert.Equal("/x", status!.Cwd);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void TryReadStatusAnswersNullForHalfWrittenGoneOrNullFiles()
    {
        var half = Path.Combine(
            Path.GetTempPath(), "cb-relay-match-" + Guid.NewGuid().ToString("N") + ".txt");
        var literal = Path.Combine(
            Path.GetTempPath(), "cb-relay-match-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(half, "{\"State\":\"id");
            File.WriteAllText(literal, "null");

            Assert.Null(RemoteControlBridge.TryReadStatus(half));
            Assert.Null(RemoteControlBridge.TryReadStatus(literal));
            Assert.Null(RemoteControlBridge.TryReadStatus(
                Path.Combine(Path.GetTempPath(), "cb-relay-match-never-exists.txt")));
        }
        finally
        {
            File.Delete(half);
            File.Delete(literal);
        }
    }
}
