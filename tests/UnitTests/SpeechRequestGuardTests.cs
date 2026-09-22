using Xunit;

namespace ClaudeBuddy.Tests;

// The rule that decides whether a summary which has just come back still gets
// spoken.
//
// It is here, pure and named, because the thing it replaces was an expression
// buried in the method that utters — `State != SpeakState.Preparing` — which no
// test could see and which was wrong. It asked whether the process-wide speak
// state had moved, when what it meant to ask was whether the *user* had asked
// for silence. Those are the same question only on a machine with one orb.
public class SpeechRequestGuardTests
{
    [Fact]
    public void SpeaksWhenNothingHasHappenedSince()
    {
        Assert.True(SpeechRequest.ShouldStillSpeak(
            startedRequest: 7, currentRequest: 7, startedStop: 3, currentStop: 3));
    }

    // The user pressed stop, or pressed the button a second time — both routes
    // go through TextToSpeech.Cancel, which is the only thing that moves this
    // counter.
    [Fact]
    public void StaysQuietWhenTheUserCancelled()
    {
        Assert.False(SpeechRequest.ShouldStillSpeak(
            startedRequest: 7, currentRequest: 7, startedStop: 3, currentStop: 4));
    }

    // Somebody asked for something else to be spoken while this summary was
    // still being written. The newer request owns the speaker.
    [Fact]
    public void StaysQuietWhenANewerRequestReplacedIt()
    {
        Assert.False(SpeechRequest.ShouldStillSpeak(
            startedRequest: 7, currentRequest: 8, startedStop: 3, currentStop: 3));
    }

    [Fact]
    public void StaysQuietWhenBothHappened()
    {
        Assert.False(SpeechRequest.ShouldStillSpeak(
            startedRequest: 7, currentRequest: 8, startedStop: 3, currentStop: 4));
    }

    // The case the old code got wrong, stated as a rule rather than a scenario:
    // neither counter moved, so nothing the user did suppresses this — however
    // much unrelated speech traffic went past in the meantime.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void UnrelatedStateChurnIsNotCancellation(int request, int stop)
    {
        Assert.True(SpeechRequest.ShouldStillSpeak(request, request, stop, stop));
    }
}
