using System.IO;
using Xunit;

namespace ClaudeBuddy.Tests;

// The set of CLI processes this app started for itself, and the scan actually
// leaving them out.
//
// Both halves are here on purpose. The membership rule alone would pass while
// nothing consulted it — which is the shape of failure this repo keeps paying
// for — so the second class below writes real status files and asserts on what
// comes back out of the scan, not on what the rule would have said.
[Collection("InternalSessions")]
public class InternalSessionsTests : IDisposable
{
    public InternalSessionsTests() => InternalSessions.Clear();
    public void Dispose() => InternalSessions.Clear();

    [Fact]
    public void RemembersAPidItWasGiven()
    {
        InternalSessions.Remember(4242);
        Assert.True(InternalSessions.IsInternal(4242));
    }

    [Fact]
    public void KnowsNothingAboutAPidItNeverSaw()
    {
        InternalSessions.Remember(4242);
        Assert.False(InternalSessions.IsInternal(9999));
    }

    // Released when the child ends, so a pid the OS hands to somebody else later
    // is not still being hidden.
    [Fact]
    public void ForgetsWhenTheProcessIsDone()
    {
        InternalSessions.Remember(4242);
        InternalSessions.Forget(4242);
        Assert.False(InternalSessions.IsInternal(4242));
    }

    // A status file whose pid never got written carries 0, and a caller that
    // failed to start a process has nothing useful to hand over either. Neither
    // may become a match — a stored 0 would hide every session missing a pid.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPidIsNeitherStoredNorMatched(int pid)
    {
        InternalSessions.Remember(pid);
        Assert.False(InternalSessions.IsInternal(pid));
    }

    [Fact]
    public void ForgettingSomethingItNeverKnewIsHarmless()
    {
        InternalSessions.Forget(4242);
        Assert.False(InternalSessions.IsInternal(4242));
    }
}

// The scan consulting it, driven through HeadlessSnapshot — which takes its
// status directory and its liveness answer, so this asserts the real filter
// rather than a stand-in for it.
[Collection("InternalSessions")]
public class InternalSessionScanTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cb-internal-" + Guid.NewGuid().ToString("N"));

    public InternalSessionScanTests()
    {
        InternalSessions.Clear();
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        InternalSessions.Clear();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // One line, exactly as ClaudeBuddyHook.sh's printf writes it. Written from
    // the real format rather than prettified: a fixture that reformats what the
    // hook produces is testing a file nothing ever creates.
    private void WriteStatus(string sessionId, int pid) =>
        File.WriteAllText(
            Path.Combine(_dir, sessionId + ".txt"),
            "{\"state\":\"generating\",\"cli\":\"claude\",\"cwd\":\"/Users/someone/project\","
            + "\"title\":\"\",\"color\":\"cyan\",\"term_program\":\"tmux\",\"term_id\":\"\","
            + "\"tty\":\"ttys001\",\"tmux_socket\":\"/tmp/tmux-501/default\",\"tmux_pane\":\"%1\","
            + "\"tmux_bin\":\"/opt/homebrew/bin/tmux\",\"session_pid\":" + pid
            + ",\"transcript_path\":\"\"}");

    [Fact]
    public void TheSummariserItSpawnedDrawsNoOrb()
    {
        WriteStatus("11111111-1111-1111-1111-111111111111", 4242);
        InternalSessions.Remember(4242);

        var found = SessionManager.HeadlessSnapshot(
            statusDir: _dir, isRunning: _ => true, honourOrbLifetime: false);

        Assert.Empty(found);
    }

    // The other direction, and the one that matters most: a session with the
    // same shape that this app did *not* start is still shown. A filter that
    // guessed from the invocation rather than the pid would fail here, which is
    // why the pid is what gets recorded.
    [Fact]
    public void AUserSessionOfTheSameShapeIsStillShown()
    {
        WriteStatus("22222222-2222-2222-2222-222222222222", 4242);

        var found = SessionManager.HeadlessSnapshot(
            statusDir: _dir, isRunning: _ => true, honourOrbLifetime: false);

        Assert.Single(found);
    }

    [Fact]
    public void OnlyTheInternalOneIsDroppedWhenBothAreRunning()
    {
        WriteStatus("33333333-3333-3333-3333-333333333333", 4242);
        WriteStatus("44444444-4444-4444-4444-444444444444", 5353);
        InternalSessions.Remember(4242);

        var found = SessionManager.HeadlessSnapshot(
            statusDir: _dir, isRunning: _ => true, honourOrbLifetime: false);

        Assert.Equal("44444444-4444-4444-4444-444444444444", Assert.Single(found).SessionId);
    }

    // Once the child has exited the claim is released, so a later session that
    // inherits the pid is visible again.
    [Fact]
    public void AReusedPidIsVisibleOnceTheChildHasGone()
    {
        WriteStatus("55555555-5555-5555-5555-555555555555", 4242);
        InternalSessions.Remember(4242);
        InternalSessions.Forget(4242);

        var found = SessionManager.HeadlessSnapshot(
            statusDir: _dir, isRunning: _ => true, honourOrbLifetime: false);

        Assert.Single(found);
    }
}

// Both classes above drive the same process-wide set, so they are serialised
// against each other. Without this they raced: one class's Clear() in a
// constructor wiped the pid the other had just remembered, and the scan case
// failed while the rule it depends on was perfectly correct. The same hazard
// tests/UiTests documents for settings, and the same answer.
[CollectionDefinition("InternalSessions", DisableParallelization = true)]
public class InternalSessionsCollection { }
