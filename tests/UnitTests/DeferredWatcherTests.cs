using Xunit;

namespace ClaudeBuddy.Tests;

// CB-234. On macOS turning a FileSystemWatcher on can wait on the kernel's
// machine-wide sync(2) -- 118.7 s once, measured. DeferredWatcher exists so no
// caller waits for it. None of these tests turns a real watcher on: that call
// is the thing being kept off the test thread, and every one of them would
// inherit its timing if it did.
public class DeferredWatcherTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cb-deferredwatcher-" + Guid.NewGuid().ToString("N"));

    public DeferredWatcherTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // Built, not turned on: constructing a FileSystemWatcher is instant, enabling
    // it is the blocking call.
    private FileSystemWatcher Built() => new(_dir, "*.txt");

    // The negative control is built in. A start that never returns is forced; a
    // watchdog releases it after ten seconds and records that it had to. With the
    // start run inline (what the app did before) the constructor returns only
    // when the watchdog lets it, and the first assertion fails with the reason.
    // The ten seconds detects a hang -- nothing waits on it when the code is right.
    [Fact]
    public void AStartThatNeverReturnsDoesNotHoldUpTheConstructor()
    {
        var never = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        var watchdogFired = false;
        using var watchdog = new Timer(_ => { watchdogFired = true; never.Set(); }, null,
            TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        var deferred = new DeferredWatcher(
            () => { entered.Set(); never.Wait(); throw new IOException("released by the test"); },
            _ => { }, "test");

        Assert.False(watchdogFired, "the constructor waited for the watcher's start instead of returning");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the start was never attempted");
        Assert.Null(deferred.Watcher);

        never.Set();
        deferred.Starter.Join();
        Assert.Null(deferred.Watcher);
        deferred.Dispose();
    }

    [Fact]
    public void AStartThatThrowsLeavesNoWatcherAndNoException()
    {
        var wired = false;
        var deferred = new DeferredWatcher(
            () => throw new IOException("no fsevents today"), _ => wired = true, "test");

        deferred.Starter.Join();

        Assert.Null(deferred.Watcher);
        Assert.False(wired);
        deferred.Dispose();
    }

    [Fact]
    public void ASuccessfulStartIsWiredAndKept()
    {
        var made = Built();
        FileSystemWatcher? wiredWith = null;
        var deferred = new DeferredWatcher(() => made, w => wiredWith = w, "test");

        deferred.Starter.Join();

        Assert.Same(made, deferred.Watcher);
        Assert.Same(made, wiredWith);
        deferred.Dispose();
        Assert.Null(deferred.Watcher);
        Assert.Throws<ObjectDisposedException>(() => made.EnableRaisingEvents = true);
    }

    // Disposed while the start was still in flight: whoever finishes it finds no
    // owner, so it disposes the watcher rather than leaking a live FSEvents stream.
    [Fact]
    public void AWatcherThatArrivesAfterDisposeIsDisposed()
    {
        var release = new ManualResetEventSlim(false);
        var made = Built();
        var deferred = new DeferredWatcher(() => { release.Wait(); return made; }, _ => { }, "test");

        deferred.Dispose();
        release.Set();
        deferred.Starter.Join();

        Assert.Null(deferred.Watcher);
        Assert.Throws<ObjectDisposedException>(() => made.EnableRaisingEvents = true);
    }

    [Fact]
    public void TheStarterIsABackgroundThreadSoAStuckStartCannotKeepTheProcessAlive()
    {
        var release = new ManualResetEventSlim(false);
        var deferred = new DeferredWatcher(() => { release.Wait(); throw new IOException(); }, _ => { }, "cb-test-starter");

        Assert.True(deferred.Starter.IsBackground);
        Assert.Equal("cb-test-starter", deferred.Starter.Name);

        release.Set();
        deferred.Starter.Join();
    }
}
