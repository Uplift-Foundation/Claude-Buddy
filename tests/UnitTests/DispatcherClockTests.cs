using Xunit;

namespace ClaudeBuddy.Tests;

// The pure half of DispatcherClock: how far apart the dispatcher's clock and
// the platform's are, and when that is worth acting on. The reflection half is
// in tests/UiTests, against a real dispatcher.
public class DispatcherClockTests
{
    [Theory]
    [InlineData(104, 231, 104, 127)]    // measured on a dev instance: the dispatcher's clock started first
    [InlineData(104, 231, 110, 121)]    // measured to the nearer edge of the bracket
    [InlineData(231, 104, 231, -127)]
    [InlineData(500, 500, 500, 0)]
    [InlineData(500, 502, 504, 0)]      // preempted between reads: same clock, no offset
    [InlineData(500, 500, 504, 0)]
    [InlineData(500, 504, 504, 0)]
    public void TheOffsetIsHowFarTheDispatcherReadsOutsideTheBracket(
        long platformBefore, long dispatcherNow, long platformAfter, long offset) =>
        Assert.Equal(offset, DispatcherClock.Offset(platformBefore, dispatcherNow, platformAfter));

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
