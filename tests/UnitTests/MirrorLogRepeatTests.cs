using Xunit;

namespace ClaudeBuddy.UnitTests;

// Whether a line that repeats forever should be written every time.
//
// **The mini made this necessary within a minute of being deployed.** It cannot
// dial without a Local Network grant that a headless Mac has no way to obtain,
// so it wrote the identical connect failure every ten seconds — roughly 8,600
// lines a day into a log with a 1MB ceiling. The failure was real and worth
// knowing once; what it actually achieved was evicting the history that made
// the log worth reading at all, which is the opposite of what the log is for.
public class MirrorLogRepeatTests
{
    [Fact]
    public void TheFirstTimeIsAlwaysWorthSaying()
    {
        Assert.True(MirrorLog.WorthSaying(lastDetail: null, since: 0, "no route to host"));
    }

    [Fact]
    public void TheSameThingAgainImmediatelyIsNot()
    {
        Assert.False(MirrorLog.WorthSaying("no route to host", since: 1, "no route to host"));
    }

    [Fact]
    public void SomethingDifferentIsAlwaysWorthSaying()
    {
        // A fault that *changes* is news even mid-streak — "no route to host"
        // becoming "connection refused" is the machine coming back.
        Assert.True(MirrorLog.WorthSaying("no route to host", since: 3, "connection refused"));
    }

    [Fact]
    public void ARepeatIsRestatedEventually()
    {
        // Not silence. A fault still happening an hour later should still be
        // visible, and a log that goes quiet reads as a fault that stopped.
        Assert.True(MirrorLog.WorthSaying(
            "no route to host", MirrorLog.RepeatEvery, "no route to host"));
    }

    [Fact]
    public void OneShortOfTheThresholdIsStillQuiet()
    {
        // The boundary, because a `>=` written as `>` is one whole interval of
        // silence and only one of them was meant.
        Assert.False(MirrorLog.WorthSaying(
            "no route to host", MirrorLog.RepeatEvery - 1, "no route to host"));
    }

    [Fact]
    public void PastTheThresholdStaysWorthSaying()
    {
        Assert.True(MirrorLog.WorthSaying(
            "no route to host", MirrorLog.RepeatEvery * 4, "no route to host"));
    }

    [Fact]
    public void GoingQuietIsItselfADifferentThing()
    {
        // An empty detail after a non-empty one is a change, so a fault that
        // stops saying anything still registers rather than being swallowed as
        // "the same as before".
        Assert.True(MirrorLog.WorthSaying("no route to host", since: 2, ""));
    }

    [Fact]
    public void TheThresholdIsLongEnoughToBeWorthHaving()
    {
        // A guard on the constant rather than on behaviour: at a ten-second
        // tick, anything under about twenty would still fill the log, and this
        // number is the whole of the fix.
        Assert.True(MirrorLog.RepeatEvery >= 20);
    }
}
