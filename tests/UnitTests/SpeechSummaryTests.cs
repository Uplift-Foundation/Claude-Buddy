using Xunit;

namespace ClaudeBuddy.Tests;

// The decisions behind "what does the speaker read", with no speech engine,
// no settings file and no subprocess anywhere near them.
//
// This is the whole reason SpeechPlan was lifted out of ChatPanel.SpeakLatest.
// The utterance itself is excluded from coverage — TextToSpeech.Speak makes the
// machine make a noise — so a choice left inside the method that utters is a
// choice nothing can assert.
public class SpeechPlanTests
{
    private static string Long(int chars) => new('x', chars);

    // --- nothing to say ---

    // Three ways a reply can be absent, all of which have to answer the same
    // way in both modes. A summary mode that asked a model to summarise an
    // empty string would spend seconds to produce nothing.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void NothingToSayIsSilentInBothModes(string? text)
    {
        Assert.True(SpeechPlan.For(text, SpeakScope.Full).Silent);

        var summary = SpeechPlan.For(text, SpeakScope.Summary);
        Assert.True(summary.Silent);
        Assert.False(summary.NeedsSummary);
    }

    // --- full ---

    // The existing behaviour, which the default has to keep exactly: the whole
    // text, untouched, and no round trip.
    [Fact]
    public void FullModeSpeaksTheWholeReplyAndAsksNobody()
    {
        var reply = Long(SpeechPlan.ShortEnoughChars * 3);

        var plan = SpeechPlan.For(reply, SpeakScope.Full);

        Assert.Equal(reply, plan.Text);
        Assert.False(plan.NeedsSummary);
        Assert.False(plan.Silent);
    }

    // Full mode does not trim, because it does not measure. Asserted so the
    // trimming in the summary arm is visibly a consequence of measuring rather
    // than a general tidy-up somebody can move.
    [Fact]
    public void FullModeDoesNotTouchTheText()
    {
        const string padded = "  the reply  ";

        Assert.Equal(padded, SpeechPlan.For(padded, SpeakScope.Full).Text);
    }

    // --- summary ---

    // The case the mode exists for.
    [Fact]
    public void ALongReplyInSummaryModeNeedsSummarising()
    {
        var plan = SpeechPlan.For(Long(SpeechPlan.ShortEnoughChars + 1), SpeakScope.Summary);

        Assert.True(plan.NeedsSummary);
        Assert.False(plan.Silent);
    }

    // A reply already about as long as its own summary would be. Summarising it
    // buys several seconds of silence in exchange for nothing, which is exactly
    // how the mode would come to read as broken on short replies.
    [Fact]
    public void AShortReplyInSummaryModeIsSpokenAsItIs()
    {
        var reply = Long(SpeechPlan.ShortEnoughChars);

        var plan = SpeechPlan.For(reply, SpeakScope.Summary);

        Assert.False(plan.NeedsSummary);
        Assert.Equal(reply, plan.Text);
    }

    // Both sides of the boundary in one case, because an off-by-one here is the
    // difference between "at the threshold" and "one past it" and neither reads
    // wrong on its own.
    [Fact]
    public void TheThresholdIsInclusive()
    {
        Assert.False(SpeechPlan.For(Long(SpeechPlan.ShortEnoughChars), SpeakScope.Summary).NeedsSummary);
        Assert.True(SpeechPlan.For(Long(SpeechPlan.ShortEnoughChars + 1), SpeakScope.Summary).NeedsSummary);
    }

    // Padding is not length. A short reply wrapped in blank lines is still a
    // short reply, and measuring it untrimmed would send it on a round trip it
    // does not need.
    [Fact]
    public void WhitespacePaddingDoesNotMakeAReplyLong()
    {
        var padded = "\n\n\n" + Long(SpeechPlan.ShortEnoughChars) + "\n\n\n";

        var plan = SpeechPlan.For(padded, SpeakScope.Summary);

        Assert.False(plan.NeedsSummary);
        Assert.Equal(Long(SpeechPlan.ShortEnoughChars), plan.Text);
    }
}

// The two pure halves of producing the summary: what gets asked, and what is
// done with the answer. The subprocess between them is excluded and covered by
// the seam in the UI suite.
public class SpeechSummaryTextTests
{
    // --- the prompt ---

