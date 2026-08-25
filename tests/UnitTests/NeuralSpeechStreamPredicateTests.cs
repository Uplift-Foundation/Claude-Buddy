using Xunit;

namespace ClaudeBuddy.UnitTests;

// The two decisions NeuralSpeech makes about the engine's own output while it is
// speaking. Both were lambdas inside the method that starts the engine process,
// which is excluded — and an excluded method does not exclude the lambdas hoisted
// out of it, so both were being counted while the method around them was not.
//
// Small, but not trivial: the first is the difference between the UI reacting
// when audio begins and reacting when a process was launched seconds earlier, and
// the second is the difference between a failing engine saying so and the speak
// button appearing to do nothing.
public class NeuralSpeechStreamPredicateTests
{
    [Fact]
    public void TheSpeakingMarkerIsRecognised()
    {
        Assert.True(NeuralSpeech.IsSpeakingMarker("speaking"));
    }

    // The line carries a duration after the word, which is why this is a prefix
    // match rather than an equality check.
    [Fact]
    public void TheMarkerIsRecognisedWithATrailingDuration()
    {
        Assert.True(NeuralSpeech.IsSpeakingMarker("speaking 3.4"));
    }

    [Fact]
    public void OtherOutputIsNotTheMarker()
    {
        Assert.False(NeuralSpeech.IsSpeakingMarker("loading model"));
        Assert.False(NeuralSpeech.IsSpeakingMarker(""));
    }

    // Prefix, not substring: a line that merely mentions the word must not
    // un-grey the button before any audio exists.
    [Fact]
    public void TheWordHasToBeAtTheStartOfTheLine()
    {
        Assert.False(NeuralSpeech.IsSpeakingMarker("now speaking"));
    }

    // Ordinal, so the answer does not depend on the machine's locale. Case
    // matters for the same reason — this is a machine-readable marker, not text
    // for a person.
    [Fact]
    public void TheMarkerIsCaseSensitive()
    {
        Assert.False(NeuralSpeech.IsSpeakingMarker("Speaking"));
        Assert.False(NeuralSpeech.IsSpeakingMarker("SPEAKING"));
    }

    // The stream ends with a null, which arrives as a data event like any other.
    [Fact]
    public void TheEndOfTheStreamIsNotTheMarker()
    {
        Assert.False(NeuralSpeech.IsSpeakingMarker(null));
    }

    // ---- stderr ----------------------------------------------------------

    [Fact]
    public void RealStderrOutputIsWorthReporting()
    {
        Assert.True(NeuralSpeech.IsWorthReporting("onnxruntime: failed to load model"));
    }

    // Blank lines are not a failure, and printing them would bury the one line
    // that is. Whitespace-only counts as blank, which is what
    // IsNullOrWhiteSpace buys over IsNullOrEmpty — a process writing "\n" per
    // flush would otherwise print a screenful of nothing.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void BlankStderrIsNotWorthReporting(string? line)
    {
        Assert.False(NeuralSpeech.IsWorthReporting(line));
    }
}
