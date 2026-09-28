using Xunit;

namespace ClaudeBuddy.Tests;

// The pure half of DispatcherClock: how far apart the dispatcher's clock and
// the platform's are, and when that is worth acting on. The reflection half is
// in tests/UiTests, against a real dispatcher.
public class DispatcherClockTests
{
    [Theory]
    [InlineData(231, 104, 127)]     // measured on a dev instance: the dispatcher's clock started first
    [InlineData(104, 231, -127)]
    [InlineData(500, 500, 0)]
    public void TheOffsetIsTheDispatchersClockLessThePlatforms(long dispatcherNow, long platformNow, long offset) =>
        Assert.Equal(offset, DispatcherClock.Offset(dispatcherNow, platformNow));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]          // two stopwatches read back to back
    [InlineData(-1, false)]
    [InlineData(2, true)]
    [InlineData(-2, true)]
    [InlineData(215, true)]         // the installed build's lateness
    public void OnlyAnOffsetBeyondAMillisecondIsAligned(long offsetMs, bool needed) =>
        Assert.Equal(needed, DispatcherClock.NeedsAlignment(offsetMs));
}