    // The instruction has to say the output is *heard*. Every constraint in it
    // exists because the alternative is spoken aloud: a preamble is wasted
    // audio, markdown is read as punctuation.
    [Fact]
    public void ThePromptCarriesTheReplyAndAsksForSpeakableProse()
    {
        var prompt = SpeechSummary.Prompt("the assistant said something");

        Assert.Contains("the assistant said something", prompt);
        Assert.Contains("two or three sentences", prompt);
        Assert.Contains("read aloud", prompt);
        Assert.Contains("No preamble", prompt);
    }

    // Reply is the default kind, so the two-argument call above and this one
    // stay indistinguishable — the negative control for the variant below.
    [Fact]
    public void ThePromptDefaultsToTheReplyKind()
    {
        Assert.Equal(
            SpeechSummary.Prompt("some reply"),
            SpeechSummary.Prompt("some reply", SpeechSummaryKind.Reply));
    }

    // CB-167's vibe summary: a different question from the reply summary
    // above — "what's next" rather than "what did it say" — because this is
    // what gets spoken instead of a chime when a turn finishes, and knowing
    // whether to come back is the whole reason to prefer it over a Glass
    // sound.
    [Fact]
    public void TheTurnFinishedPromptAsksWhatWasDoneAndWhatsNext()
    {
        var prompt = SpeechSummary.Prompt("the assistant did something", SpeechSummaryKind.TurnFinished);

        Assert.Contains("the assistant did something", prompt);
        Assert.Contains("what's next", prompt);
        Assert.Contains("one to three", prompt);
        Assert.Contains("read aloud", prompt);
        Assert.Contains("No preamble", prompt);
    }

    // The two kinds ask different questions, not the same question worded
    // differently — this is what would fail if TurnFinished silently reused
    // the Reply instruction with the kind parameter ignored.
    [Fact]
    public void TheTwoKindsProduceDifferentPrompts()
    {
        Assert.NotEqual(
            SpeechSummary.Prompt("a reply", SpeechSummaryKind.Reply),
            SpeechSummary.Prompt("a reply", SpeechSummaryKind.TurnFinished));
    }

    // A very long reply is exactly what this mode is for, but its tail adds
    // little to three sentences and costs latency on the slow leg.
    [Fact]
    public void AVeryLongReplyIsTruncatedBeforeBeingSent()
    {
        var huge = new string('y', SpeechSummary.MaxSourceChars * 2);

        var prompt = SpeechSummary.Prompt(huge);

        Assert.True(prompt.Length < huge.Length);
        Assert.Contains(new string('y', 100), prompt);
    }

    // The negative control for the case above: a reply under the bound is sent
    // whole. Without this, a Prompt that truncated everything would pass.
    [Fact]
    public void AReplyUnderTheBoundIsSentWhole()
    {
        var reply = new string('y', SpeechSummary.MaxSourceChars - 1);

        Assert.Contains(reply, SpeechSummary.Prompt(reply));
    }

    // --- cleaning the answer ---

    [Fact]
    public void AnOrdinarySummaryIsPassedThrough()
    {
        Assert.Equal(
            "The retry logic moved into its own class. Tests were added.",
            SpeechSummary.Clean("The retry logic moved into its own class. Tests were added."));
    }

    // Told not to write a preamble; sometimes writes one anyway. Only stripped
    // when it is short, ends in a colon and has something after it.
    [Fact]
    public void AShortPreambleLineIsRemoved()
    {
        Assert.Equal(
            "The retry logic moved.",
            SpeechSummary.Clean("Summary:\nThe retry logic moved."));
    }

    // A summary whose own first sentence contains a colon is not a preamble,
    // and losing it would lose the summary's opening. This is the arm that
    // makes the rule above safe.
    [Fact]
    public void ARealSentenceEndingInAColonSurvives()
    {
        const string text = "One thing changed and it is this: the timeout is per attempt now.";

        Assert.Equal(text, SpeechSummary.Clean(text));
    }

    // Told not to write code; a model that fenced its prose anyway would have
    // the fence spoken as backticks.
    [Fact]
    public void AFencedBlockIsUnwrapped()
    {
        Assert.Equal(
            "The retry logic moved.",
            SpeechSummary.Clean("```\nThe retry logic moved.\n```"));
    }

    // Paragraph breaks are not information once spoken — engines pause at them
    // as if a new topic had started.
    [Fact]
    public void ParagraphBreaksAreCollapsedToSingleSpaces()
    {
        Assert.Equal(
            "First sentence. Second sentence.",
            SpeechSummary.Clean("First sentence.\n\n\nSecond sentence."));
    }

    // Nothing usable came back. Null rather than an empty string, so the caller
    // takes the stated-failure path instead of speaking silence.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("```\n\n```")]
    public void NothingUsableAnswersNull(string? output)
    {
        Assert.Null(SpeechSummary.Clean(output));
    }

    // Stated, not silent, and not the full reply — a user who chose this mode
    // to avoid a five-minute reading is not helped by being given one.
    [Fact]
    public void TheFailureAnswerSaysSomething()
    {
        Assert.False(string.IsNullOrWhiteSpace(SpeechSummary.Unavailable));
    }
}

// What actually gets spoken once the summariser has had its turn — every arm
// of it, including the ones that only happen when something goes wrong.
//
// This exists as its own class because it drives the seam, which is
// process-wide: the disposal below has to put it back or a later class in this
// assembly gets this one's fake summariser.
public class SpeechSummaryOutcomeTests : IDisposable
{
    public void Dispose() => SpeechSummary.SummarizerForTests = null;

    private static void Answer(Func<string, Task<string?>> summarizer) =>
        SpeechSummary.SummarizerForTests = summarizer;

    // The ordinary case, and the negative control for every failure below:
    // without it, an implementation that always answered Unavailable would
    // satisfy all of them.
    [Fact]
    public async Task AWorkingSummariserIsWhatGetsSpoken()
    {
        Answer(_ => Task.FromResult<string?>("Three files changed and the tests pass."));

        Assert.Equal(
            "Three files changed and the tests pass.",
            await SpeechSummary.SummarizeOrSayWhyAsync("a very long reply"));
    }

    // The reply reaches the summariser intact. Without this, a summariser being
    // handed the wrong text would still pass every other case here.
    [Fact]
    public async Task TheReplyIsWhatTheSummariserIsAskedAbout()
    {
        string? seen = null;
        Answer(reply => { seen = reply; return Task.FromResult<string?>("ok"); });

        await SpeechSummary.SummarizeOrSayWhyAsync("the original reply");

        Assert.Equal("the original reply", seen);
    }

    // Nothing usable came back. Stated, not silent — a speaker that says
    // nothing is indistinguishable from a broken one.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NothingUsableIsSaidRatherThanSwallowed(string? answer)
    {
        Answer(_ => Task.FromResult(answer));

        Assert.Equal(SpeechSummary.Unavailable, await SpeechSummary.SummarizeOrSayWhyAsync("reply"));
    }

    // A summariser that threw. Same answer as any other failure, and critically
    // it does not propagate: this is awaited from a UI thread, and an exception
    // escaping would take the panel with it.
    [Fact]
    public async Task AThrowingSummariserFailsToASentenceRatherThanUpwards()
    {
        Answer(_ => throw new InvalidOperationException("no CLI here"));

        Assert.Equal(SpeechSummary.Unavailable, await SpeechSummary.SummarizeOrSayWhyAsync("reply"));
    }

    // The same, for a faulted task rather than a synchronous throw — the shape
    // a real async failure actually arrives in.
    [Fact]
    public async Task AFaultedSummariserTaskAlsoFailsToASentence()
    {
        Answer(_ => Task.FromException<string?>(new TimeoutException("took too long")));

        Assert.Equal(SpeechSummary.Unavailable, await SpeechSummary.SummarizeOrSayWhyAsync("reply"));
    }

    // Deliberately *not* the full reply. Somebody who chose summary mode chose
    // it to avoid a five-minute reading, and handing them one because the
    // summariser failed is the opposite of what they asked for. This is the
    // assertion that stops a future "helpful" fallback being added.
    [Fact]
    public async Task AFailureNeverFallsBackToReadingTheWholeReply()
    {
        var reply = new string('x', 10_000);
        Answer(_ => Task.FromResult<string?>(null));

        var spoken = await SpeechSummary.SummarizeOrSayWhyAsync(reply);

        Assert.NotEqual(reply, spoken);
        Assert.True(spoken.Length < 200);
    }

    // CB-167's entry point: the two-argument overload SpeechRequest.
    // SpeakTurnSummary calls. The seam itself is kind-agnostic (it never
    // builds a prompt), so this proves the overload reaches the same working
    // path as the reply-kind default rather than a parallel one nobody
    // exercises.
    [Fact]
    public async Task TheTurnFinishedOverloadReachesTheSameSeamAsTheDefault()
    {
        Answer(_ => Task.FromResult<string?>("Fixed the bug and pushed it."));

        Assert.Equal(
            "Fixed the bug and pushed it.",
            await SpeechSummary.SummarizeOrSayWhyAsync("a very long reply", SpeechSummaryKind.TurnFinished));
    }
}
